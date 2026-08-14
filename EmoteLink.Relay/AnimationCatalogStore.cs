using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EmoteLink.Relay;

public sealed class AnimationCatalogStore
{
    public const int MaximumBatchSize = 128;
    public const int MaximumSignalRMessageBytes = 256 * 1024;
    public const int MaximumPayloadBytes = 120 * 1024;
    public const int MaximumAggregatePayloadBytes = 1024 * 1024;
    public const int MaximumNamesPerArtifact = 20;
    public const int MaximumPayloadCandidatesPerArtifact = 5;
    public const int MaximumReportsPerArtifact = 64;
    public const int DefaultMaximumArtifacts = 250_000;
    public const long DefaultMaximumStoredPayloadBytes = 2L * 1024 * 1024 * 1024;
    public const int NonAnimationConsensusThreshold = 3;
    public const string SupportedSignatureAlgorithm = "synastry-manifest-v1";
    private readonly object gate = new();
    private readonly RelayDatabase database;
    private readonly ILogger<AnimationCatalogStore> logger;
    private readonly int maximumArtifacts;
    private readonly long maximumStoredPayloadBytes;

    public AnimationCatalogStore(RelayDatabase database, ILogger<AnimationCatalogStore> logger)
    {
        this.database = database;
        this.logger = logger;
        maximumArtifacts = ReadPositiveIntEnvironment(
            "EMOTELINK_CATALOG_MAX_ARTIFACTS", DefaultMaximumArtifacts, 10_000);
        maximumStoredPayloadBytes = ReadPositiveLongEnvironment(
            "EMOTELINK_CATALOG_MAX_PAYLOAD_BYTES", DefaultMaximumStoredPayloadBytes,
            128L * 1024 * 1024);
    }

    public int CountUnknownArtifacts(IReadOnlyCollection<AnimationArtifactLookupKey> artifacts)
    {
        var requested = artifacts.Select(artifact =>
                BuildArtifactKey(RequireSignatureAlgorithm(artifact.SignatureAlgorithm),
                    RequireSignature(artifact.Signature)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumBatchSize)
            .ToArray();
        if (requested.Length == 0) return 0;
        using var connection = database.OpenConnection();
        var existing = 0;
        foreach (var batch in requested.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var parameters = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                parameters[index] = "$signature" + index;
                command.Parameters.AddWithValue(parameters[index], batch[index]);
            }
            command.CommandText = $"SELECT COUNT(*) FROM animation_artifacts WHERE signature IN ({string.Join(',', parameters)});";
            existing += Convert.ToInt32(command.ExecuteScalar());
        }
        return requested.Length - existing;
    }

    public IReadOnlyList<AnimationArtifactCatalogEntry> Lookup(
        IReadOnlyCollection<AnimationArtifactLookupKey> artifacts)
    {
        if (artifacts.Count > MaximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(artifacts),
                $"Look up at most {MaximumBatchSize} artifacts per batch.");
        var requested = artifacts.Select(artifact =>
                BuildArtifactKey(RequireSignatureAlgorithm(artifact.SignatureAlgorithm),
                    RequireSignature(artifact.Signature)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumBatchSize)
            .ToArray();
        if (requested.Length == 0) return [];
        using var connection = database.OpenConnection();
        return LookupCore(connection, requested, false);
    }

    /// <summary>
    /// Records one reporter's latest observation for each exact manifest signature and updates
    /// consensus atomically. A positive animation observation is useful immediately; a negative
    /// result needs three independent reporters because a false negative would hide valid content.
    /// </summary>
    public IReadOnlyList<AnimationArtifactCatalogEntry> SubmitReports(
        string reporterId, IReadOnlyCollection<AnimationArtifactReportSubmission> reports)
    {
        if (string.IsNullOrWhiteSpace(reporterId))
            throw new ArgumentException("A stable reporter identifier is required.", nameof(reporterId));
        if (reports.Count is < 1 or > MaximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(reports), $"Submit 1-{MaximumBatchSize} reports per batch.");

        var normalized = reports
            .GroupBy(report => BuildArtifactKey(RequireSignatureAlgorithm(report.SignatureAlgorithm),
                RequireSignature(report.Signature)), StringComparer.OrdinalIgnoreCase)
            .Where(grouping => grouping.Key.Length > 0)
            .Select(grouping => NormalizeReport(grouping.Last(), grouping.Key))
            .ToArray();
        var aggregatePayloadBytes = normalized.Sum(report => report.Payload is null
            ? 0
            : Encoding.UTF8.GetByteCount(report.Payload.Json));
        if (aggregatePayloadBytes > MaximumAggregatePayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(reports),
                $"A report batch may contain at most {MaximumAggregatePayloadBytes:N0} payload bytes.");
        var now = DateTimeOffset.UtcNow.ToString("O");

        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            EnsureCatalogCapacity(connection, transaction, normalized);
            foreach (var report in normalized)
            {
                // Per-artifact derivation deduplicates a reporter without creating a stable
                // cross-mod inventory identifier in the database.
                var reporterHash = HashIdentity(reporterId + "\n" + report.Signature);
                UpsertArtifact(connection, transaction, report, now);
                UpsertName(connection, transaction, report.Signature, report.DisplayName, now);
                if (!UpsertReport(connection, transaction, report, reporterHash, now)) continue;
                UpdatePayload(connection, transaction, report, reporterHash, now);
                RecalculateConsensus(connection, transaction, report.Signature, now);
            }
            transaction.Commit();
            return LookupCore(connection, normalized.Select(report => report.Signature).ToArray(), false);
        }
    }

    public IReadOnlyList<AnimationArtifactCatalogEntry> Search(string term, int limit = 100)
    {
        var cleanTerm = Clean(term, 160);
        if (cleanTerm.Length == 0) return [];
        var cleanLimit = Math.Clamp(limit, 1, 500);
        var classification = ParseClassificationSearch(cleanTerm);
        var hasTextQuery = CatalogSearchSyntax.TryBuildTrigramQuery(cleanTerm, out var ftsQuery);
        if (!hasTextQuery && classification < 0) return [];
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
            command.CommandText = hasTextQuery ? """
                SELECT DISTINCT a.signature
                FROM animation_artifacts a
                LEFT JOIN animation_artifact_consensus c ON c.signature = a.signature
                LEFT JOIN animation_artifact_overrides o
                    ON o.signature = a.signature AND o.revoked_utc IS NULL
                WHERE a.signature IN (
                        SELECT artifact_key FROM animation_artifact_search
                        WHERE animation_artifact_search MATCH $ftsQuery)
                   OR ($classification >= 0 AND COALESCE(o.classification, c.classification, 0) = $classification)
                ORDER BY a.last_seen_utc DESC
                LIMIT $limit;
                """ : """
                SELECT a.signature
                FROM animation_artifacts a
                LEFT JOIN animation_artifact_consensus c ON c.signature = a.signature
                LEFT JOIN animation_artifact_overrides o
                    ON o.signature = a.signature AND o.revoked_utc IS NULL
                WHERE COALESCE(o.classification, c.classification, 0) = $classification
                ORDER BY a.last_seen_utc DESC
                LIMIT $limit;
                """;
        if (hasTextQuery) command.Parameters.AddWithValue("$ftsQuery", ftsQuery);
        command.Parameters.AddWithValue("$classification", classification);
        command.Parameters.AddWithValue("$limit", cleanLimit);
        var signatures = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) signatures.Add(reader.GetString(0));
        return LookupCore(connection, signatures, true);
    }

    public IReadOnlyList<AnimationArtifactCatalogEntry> GetAll(int limit = 1_000)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT signature FROM animation_artifacts ORDER BY last_seen_utc DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));
        var signatures = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) signatures.Add(reader.GetString(0));
        return LookupCore(connection, signatures, true);
    }

    public AdminAnimationArtifactPage GetAdminPage(
        AdminAnimationArtifactView view, int page = 1, int pageSize = 500, string? query = null)
    {
        var cleanPageSize = Math.Clamp(pageSize, 25, 500);
        var searchTerms = Clean(query ?? "", 160)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(12)
            .ToArray();
        var conflict =
            "COALESCE(c.animation_reports, 0) > 0 AND COALESCE(c.non_animation_reports, 0) > 0";
        var effective = "COALESCE(o.classification, c.classification, 0)";
        var predicate = view switch
        {
            AdminAnimationArtifactView.Animation => $"NOT ({conflict}) AND {effective} = 1",
            AdminAnimationArtifactView.AnimationOverrides =>
                $"NOT ({conflict}) AND {effective} = 1 AND o.signature IS NOT NULL",
            AdminAnimationArtifactView.Other => $"({conflict}) OR {effective} <> 1",
            AdminAnimationArtifactView.NonAnimation => $"NOT ({conflict}) AND {effective} = 2",
            AdminAnimationArtifactView.Unknown => $"NOT ({conflict}) AND {effective} = 0",
            AdminAnimationArtifactView.Conflict => conflict,
            AdminAnimationArtifactView.OtherOverrides =>
                $"(({conflict}) OR {effective} <> 1) AND o.signature IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        };
        var searchPredicates = searchTerms.Select((term, index) =>
        {
            return CatalogSearchSyntax.TryBuildTrigramQuery(term, out _)
                ? $"""
                    a.signature IN (
                        SELECT artifact_key FROM animation_artifact_search
                        WHERE animation_artifact_search MATCH $fts{index})
                    """
                : $"""
                    (EXISTS (
                        SELECT 1 FROM animation_artifact_names n
                        WHERE n.signature = a.signature
                          AND n.display_name LIKE $like{index} ESCAPE '\')
                     OR a.signature LIKE $like{index} ESCAPE '\'
                     OR COALESCE(o.reason_code, '') LIKE $like{index} ESCAPE '\'
                     OR COALESCE(o.note, '') LIKE $like{index} ESCAPE '\'
                     OR COALESCE(o.approved_payload_sha256, '') LIKE $like{index} ESCAPE '\'
                     OR EXISTS (
                        SELECT 1 FROM animation_artifact_payloads p
                        WHERE p.signature = a.signature
                          AND p.payload_sha256 LIKE $like{index} ESCAPE '\'))
                    """;
        }).ToArray();
        var searchPredicate = searchPredicates.Length == 0
            ? ""
            : " AND " + string.Join(" AND ", searchPredicates);

        void AddSearchParameters(SqliteCommand command)
        {
            for (var index = 0; index < searchTerms.Length; index++)
            {
                var term = searchTerms[index];
                if (CatalogSearchSyntax.TryBuildTrigramQuery(term, out var ftsQuery))
                    command.Parameters.AddWithValue($"$fts{index}", ftsQuery);
                else
                    command.Parameters.AddWithValue($"$like{index}", $"%{EscapeLike(term)}%");
            }
        }

        using var connection = database.OpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = $"""
            SELECT COUNT(*)
            FROM animation_artifacts a
            LEFT JOIN animation_artifact_consensus c ON c.signature = a.signature
            LEFT JOIN animation_artifact_overrides o
                ON o.signature = a.signature AND o.revoked_utc IS NULL
            WHERE {predicate}{searchPredicate};
            """;
        AddSearchParameters(count);
        var totalCount = Convert.ToInt32(count.ExecuteScalar());
        var totalPages = Math.Max(1, (totalCount + cleanPageSize - 1) / cleanPageSize);
        var cleanPage = Math.Clamp(page, 1, totalPages);
        var offset = (cleanPage - 1) * cleanPageSize;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT a.signature
            FROM animation_artifacts a
            LEFT JOIN animation_artifact_consensus c ON c.signature = a.signature
            LEFT JOIN animation_artifact_overrides o
                ON o.signature = a.signature AND o.revoked_utc IS NULL
            WHERE {predicate}{searchPredicate}
            ORDER BY a.last_seen_utc DESC, a.signature COLLATE NOCASE
            LIMIT $limit OFFSET $offset;
            """;
        AddSearchParameters(command);
        command.Parameters.AddWithValue("$limit", cleanPageSize);
        command.Parameters.AddWithValue("$offset", offset);
        var signatures = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) signatures.Add(reader.GetString(0));

        var entriesByKey = LookupCore(connection, signatures, true)
            .ToDictionary(entry => entry.ArtifactKey, StringComparer.OrdinalIgnoreCase);
        var entries = signatures.Where(entriesByKey.ContainsKey)
            .Select(signature => entriesByKey[signature])
            .ToList();
        return new AdminAnimationArtifactPage(cleanPage, cleanPageSize, totalCount, entries);
    }

    internal VerifiedUploadedAnimationArtifact? IndexUploadedPackage(string packagePath, string displayName)
    {
        var verified = UploadedAnimationPackageVerifier.Inspect(packagePath);
        if (verified is null) return null;
        var entry = SubmitReports("relay-package-verifier-v1",
        [
            new AnimationArtifactReportSubmission(
                verified.Signature,
                SupportedSignatureAlgorithm,
                displayName,
                AnimationArtifactClassification.Animation,
                verified.ManifestFileCount,
                verified.ManifestBytes,
                new PortableAnimationPayloadSubmission(1, "synastry-extractor-v1", verified.PayloadJson))
        ]).Single();
        // Exact package parsing establishes that this candidate is internally consistent,
        // but it does not grant moderation approval. The private admin tool must still pin
        // the exact payload hash before ordinary clients may hydrate it.
        logger.LogInformation(
            "Indexed relay-verified package candidate {ArtifactKey} with payload {PayloadSha256}; awaiting moderation",
            entry.ArtifactKey, verified.PayloadSha256);
        return verified;
    }

    public AnimationArtifactCatalogEntry SetAdminOverride(
        string artifactKey, AnimationArtifactClassification? classification,
        AnimationSharingPolicy sharingPolicy, string reasonCode, string note, string administratorId,
        string? approvedPayloadSha256)
    {
        var cleanSignature = RequireArtifactKey(artifactKey);
        if (classification == AnimationArtifactClassification.Unknown)
            classification = null;
        if (classification is not null && classification is not
            (AnimationArtifactClassification.Animation or AnimationArtifactClassification.NonAnimation))
            throw new ArgumentOutOfRangeException(nameof(classification));
        if (!Enum.IsDefined(sharingPolicy)) throw new ArgumentOutOfRangeException(nameof(sharingPolicy));
        if (string.IsNullOrWhiteSpace(administratorId))
            throw new ArgumentException("An administrator identifier is required.", nameof(administratorId));
        var approvedPayload = string.IsNullOrWhiteSpace(approvedPayloadSha256)
            ? null
            : RequireSha256(approvedPayloadSha256);
        if (approvedPayload is not null && classification != AnimationArtifactClassification.Animation)
            throw new ArgumentException("A payload can only be approved with an animation classification.",
                nameof(approvedPayloadSha256));
        var now = DateTimeOffset.UtcNow.ToString("O");
        var artifactAlgorithm = cleanSignature[..cleanSignature.IndexOf(':')];

        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var artifact = connection.CreateCommand())
            {
                artifact.Transaction = transaction;
                artifact.CommandText = """
                    INSERT INTO animation_artifacts
                        (signature, signature_algorithm, first_seen_utc, last_seen_utc)
                    VALUES ($signature, $algorithm, $now, $now)
                    ON CONFLICT(signature) DO UPDATE SET last_seen_utc = excluded.last_seen_utc;
                    """;
                artifact.Parameters.AddWithValue("$signature", cleanSignature);
                artifact.Parameters.AddWithValue("$algorithm", artifactAlgorithm);
                artifact.Parameters.AddWithValue("$now", now);
                artifact.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                if (approvedPayload is not null)
                {
                    using var payload = connection.CreateCommand();
                    payload.Transaction = transaction;
                    payload.CommandText = """
                        SELECT 1 FROM animation_artifact_payloads
                        WHERE signature = $signature COLLATE NOCASE
                          AND payload_sha256 = $payload COLLATE NOCASE;
                        """;
                    payload.Parameters.AddWithValue("$signature", cleanSignature);
                    payload.Parameters.AddWithValue("$payload", approvedPayload);
                    if (payload.ExecuteScalar() is null)
                        throw new ArgumentException(
                            "The approved payload hash is not an existing candidate for this artifact.",
                            nameof(approvedPayloadSha256));
                }
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO animation_artifact_overrides
                        (signature, classification, sharing_policy, reason_code, note,
                         created_by_hash, created_utc, updated_utc, revoked_utc,
                         approved_payload_sha256)
                    VALUES ($signature, $classification, $sharingPolicy, $reasonCode, $note,
                            $administrator, $now, $now, NULL, $approvedPayload)
                    ON CONFLICT(signature) DO UPDATE SET
                        classification = excluded.classification,
                        sharing_policy = excluded.sharing_policy,
                        reason_code = excluded.reason_code,
                        note = excluded.note,
                        created_by_hash = excluded.created_by_hash,
                        updated_utc = excluded.updated_utc,
                        revoked_utc = NULL,
                        approved_payload_sha256 = excluded.approved_payload_sha256;
                    """;
                command.Parameters.AddWithValue("$signature", cleanSignature);
                command.Parameters.AddWithValue("$classification",
                    classification is null ? DBNull.Value : (object)(int)classification.Value);
                command.Parameters.AddWithValue("$sharingPolicy", (int)sharingPolicy);
                command.Parameters.AddWithValue("$reasonCode", Clean(reasonCode, 40));
                command.Parameters.AddWithValue("$note", Clean(note, 500));
                command.Parameters.AddWithValue("$administrator", HashIdentity(administratorId));
                command.Parameters.AddWithValue("$approvedPayload",
                    approvedPayload is null ? DBNull.Value : approvedPayload);
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return LookupCore(connection, [cleanSignature], true).Single();
        }
    }

    public bool RevokeAdminOverride(string artifactKey)
    {
        var cleanSignature = RequireArtifactKey(artifactKey);
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE animation_artifact_overrides
                SET revoked_utc = $now, updated_utc = $now, approved_payload_sha256 = NULL
                WHERE signature = $signature COLLATE NOCASE AND revoked_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$signature", cleanSignature);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return command.ExecuteNonQuery() > 0;
        }
    }

    private static AnimationArtifactReportSubmission NormalizeReport(
        AnimationArtifactReportSubmission source, string artifactKey)
    {
        if (source.Classification is not
            (AnimationArtifactClassification.Animation or AnimationArtifactClassification.NonAnimation))
            throw new ArgumentOutOfRangeException(nameof(source.Classification),
                "Reports must identify an animation or non-animation artifact.");
        var algorithm = RequireSignatureAlgorithm(source.SignatureAlgorithm);
        if (source.ManifestFileCount < 0 || source.ManifestBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Manifest measurements cannot be negative.");
        var payload = source.Payload is null ? null : ValidatePayload(source.Payload);
        if (payload is not null && source.Classification != AnimationArtifactClassification.Animation)
            throw new ArgumentException("Only animation artifacts may include an extraction payload.");
        return source with
        {
            Signature = artifactKey,
            SignatureAlgorithm = algorithm,
            DisplayName = Clean(source.DisplayName, 160),
            Payload = payload
        };
    }

    private static PortableAnimationPayloadSubmission ValidatePayload(PortableAnimationPayloadSubmission payload)
    {
        if (payload.SchemaVersion != 1)
            throw new ArgumentOutOfRangeException(nameof(payload.SchemaVersion), "Only payload schema 1 is accepted.");
        var extractorVersion = Clean(payload.ExtractorVersion, 40);
        if (!extractorVersion.Equals("synastry-extractor-v1", StringComparison.Ordinal))
            throw new ArgumentException("Unsupported animation payload extractor version.");
        var byteCount = Encoding.UTF8.GetByteCount(payload.Json);
        if (byteCount is < 2 or > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload.Json),
                $"Payload must be 2-{MaximumPayloadBytes:N0} UTF-8 bytes.");
        using var document = JsonDocument.Parse(payload.Json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Extraction payload must be a JSON object.");
        ValidatePortablePayloadV1(document.RootElement);
        return payload with { ExtractorVersion = extractorVersion, Json = document.RootElement.GetRawText() };
    }

    private static void ValidatePortablePayloadV1(JsonElement root)
    {
        RequireExactProperties(root,
            "schemaVersion", "extractorVersion", "papGamePaths", "optionGroups",
            "optionPoses", "poses", "multiSelectGroups");
        if (!root.GetProperty("schemaVersion").TryGetInt32(out var schemaVersion) || schemaVersion != 1)
            throw new ArgumentException("Payload schemaVersion must be 1.");
        if (root.GetProperty("extractorVersion").ValueKind != JsonValueKind.String ||
            !string.Equals(root.GetProperty("extractorVersion").GetString(),
                "synastry-extractor-v1", StringComparison.Ordinal))
            throw new ArgumentException("Payload extractorVersion is invalid.");

        var papPaths = RequireArray(root.GetProperty("papGamePaths"), "papGamePaths", 1, 50_000);
        foreach (var pathElement in papPaths.EnumerateArray())
        {
            var path = RequireString(pathElement, "papGamePaths entry", 1, 512);
            if (!path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) ||
                path.Contains('\\') || path.StartsWith('/') || path.Contains(':') ||
                path.Split('/').Any(segment => segment is "." or ".."))
                throw new ArgumentException("papGamePaths must contain normalized relative .pap game paths.");
        }

        var optionGroups = RequireArray(root.GetProperty("optionGroups"), "optionGroups", 0, 2_000);
        var optionCount = 0;
        foreach (var group in optionGroups.EnumerateArray())
        {
            RequireObject(group, "optionGroups entry");
            RequireExactProperties(group, "name", "options", "isMultiSelect");
            RequireString(group.GetProperty("name"), "option group name", 1, 160);
            var options = RequireArray(group.GetProperty("options"), "option group options", 0, 10_000);
            optionCount += options.GetArrayLength();
            if (optionCount > 20_000) throw new ArgumentException("Payload contains too many option names.");
            foreach (var option in options.EnumerateArray()) RequireString(option, "option name", 1, 160);
            if (group.GetProperty("isMultiSelect").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("isMultiSelect must be a boolean.");
        }

        var optionPoses = RequireArray(root.GetProperty("optionPoses"), "optionPoses", 0, 10_000);
        foreach (var pose in optionPoses.EnumerateArray())
        {
            RequireObject(pose, "optionPoses entry");
            RequireExactProperties(pose, "group", "option", "kind", "index");
            RequireString(pose.GetProperty("group"), "option pose group", 1, 160);
            RequireString(pose.GetProperty("option"), "option pose option", 1, 160);
            RequirePoseNumber(pose.GetProperty("kind"), "option pose kind", 3);
            RequirePoseNumber(pose.GetProperty("index"), "option pose index", 6);
        }

        var poses = RequireArray(root.GetProperty("poses"), "poses", 0, 1_000);
        foreach (var pose in poses.EnumerateArray())
        {
            RequireObject(pose, "poses entry");
            RequireExactProperties(pose, "kind", "index");
            RequirePoseNumber(pose.GetProperty("kind"), "pose kind", 3);
            RequirePoseNumber(pose.GetProperty("index"), "pose index", 6);
        }

        var multiGroups = root.GetProperty("multiSelectGroups");
        RequireObject(multiGroups, "multiSelectGroups");
        var multiCount = 0;
        var seenMultiGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in multiGroups.EnumerateObject())
        {
            if (++multiCount > 2_000) throw new ArgumentException("Payload contains too many multi-select groups.");
            if (!seenMultiGroups.Add(property.Name))
                throw new ArgumentException("Payload contains a duplicate multi-select group.");
            ValidateText(property.Name, "multi-select group name", 1, 160);
            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("Multi-select group values must be booleans.");
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        RequireObject(element, "payload object");
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new ArgumentException($"Payload contains unknown field '{property.Name}'.");
            if (!found.Add(property.Name))
                throw new ArgumentException($"Payload contains duplicate field '{property.Name}'.");
        }
        if (found.Count != allowed.Count)
            throw new ArgumentException("Payload is missing one or more required fields.");
    }

    private static void RequireObject(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"{label} must be an object.");
    }

    private static JsonElement RequireArray(JsonElement element, string label, int minimum, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < minimum ||
            element.GetArrayLength() > maximum)
            throw new ArgumentException($"{label} must contain {minimum:N0}-{maximum:N0} entries.");
        return element;
    }

    private static string RequireString(JsonElement element, string label, int minimum, int maximum)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{label} must be text.");
        var value = element.GetString() ?? "";
        ValidateText(value, label, minimum, maximum);
        return value;
    }

    private static void ValidateText(string value, string label, int minimum, int maximum)
    {
        if (value.Length < minimum || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException($"{label} must be {minimum:N0}-{maximum:N0} non-control characters.");
    }

    private static void RequirePoseNumber(JsonElement element, string label, int maximum)
    {
        if (!element.TryGetInt32(out var value) || value < 0 || value > maximum)
            throw new ArgumentException($"{label} must be an integer from 0 through {maximum}.");
    }

    private void EnsureCatalogCapacity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<AnimationArtifactReportSubmission> reports)
    {
        var additionalArtifacts = 0;
        long additionalPayloadBytes = 0;
        foreach (var report in reports)
        {
            using (var artifact = connection.CreateCommand())
            {
                artifact.Transaction = transaction;
                artifact.CommandText =
                    "SELECT 1 FROM animation_artifacts WHERE signature = $signature COLLATE NOCASE;";
                artifact.Parameters.AddWithValue("$signature", report.Signature);
                if (artifact.ExecuteScalar() is null) additionalArtifacts++;
            }
            if (report.Payload is not { } payload) continue;
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.Json)));
            using var candidate = connection.CreateCommand();
            candidate.Transaction = transaction;
            candidate.CommandText = """
                SELECT 1 FROM animation_artifact_payloads
                WHERE signature = $signature COLLATE NOCASE
                  AND payload_sha256 = $payloadHash COLLATE NOCASE;
                """;
            candidate.Parameters.AddWithValue("$signature", report.Signature);
            candidate.Parameters.AddWithValue("$payloadHash", payloadHash);
            if (candidate.ExecuteScalar() is null)
                additionalPayloadBytes += Encoding.UTF8.GetByteCount(payload.Json);
        }

        using var stats = connection.CreateCommand();
        stats.Transaction = transaction;
        stats.CommandText = """
            SELECT artifact_count, payload_bytes
            FROM animation_catalog_storage_stats WHERE singleton = 1;
            """;
        using var reader = stats.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Animation catalog storage counters are unavailable.");
        var artifactCount = reader.GetInt64(0);
        var payloadBytes = reader.GetInt64(1);
        if (artifactCount + additionalArtifacts > maximumArtifacts ||
            payloadBytes + additionalPayloadBytes > maximumStoredPayloadBytes)
        {
            logger.LogWarning(
                "Rejected animation catalog batch at storage safety budget: {Artifacts}/{MaximumArtifacts} " +
                "artifacts, {PayloadBytes}/{MaximumPayloadBytes} payload bytes.",
                artifactCount, maximumArtifacts, payloadBytes, maximumStoredPayloadBytes);
            throw new InvalidOperationException(
                "The animation catalog is at its storage safety budget; new artifacts are temporarily rejected.");
        }
    }

    private void UpsertArtifact(SqliteConnection connection, SqliteTransaction transaction,
        AnimationArtifactReportSubmission report, string now)
    {
        using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = """
                SELECT CASE
                    WHEN EXISTS (SELECT 1 FROM animation_artifacts
                                 WHERE signature = $signature COLLATE NOCASE) THEN 1
                    WHEN (SELECT COUNT(*) FROM animation_artifacts) < $maximum THEN 1
                    ELSE 0 END;
                """;
            capacity.Parameters.AddWithValue("$signature", report.Signature);
            capacity.Parameters.AddWithValue("$maximum", maximumArtifacts);
            if (Convert.ToInt32(capacity.ExecuteScalar()) == 0)
                throw new InvalidOperationException("The animation artifact catalog has reached its safety limit.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_artifacts
                (signature, signature_algorithm, manifest_file_count, manifest_bytes,
                 first_seen_utc, last_seen_utc)
            VALUES ($signature, $algorithm, $fileCount, $bytes, $now, $now)
            ON CONFLICT(signature) DO UPDATE SET
                signature_algorithm = excluded.signature_algorithm,
                manifest_file_count = excluded.manifest_file_count,
                manifest_bytes = excluded.manifest_bytes,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$signature", report.Signature);
        command.Parameters.AddWithValue("$algorithm", report.SignatureAlgorithm);
        command.Parameters.AddWithValue("$fileCount", report.ManifestFileCount);
        command.Parameters.AddWithValue("$bytes", report.ManifestBytes);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void UpsertName(SqliteConnection connection, SqliteTransaction transaction,
        string signature, string displayName, string now)
    {
        if (displayName.Length == 0) return;
        using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = """
                SELECT CASE
                    WHEN EXISTS (
                        SELECT 1 FROM animation_artifact_names
                        WHERE signature = $signature COLLATE NOCASE
                          AND display_name = $displayName COLLATE NOCASE) THEN 1
                    WHEN (SELECT COUNT(*) FROM animation_artifact_names
                          WHERE signature = $signature COLLATE NOCASE) < $maximum THEN 1
                    ELSE 0 END;
                """;
            capacity.Parameters.AddWithValue("$signature", signature);
            capacity.Parameters.AddWithValue("$displayName", displayName);
            capacity.Parameters.AddWithValue("$maximum", MaximumNamesPerArtifact);
            if (Convert.ToInt32(capacity.ExecuteScalar()) == 0) return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_artifact_names(signature, display_name, first_seen_utc, last_seen_utc)
            VALUES ($signature, $displayName, $now, $now)
            ON CONFLICT(signature, display_name) DO UPDATE SET last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$signature", signature);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static bool UpsertReport(SqliteConnection connection, SqliteTransaction transaction,
        AnimationArtifactReportSubmission report, string reporterHash, string now)
    {
        using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = """
                SELECT CASE
                    WHEN EXISTS (
                        SELECT 1 FROM animation_artifact_reports
                        WHERE signature = $signature COLLATE NOCASE
                          AND reporter_hash = $reporterHash COLLATE NOCASE) THEN 1
                    WHEN (SELECT COUNT(*) FROM animation_artifact_reports
                          WHERE signature = $signature COLLATE NOCASE) < $maximum THEN 1
                    ELSE 0 END;
                """;
            capacity.Parameters.AddWithValue("$signature", report.Signature);
            capacity.Parameters.AddWithValue("$reporterHash", reporterHash);
            capacity.Parameters.AddWithValue("$maximum", MaximumReportsPerArtifact);
            if (Convert.ToInt32(capacity.ExecuteScalar()) == 0) return false;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_artifact_reports(signature, reporter_hash, classification, observed_utc)
            VALUES ($signature, $reporterHash, $classification, $now)
            ON CONFLICT(signature, reporter_hash) DO UPDATE SET
                classification = excluded.classification, observed_utc = excluded.observed_utc;
            """;
        command.Parameters.AddWithValue("$signature", report.Signature);
        command.Parameters.AddWithValue("$reporterHash", reporterHash);
        command.Parameters.AddWithValue("$classification", (int)report.Classification);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return true;
    }

    private static void UpdatePayload(SqliteConnection connection, SqliteTransaction transaction,
        AnimationArtifactReportSubmission report, string reporterHash, string now)
    {
        string? payloadHash = null;
        if (report.Payload is { } payload)
        {
            payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.Json)));
            using var candidate = connection.CreateCommand();
            candidate.Transaction = transaction;
            candidate.CommandText = """
                INSERT INTO animation_artifact_payloads
                    (signature, payload_schema_version, extractor_version, payload_sha256,
                     payload_json, verification_reports, updated_utc)
                VALUES ($signature, $schemaVersion, $extractorVersion, $payloadHash, $json, 1, $now)
                ON CONFLICT(signature, payload_sha256) DO UPDATE SET
                    extractor_version = excluded.extractor_version,
                    payload_schema_version = excluded.payload_schema_version,
                    payload_json = excluded.payload_json,
                    updated_utc = excluded.updated_utc;
                """;
            candidate.Parameters.AddWithValue("$signature", report.Signature);
            candidate.Parameters.AddWithValue("$schemaVersion", payload.SchemaVersion);
            candidate.Parameters.AddWithValue("$extractorVersion", payload.ExtractorVersion);
            candidate.Parameters.AddWithValue("$payloadHash", payloadHash);
            candidate.Parameters.AddWithValue("$json", payload.Json);
            candidate.Parameters.AddWithValue("$now", now);
            candidate.ExecuteNonQuery();
        }

        using (var removeOld = connection.CreateCommand())
        {
            removeOld.Transaction = transaction;
            removeOld.CommandText = """
                DELETE FROM animation_artifact_payload_reports
                WHERE signature = $signature COLLATE NOCASE AND reporter_hash = $reporterHash COLLATE NOCASE;
                """;
            removeOld.Parameters.AddWithValue("$signature", report.Signature);
            removeOld.Parameters.AddWithValue("$reporterHash", reporterHash);
            removeOld.ExecuteNonQuery();
        }
        if (payloadHash is not null)
        {
            using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText = """
                INSERT INTO animation_artifact_payload_reports
                    (signature, reporter_hash, payload_sha256, observed_utc)
                VALUES ($signature, $reporterHash, $payloadHash, $now);
                """;
            verify.Parameters.AddWithValue("$signature", report.Signature);
            verify.Parameters.AddWithValue("$reporterHash", reporterHash);
            verify.Parameters.AddWithValue("$payloadHash", payloadHash);
            verify.Parameters.AddWithValue("$now", now);
            verify.ExecuteNonQuery();
        }

        // A payload approval is valid only while at least one matching evidence report
        // exists. Clear an orphaned pin before the candidate is removed below.
        using (var clearOrphanedApproval = connection.CreateCommand())
        {
            clearOrphanedApproval.Transaction = transaction;
            clearOrphanedApproval.CommandText = """
                UPDATE animation_artifact_overrides
                SET approved_payload_sha256 = NULL, updated_utc = $now
                WHERE signature = $signature COLLATE NOCASE
                  AND revoked_utc IS NULL
                  AND approved_payload_sha256 IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM animation_artifact_payload_reports r
                      WHERE r.signature = animation_artifact_overrides.signature
                        AND r.payload_sha256 = animation_artifact_overrides.approved_payload_sha256);
                """;
            clearOrphanedApproval.Parameters.AddWithValue("$signature", report.Signature);
            clearOrphanedApproval.Parameters.AddWithValue("$now", now);
            clearOrphanedApproval.ExecuteNonQuery();
        }

        using var recalculate = connection.CreateCommand();
        recalculate.Transaction = transaction;
        recalculate.CommandText = """
            UPDATE animation_artifact_payloads
            SET verification_reports = (
                    SELECT COUNT(*) FROM animation_artifact_payload_reports r
                    WHERE r.signature = animation_artifact_payloads.signature
                      AND r.payload_sha256 = animation_artifact_payloads.payload_sha256),
                updated_utc = $now
            WHERE signature = $signature COLLATE NOCASE
              AND EXISTS (
                  SELECT 1 FROM animation_artifact_payload_reports r
                  WHERE r.signature = animation_artifact_payloads.signature
                    AND r.payload_sha256 = animation_artifact_payloads.payload_sha256);
            DELETE FROM animation_artifact_payloads
            WHERE signature = $signature COLLATE NOCASE
              AND NOT EXISTS (
                  SELECT 1 FROM animation_artifact_payload_reports r
                  WHERE r.signature = animation_artifact_payloads.signature
                    AND r.payload_sha256 = animation_artifact_payloads.payload_sha256);
            """;
        recalculate.Parameters.AddWithValue("$signature", report.Signature);
        recalculate.Parameters.AddWithValue("$now", now);
        recalculate.ExecuteNonQuery();

        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM animation_artifact_payloads
            WHERE rowid IN (
                SELECT p.rowid
                FROM animation_artifact_payloads p
                LEFT JOIN animation_artifact_overrides o
                  ON o.signature = p.signature
                 AND o.revoked_utc IS NULL
                WHERE p.signature = $signature COLLATE NOCASE
                ORDER BY CASE WHEN p.payload_sha256 = o.approved_payload_sha256 THEN 0 ELSE 1 END,
                         p.verification_reports DESC, p.updated_utc DESC
                LIMIT -1 OFFSET $maximum);
            """;
        prune.Parameters.AddWithValue("$signature", report.Signature);
        prune.Parameters.AddWithValue("$maximum", MaximumPayloadCandidatesPerArtifact);
        prune.ExecuteNonQuery();
    }

    private static void RecalculateConsensus(SqliteConnection connection, SqliteTransaction transaction,
        string signature, string now)
    {
        var animationReports = 0;
        var nonAnimationReports = 0;
        using (var counts = connection.CreateCommand())
        {
            counts.Transaction = transaction;
            counts.CommandText = """
                SELECT classification, COUNT(*) FROM animation_artifact_reports
                WHERE signature = $signature COLLATE NOCASE GROUP BY classification;
                """;
            counts.Parameters.AddWithValue("$signature", signature);
            using var reader = counts.ExecuteReader();
            while (reader.Read())
            {
                if ((AnimationArtifactClassification)reader.GetInt32(0) == AnimationArtifactClassification.Animation)
                    animationReports = reader.GetInt32(1);
                else
                    nonAnimationReports = reader.GetInt32(1);
            }
        }

        var classification = animationReports > 0 && nonAnimationReports == 0
            ? AnimationArtifactClassification.Animation
            : nonAnimationReports >= NonAnimationConsensusThreshold && animationReports == 0
                ? AnimationArtifactClassification.NonAnimation
                : AnimationArtifactClassification.Unknown;
        var total = animationReports + nonAnimationReports;
        var confidence = total == 0 ? 0d : (double)Math.Max(animationReports, nonAnimationReports) / total;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_artifact_consensus
                (signature, classification, confidence, animation_reports, non_animation_reports, calculated_utc)
            VALUES ($signature, $classification, $confidence, $animationReports, $nonAnimationReports, $now)
            ON CONFLICT(signature) DO UPDATE SET
                classification = excluded.classification,
                confidence = excluded.confidence,
                animation_reports = excluded.animation_reports,
                non_animation_reports = excluded.non_animation_reports,
                calculated_utc = excluded.calculated_utc;
            """;
        command.Parameters.AddWithValue("$signature", signature);
        command.Parameters.AddWithValue("$classification", (int)classification);
        command.Parameters.AddWithValue("$confidence", confidence);
        command.Parameters.AddWithValue("$animationReports", animationReports);
        command.Parameters.AddWithValue("$nonAnimationReports", nonAnimationReports);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<AnimationArtifactCatalogEntry> LookupCore(
        SqliteConnection connection, IReadOnlyCollection<string> signatures, bool includeAdminMetadata)
    {
        if (signatures.Count == 0) return [];
        var result = new List<AnimationArtifactCatalogEntry>();
        foreach (var batch in signatures.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var parameters = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                parameters[index] = "$signature" + index;
                command.Parameters.AddWithValue(parameters[index], batch[index]);
            }
            command.CommandText = $"""
                SELECT a.signature, a.signature_algorithm, a.manifest_file_count, a.manifest_bytes,
                       a.first_seen_utc, a.last_seen_utc,
                       COALESCE(c.classification, 0), COALESCE(c.confidence, 0),
                       COALESCE(c.animation_reports, 0), COALESCE(c.non_animation_reports, 0),
                       o.classification, COALESCE(o.sharing_policy, 0), COALESCE(o.reason_code, ''),
                       COALESCE(o.note, ''), COALESCE(o.approved_payload_sha256, ''),
                       p.payload_schema_version, p.extractor_version, p.payload_sha256,
                       p.payload_json, p.verification_reports, p.updated_utc
                FROM animation_artifacts a
                LEFT JOIN animation_artifact_consensus c ON c.signature = a.signature
                LEFT JOIN animation_artifact_overrides o
                    ON o.signature = a.signature AND o.revoked_utc IS NULL
                LEFT JOIN animation_artifact_payloads p
                    ON p.signature = o.signature
                   AND p.payload_sha256 = o.approved_payload_sha256
                WHERE a.signature IN ({string.Join(',', parameters)});
                """;
            var builders = new Dictionary<string, CatalogEntryBuilder>(StringComparer.OrdinalIgnoreCase);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var consensus = (AnimationArtifactClassification)reader.GetInt32(6);
                    var overrideClassification = reader.IsDBNull(10)
                        ? (AnimationArtifactClassification?)null
                        : (AnimationArtifactClassification)reader.GetInt32(10);
                    var effective = overrideClassification ?? consensus;
                    if (!includeAdminMetadata && overrideClassification is null &&
                        effective == AnimationArtifactClassification.NonAnimation)
                        effective = AnimationArtifactClassification.Unknown;
                    var approvedPayloadHash = reader.GetString(14);
                    var payloadModeratorVerified = overrideClassification == AnimationArtifactClassification.Animation &&
                        approvedPayloadHash.Length == 64 && !reader.IsDBNull(15) &&
                        approvedPayloadHash.Equals(reader.GetString(17), StringComparison.OrdinalIgnoreCase);
                    var moderatorVerified = overrideClassification == AnimationArtifactClassification.NonAnimation ||
                        payloadModeratorVerified;
                    PortableAnimationPayloadDto? payload = null;
                    if (payloadModeratorVerified)
                        payload = new PortableAnimationPayloadDto(reader.GetInt32(15), reader.GetString(16),
                            reader.GetString(17), reader.GetString(18), reader.GetInt32(19));
                    var signature = reader.GetString(0);
                    builders[signature] = new CatalogEntryBuilder(
                        signature, reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
                        DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)),
                        consensus, effective, reader.GetDouble(7), reader.GetInt32(8), reader.GetInt32(9),
                        (AnimationSharingPolicy)reader.GetInt32(11),
                        includeAdminMetadata ? reader.GetString(12) : "",
                        includeAdminMetadata ? reader.GetString(13) : "",
                        includeAdminMetadata ? approvedPayloadHash : "",
                        payload, moderatorVerified, payloadModeratorVerified);
                }
            }

            if (includeAdminMetadata && builders.Count > 0)
            {
                using var names = connection.CreateCommand();
                for (var index = 0; index < batch.Length; index++)
                    names.Parameters.AddWithValue(parameters[index], batch[index]);
                names.CommandText = $"""
                    SELECT signature, display_name FROM animation_artifact_names
                    WHERE signature IN ({string.Join(',', parameters)}) ORDER BY display_name COLLATE NOCASE;
                    """;
                using var reader = names.ExecuteReader();
                while (reader.Read())
                    if (builders.TryGetValue(reader.GetString(0), out var builder))
                        builder.Names.Add(reader.GetString(1));

                using var payloads = connection.CreateCommand();
                for (var index = 0; index < batch.Length; index++)
                    payloads.Parameters.AddWithValue(parameters[index], batch[index]);
                payloads.CommandText = $"""
                    SELECT signature, payload_sha256, payload_schema_version, extractor_version,
                           verification_reports, length(CAST(payload_json AS BLOB)), updated_utc
                    FROM animation_artifact_payloads
                    WHERE signature IN ({string.Join(',', parameters)})
                    ORDER BY signature, verification_reports DESC, updated_utc DESC;
                    """;
                using var payloadReader = payloads.ExecuteReader();
                while (payloadReader.Read())
                {
                    if (!builders.TryGetValue(payloadReader.GetString(0), out var builder) ||
                        builder.PayloadCandidates.Count >= MaximumPayloadCandidatesPerArtifact) continue;
                    var hash = payloadReader.GetString(1);
                    builder.PayloadCandidates.Add(new AnimationPayloadCandidateDto(
                        hash,
                        payloadReader.GetInt32(2),
                        payloadReader.GetString(3),
                        payloadReader.GetInt32(4),
                        payloadReader.GetInt64(5),
                        DateTimeOffset.Parse(payloadReader.GetString(6)),
                        hash.Equals(builder.ApprovedPayloadSha256, StringComparison.OrdinalIgnoreCase)));
                }
            }
            result.AddRange(builders.Values.Select(builder => builder.Build()));
        }
        return result;
    }

    private static int ParseClassificationSearch(string term) => term.Trim().ToLowerInvariant() switch
    {
        "animation" or "animations" => (int)AnimationArtifactClassification.Animation,
        "non-animation" or "non animation" or "nonanimation" =>
            (int)AnimationArtifactClassification.NonAnimation,
        "unknown" => (int)AnimationArtifactClassification.Unknown,
        _ => -1
    };

    private static string RequireSignature(string value)
    {
        var signature = value.Trim();
        if (signature.Length != 64 || signature.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException(
                $"{SupportedSignatureAlgorithm} signatures must contain exactly 64 hexadecimal characters.");
        return signature.ToUpperInvariant();
    }

    private static string RequireSignatureAlgorithm(string value)
    {
        var algorithm = value.Trim().ToLowerInvariant();
        return algorithm.Equals(SupportedSignatureAlgorithm, StringComparison.Ordinal)
            ? algorithm
            : throw new ArgumentException(
                $"Only the {SupportedSignatureAlgorithm} signature algorithm is accepted.");
    }

    private static string BuildArtifactKey(string signatureAlgorithm, string signature) =>
        signatureAlgorithm + ":" + signature;

    private static string RequireArtifactKey(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new ArgumentException("Artifact key must contain a versioned algorithm and signature.");
        var algorithm = RequireSignatureAlgorithm(value[..separator]);
        var signature = RequireSignature(value[(separator + 1)..]);
        return BuildArtifactKey(algorithm, signature);
    }

    private static string RequireSha256(string value) => RequireSignature(value);

    private static string SourceSignature(string artifactKey, string signatureAlgorithm) =>
        artifactKey.StartsWith(signatureAlgorithm + ":", StringComparison.OrdinalIgnoreCase)
            ? artifactKey[(signatureAlgorithm.Length + 1)..]
            : artifactKey;

    private static string Clean(string value, int maximumLength)
    {
        var clean = value.Trim();
        return clean[..Math.Min(maximumLength, clean.Length)];
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string HashIdentity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static int ReadPositiveIntEnvironment(string name, int fallback, int minimum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value >= minimum ? value : fallback;
    }

    private static long ReadPositiveLongEnvironment(string name, long fallback, long minimum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, out var value) && value >= minimum ? value : fallback;
    }

    private sealed class CatalogEntryBuilder(
        string signature, string signatureAlgorithm, int manifestFileCount, long manifestBytes,
        DateTimeOffset firstSeenUtc, DateTimeOffset lastSeenUtc,
        AnimationArtifactClassification consensusClassification,
        AnimationArtifactClassification effectiveClassification, double confidence,
        int animationReports, int nonAnimationReports, AnimationSharingPolicy sharingPolicy,
        string overrideReasonCode, string overrideNote, string approvedPayloadSha256,
        PortableAnimationPayloadDto? payload, bool isModeratorVerified,
        bool isPayloadModeratorVerified)
    {
        public List<string> Names { get; } = [];
        public List<AnimationPayloadCandidateDto> PayloadCandidates { get; } = [];
        public string ApprovedPayloadSha256 { get; } = approvedPayloadSha256;

        public AnimationArtifactCatalogEntry Build() => new(signature, SourceSignature(signature, signatureAlgorithm),
            signatureAlgorithm, Names,
            manifestFileCount, manifestBytes, firstSeenUtc, lastSeenUtc, consensusClassification,
            effectiveClassification, confidence, animationReports, nonAnimationReports, sharingPolicy,
            overrideReasonCode, overrideNote, payload, isModeratorVerified,
            isPayloadModeratorVerified, ApprovedPayloadSha256, PayloadCandidates);
    }
}

public enum AnimationArtifactClassification
{
    Unknown = 0,
    Animation = 1,
    NonAnimation = 2
}

public enum AdminAnimationArtifactView
{
    Animation,
    AnimationOverrides,
    Other,
    NonAnimation,
    Unknown,
    Conflict,
    OtherOverrides
}

public enum AnimationSharingPolicy
{
    Default = 0,
    Allowed = 1,
    // A catalog moderation marker only. Relay transfer enforcement is handled by
    // transfer_sharing_bans, whose keys match the transfer protocol's identifiers.
    CatalogOnlyBlocked = 2
}

public sealed record AnimationArtifactLookupKey(string SignatureAlgorithm, string Signature);

public sealed record PortableAnimationPayloadSubmission(int SchemaVersion, string ExtractorVersion, string Json);

public sealed record AnimationArtifactReportSubmission(
    string Signature,
    string SignatureAlgorithm,
    string DisplayName,
    AnimationArtifactClassification Classification,
    int ManifestFileCount,
    long ManifestBytes,
    PortableAnimationPayloadSubmission? Payload = null);

public sealed record PortableAnimationPayloadDto(
    int SchemaVersion,
    string ExtractorVersion,
    string Sha256,
    string Json,
    int VerificationReports);

public sealed record AnimationPayloadCandidateDto(
    string Sha256,
    int SchemaVersion,
    string ExtractorVersion,
    int VerificationReports,
    long JsonBytes,
    DateTimeOffset UpdatedUtc,
    bool IsApproved);

public sealed record AnimationArtifactCatalogEntry(
    string ArtifactKey,
    string Signature,
    string SignatureAlgorithm,
    IReadOnlyList<string> Names,
    int ManifestFileCount,
    long ManifestBytes,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    AnimationArtifactClassification ConsensusClassification,
    AnimationArtifactClassification EffectiveClassification,
    double Confidence,
    int AnimationReports,
    int NonAnimationReports,
    AnimationSharingPolicy SharingPolicy,
    string OverrideReasonCode,
    string OverrideNote,
    PortableAnimationPayloadDto? Payload,
    bool IsModeratorVerified,
    bool IsPayloadModeratorVerified,
    string ApprovedPayloadSha256,
    IReadOnlyList<AnimationPayloadCandidateDto> PayloadCandidates);

public sealed record AdminAnimationArtifactPage(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AnimationArtifactCatalogEntry> Items);

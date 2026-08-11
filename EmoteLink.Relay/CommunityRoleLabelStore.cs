using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EmoteLink.Relay;

public sealed class CommunityRoleLabelStore
{
    public const int InitialAcceptanceThreshold = 1;
    public const int CorrectionAcceptanceThreshold = 5;
    private const int MaximumRecords = 100_000;
    private const int MaximumVotesPerRecord = 1_000;
    private const string LegacyImportKey = "community-role-labels-json-v1";
    private readonly object gate = new();
    private readonly RelayDatabase database;
    private readonly ILogger<CommunityRoleLabelStore> logger;
    private readonly bool compatibilityMirrorEnabled;

    public CommunityRoleLabelStore(RelayDatabase database, ILogger<CommunityRoleLabelStore> logger)
    {
        this.database = database;
        this.logger = logger;
        compatibilityMirrorEnabled = string.Equals(
            Environment.GetEnvironmentVariable("EMOTELINK_WRITE_LEGACY_LABEL_JSON"),
            "true", StringComparison.OrdinalIgnoreCase);
        ImportLegacyJsonOnce();
    }

    public IReadOnlyList<CommunityRoleLabelDto> Get(IReadOnlyCollection<string> fingerprints)
    {
        var requested = fingerprints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0) return [];

        lock (gate)
        {
            using var connection = database.OpenConnection();
            var result = new List<CommunityRoleLabelDto>();
            // Stay below SQLite's parameter ceiling even on older native builds.
            foreach (var batch in requested.Chunk(400))
            {
                using var command = connection.CreateCommand();
                var parameterNames = new string[batch.Length];
                for (var index = 0; index < batch.Length; index++)
                {
                    parameterNames[index] = "$fingerprint" + index;
                    command.Parameters.AddWithValue(parameterNames[index], batch[index]);
                }
                command.CommandText = $"""
                    SELECT fingerprint, option_group, option_name, accepted_label
                    FROM community_role_labels
                    WHERE accepted_label <> '' AND fingerprint IN ({string.Join(',', parameterNames)});
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(new CommunityRoleLabelDto(reader.GetString(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3)));
            }
            return result;
        }
    }

    public IReadOnlyList<AdminRoleLabelDto> GetAll()
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            return QueryAdminRecords(connection, null, MaximumRecords);
        }
    }

    public IReadOnlyList<AdminRoleLabelDto> Search(string term, int limit = 100)
    {
        var cleanTerm = term.Trim()[..Math.Min(160, term.Trim().Length)];
        if (!CatalogSearchSyntax.TryBuildTrigramQuery(cleanTerm, out var ftsQuery)) return [];
        lock (gate)
        {
            using var connection = database.OpenConnection();
            return QueryAdminRecords(connection, ftsQuery, Math.Clamp(limit, 1, 500));
        }
    }

    public AdminRoleLabelDto? ApproveLeadingVote(string key)
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var winner = GetLeadingVote(connection, transaction, key);
            if (winner is null) return null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE community_role_labels
                    SET accepted_label = $label, updated_utc = $now
                    WHERE record_key = $key COLLATE NOCASE;
                    DELETE FROM community_role_label_votes WHERE record_key = $key COLLATE NOCASE;
                    """;
                command.Parameters.AddWithValue("$label", winner.Value.Label);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$key", key);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            WriteCompatibilitySnapshot();
            return GetAdminRecord(connection, key);
        }
    }

    public AdminRoleLabelDto? SetAcceptedLabel(string key, string label)
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE community_role_labels
                SET accepted_label = $label, updated_utc = $now
                WHERE record_key = $key COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$label", label.Trim());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$key", key);
            if (command.ExecuteNonQuery() == 0) return null;
            command.CommandText =
                "DELETE FROM community_role_label_votes WHERE record_key = $key COLLATE NOCASE;";
            command.ExecuteNonQuery();
            transaction.Commit();
            WriteCompatibilitySnapshot();
            return GetAdminRecord(connection, key);
        }
    }

    public bool ClearVotes(string key)
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            if (!RecordExists(connection, key)) return false;
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM community_role_label_votes WHERE record_key = $key COLLATE NOCASE;";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
            WriteCompatibilitySnapshot();
            return true;
        }
    }

    public bool Delete(string key)
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM community_role_labels WHERE record_key = $key COLLATE NOCASE;";
            command.Parameters.AddWithValue("$key", key);
            if (command.ExecuteNonQuery() == 0) return false;
            WriteCompatibilitySnapshot();
            return true;
        }
    }

    public bool RegisterMetadata(
        string fingerprint, string group, string option, string modName, string animationName)
    {
        var key = MakeKey(fingerprint, group, option);
        var cleanModName = Clean(modName, 160);
        var cleanAnimationName = Clean(animationName, 120);
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE community_role_labels
                SET mod_name = CASE WHEN $modName = '' THEN mod_name ELSE $modName END,
                    animation_name = CASE WHEN $animationName = '' THEN animation_name ELSE $animationName END,
                    updated_utc = CASE WHEN $modName = '' AND $animationName = '' THEN updated_utc ELSE $now END
                WHERE record_key = $key COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$modName", cleanModName);
            command.Parameters.AddWithValue("$animationName", cleanAnimationName);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$key", key);
            var found = command.ExecuteNonQuery() > 0;
            if (found && (cleanModName.Length > 0 || cleanAnimationName.Length > 0))
                WriteCompatibilitySnapshot();
            return found;
        }
    }

    public (CommunityRoleLabelDto? Accepted, bool Changed) Submit(
        string fingerprint, string group, string option, string label, string reporterId,
        string modName = "", string animationName = "")
    {
        var key = MakeKey(fingerprint, group, option);
        var cleanModName = Clean(modName, 160);
        var cleanAnimationName = Clean(animationName, 120);
        var reporterHash = HashIdentity(reporterId);
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var record = GetStoredRecord(connection, transaction, key);
            if (record is null)
            {
                if (CountRecords(connection, transaction) >= MaximumRecords)
                    throw new InvalidOperationException("The community label database is full.");
                record = new StoredRoleLabel
                {
                    Fingerprint = fingerprint,
                    Group = group,
                    Option = option
                };
                var now = DateTimeOffset.UtcNow.ToString("O");
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO community_role_labels
                        (record_key, fingerprint, mod_name, animation_name, option_group, option_name,
                         accepted_label, created_utc, updated_utc)
                    VALUES ($key, $fingerprint, $modName, $animationName, $group, $option, '', $now, $now);
                    """;
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$fingerprint", fingerprint);
                insert.Parameters.AddWithValue("$modName", cleanModName);
                insert.Parameters.AddWithValue("$animationName", cleanAnimationName);
                insert.Parameters.AddWithValue("$group", group);
                insert.Parameters.AddWithValue("$option", option);
                insert.Parameters.AddWithValue("$now", now);
                insert.ExecuteNonQuery();
                record.ModName = cleanModName;
                record.AnimationName = cleanAnimationName;
            }
            else
            {
                var changedMetadata = false;
                if (cleanModName.Length > 0 && !record.ModName.Equals(cleanModName, StringComparison.Ordinal))
                {
                    record.ModName = cleanModName;
                    changedMetadata = true;
                }
                if (cleanAnimationName.Length > 0 &&
                    !record.AnimationName.Equals(cleanAnimationName, StringComparison.Ordinal))
                {
                    record.AnimationName = cleanAnimationName;
                    changedMetadata = true;
                }
                if (changedMetadata) UpdateMetadata(connection, transaction, key, record);
            }

            if (label.Equals(record.AcceptedLabel, StringComparison.OrdinalIgnoreCase))
            {
                DeleteVote(connection, transaction, key, reporterHash);
                transaction.Commit();
                WriteCompatibilitySnapshot();
                return (ToDto(record), false);
            }

            if (!VoteExists(connection, transaction, key, reporterHash) &&
                CountVotes(connection, transaction, key) >= MaximumVotesPerRecord)
                throw new InvalidOperationException("This role label has too many reports.");

            using (var vote = connection.CreateCommand())
            {
                vote.Transaction = transaction;
                vote.CommandText = """
                    INSERT INTO community_role_label_votes(record_key, reporter_hash, label, updated_utc)
                    VALUES ($key, $reporterHash, $label, $now)
                    ON CONFLICT(record_key, reporter_hash) DO UPDATE SET
                        label = excluded.label, updated_utc = excluded.updated_utc;
                    """;
                vote.Parameters.AddWithValue("$key", key);
                vote.Parameters.AddWithValue("$reporterHash", reporterHash);
                vote.Parameters.AddWithValue("$label", label);
                vote.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                vote.ExecuteNonQuery();
            }

            var winner = GetLeadingVote(connection, transaction, key)!.Value;
            var requiredVotes = record.AcceptedLabel.Length == 0
                ? InitialAcceptanceThreshold
                : CorrectionAcceptanceThreshold;
            var changed = winner.Count >= requiredVotes &&
                !winner.Label.Equals(record.AcceptedLabel, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                record.AcceptedLabel = winner.Label;
                using var accept = connection.CreateCommand();
                accept.Transaction = transaction;
                accept.CommandText = """
                    UPDATE community_role_labels
                    SET accepted_label = $label, updated_utc = $now
                    WHERE record_key = $key COLLATE NOCASE;
                    DELETE FROM community_role_label_votes WHERE record_key = $key COLLATE NOCASE;
                    """;
                accept.Parameters.AddWithValue("$label", winner.Label);
                accept.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                accept.Parameters.AddWithValue("$key", key);
                accept.ExecuteNonQuery();
            }

            transaction.Commit();
            WriteCompatibilitySnapshot();
            return (record.AcceptedLabel.Length == 0 ? null : ToDto(record), changed);
        }
    }

    private void ImportLegacyJsonOnce()
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            if (LegacyImportComplete(connection))
            {
                ReconcileLegacyJsonIfNeeded(connection);
                return;
            }
            if (!File.Exists(database.LegacyCommunityLabelsPath)) return;

            Dictionary<string, StoredRoleLabel>? loaded;
            try
            {
                var json = File.ReadAllText(database.LegacyCommunityLabelsPath);
                loaded = JsonSerializer.Deserialize<Dictionary<string, StoredRoleLabel>>(json);
                if (loaded is null) throw new JsonException("The legacy label JSON contained no object.");
            }
            catch (Exception exception)
            {
                // Fail closed: keep the source untouched and retry next startup. An empty or
                // partially imported database must never silently replace a damaged source.
                logger.LogError(exception, "Could not validate legacy community-role-labels.json; " +
                    "SQLite import was not marked complete");
                throw new InvalidOperationException(
                    "The legacy community label file could not be validated; relay startup was stopped.", exception);
            }

            var sourceHash = HashFile(database.LegacyCommunityLabelsPath);
            var backupPath = database.LegacyCommunityLabelsPath + ".pre-sqlite." +
                DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "." +
                Convert.ToHexString(sourceHash)[..12] + ".bak";
            try
            {
                File.Copy(database.LegacyCommunityLabelsPath, backupPath, false);
                var backupHash = HashFile(backupPath);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, backupHash))
                    throw new InvalidDataException("The legacy label backup checksum did not match its source.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not create {BackupPath}; refusing to import legacy labels", backupPath);
                throw new InvalidOperationException(
                    "The legacy community label backup could not be created; relay startup was stopped.", exception);
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                foreach (var (key, source) in loaded)
                {
                    source.Votes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    using var record = connection.CreateCommand();
                    record.Transaction = transaction;
                    record.CommandText = """
                        INSERT INTO community_role_labels
                            (record_key, fingerprint, mod_name, animation_name, option_group, option_name,
                             accepted_label, created_utc, updated_utc)
                        VALUES ($key, $fingerprint, $modName, $animationName, $group, $option,
                                $acceptedLabel, $now, $now)
                        ON CONFLICT(record_key) DO NOTHING;
                        """;
                    record.Parameters.AddWithValue("$key", key);
                    record.Parameters.AddWithValue("$fingerprint", source.Fingerprint ?? "");
                    record.Parameters.AddWithValue("$modName", source.ModName ?? "");
                    record.Parameters.AddWithValue("$animationName", source.AnimationName ?? "");
                    record.Parameters.AddWithValue("$group", source.Group ?? "");
                    record.Parameters.AddWithValue("$option", source.Option ?? "");
                    record.Parameters.AddWithValue("$acceptedLabel", source.AcceptedLabel ?? "");
                    record.Parameters.AddWithValue("$now", now);
                    record.ExecuteNonQuery();

                    foreach (var (reporterHash, voteLabel) in source.Votes)
                    {
                        using var vote = connection.CreateCommand();
                        vote.Transaction = transaction;
                        vote.CommandText = """
                            INSERT INTO community_role_label_votes
                                (record_key, reporter_hash, label, updated_utc)
                            VALUES ($key, $reporterHash, $label, $now)
                            ON CONFLICT(record_key, reporter_hash) DO NOTHING;
                            """;
                        vote.Parameters.AddWithValue("$key", key);
                        vote.Parameters.AddWithValue("$reporterHash", reporterHash);
                        vote.Parameters.AddWithValue("$label", voteLabel);
                        vote.Parameters.AddWithValue("$now", now);
                        vote.ExecuteNonQuery();
                    }
                }

                // Verify semantic equivalence before recording success. INSERT OR IGNORE permits
                // a populated SQLite database, but it must never silently mask conflicting data.
                foreach (var (key, expected) in loaded)
                    VerifyLegacyRecord(connection, transaction, key, expected);

                using var imported = connection.CreateCommand();
                imported.Transaction = transaction;
                imported.CommandText = """
                    INSERT INTO legacy_data_imports
                        (import_key, source_path, source_last_write_utc, imported_utc, record_count,
                         source_sha256, sqlite_snapshot_sha256)
                    VALUES ($key, $path, $sourceModified, $now, $recordCount,
                            $sourceHash, $sqliteHash);
                    """;
                imported.Parameters.AddWithValue("$key", LegacyImportKey);
                imported.Parameters.AddWithValue("$path", database.LegacyCommunityLabelsPath);
                imported.Parameters.AddWithValue("$sourceModified",
                    File.GetLastWriteTimeUtc(database.LegacyCommunityLabelsPath).ToString("O"));
                imported.Parameters.AddWithValue("$now", now);
                imported.Parameters.AddWithValue("$recordCount", loaded.Count);
                imported.Parameters.AddWithValue("$sourceHash", Convert.ToHexString(sourceHash));
                imported.Parameters.AddWithValue("$sqliteHash",
                    HashSnapshot(LoadSnapshot(connection, transaction)));
                imported.ExecuteNonQuery();
                transaction.Commit();
                logger.LogInformation("Imported {Count} legacy community label records into SQLite; backup: {Backup}",
                    loaded.Count, backupPath);
            }
            catch (Exception exception)
            {
                transaction.Rollback();
                logger.LogError(exception, "Legacy community label import failed and was rolled back");
                throw new InvalidOperationException(
                    "The legacy community label migration failed and was rolled back; relay startup was stopped.",
                    exception);
            }
        }
    }

    private bool LegacyImportComplete(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM legacy_data_imports WHERE import_key = $key;";
        command.Parameters.AddWithValue("$key", LegacyImportKey);
        return command.ExecuteScalar() is not null;
    }

    private void ReconcileLegacyJsonIfNeeded(SqliteConnection connection)
    {
        if (!File.Exists(database.LegacyCommunityLabelsPath)) return;
        using var marker = connection.CreateCommand();
        marker.CommandText = """
            SELECT source_sha256, sqlite_snapshot_sha256, sqlite_revision, mirrored_sqlite_revision
            FROM legacy_data_imports WHERE import_key = $key;
            """;
        marker.Parameters.AddWithValue("$key", LegacyImportKey);
        using var markerReader = marker.ExecuteReader();
        if (!markerReader.Read()) return;
        var recordedSourceHash = markerReader.GetString(0);
        var sqliteRevision = markerReader.GetInt64(2);
        var mirroredRevision = markerReader.GetInt64(3);
        markerReader.Close();

        var currentSourceHash = Convert.ToHexString(HashFile(database.LegacyCommunityLabelsPath));
        if (recordedSourceHash.Length > 0 &&
            currentSourceHash.Equals(recordedSourceHash, StringComparison.OrdinalIgnoreCase)) return;

        var reconciliation = Environment.GetEnvironmentVariable(
            "EMOTELINK_RECONCILE_LEGACY_LABEL_JSON")?.Trim();
        if (!string.Equals(reconciliation, "accept-reconciled", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "community-role-labels.json changed after the SQLite cutover" +
                (sqliteRevision != mirroredRevision ? " and SQLite also changed" : "") +
                ". Startup was stopped to prevent silent rollback data loss. Back up both files, " +
                "manually reconcile the JSON and SQLite records, then set " +
                "EMOTELINK_RECONCILE_LEGACY_LABEL_JSON=accept-reconciled for one startup.");

        Dictionary<string, StoredRoleLabel> legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<Dictionary<string, StoredRoleLabel>>(
                         File.ReadAllText(database.LegacyCommunityLabelsPath)) ??
                     throw new JsonException("The legacy label JSON contained no object.");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The reconciled legacy label JSON is invalid; startup was stopped.", exception);
        }
        var sqliteSnapshot = LoadSnapshot(connection);
        var sqliteHash = HashSnapshot(sqliteSnapshot);
        if (!HashSnapshot(legacy).Equals(sqliteHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The legacy JSON and SQLite community labels are still different. The reconciliation " +
                "switch cannot choose a side or overwrite data; manually make them semantically equal first.");

        UpdateLegacyImportHashes(connection, currentSourceHash, sqliteHash,
            File.GetLastWriteTimeUtc(database.LegacyCommunityLabelsPath).ToString("O"));
        logger.LogWarning(
            "Accepted manually reconciled legacy community-label JSON. Remove " +
            "EMOTELINK_RECONCILE_LEGACY_LABEL_JSON before the next restart.");
    }

    private static void UpdateLegacyImportHashes(
        SqliteConnection connection,
        string sourceHash,
        string sqliteHash,
        string? sourceModified = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE legacy_data_imports
            SET source_sha256 = $sourceHash,
                sqlite_snapshot_sha256 = $sqliteHash,
                mirrored_sqlite_revision = sqlite_revision,
                source_last_write_utc = $modified
            WHERE import_key = $key;
            """;
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        command.Parameters.AddWithValue("$sqliteHash", sqliteHash);
        command.Parameters.AddWithValue("$modified", sourceModified ?? DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$key", LegacyImportKey);
        command.ExecuteNonQuery();
    }

    private void WriteCompatibilitySnapshot()
    {
        if (!compatibilityMirrorEnabled) return;
        try
        {
            using var connection = database.OpenConnection();
            var snapshot = LoadSnapshot(connection);
            var temporary = database.LegacyCommunityLabelsPath + ".tmp";
            var bytes = SerializeSnapshot(snapshot);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, database.LegacyCommunityLabelsPath, true);
            UpdateLegacyImportHashes(connection,
                Convert.ToHexString(SHA256.HashData(bytes)),
                HashSnapshot(snapshot),
                File.GetLastWriteTimeUtc(database.LegacyCommunityLabelsPath).ToString("O"));
        }
        catch (Exception exception)
        {
            // SQLite is authoritative after migration. A rollback mirror failure is logged but
            // must not undo a successful database transaction.
            logger.LogWarning(exception, "Could not update the legacy community label JSON mirror");
        }
    }

    private static Dictionary<string, StoredRoleLabel> LoadSnapshot(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var snapshot = new Dictionary<string, StoredRoleLabel>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.record_key, r.fingerprint, r.mod_name, r.animation_name, r.option_group,
                   r.option_name, r.accepted_label, v.reporter_hash, v.label
            FROM community_role_labels r
            LEFT JOIN community_role_label_votes v ON v.record_key = r.record_key
            ORDER BY r.record_key COLLATE NOCASE, v.reporter_hash COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!snapshot.TryGetValue(key, out var record))
            {
                snapshot[key] = record = new StoredRoleLabel
                {
                    Fingerprint = reader.GetString(1),
                    ModName = reader.GetString(2),
                    AnimationName = reader.GetString(3),
                    Group = reader.GetString(4),
                    Option = reader.GetString(5),
                    AcceptedLabel = reader.GetString(6)
                };
            }
            if (!reader.IsDBNull(7)) record.Votes[reader.GetString(7)] = reader.GetString(8);
        }
        return snapshot;
    }

    private static string HashSnapshot(Dictionary<string, StoredRoleLabel> source) =>
        Convert.ToHexString(SHA256.HashData(SerializeSnapshot(source)));

    private static byte[] SerializeSnapshot(Dictionary<string, StoredRoleLabel> source)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, record) in source
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                writer.WriteStartObject();
                writer.WriteString(nameof(StoredRoleLabel.Fingerprint), record.Fingerprint ?? "");
                writer.WriteString(nameof(StoredRoleLabel.ModName), record.ModName ?? "");
                writer.WriteString(nameof(StoredRoleLabel.AnimationName), record.AnimationName ?? "");
                writer.WriteString(nameof(StoredRoleLabel.Group), record.Group ?? "");
                writer.WriteString(nameof(StoredRoleLabel.Option), record.Option ?? "");
                writer.WriteString(nameof(StoredRoleLabel.AcceptedLabel), record.AcceptedLabel ?? "");
                writer.WritePropertyName(nameof(StoredRoleLabel.Votes));
                writer.WriteStartObject();
                foreach (var (reporter, label) in (record.Votes ?? [])
                             .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(pair => pair.Key, StringComparer.Ordinal))
                    writer.WriteString(reporter, label ?? "");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static IReadOnlyList<AdminRoleLabelDto> QueryAdminRecords(
        SqliteConnection connection, string? term, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = term is null
            ? """
              SELECT r.record_key, r.fingerprint, r.mod_name, r.animation_name, r.option_group,
                     r.option_name, r.accepted_label, v.label, COUNT(v.reporter_hash)
              FROM community_role_labels r
              LEFT JOIN community_role_label_votes v ON v.record_key = r.record_key
              GROUP BY r.record_key, v.label COLLATE NOCASE
              ORDER BY r.mod_name COLLATE NOCASE, r.animation_name COLLATE NOCASE;
              """
            : """
              WITH matches AS (
                  SELECT r.record_key
                  FROM community_role_labels r
                  WHERE r.record_key IN (
                      SELECT record_key FROM community_label_search
                      WHERE community_label_search MATCH $term
                      UNION
                      SELECT record_key FROM community_vote_search
                      WHERE community_vote_search MATCH $term)
                  ORDER BY r.updated_utc DESC
                  LIMIT $limit
              )
              SELECT r.record_key, r.fingerprint, r.mod_name, r.animation_name, r.option_group,
                     r.option_name, r.accepted_label, v.label, COUNT(v.reporter_hash)
              FROM matches m
              JOIN community_role_labels r ON r.record_key = m.record_key
              LEFT JOIN community_role_label_votes v ON v.record_key = r.record_key
              GROUP BY r.record_key, v.label COLLATE NOCASE
              ORDER BY r.mod_name COLLATE NOCASE, r.animation_name COLLATE NOCASE;
              """;
        if (term is not null)
        {
            command.Parameters.AddWithValue("$term", term);
            command.Parameters.AddWithValue("$limit", limit);
        }

        var builders = new Dictionary<string, AdminRecordBuilder>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!builders.TryGetValue(key, out var builder))
            {
                builders[key] = builder = new AdminRecordBuilder(
                    key, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6));
            }
            if (!reader.IsDBNull(7)) builder.Votes.Add(new AdminVoteDto(reader.GetString(7), reader.GetInt32(8)));
        }
        return builders.Values.Select(builder => builder.Build()).ToList();
    }

    private static AdminRoleLabelDto? GetAdminRecord(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.record_key, r.fingerprint, r.mod_name, r.animation_name, r.option_group,
                   r.option_name, r.accepted_label, v.label, COUNT(v.reporter_hash)
            FROM community_role_labels r
            LEFT JOIN community_role_label_votes v ON v.record_key = r.record_key
            WHERE r.record_key = $key COLLATE NOCASE
            GROUP BY r.record_key, v.label COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$key", key);
        AdminRecordBuilder? builder = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            builder ??= new AdminRecordBuilder(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
            if (!reader.IsDBNull(7)) builder.Votes.Add(new AdminVoteDto(reader.GetString(7), reader.GetInt32(8)));
        }
        return builder?.Build();
    }

    private static void VerifyLegacyRecord(SqliteConnection connection, SqliteTransaction transaction,
        string key, StoredRoleLabel expected)
    {
        var actual = GetStoredRecord(connection, transaction, key) ??
            throw new InvalidDataException($"Legacy record was not imported: {key}");
        if (!string.Equals(actual.Fingerprint, expected.Fingerprint ?? "", StringComparison.Ordinal) ||
            !string.Equals(actual.ModName, expected.ModName ?? "", StringComparison.Ordinal) ||
            !string.Equals(actual.AnimationName, expected.AnimationName ?? "", StringComparison.Ordinal) ||
            !string.Equals(actual.Group, expected.Group ?? "", StringComparison.Ordinal) ||
            !string.Equals(actual.Option, expected.Option ?? "", StringComparison.Ordinal) ||
            !string.Equals(actual.AcceptedLabel, expected.AcceptedLabel ?? "", StringComparison.Ordinal))
            throw new InvalidDataException($"Legacy record conflicts with SQLite data: {key}");

        var actualVotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT reporter_hash, label FROM community_role_label_votes
            WHERE record_key = $key COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$key", key);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) actualVotes[reader.GetString(0)] = reader.GetString(1);
        var expectedVotes = expected.Votes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (actualVotes.Count != expectedVotes.Count || expectedVotes.Any(pair =>
                !actualVotes.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidDataException($"Legacy votes conflict with SQLite data: {key}");
    }

    private static StoredRoleLabel? GetStoredRecord(
        SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fingerprint, mod_name, animation_name, option_group, option_name, accepted_label
            FROM community_role_labels WHERE record_key = $key COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : new StoredRoleLabel
        {
            Fingerprint = reader.GetString(0),
            ModName = reader.GetString(1),
            AnimationName = reader.GetString(2),
            Group = reader.GetString(3),
            Option = reader.GetString(4),
            AcceptedLabel = reader.GetString(5)
        };
    }

    private static void UpdateMetadata(
        SqliteConnection connection, SqliteTransaction transaction, string key, StoredRoleLabel record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE community_role_labels
            SET mod_name = $modName, animation_name = $animationName, updated_utc = $now
            WHERE record_key = $key COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$modName", record.ModName);
        command.Parameters.AddWithValue("$animationName", record.AnimationName);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static (string Label, int Count)? GetLeadingVote(
        SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT label, COUNT(*) AS vote_count
            FROM community_role_label_votes
            WHERE record_key = $key COLLATE NOCASE
            GROUP BY label COLLATE NOCASE
            ORDER BY vote_count DESC, label COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt32(1)) : null;
    }

    private static bool RecordExists(
        SqliteConnection connection, string key, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM community_role_labels WHERE record_key = $key COLLATE NOCASE;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is not null;
    }

    private static long CountRecords(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM community_role_labels;";
        return (long)command.ExecuteScalar()!;
    }

    private static long CountVotes(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM community_role_label_votes WHERE record_key = $key COLLATE NOCASE;";
        command.Parameters.AddWithValue("$key", key);
        return (long)command.ExecuteScalar()!;
    }

    private static bool VoteExists(
        SqliteConnection connection, SqliteTransaction transaction, string key, string reporterHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM community_role_label_votes
            WHERE record_key = $key COLLATE NOCASE AND reporter_hash = $reporterHash COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$reporterHash", reporterHash);
        return command.ExecuteScalar() is not null;
    }

    private static void DeleteVote(
        SqliteConnection connection, SqliteTransaction transaction, string key, string reporterHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM community_role_label_votes
            WHERE record_key = $key COLLATE NOCASE AND reporter_hash = $reporterHash COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$reporterHash", reporterHash);
        command.ExecuteNonQuery();
    }

    private static string MakeKey(string fingerprint, string group, string option) =>
        fingerprint + "\n" + group + "\n" + option;

    private static string Clean(string value, int maximumLength)
    {
        var clean = value.Trim();
        return clean[..Math.Min(maximumLength, clean.Length)];
    }

    private static string HashIdentity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static CommunityRoleLabelDto ToDto(StoredRoleLabel record) =>
        new(record.Fingerprint, record.Group, record.Option, record.AcceptedLabel);

    private sealed class AdminRecordBuilder(
        string key, string fingerprint, string modName, string animationName,
        string group, string option, string acceptedLabel)
    {
        public List<AdminVoteDto> Votes { get; } = [];

        public AdminRoleLabelDto Build() => new(key, fingerprint, modName, animationName, group, option,
            acceptedLabel, Votes.OrderByDescending(vote => vote.Count)
                .ThenBy(vote => vote.Label, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public sealed class StoredRoleLabel
    {
        public string Fingerprint { get; set; } = "";
        public string ModName { get; set; } = "";
        public string AnimationName { get; set; } = "";
        public string Group { get; set; } = "";
        public string Option { get; set; } = "";
        public string AcceptedLabel { get; set; } = "";
        public Dictionary<string, string> Votes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record CommunityRoleLabelDto(string Fingerprint, string Group, string Option, string Label);
public sealed record AdminVoteDto(string Label, int Count);
public sealed record AdminRoleLabelDto(string Key, string Fingerprint, string ModName, string AnimationName,
    string Group, string Option, string AcceptedLabel, IReadOnlyList<AdminVoteDto> Votes);

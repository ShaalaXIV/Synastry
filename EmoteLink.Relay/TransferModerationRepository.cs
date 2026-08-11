using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace EmoteLink.Relay;

public interface ITransferModerationRepository
{
    TransferSharingBanDto? FindMatchingBan(
        string packageSha256, string catalogFingerprint, string modNameHash);
    IReadOnlyList<TransferSharingBanDto> GetTransferBans(bool includeRevoked = false, string? query = null);
    TransferSharingBanDto UpsertTransferBan(
        TransferBanScope scope, string value, TransferBanReasonCode reasonCode, string note, string createdBy,
        string displayName = "");
    bool DeleteTransferBan(long id);
    void RecordAuditEvent(TransferAuditEventWrite auditEvent);
    IReadOnlyList<TransferAuditEventDto> GetAuditEvents(int limit = 500);
}

public sealed class SqliteTransferModerationRepository : ITransferModerationRepository
{
    private const int MaximumAuditEvents = 100_000;
    private readonly RelayDatabase database;

    public SqliteTransferModerationRepository(RelayDatabase database) => this.database = database;

    public TransferSharingBanDto? FindMatchingBan(
        string packageSha256, string catalogFingerprint, string modNameHash)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, scope, match_value, display_name, reason_code, note, created_by_hash,
                   created_utc, updated_utc, revoked_utc
            FROM transfer_sharing_bans
            WHERE revoked_utc IS NULL AND (
                   (scope = 1 AND $packageSha256 <> '' AND match_value = $packageSha256 COLLATE NOCASE)
                OR (scope = 2 AND $catalogFingerprint <> '' AND match_value = $catalogFingerprint COLLATE NOCASE)
                OR (scope = 3 AND $modNameHash <> '' AND match_value = $modNameHash COLLATE NOCASE))
            ORDER BY CASE scope WHEN 1 THEN 0 WHEN 2 THEN 1 ELSE 2 END
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$packageSha256", NormalizeMatchValue(packageSha256));
        command.Parameters.AddWithValue("$catalogFingerprint", NormalizeMatchValue(catalogFingerprint));
        command.Parameters.AddWithValue("$modNameHash", NormalizeMatchValue(modNameHash));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBan(reader) : null;
    }

    public IReadOnlyList<TransferSharingBanDto> GetTransferBans(bool includeRevoked = false, string? query = null)
    {
        var cleanQuery = Clean(query ?? "", 160);
        var hasQuery = cleanQuery.Length > 0;
        if (hasQuery && !CatalogSearchSyntax.TryBuildTrigramQuery(cleanQuery, out _)) return [];
        CatalogSearchSyntax.TryBuildTrigramQuery(cleanQuery, out var ftsQuery);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = hasQuery ? """
            SELECT id, scope, match_value, display_name, reason_code, note, created_by_hash,
                   created_utc, updated_utc, revoked_utc
            FROM transfer_sharing_bans
            WHERE ($includeRevoked = 1 OR revoked_utc IS NULL)
              AND id IN (
                  SELECT CAST(ban_id AS INTEGER) FROM transfer_ban_search
                  WHERE transfer_ban_search MATCH $ftsQuery)
            ORDER BY CASE WHEN revoked_utc IS NULL THEN 0 ELSE 1 END, updated_utc DESC;
            """ : """
            SELECT id, scope, match_value, display_name, reason_code, note, created_by_hash,
                   created_utc, updated_utc, revoked_utc
            FROM transfer_sharing_bans
            WHERE $includeRevoked = 1 OR revoked_utc IS NULL
            ORDER BY CASE WHEN revoked_utc IS NULL THEN 0 ELSE 1 END, updated_utc DESC;
            """;
        command.Parameters.AddWithValue("$includeRevoked", includeRevoked ? 1 : 0);
        if (hasQuery) command.Parameters.AddWithValue("$ftsQuery", ftsQuery);
        var result = new List<TransferSharingBanDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadBan(reader));
        return result;
    }

    public TransferSharingBanDto UpsertTransferBan(
        TransferBanScope scope, string value, TransferBanReasonCode reasonCode, string note, string createdBy,
        string displayName = "")
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (!Enum.IsDefined(reasonCode)) throw new ArgumentOutOfRangeException(nameof(reasonCode));
        var matchValue = NormalizeMatchValue(value);
        if (matchValue.Length != 64 || matchValue.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A moderation match value must be a 64-character SHA-256 hash.",
                nameof(value));
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new ArgumentException("The administrator identifier is required.", nameof(createdBy));
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO transfer_sharing_bans
                    (scope, match_value, display_name, reason_code, note, created_by_hash,
                     created_utc, updated_utc, revoked_utc)
                VALUES ($scope, $value, $displayName, $reasonCode, $note, $createdBy, $now, $now, NULL)
                ON CONFLICT(scope, match_value) DO UPDATE SET
                    display_name = CASE WHEN excluded.display_name = '' THEN display_name ELSE excluded.display_name END,
                    reason_code = excluded.reason_code,
                    note = excluded.note,
                    created_by_hash = excluded.created_by_hash,
                    updated_utc = excluded.updated_utc,
                    revoked_utc = NULL;
                """;
            command.Parameters.AddWithValue("$scope", (int)scope);
            command.Parameters.AddWithValue("$value", matchValue);
            command.Parameters.AddWithValue("$displayName", Clean(displayName, 160));
            command.Parameters.AddWithValue("$reasonCode", (int)reasonCode);
            command.Parameters.AddWithValue("$note", Clean(note, 500));
            command.Parameters.AddWithValue("$createdBy", HashIdentity(createdBy));
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
        TransferSharingBanDto result;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, scope, match_value, display_name, reason_code, note, created_by_hash,
                       created_utc, updated_utc, revoked_utc
                FROM transfer_sharing_bans WHERE scope = $scope AND match_value = $value COLLATE NOCASE;
                """;
            read.Parameters.AddWithValue("$scope", (int)scope);
            read.Parameters.AddWithValue("$value", matchValue);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The transfer ban could not be read after saving.");
            result = ReadBan(reader);
        }
        transaction.Commit();
        return result;
    }

    /// <summary>
    /// Revokes instead of physically deleting so moderation history remains auditable.
    /// Upserting the same scope/value later explicitly reactivates the record.
    /// </summary>
    public bool DeleteTransferBan(long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transfer_sharing_bans
            SET revoked_utc = $now, updated_utc = $now
            WHERE id = $id AND revoked_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    public void RecordAuditEvent(TransferAuditEventWrite auditEvent)
    {
        var eventType = Clean(auditEvent.EventType, 80);
        if (eventType.Length == 0) throw new ArgumentException("Audit event type is required.");
        var timestamp = auditEvent.Timestamp == default ? DateTimeOffset.UtcNow : auditEvent.Timestamp.ToUniversalTime();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO moderation_audit_events
                    (transfer_id, event_type, occurred_utc, package_sha256,
                     catalog_fingerprint, mod_name_hash, note)
                VALUES ($transferId, $eventType, $occurredUtc, $packageSha256,
                        $catalogFingerprint, $modNameHash, $note);
                """;
            command.Parameters.AddWithValue("$transferId", Clean(auditEvent.TransferId, 100));
            command.Parameters.AddWithValue("$eventType", eventType);
            command.Parameters.AddWithValue("$occurredUtc", timestamp.ToString("O"));
            command.Parameters.AddWithValue("$packageSha256", NormalizeMatchValue(auditEvent.PackageSha256));
            command.Parameters.AddWithValue("$catalogFingerprint", NormalizeMatchValue(auditEvent.CatalogFingerprint));
            command.Parameters.AddWithValue("$modNameHash", NormalizeMatchValue(auditEvent.ModNameHash));
            command.Parameters.AddWithValue("$note", Clean(auditEvent.Note, 500));
            command.ExecuteNonQuery();
        }
        using (var trim = connection.CreateCommand())
        {
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM moderation_audit_events
                WHERE id <= COALESCE((SELECT MAX(id) - $maximum FROM moderation_audit_events), 0);
                """;
            trim.Parameters.AddWithValue("$maximum", MaximumAuditEvents);
            trim.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<TransferAuditEventDto> GetAuditEvents(int limit = 500)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, transfer_id, event_type, occurred_utc, package_sha256,
                   catalog_fingerprint, mod_name_hash, note
            FROM moderation_audit_events ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5_000));
        var result = new List<TransferAuditEventDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new TransferAuditEventDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7)));
        return result;
    }

    private static TransferSharingBanDto ReadBan(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        (TransferBanScope)reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        (TransferBanReasonCode)reader.GetInt32(4),
        reader.GetString(5),
        reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7)),
        DateTimeOffset.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)));

    private static string NormalizeMatchValue(string? value) => (value ?? "").Trim().ToUpperInvariant();

    private static string Clean(string value, int maximumLength)
    {
        var clean = value.Trim();
        return clean[..Math.Min(maximumLength, clean.Length)];
    }

    private static string HashIdentity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public enum TransferBanScope
{
    ExactPackageSha256 = 1,
    AnimationCatalogFingerprint = 2,
    ModFamilyNameHash = 3
}

public enum TransferBanReasonCode
{
    CreatorOptOut = 1,
    CopyrightConcern = 2,
    PolicyViolation = 3,
    MalwareOrUnsafe = 4,
    Other = 5
}

public sealed record TransferSharingBanDto(
    long Id,
    TransferBanScope Scope,
    string MatchValue,
    string DisplayName,
    TransferBanReasonCode ReasonCode,
    string Note,
    string CreatedByHash,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? RevokedUtc);

public sealed record TransferAuditEventWrite(
    string TransferId,
    string EventType,
    DateTimeOffset Timestamp,
    string PackageSha256,
    string CatalogFingerprint,
    string ModNameHash,
    string Note);

public sealed record TransferAuditEventDto(
    long Id,
    string TransferId,
    string EventType,
    DateTimeOffset Timestamp,
    string PackageSha256,
    string CatalogFingerprint,
    string ModNameHash,
    string Note);

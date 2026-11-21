using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EmoteLink.Relay;

/// <summary>
/// Durable record of the sends whose package is still kept on the relay, so the retained packages
/// survive a restart and stay reviewable for their whole retention. The package bytes live in
/// <see cref="PackageDirectory"/>; SQLite only holds what the admin transfer list shows.
/// </summary>
public interface ITransferRetentionRepository
{
    string PackageDirectory { get; }
    IReadOnlyList<RetainedTransferRecord> LoadRetainedTransfers();
    void SaveRetainedTransfer(RetainedTransferRecord record);
    void DeleteRetainedTransfer(string transferId);
}

public sealed class SqliteTransferRetentionRepository : ITransferRetentionRepository
{
    private readonly RelayDatabase database;

    public SqliteTransferRetentionRepository(RelayDatabase database)
    {
        this.database = database;
        var configured = Environment.GetEnvironmentVariable("EMOTELINK_TRANSFER_DIR");
        // Beside the database rather than in /tmp: on the production host /tmp is a RAM-backed
        // tmpfs that systemd recreates (PrivateTmp) on every restart.
        PackageDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(database.DataDirectory, "transfer-packages")
            : Path.GetFullPath(configured);
    }

    public string PackageDirectory { get; }

    public IReadOnlyList<RetainedTransferRecord> LoadRetainedTransfers()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transfer_id, package_sha256, package_file, size, mod_name, mod_name_hash,
                   catalog_fingerprint, room_code, sender_name, created_utc, uploaded_utc,
                   retained_until_utc, from_stored_copy, room_detached_utc, access_blocked_utc,
                   access_block_reason, recipients_downloaded, recipients_declined, recipients_closed,
                   recipients_blocked, recipients_failed, recipients_expired
            FROM retained_transfers
            ORDER BY created_utc;
            """;
        var result = new List<RetainedTransferRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RetainedTransferRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                ParseTime(reader.GetString(9)) ?? DateTimeOffset.MinValue,
                ParseTime(reader.GetString(10)) ?? DateTimeOffset.MinValue,
                ParseTime(reader.GetString(11)) ?? DateTimeOffset.MinValue,
                reader.GetInt32(12) != 0,
                ParseTime(reader.GetString(13)),
                ParseTime(reader.GetString(14)),
                reader.GetString(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.GetInt32(19),
                reader.GetInt32(20),
                reader.GetInt32(21)));
        }
        return result;
    }

    public void SaveRetainedTransfer(RetainedTransferRecord record)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO retained_transfers
                (transfer_id, package_sha256, package_file, size, mod_name, mod_name_hash,
                 catalog_fingerprint, room_code, sender_name, created_utc, uploaded_utc,
                 retained_until_utc, from_stored_copy, room_detached_utc, access_blocked_utc,
                 access_block_reason, recipients_downloaded, recipients_declined, recipients_closed,
                 recipients_blocked, recipients_failed, recipients_expired, updated_utc)
            VALUES ($id, $sha, $file, $size, $modName, $modNameHash, $fingerprint, $room, $sender,
                    $created, $uploaded, $retainedUntil, $fromStoredCopy, $roomDetached, $blocked,
                    $blockReason, $downloaded, $declined, $closed, $adminBlocked, $failed, $expired, $now)
            ON CONFLICT(transfer_id) DO UPDATE SET
                package_sha256 = excluded.package_sha256,
                package_file = excluded.package_file,
                retained_until_utc = excluded.retained_until_utc,
                room_detached_utc = excluded.room_detached_utc,
                access_blocked_utc = excluded.access_blocked_utc,
                access_block_reason = excluded.access_block_reason,
                recipients_downloaded = excluded.recipients_downloaded,
                recipients_declined = excluded.recipients_declined,
                recipients_closed = excluded.recipients_closed,
                recipients_blocked = excluded.recipients_blocked,
                recipients_failed = excluded.recipients_failed,
                recipients_expired = excluded.recipients_expired,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$id", record.TransferId);
        command.Parameters.AddWithValue("$sha", record.PackageSha256);
        command.Parameters.AddWithValue("$file", record.PackageFile);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue("$modName", record.ModName);
        command.Parameters.AddWithValue("$modNameHash", record.ModNameHash);
        command.Parameters.AddWithValue("$fingerprint", record.CatalogFingerprint);
        command.Parameters.AddWithValue("$room", record.RoomCode);
        command.Parameters.AddWithValue("$sender", record.SenderName);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$uploaded", record.UploadedAt.ToString("O"));
        command.Parameters.AddWithValue("$retainedUntil", record.RetainedUntil.ToString("O"));
        command.Parameters.AddWithValue("$fromStoredCopy", record.FromStoredCopy ? 1 : 0);
        command.Parameters.AddWithValue("$roomDetached", record.RoomDetachedAt?.ToString("O") ?? "");
        command.Parameters.AddWithValue("$blocked", record.AccessBlockedAt?.ToString("O") ?? "");
        command.Parameters.AddWithValue("$blockReason", record.AccessBlockReason);
        command.Parameters.AddWithValue("$downloaded", record.Downloaded);
        command.Parameters.AddWithValue("$declined", record.Declined);
        command.Parameters.AddWithValue("$closed", record.Closed);
        command.Parameters.AddWithValue("$adminBlocked", record.AdminBlocked);
        command.Parameters.AddWithValue("$failed", record.DeliveryFailed);
        command.Parameters.AddWithValue("$expired", record.Expired);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteRetainedTransfer(string transferId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM retained_transfers WHERE transfer_id = $id;";
        command.Parameters.AddWithValue("$id", transferId);
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}

/// <summary>One retained send as stored in SQLite. Recipients are kept only as outcome counts.</summary>
public sealed record RetainedTransferRecord(
    string TransferId,
    string PackageSha256,
    string PackageFile,
    long Size,
    string ModName,
    string ModNameHash,
    string CatalogFingerprint,
    string RoomCode,
    string SenderName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UploadedAt,
    DateTimeOffset RetainedUntil,
    bool FromStoredCopy,
    DateTimeOffset? RoomDetachedAt,
    DateTimeOffset? AccessBlockedAt,
    string AccessBlockReason,
    int Downloaded,
    int Declined,
    int Closed,
    int AdminBlocked,
    int DeliveryFailed,
    int Expired);

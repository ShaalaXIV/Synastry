using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EmoteLink.Relay;

public sealed partial class TransferStore : BackgroundService
{
    public const string CapabilityHeaderName = "X-Synastry-Transfer-Token";
    public const string AdminActorHeaderName = "X-Synastry-Admin-Actor";
    public const long MaximumBytes = 75L * 1024 * 1024;
    public const long MaximumStorageBytes = 25L * 1024 * 1024 * 1024;
    public const int MaximumActiveTransfers = 4_096;
    public const int MaximumActiveTransfersPerSender = 32;
    public const int MaximumActiveTransfersPerRoom = 128;

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownCleanupWindow = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, Transfer> transfers = new();
    private readonly ConcurrentDictionary<string, int> startupOrphans = new(StringComparer.OrdinalIgnoreCase);
    private readonly object reservationGate = new();
    private readonly Dictionary<string, int> senderReservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> roomReservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TransferStore> logger;
    private readonly AdminTransferEventBroker adminEvents;
    private readonly ITransferModerationRepository moderation;
    private readonly string root;
    private long reservedBytes;
    private int activeTransferCount;

    public TransferStore(
        ILogger<TransferStore> logger,
        AdminTransferEventBroker adminEvents,
        ITransferModerationRepository moderation)
        : this(logger, adminEvents, moderation, null)
    {
    }

    internal TransferStore(
        ILogger<TransferStore> logger,
        AdminTransferEventBroker adminEvents,
        ITransferModerationRepository moderation,
        string? rootOverride)
    {
        this.logger = logger;
        this.adminEvents = adminEvents;
        this.moderation = moderation;
        root = rootOverride ?? Path.Combine(Path.GetTempPath(), "emotelink-transfers");
        Directory.CreateDirectory(root);
        SweepStartupOrphans();
    }

    public TransferUploadDto Begin(string roomCode, string senderId, string senderName, string modName,
        long size, string sha256, IReadOnlyList<string> recipients, string catalogFingerprint = "",
        int alreadyReceived = 0)
    {
        if (size <= 0 || size > MaximumBytes) throw new InvalidOperationException("Mod must be 75 MB or smaller.");
        var hash = NormalizeHash(sha256);
        if (hash.Length != 64) throw new InvalidOperationException("A valid SHA-256 checksum is required.");
        var recipientIds = recipients.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
        if (recipientIds.Count == 0) throw new InvalidOperationException("There is nobody else in the room.");

        var cleanName = CleanName(modName);
        var modNameHash = ComputeModNameHash(cleanName);
        var cleanFingerprint = NormalizeHash(catalogFingerprint);
        if (moderation.FindMatchingBan(hash, cleanFingerprint, modNameHash) is { } matchingBan)
        {
            RecordRejectedAttempt(hash, cleanFingerprint, modNameHash, matchingBan);
            throw new TransferSharingBlockedException(matchingBan.ReasonCode);
        }

        var cleanRoomCode = CleanCode(roomCode);
        var now = DateTimeOffset.UtcNow;
        Transfer transfer;
        lock (reservationGate)
        {
            if (activeTransferCount >= MaximumActiveTransfers)
                throw new InvalidOperationException("The relay has too many active transfers. Try again shortly.");
            if (senderReservations.GetValueOrDefault(senderId) >= MaximumActiveTransfersPerSender)
                throw new InvalidOperationException("You have too many active transfers. Try again shortly.");
            if (roomReservations.GetValueOrDefault(cleanRoomCode) >= MaximumActiveTransfersPerRoom)
                throw new InvalidOperationException("This room has too many active transfers. Try again shortly.");

            // Normal reservation checks are O(1). The bounded scan below occurs only
            // when the global byte ceiling is actually under pressure.
            while (reservedBytes + size > MaximumStorageBytes)
            {
                var candidate = transfers.Values
                    .Where(value => !value.UploadInProgress && !value.RemovalRequested)
                    .OrderBy(value => value.ExpiresAt)
                    .FirstOrDefault();
                if (candidate is null)
                    throw new InvalidOperationException("The relay transfer storage is currently full.");
                _ = RequestRemoval(candidate.Id, TransferRemovalReason.StoragePressure);
            }

            var id = RandomToken(18);
            var recipientStates = recipientIds.ToDictionary(
                value => value,
                _ => new TransferRecipient(RandomToken(32)));
            transfer = new Transfer(
                id,
                cleanName,
                modNameHash,
                cleanRoomCode,
                senderId,
                CleanDisplayName(senderName),
                size,
                hash,
                now,
                now.Add(Lifetime),
                RandomToken(32),
                Path.Combine(root, id + ".pmp"),
                recipientStates,
                cleanFingerprint);
            if (!transfers.TryAdd(id, transfer)) throw new InvalidOperationException("Could not create the transfer.");
            reservedBytes += size;
            activeTransferCount++;
            senderReservations[senderId] = senderReservations.GetValueOrDefault(senderId) + 1;
            roomReservations[cleanRoomCode] = roomReservations.GetValueOrDefault(cleanRoomCode) + 1;
        }

        logger.LogInformation(
            "Transfer {TransferId} reserved: modNameHash={ModNameHash}, packageSha256={PackageSha256}, " +
            "catalogFingerprint={CatalogFingerprint}, room={RoomCode}, recipients={RecipientCount}, size={Size}",
            transfer.Id, transfer.ModNameHash, transfer.Sha256, transfer.CatalogFingerprint,
            transfer.RoomCode, transfer.Recipients.Count, transfer.Size);
        PublishEvent("created", transfer);
        return new TransferUploadDto(transfer.Id, transfer.UploadToken, recipientIds.Count, alreadyReceived);
    }

    public Transfer? GetUpload(string id, string token)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return null;
        if (transfer.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _ = RequestRemoval(id, TransferRemovalReason.Expired);
            return null;
        }
        if (FindMatchingBan(transfer) is { } matchingBan)
        {
            _ = BlockRecipientAccess(id, BanAuditNote(matchingBan));
            return null;
        }

        lock (transfer.Gate)
        {
            if (!FixedEquals(transfer.UploadToken, token) || transfer.Uploaded || transfer.UploadInProgress ||
                transfer.RoomDetached || transfer.RecipientAccessBlocked || transfer.RemovalRequested)
                return null;
            transfer.UploadInProgress = true;
            transfer.UploadStartedAt = DateTimeOffset.UtcNow;
            transfer.LastFailure = "";
            transfer.UploadCancellation?.Dispose();
            var remaining = transfer.ExpiresAt - DateTimeOffset.UtcNow;
            transfer.UploadCancellation = new CancellationTokenSource(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        PublishEvent("upload-started", transfer);
        return transfer;
    }

    public CancellationToken GetUploadCancellationToken(Transfer transfer)
    {
        lock (transfer.Gate)
            return transfer.UploadCancellation?.Token ?? new CancellationToken(true);
    }

    public void AbortUpload(Transfer transfer, string reason)
    {
        CancellationTokenSource? cancellation;
        var expired = false;
        var removalRequested = false;
        lock (transfer.Gate)
        {
            transfer.UploadInProgress = false;
            transfer.LastFailure = CleanAuditText(reason, 240);
            cancellation = transfer.UploadCancellation;
            transfer.UploadCancellation = null;
            expired = transfer.ExpiresAt <= DateTimeOffset.UtcNow;
            removalRequested = transfer.RemovalRequested;
        }
        cancellation?.Dispose();

        if (expired)
        {
            _ = RequestRemoval(transfer.Id, TransferRemovalReason.Expired);
            return;
        }
        if (removalRequested)
        {
            _ = TryFinalizeRemoval(transfer);
            return;
        }

        try
        {
            if (File.Exists(transfer.Path)) File.Delete(transfer.Path);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Could not remove partial upload for transfer {TransferId}; cleanup is now deferred", transfer.Id);
            _ = RequestRemoval(transfer.Id, TransferRemovalReason.UploadCleanupFailure);
            return;
        }

        // The reservation intentionally survives an ordinary network failure so the
        // authenticated sender may retry, but never beyond the original expiry.
        logger.LogWarning("Transfer {TransferId} upload aborted: {Reason}", transfer.Id, transfer.LastFailure);
        PublishEvent("upload-failed", transfer, transfer.LastFailure);
    }

    public IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> FinishUpload(Transfer transfer)
    {
        CancellationTokenSource? cancellation;
        IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> offers;
        var mustRemove = false;
        lock (transfer.Gate)
        {
            transfer.UploadInProgress = false;
            cancellation = transfer.UploadCancellation;
            transfer.UploadCancellation = null;
            if (transfer.RemovalRequested || transfer.ExpiresAt <= DateTimeOffset.UtcNow ||
                !transfers.ContainsKey(transfer.Id))
            {
                mustRemove = true;
                offers = [];
            }
            else
            {
                transfer.Uploaded = true;
                transfer.UploadedAt = DateTimeOffset.UtcNow;
                offers = transfer.RoomDetached || transfer.RecipientAccessBlocked
                    ? []
                    : transfer.Recipients
                        .Where(pair => pair.Value.State == TransferRecipientState.Pending)
                        .Select(pair => (pair.Key,
                            new ModTransferOfferDto(transfer.Id, transfer.ModName, transfer.SenderName, transfer.Size,
                                transfer.Sha256, pair.Value.Token, transfer.ExpiresAt,
                                transfer.CatalogFingerprint)))
                        .ToList();
            }
        }
        cancellation?.Dispose();

        if (mustRemove)
        {
            _ = RequestRemoval(transfer.Id, transfer.RemovalReason ?? TransferRemovalReason.Expired);
            return [];
        }
        logger.LogInformation("Transfer {TransferId} upload completed", transfer.Id);
        PublishEvent("uploaded", transfer);
        return offers;
    }

    public bool CanDeliverOffer(string id, string connectionId)
    {
        if (!transfers.TryGetValue(id, out var transfer) || transfer.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;
        lock (transfer.Gate)
            return transfer.Uploaded && !transfer.RemovalRequested && !transfer.RoomDetached &&
                   !transfer.RecipientAccessBlocked && File.Exists(transfer.Path) &&
                   transfer.Recipients.TryGetValue(connectionId, out var recipient) &&
                   recipient.State == TransferRecipientState.Pending && recipient.Token.Length > 0;
    }

    public void MarkOfferDeliveryFailed(string id, string connectionId, string reason)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return;
        var changed = false;
        lock (transfer.Gate)
        {
            if (transfer.Recipients.TryGetValue(connectionId, out var recipient) &&
                recipient.State == TransferRecipientState.Pending)
            {
                recipient.State = TransferRecipientState.DeliveryFailed;
                recipient.HandledAt = DateTimeOffset.UtcNow;
                recipient.Token = "";
                changed = true;
            }
        }
        if (changed) PublishEvent("offer-delivery-failed", transfer, CleanAuditText(reason, 160));
    }

    public TransferDownloadHandle? OpenDownload(string id, string token)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return null;
        if (transfer.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _ = RequestRemoval(id, TransferRemovalReason.Expired);
            return null;
        }
        if (FindMatchingBan(transfer) is { } matchingBan)
        {
            _ = BlockRecipientAccess(id, BanAuditNote(matchingBan));
            return null;
        }
        lock (transfer.Gate)
        {
            if (!transfer.Uploaded || transfer.RoomDetached || transfer.RecipientAccessBlocked ||
                transfer.RemovalRequested || !File.Exists(transfer.Path))
                return null;
            if (!transfer.Recipients.Values.Any(recipient =>
                    recipient.State == TransferRecipientState.Pending && FixedEquals(recipient.Token, token)))
                return null;
            try
            {
                return new TransferDownloadHandle(
                    transfer,
                    new FileStream(transfer.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }
            catch (Exception exception)
            {
                transfer.LastFailure = CleanAuditText(exception.Message, 240);
                logger.LogWarning(exception, "Could not open transfer {TransferId} for recipient download", id);
                return null;
            }
        }
    }

    public TransferDownloadHandle? OpenAdminDownload(string id, string? actor)
    {
        if (!transfers.TryGetValue(id, out var transfer) || transfer.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;
        FileStream stream;
        lock (transfer.Gate)
        {
            if (!transfer.Uploaded || transfer.RemovalRequested || !File.Exists(transfer.Path)) return null;
            try
            {
                stream = new FileStream(transfer.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception exception)
            {
                transfer.LastFailure = CleanAuditText(exception.Message, 240);
                logger.LogWarning(exception, "Could not open transfer {TransferId} for admin review", id);
                return null;
            }
        }
        try
        {
            AuditAdminAction(transfer, "admin-review-download", actor, "moderation review copy requested");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        return new TransferDownloadHandle(transfer, stream);
    }

    public bool MarkDownloaded(string id, string connectionId) =>
        MarkRecipientHandled(id, connectionId, TransferRecipientState.Downloaded);

    public void Decline(string id, string connectionId) =>
        _ = MarkRecipientHandled(id, connectionId, TransferRecipientState.Declined);

    public int DetachRecipient(string connectionId)
    {
        var detached = 0;
        foreach (var transfer in transfers.Values)
        {
            var changed = false;
            lock (transfer.Gate)
            {
                if (transfer.Recipients.TryGetValue(connectionId, out var recipient) &&
                    recipient.State == TransferRecipientState.Pending)
                {
                    recipient.State = TransferRecipientState.RoomClosed;
                    recipient.HandledAt = DateTimeOffset.UtcNow;
                    recipient.Token = "";
                    changed = true;
                    detached++;
                }
            }
            if (changed) PublishEvent("recipient-detached", transfer);
        }
        return detached;
    }

    private bool MarkRecipientHandled(string id, string connectionId, TransferRecipientState state)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return false;
        lock (transfer.Gate)
        {
            if (!transfer.Recipients.TryGetValue(connectionId, out var recipient) ||
                recipient.State != TransferRecipientState.Pending)
                return false;
            recipient.State = state;
            recipient.HandledAt = DateTimeOffset.UtcNow;
            recipient.Token = "";
        }
        logger.LogInformation("Transfer {TransferId} recipient state changed to {RecipientState}", id, state);
        PublishEvent(state == TransferRecipientState.Downloaded ? "recipient-downloaded" : "recipient-declined",
            transfer);
        return true;
    }

    public void RemoveForRoom(string roomCode)
    {
        var cleanRoom = CleanCode(roomCode);
        foreach (var transfer in transfers.Values.Where(value =>
                     value.RoomCode.Equals(cleanRoom, StringComparison.OrdinalIgnoreCase)))
        {
            var changed = false;
            lock (transfer.Gate)
            {
                if (!transfer.RoomDetached)
                {
                    transfer.RoomDetached = true;
                    transfer.RoomDetachedAt = DateTimeOffset.UtcNow;
                    transfer.UploadToken = "";
                    foreach (var recipient in transfer.Recipients.Values.Where(value =>
                                 value.State == TransferRecipientState.Pending))
                    {
                        recipient.State = TransferRecipientState.RoomClosed;
                        recipient.HandledAt = transfer.RoomDetachedAt;
                        recipient.Token = "";
                    }
                    changed = true;
                }
            }
            if (!changed) continue;
            logger.LogInformation(
                "Transfer {TransferId} detached from closed room {RoomCode}; evidence retained until {ExpiresAt}",
                transfer.Id, transfer.RoomCode, transfer.ExpiresAt);
            PublishEvent("room-detached", transfer);
        }
    }

    public TransferBlockResult BlockRecipientAccess(
        string id, string? reason, string? administratorActor = null)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return TransferBlockResult.NotFound;
        var cleanReason = CleanAuditText(reason ?? "Blocked by moderation", 240);
        if (administratorActor is not null)
            AuditAdminAction(transfer, "admin-access-block", administratorActor, cleanReason);
        var newlyBlocked = false;
        var reasonEnriched = false;
        lock (transfer.Gate)
        {
            if (transfer.RecipientAccessBlocked)
            {
                if (transfer.RecipientAccessBlockReason.Length == 0 && cleanReason.Length > 0)
                {
                    transfer.RecipientAccessBlockReason = cleanReason;
                    reasonEnriched = true;
                }
            }
            else
            {
                newlyBlocked = true;
                transfer.RecipientAccessBlocked = true;
                transfer.RecipientAccessBlockedAt = DateTimeOffset.UtcNow;
                transfer.RecipientAccessBlockReason = cleanReason;
                foreach (var recipient in transfer.Recipients.Values.Where(value =>
                             value.State == TransferRecipientState.Pending))
                {
                    recipient.State = TransferRecipientState.AdminBlocked;
                    recipient.HandledAt = transfer.RecipientAccessBlockedAt;
                    recipient.Token = "";
                }
            }
        }

        if (newlyBlocked || reasonEnriched)
        {
            logger.LogWarning("Transfer {TransferId} recipient access blocked: {Reason}",
                id, transfer.RecipientAccessBlockReason);
            PublishEvent(reasonEnriched ? "access-block-reason-updated" : "access-blocked",
                transfer, transfer.RecipientAccessBlockReason);
        }
        return newlyBlocked ? TransferBlockResult.Blocked : TransferBlockResult.AlreadyBlocked;
    }

    public int ApplyCurrentBans()
    {
        var blocked = 0;
        foreach (var transfer in transfers.Values)
        {
            var ban = FindMatchingBan(transfer);
            if (ban is not null &&
                BlockRecipientAccess(transfer.Id, BanAuditNote(ban)) == TransferBlockResult.Blocked)
                blocked++;
        }
        return blocked;
    }

    public IReadOnlyList<TransferAdminDto> GetAdminTransfers(string? query = null, string? status = null)
    {
        var cleanQuery = CleanAuditText(query ?? "", 160);
        var cleanStatus = CleanAuditText(status ?? "", 40);
        return transfers.Values
            .OrderByDescending(value => value.CreatedAt)
            .Select(ToAdminDto)
            .Where(value => cleanQuery.Length == 0 || MatchesQuery(value, cleanQuery))
            .Where(value => cleanStatus.Length == 0 ||
                            value.Status.Equals(cleanStatus, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public TransferAdminDto? GetAdminTransfer(string id) =>
        transfers.TryGetValue(id, out var transfer) ? ToAdminDto(transfer) : null;

    public TransferRemovalResult AdminDelete(string id, string? administratorActor)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return TransferRemovalResult.NotFound;
        AuditAdminAction(transfer, "admin-delete-requested", administratorActor, "moderation deletion requested");
        return RequestRemoval(id, TransferRemovalReason.AdminDeleted);
    }

    public bool AuditAdminAction(string id, string eventType, string? administratorActor, string note)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return false;
        AuditAdminAction(transfer, eventType, administratorActor, note);
        return true;
    }

    private void AuditAdminAction(Transfer transfer, string eventType, string? administratorActor, string note)
    {
        var actorHash = ComputeAdministratorHash(administratorActor);
        var snapshot = ToAdminDto(transfer);
        var auditNote = $"actorHash={actorHash}; {CleanAuditText(note, 160)}".TrimEnd(' ', ';');
        // Administrative actions fail closed if their durable audit write fails.
        moderation.RecordAuditEvent(new TransferAuditEventWrite(
            snapshot.TransferId,
            eventType,
            DateTimeOffset.UtcNow,
            snapshot.Sha256,
            snapshot.CatalogFingerprint,
            snapshot.ModNameHash,
            auditNote));
        adminEvents.Publish(eventType, snapshot);
    }

    public static string ComputeAdministratorHash(string? actor)
    {
        var clean = CleanAuditText(string.IsNullOrWhiteSpace(actor) ? "local-admin" : actor, 160);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clean)));
    }

    private static bool MatchesQuery(TransferAdminDto transfer, string query) =>
        transfer.TransferId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        transfer.ModName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        transfer.ModNameHash.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        transfer.SenderName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        transfer.RoomCode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        transfer.Status.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        transfer.Sha256.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        transfer.CatalogFingerprint.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static TransferAdminDto ToAdminDto(Transfer transfer)
    {
        lock (transfer.Gate)
        {
            var states = transfer.Recipients.Values.GroupBy(value => value.State)
                .ToDictionary(group => group.Key, group => group.Count());
            return new TransferAdminDto(
                transfer.Id,
                GetStatus(transfer),
                transfer.ModName,
                transfer.ModNameHash,
                transfer.SenderName,
                transfer.RoomCode,
                transfer.Size,
                transfer.Sha256,
                transfer.CatalogFingerprint,
                transfer.CreatedAt,
                transfer.UploadStartedAt,
                transfer.UploadedAt,
                transfer.ExpiresAt,
                transfer.Uploaded,
                File.Exists(transfer.Path),
                transfer.RoomDetached,
                transfer.RoomDetachedAt,
                transfer.RecipientAccessBlocked,
                transfer.RecipientAccessBlockedAt,
                transfer.RecipientAccessBlockReason,
                transfer.Recipients.Count,
                states.GetValueOrDefault(TransferRecipientState.Pending),
                states.GetValueOrDefault(TransferRecipientState.Downloaded),
                states.GetValueOrDefault(TransferRecipientState.Declined),
                states.GetValueOrDefault(TransferRecipientState.RoomClosed),
                states.GetValueOrDefault(TransferRecipientState.AdminBlocked),
                states.GetValueOrDefault(TransferRecipientState.DeliveryFailed),
                transfer.DeletionPending,
                transfer.RemovalRequestedAt,
                transfer.RemovalReason?.ToString() ?? "",
                transfer.DeletionAttempts,
                transfer.LastDeletionError,
                transfer.LastFailure);
        }
    }

    private TransferRemovalResult RequestRemoval(string id, TransferRemovalReason reason)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return TransferRemovalResult.NotFound;
        CancellationTokenSource? uploadCancellation;
        var newlyRequested = false;
        var uploadInProgress = false;
        lock (transfer.Gate)
        {
            if (transfer.Removed) return TransferRemovalResult.NotFound;
            if (!transfer.RemovalRequested)
            {
                transfer.RemovalRequested = true;
                transfer.RemovalRequestedAt = DateTimeOffset.UtcNow;
                transfer.RemovalReason = reason;
                transfer.DeletionPending = true;
                transfer.UploadToken = "";
                foreach (var recipient in transfer.Recipients.Values.Where(value =>
                             value.State == TransferRecipientState.Pending))
                {
                    recipient.State = reason == TransferRemovalReason.AdminDeleted
                        ? TransferRecipientState.AdminBlocked
                        : TransferRecipientState.RoomClosed;
                    recipient.HandledAt = transfer.RemovalRequestedAt;
                    recipient.Token = "";
                }
                newlyRequested = true;
            }
            uploadCancellation = transfer.UploadCancellation;
            uploadInProgress = transfer.UploadInProgress;
        }

        try { uploadCancellation?.Cancel(); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not signal upload cancellation for transfer {TransferId}", id);
        }
        if (newlyRequested)
            PublishEvent("removal-requested-" + reason.ToString().ToLowerInvariant(), transfer);
        if (uploadInProgress)
        {
            if (newlyRequested) PublishEvent("deletion-deferred-upload-active", transfer);
            return TransferRemovalResult.Deferred;
        }
        return TryFinalizeRemoval(transfer);
    }

    private TransferRemovalResult TryFinalizeRemoval(Transfer transfer)
    {
        lock (transfer.Gate)
        {
            if (transfer.Removed) return TransferRemovalResult.NotFound;
            if (transfer.UploadInProgress || transfer.DeletionAttemptInProgress)
                return TransferRemovalResult.Deferred;
            transfer.DeletionAttemptInProgress = true;
            transfer.DeletionAttempts++;
            transfer.LastDeletionAttemptAt = DateTimeOffset.UtcNow;
        }

        Exception? deletionFailure = null;
        try
        {
            if (File.Exists(transfer.Path)) File.Delete(transfer.Path);
            if (File.Exists(transfer.Path)) throw new IOException("The transfer package still exists after deletion.");
        }
        catch (Exception exception)
        {
            deletionFailure = exception;
        }

        if (deletionFailure is not null)
        {
            var firstFailure = false;
            lock (transfer.Gate)
            {
                transfer.DeletionAttemptInProgress = false;
                transfer.DeletionPending = true;
                firstFailure = transfer.LastDeletionError.Length == 0;
                transfer.LastDeletionError = CleanAuditText(deletionFailure.Message, 240);
            }
            logger.LogError(deletionFailure,
                "Deletion attempt {Attempt} failed for transfer {TransferId}; it remains tombstoned for retry",
                transfer.DeletionAttempts, transfer.Id);
            if (firstFailure) PublishEvent("deletion-failed", transfer, transfer.LastDeletionError);
            return TransferRemovalResult.Deferred;
        }

        TransferAdminDto removedSnapshot;
        lock (transfer.Gate)
        {
            transfer.DeletionAttemptInProgress = false;
            transfer.DeletionPending = false;
            transfer.Removed = true;
            transfer.UploadCancellation?.Dispose();
            transfer.UploadCancellation = null;
            removedSnapshot = ToAdminDto(transfer);
        }

        var removed = false;
        lock (reservationGate)
        {
            removed = transfers.TryRemove(new KeyValuePair<string, Transfer>(transfer.Id, transfer));
            if (removed)
            {
                reservedBytes -= transfer.Size;
                activeTransferCount--;
                DecrementReservation(senderReservations, transfer.SenderId);
                DecrementReservation(roomReservations, transfer.RoomCode);
            }
        }
        if (!removed) return TransferRemovalResult.NotFound;

        logger.LogInformation("Transfer {TransferId} removed: {RemovalReason}",
            transfer.Id, transfer.RemovalReason ?? TransferRemovalReason.Expired);
        PublishEvent("removed-" + (transfer.RemovalReason ?? TransferRemovalReason.Expired)
            .ToString().ToLowerInvariant(), removedSnapshot);
        return TransferRemovalResult.Removed;
    }

    private static void DecrementReservation(Dictionary<string, int> reservations, string key)
    {
        if (!reservations.TryGetValue(key, out var count)) return;
        if (count <= 1) reservations.Remove(key);
        else reservations[key] = count - 1;
    }

    private static string GetStatus(Transfer transfer)
    {
        if (transfer.Removed) return "removed";
        if (transfer.RemovalRequested || transfer.DeletionPending) return "deletion-pending";
        if (transfer.ExpiresAt <= DateTimeOffset.UtcNow) return "expired";
        if (transfer.RecipientAccessBlocked) return "blocked";
        if (transfer.RoomDetached) return "room-detached";
        if (transfer.Uploaded && transfer.Recipients.Values.All(value =>
                value.State != TransferRecipientState.Pending)) return "completed";
        if (transfer.Uploaded) return "available";
        if (transfer.UploadInProgress) return "uploading";
        if (transfer.LastFailure.Length > 0) return "upload-failed";
        return "awaiting-upload";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(CleanupInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            RetryStartupOrphans();
            foreach (var transfer in transfers.Values)
            {
                if (transfer.ExpiresAt <= DateTimeOffset.UtcNow)
                    _ = RequestRemoval(transfer.Id, TransferRemovalReason.Expired);
                else if (transfer.DeletionPending && !transfer.UploadInProgress)
                    _ = TryFinalizeRemoval(transfer);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var transfer in transfers.Values)
            _ = RequestRemoval(transfer.Id, TransferRemovalReason.Shutdown);

        var deadline = DateTimeOffset.UtcNow.Add(ShutdownCleanupWindow);
        while (!transfers.IsEmpty && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            foreach (var transfer in transfers.Values.Where(value => !value.UploadInProgress))
                _ = TryFinalizeRemoval(transfer);
            try { await Task.Delay(100, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
        if (!transfers.IsEmpty)
            logger.LogWarning(
                "Relay stopped with {Count} tombstoned transfer file(s); the startup sweep will retry them",
                transfers.Count);
        await base.StopAsync(cancellationToken);
    }

    private void SweepStartupOrphans()
    {
        var found = 0;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.pmp"))
        {
            found++;
            try
            {
                File.Delete(path);
                deleted++;
                logger.LogInformation("Startup transfer sweep removed orphan package {FileName}",
                    Path.GetFileName(path));
            }
            catch (Exception exception)
            {
                startupOrphans[path] = 0;
                logger.LogError(exception,
                    "Startup transfer sweep could not remove orphan package {FileName}; it will be retried",
                    Path.GetFileName(path));
            }
        }
        logger.LogInformation(
            "Startup transfer sweep complete: found={Found}, deleted={Deleted}, pendingRetry={Pending}",
            found, deleted, startupOrphans.Count);
    }

    private void RetryStartupOrphans()
    {
        foreach (var pair in startupOrphans)
        {
            try
            {
                if (File.Exists(pair.Key)) File.Delete(pair.Key);
                startupOrphans.TryRemove(pair.Key, out _);
                logger.LogInformation("Removed deferred startup orphan package {FileName} after {Attempts} retry(s)",
                    Path.GetFileName(pair.Key), pair.Value + 1);
            }
            catch (Exception exception)
            {
                startupOrphans[pair.Key] = pair.Value + 1;
                logger.LogWarning(exception,
                    "Deferred startup orphan package {FileName} still cannot be removed (attempt {Attempt})",
                    Path.GetFileName(pair.Key), pair.Value + 1);
            }
        }
    }

    public static string ComputeModNameHash(string value)
    {
        var normalized = WhitespaceRegex().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ")
            .ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string NormalizeHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clean = value.Trim();
        return clean.Length == 64 && clean.All(Uri.IsHexDigit) ? clean.ToUpperInvariant() : "";
    }

    private static string CleanName(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();
        return clean.Length == 0 ? "Animation mod" : clean;
    }

    private static string CleanDisplayName(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(80).ToArray()).Trim();
        return clean.Length == 0 ? "Player" : clean;
    }

    private static string CleanCode(string value) =>
        new(value.Where(char.IsLetterOrDigit).Take(8).Select(char.ToUpperInvariant).ToArray());

    private static string CleanAuditText(string value, int maximumLength) =>
        new string(value.Where(character => !char.IsControl(character)).Take(maximumLength).ToArray()).Trim();

    private static string RandomToken(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    private static bool FixedEquals(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private TransferSharingBanDto? FindMatchingBan(Transfer transfer) =>
        moderation.FindMatchingBan(transfer.Sha256, transfer.CatalogFingerprint, transfer.ModNameHash);

    private void RecordRejectedAttempt(
        string packageSha256,
        string catalogFingerprint,
        string modNameHash,
        TransferSharingBanDto ban)
    {
        var note = BanAuditNote(ban);
        try
        {
            moderation.RecordAuditEvent(new TransferAuditEventWrite(
                "",
                "upload-rejected-before-body",
                DateTimeOffset.UtcNow,
                packageSha256,
                catalogFingerprint,
                modNameHash,
                note));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not persist the rejected transfer audit event");
        }
        logger.LogWarning(
            "Rejected transfer before upload body: packageSha256={PackageSha256}, " +
            "catalogFingerprint={CatalogFingerprint}, modNameHash={ModNameHash}, {Ban}",
            packageSha256, catalogFingerprint, modNameHash, note);
    }

    private void PublishEvent(string eventType, Transfer transfer, string note = "") =>
        PublishEvent(eventType, ToAdminDto(transfer), note);

    private void PublishEvent(string eventType, TransferAdminDto snapshot, string note = "")
    {
        adminEvents.Publish(eventType, snapshot);
        try
        {
            moderation.RecordAuditEvent(new TransferAuditEventWrite(
                snapshot.TransferId,
                eventType,
                DateTimeOffset.UtcNow,
                snapshot.Sha256,
                snapshot.CatalogFingerprint,
                snapshot.ModNameHash,
                CleanAuditText(note, 240)));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not persist transfer audit event {EventType} for {TransferId}",
                eventType, snapshot.TransferId);
        }
    }

    private static string BanAuditNote(TransferSharingBanDto ban) =>
        $"ban={ban.Id}; scope={ban.Scope}; reason={ban.ReasonCode}; note={CleanAuditText(ban.Note, 160)}";

    public sealed class Transfer(
        string id,
        string modName,
        string modNameHash,
        string roomCode,
        string senderId,
        string senderName,
        long size,
        string sha256,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string uploadToken,
        string path,
        Dictionary<string, TransferRecipient> recipients,
        string catalogFingerprint)
    {
        public object Gate { get; } = new();
        public string Id { get; } = id;
        public string ModName { get; } = modName;
        public string ModNameHash { get; } = modNameHash;
        public string RoomCode { get; } = roomCode;
        public string SenderId { get; } = senderId;
        public string SenderName { get; } = senderName;
        public long Size { get; } = size;
        public string Sha256 { get; } = sha256;
        public string CatalogFingerprint { get; } = catalogFingerprint;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string UploadToken { get; set; } = uploadToken;
        public string Path { get; } = path;
        public Dictionary<string, TransferRecipient> Recipients { get; } = recipients;
        public DateTimeOffset? UploadStartedAt { get; set; }
        public DateTimeOffset? UploadedAt { get; set; }
        public DateTimeOffset? RoomDetachedAt { get; set; }
        public DateTimeOffset? RecipientAccessBlockedAt { get; set; }
        public DateTimeOffset? RemovalRequestedAt { get; set; }
        public DateTimeOffset? LastDeletionAttemptAt { get; set; }
        public string RecipientAccessBlockReason { get; set; } = "";
        public string LastFailure { get; set; } = "";
        public string LastDeletionError { get; set; } = "";
        public CancellationTokenSource? UploadCancellation { get; set; }
        public TransferRemovalReason? RemovalReason { get; set; }
        public int DeletionAttempts { get; set; }
        public bool UploadInProgress { get; set; }
        public bool Uploaded { get; set; }
        public bool RoomDetached { get; set; }
        public bool RecipientAccessBlocked { get; set; }
        public bool RemovalRequested { get; set; }
        public bool DeletionPending { get; set; }
        public bool DeletionAttemptInProgress { get; set; }
        public bool Removed { get; set; }
    }

    public sealed class TransferRecipient(string token)
    {
        public string Token { get; set; } = token;
        public TransferRecipientState State { get; set; }
        public DateTimeOffset? HandledAt { get; set; }
    }
}

public enum TransferRecipientState
{
    Pending,
    Downloaded,
    Declined,
    RoomClosed,
    AdminBlocked,
    DeliveryFailed
}

public enum TransferRemovalReason
{
    Expired,
    StoragePressure,
    AdminDeleted,
    Shutdown,
    UploadCleanupFailure
}

public enum TransferRemovalResult
{
    NotFound,
    Removed,
    Deferred
}

public enum TransferBlockResult
{
    NotFound,
    Blocked,
    AlreadyBlocked
}

public sealed record TransferUploadDto(
    string TransferId,
    string UploadToken,
    int PendingRecipients = 0,
    int AlreadyReceived = 0);

public sealed record ModTransferOfferDto(
    string TransferId,
    string ModName,
    string SenderName,
    long Size,
    string Sha256,
    string DownloadToken,
    DateTimeOffset ExpiresAt,
    string CatalogFingerprint = "");

public sealed record TransferAdminDto(
    string TransferId,
    string Status,
    string ModName,
    string ModNameHash,
    string SenderName,
    string RoomCode,
    long Size,
    string Sha256,
    string CatalogFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UploadStartedAt,
    DateTimeOffset? UploadedAt,
    DateTimeOffset ExpiresAt,
    bool Uploaded,
    bool FileAvailable,
    bool RoomDetached,
    DateTimeOffset? RoomDetachedAt,
    bool RecipientAccessBlocked,
    DateTimeOffset? RecipientAccessBlockedAt,
    string RecipientAccessBlockReason,
    int RecipientCount,
    int PendingRecipients,
    int DownloadedRecipients,
    int DeclinedRecipients,
    int RoomClosedRecipients,
    int AdminBlockedRecipients,
    int DeliveryFailedRecipients,
    bool DeletionPending,
    DateTimeOffset? RemovalRequestedAt,
    string RemovalReason,
    int DeletionAttempts,
    string LastDeletionError,
    string LastFailure);

public sealed record TransferBlockRequest(string? Reason);

public sealed record TransferDownloadHandle(TransferStore.Transfer Transfer, FileStream Stream);

public sealed class TransferSharingBlockedException(TransferBanReasonCode reasonCode)
    : InvalidOperationException(reasonCode == TransferBanReasonCode.CreatorOptOut
        ? "This animation creator has opted out of relay sharing."
        : "This animation package is not allowed on the relay.")
{
    public TransferBanReasonCode ReasonCode { get; } = reasonCode;
}

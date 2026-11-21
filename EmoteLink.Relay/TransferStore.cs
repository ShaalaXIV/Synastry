using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EmoteLink.Relay;

/// <summary>
/// Room-to-room animation transfers. Recipients have <see cref="OfferLifetime"/> to accept an offer
/// (and the sender as long to finish the upload), while every uploaded package is kept on disk for
/// the retention period (24 hours by default) so moderators can review it. Packages are stored once
/// per SHA-256: a repeat send of the same bytes is offered straight from the stored copy, with no
/// upload. Retained sends are recorded in SQLite and restored after a relay restart.
/// </summary>
public sealed partial class TransferStore : BackgroundService
{
    public const string CapabilityHeaderName = "X-Synastry-Transfer-Token";
    public const string AdminActorHeaderName = "X-Synastry-Admin-Actor";
    public const long MaximumBytes = 75L * 1024 * 1024;
    public const long MaximumStorageBytes = 25L * 1024 * 1024 * 1024;
    // Active transfers are the ones still awaiting their upload or still offering the package to
    // someone. A send that has finished offering stays retained for review without counting here.
    public const int MaximumActiveTransfers = 4_096;
    public const int MaximumActiveTransfersPerSender = 32;
    public const int MaximumActiveTransfersPerRoom = 128;
    public const int MaximumRetainedTransfers = 20_000;
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, Transfer> transfers = new();
    // Stored packages by SHA-256, guarded by reservationGate like the counters below.
    private readonly Dictionary<string, StoredPackage> packages = new(StringComparer.OrdinalIgnoreCase);
    // Package files whose deletion failed, with the bytes they still occupy.
    private readonly ConcurrentDictionary<string, long> pendingPackageDeletions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> startupOrphans = new(StringComparer.OrdinalIgnoreCase);
    private readonly object reservationGate = new();
    private readonly Dictionary<string, int> senderReservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> roomReservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TransferStore> logger;
    private readonly AdminTransferEventBroker adminEvents;
    private readonly ITransferModerationRepository moderation;
    private readonly ITransferRetentionRepository retention;
    private readonly TimeSpan retentionPeriod;
    private readonly string root;
    // Upload reservations, stored packages and package files still waiting to be deleted.
    private long reservedBytes;
    private int activeTransferCount;

    public TransferStore(
        ILogger<TransferStore> logger,
        AdminTransferEventBroker adminEvents,
        ITransferModerationRepository moderation,
        ITransferRetentionRepository retention)
        : this(logger, adminEvents, moderation, retention, ReadRetentionPeriod())
    {
    }

    internal TransferStore(
        ILogger<TransferStore> logger,
        AdminTransferEventBroker adminEvents,
        ITransferModerationRepository moderation,
        ITransferRetentionRepository retention,
        TimeSpan retentionPeriod)
    {
        this.logger = logger;
        this.adminEvents = adminEvents;
        this.moderation = moderation;
        this.retention = retention;
        this.retentionPeriod = retentionPeriod;
        root = retention.PackageDirectory;
        Directory.CreateDirectory(root);
        RestoreRetainedTransfers();
        SweepLegacyTemporaryPackages();
    }

    public TimeSpan RetentionPeriod => retentionPeriod;

    public TransferBeginResult Begin(string roomCode, string senderId, string senderName, string modName,
        long size, string sha256, IReadOnlyList<string> recipients, string catalogFingerprint = "",
        int alreadyReceived = 0, bool allowStoredCopy = false)
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
            while (transfers.Count >= MaximumRetainedTransfers)
                if (!EvictOldestFinishedTransfer())
                    throw new InvalidOperationException("The relay has too many active transfers. Try again shortly.");

            var stored = allowStoredCopy ? FindReusablePackage(hash, size) : null;
            // Normal reservation checks are O(1). The bounded scan below occurs only
            // when the global byte ceiling is actually under pressure.
            while (stored is null && reservedBytes + size > MaximumStorageBytes)
                if (!EvictOldestPackage())
                    throw new InvalidOperationException("The relay transfer storage is currently full.");

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
                now.Add(OfferLifetime),
                stored is null ? RandomToken(32) : "",
                Path.Combine(root, id + ".part"),
                recipientStates,
                cleanFingerprint);
            transfer.HoldsActiveSlot = true;
            if (stored is null)
            {
                transfer.ReservedBytes = size;
            }
            else
            {
                // The exact bytes are already on the relay, so this send has nothing to upload.
                transfer.Package = stored;
                transfer.FromStoredCopy = true;
                transfer.Uploaded = true;
                transfer.UploadedAt = now;
                transfer.RetainedUntil = RetentionEnd(transfer, now);
            }
            if (!transfers.TryAdd(id, transfer)) throw new InvalidOperationException("Could not create the transfer.");
            stored?.TransferIds.Add(id);
            reservedBytes += transfer.ReservedBytes;
            activeTransferCount++;
            senderReservations[senderId] = senderReservations.GetValueOrDefault(senderId) + 1;
            roomReservations[cleanRoomCode] = roomReservations.GetValueOrDefault(cleanRoomCode) + 1;
        }

        logger.LogInformation(
            "Transfer {TransferId} reserved: modNameHash={ModNameHash}, packageSha256={PackageSha256}, " +
            "catalogFingerprint={CatalogFingerprint}, room={RoomCode}, recipients={RecipientCount}, size={Size}, " +
            "fromStoredCopy={FromStoredCopy}",
            transfer.Id, transfer.ModNameHash, transfer.Sha256, transfer.CatalogFingerprint,
            transfer.RoomCode, transfer.Recipients.Count, transfer.Size, transfer.FromStoredCopy);
        PublishEvent(transfer.FromStoredCopy ? "created-from-stored-copy" : "created", transfer);
        IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> offers = [];
        if (transfer.FromStoredCopy)
            lock (transfer.Gate)
                offers = PendingOffers(transfer);
        return new TransferBeginResult(
            new TransferUploadDto(transfer.Id, transfer.UploadToken, recipientIds.Count, alreadyReceived,
                transfer.FromStoredCopy),
            offers);
    }

    public Transfer? GetUpload(string id, string token)
    {
        if (!transfers.TryGetValue(id, out var transfer) || transfer.Uploaded) return null;
        if (transfer.OfferExpiresAt <= DateTimeOffset.UtcNow)
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
            var remaining = transfer.OfferExpiresAt - DateTimeOffset.UtcNow;
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
            expired = transfer.OfferExpiresAt <= DateTimeOffset.UtcNow;
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
            if (File.Exists(transfer.UploadPath)) File.Delete(transfer.UploadPath);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Could not remove partial upload for transfer {TransferId}; cleanup is now deferred", transfer.Id);
            _ = RequestRemoval(transfer.Id, TransferRemovalReason.UploadCleanupFailure);
            return;
        }

        // The reservation intentionally survives an ordinary network failure so the
        // authenticated sender may retry, but never beyond the offer's expiry.
        logger.LogWarning("Transfer {TransferId} upload aborted: {Reason}", transfer.Id, transfer.LastFailure);
        PublishEvent("upload-failed", transfer, transfer.LastFailure);
    }

    /// <summary>
    /// Moves a verified upload into the package store and returns the offers to deliver. The upload
    /// stays marked in progress until the package is committed, so a removal requested meanwhile is
    /// deferred rather than deleting the file from under the move. A failed move throws, and the
    /// caller aborts the upload as for any other failure.
    /// </summary>
    public IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> FinishUpload(Transfer transfer)
    {
        bool abandoned;
        lock (transfer.Gate)
            abandoned = transfer.RemovalRequested || transfer.OfferExpiresAt <= DateTimeOffset.UtcNow ||
                        !transfers.ContainsKey(transfer.Id);
        if (!abandoned) CommitPackage(transfer);

        CancellationTokenSource? cancellation;
        IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> offers = [];
        var mustRemove = abandoned;
        lock (transfer.Gate)
        {
            transfer.UploadInProgress = false;
            cancellation = transfer.UploadCancellation;
            transfer.UploadCancellation = null;
            if (transfer.RemovalRequested || !transfers.ContainsKey(transfer.Id))
                mustRemove = true;
            if (!mustRemove)
            {
                transfer.Uploaded = true;
                transfer.UploadedAt = DateTimeOffset.UtcNow;
                transfer.RetainedUntil = RetentionEnd(transfer, transfer.UploadedAt.Value);
                offers = PendingOffers(transfer);
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
        ReleaseActiveSlotIfFinished(transfer);
        return offers;
    }

    private void CommitPackage(Transfer transfer)
    {
        lock (reservationGate)
        {
            if (packages.TryGetValue(transfer.Sha256, out var existing) &&
                (existing.Withdrawn || existing.Size != transfer.Size || !File.Exists(existing.Path)))
                existing = null;
            if (existing is not null)
            {
                // Another send already stored these exact bytes; keep that copy and drop this one.
                try
                {
                    if (File.Exists(transfer.UploadPath)) File.Delete(transfer.UploadPath);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Could not remove the duplicate upload for transfer {TransferId}; the startup sweep will",
                        transfer.Id);
                }
                reservedBytes -= transfer.ReservedBytes;
            }
            else
            {
                var path = Path.Combine(root, $"{transfer.Sha256}-{RandomToken(8).ToLowerInvariant()}.pmp");
                File.Move(transfer.UploadPath, path);
                existing = new StoredPackage(transfer.Sha256, transfer.Size, path);
                // A withdrawn copy keeps serving its remaining sends until they end; new sends use this one.
                packages[transfer.Sha256] = existing;
                // The upload's reservation now accounts for the stored package's bytes.
            }
            transfer.ReservedBytes = 0;
            existing.TransferIds.Add(transfer.Id);
            transfer.Package = existing;
        }
    }

    public bool CanDeliverOffer(string id, string connectionId)
    {
        if (!transfers.TryGetValue(id, out var transfer) || transfer.OfferExpiresAt <= DateTimeOffset.UtcNow)
            return false;
        lock (transfer.Gate)
            return transfer.Uploaded && !transfer.RemovalRequested && !transfer.RoomDetached &&
                   !transfer.RecipientAccessBlocked && transfer.Package is { } package && File.Exists(package.Path) &&
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
        if (!changed) return;
        PublishEvent("offer-delivery-failed", transfer, CleanAuditText(reason, 160));
        ReleaseActiveSlotIfFinished(transfer);
    }

    public TransferDownloadHandle? OpenDownload(string id, string token)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return null;
        // An expired offer ends the recipient's access; the package itself stays retained for review.
        if (transfer.OfferExpiresAt <= DateTimeOffset.UtcNow) return null;
        if (FindMatchingBan(transfer) is { } matchingBan)
        {
            _ = BlockRecipientAccess(id, BanAuditNote(matchingBan));
            return null;
        }
        lock (transfer.Gate)
        {
            if (!transfer.Uploaded || transfer.RoomDetached || transfer.RecipientAccessBlocked ||
                transfer.RemovalRequested || transfer.Package is not { } package || !File.Exists(package.Path))
                return null;
            if (!transfer.Recipients.Values.Any(recipient =>
                    recipient.State == TransferRecipientState.Pending && FixedEquals(recipient.Token, token)))
                return null;
            try
            {
                return new TransferDownloadHandle(
                    transfer,
                    new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
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
        if (!transfers.TryGetValue(id, out var transfer) || transfer.RetainedUntil <= DateTimeOffset.UtcNow)
            return null;
        FileStream stream;
        lock (transfer.Gate)
        {
            if (!transfer.Uploaded || transfer.RemovalRequested || transfer.Package is not { } package ||
                !File.Exists(package.Path))
                return null;
            try
            {
                stream = new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
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

    // A download that began just before its offer expired still counts when it completes.
    public bool MarkDownloaded(string id, string connectionId) =>
        MarkRecipientHandled(id, connectionId, TransferRecipientState.Downloaded, acceptExpired: true);

    public void Decline(string id, string connectionId) =>
        _ = MarkRecipientHandled(id, connectionId, TransferRecipientState.Declined, acceptExpired: false);

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
            if (!changed) continue;
            PublishEvent("recipient-detached", transfer);
            ReleaseActiveSlotIfFinished(transfer);
        }
        return detached;
    }

    private bool MarkRecipientHandled(string id, string connectionId, TransferRecipientState state,
        bool acceptExpired)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return false;
        lock (transfer.Gate)
        {
            if (!transfer.Recipients.TryGetValue(connectionId, out var recipient) ||
                !(recipient.State == TransferRecipientState.Pending ||
                  acceptExpired && recipient.State == TransferRecipientState.Expired))
                return false;
            recipient.State = state;
            recipient.HandledAt = DateTimeOffset.UtcNow;
            recipient.Token = "";
        }
        logger.LogInformation("Transfer {TransferId} recipient state changed to {RecipientState}", id, state);
        PublishEvent(state == TransferRecipientState.Downloaded ? "recipient-downloaded" : "recipient-declined",
            transfer);
        ReleaseActiveSlotIfFinished(transfer);
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
                "Transfer {TransferId} detached from closed room {RoomCode}; evidence retained until {RetainedUntil}",
                transfer.Id, transfer.RoomCode, transfer.RetainedUntil);
            PublishEvent("room-detached", transfer);
            ReleaseActiveSlotIfFinished(transfer);
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
        if (newlyBlocked) ReleaseActiveSlotIfFinished(transfer);
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

    /// <summary>
    /// Deletes the transfer's package from relay storage now. Every send of the same bytes shares
    /// that one stored copy, so they all end with it.
    /// </summary>
    public TransferRemovalResult AdminDelete(string id, string? administratorActor)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return TransferRemovalResult.NotFound;
        AuditAdminAction(transfer, "admin-delete-requested", administratorActor, "moderation deletion requested");
        List<string> sharing = [];
        lock (reservationGate)
        {
            if (transfer.Package is { } package)
            {
                package.Withdrawn = true;
                sharing = package.TransferIds.Where(other => other != id).ToList();
            }
        }
        var result = RequestRemoval(id, TransferRemovalReason.AdminDeleted);
        foreach (var other in sharing) _ = RequestRemoval(other, TransferRemovalReason.AdminDeleted);
        return result;
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
                transfer.RetainedUntil,
                transfer.Uploaded,
                transfer.Package is { } package && File.Exists(package.Path),
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
                transfer.LastFailure,
                transfer.OfferExpiresAt,
                transfer.FromStoredCopy,
                states.GetValueOrDefault(TransferRecipientState.Expired),
                transfer.Package?.TransferIds.Count ?? 0,
                transfer.Restored);
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

        // Only a partial upload belongs to the transfer itself. A stored package is shared and is
        // deleted below once no remaining send uses it.
        Exception? deletionFailure = null;
        try
        {
            if (File.Exists(transfer.UploadPath)) File.Delete(transfer.UploadPath);
            if (File.Exists(transfer.UploadPath))
                throw new IOException("The partial transfer upload still exists after deletion.");
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
        StoredPackage? unused = null;
        lock (reservationGate)
        {
            removed = transfers.TryRemove(new KeyValuePair<string, Transfer>(transfer.Id, transfer));
            if (removed)
            {
                reservedBytes -= transfer.ReservedBytes;
                transfer.ReservedBytes = 0;
                ReleaseActiveSlot(transfer);
                if (transfer.Package is { } package && package.TransferIds.Remove(transfer.Id) &&
                    package.TransferIds.Count == 0)
                {
                    if (packages.TryGetValue(package.Sha256, out var current) && ReferenceEquals(current, package))
                        packages.Remove(package.Sha256);
                    unused = package;
                }
            }
        }
        if (!removed) return TransferRemovalResult.NotFound;

        if (unused is not null) DeletePackageFile(unused.Path, unused.Size);
        try
        {
            retention.DeleteRetainedTransfer(transfer.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not delete the retained record of transfer {TransferId}", transfer.Id);
        }
        logger.LogInformation("Transfer {TransferId} removed: {RemovalReason}",
            transfer.Id, transfer.RemovalReason ?? TransferRemovalReason.Expired);
        PublishEvent("removed-" + (transfer.RemovalReason ?? TransferRemovalReason.Expired)
            .ToString().ToLowerInvariant(), removedSnapshot);
        return TransferRemovalResult.Removed;
    }

    private void DeletePackageFile(string path, long size)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path)) throw new IOException("The stored package still exists after deletion.");
            lock (reservationGate) reservedBytes -= size;
            logger.LogInformation("Deleted stored transfer package {FileName}", Path.GetFileName(path));
        }
        catch (Exception exception)
        {
            pendingPackageDeletions[path] = size;
            logger.LogError(exception,
                "Could not delete stored transfer package {FileName}; it will be retried", Path.GetFileName(path));
        }
    }

    private void RetryPackageDeletions()
    {
        foreach (var pair in pendingPackageDeletions)
        {
            try
            {
                if (File.Exists(pair.Key)) File.Delete(pair.Key);
                if (File.Exists(pair.Key)) continue;
                if (pendingPackageDeletions.TryRemove(pair.Key, out var size))
                    lock (reservationGate) reservedBytes -= size;
                logger.LogInformation("Deleted stored transfer package {FileName} on retry", Path.GetFileName(pair.Key));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Stored transfer package {FileName} still cannot be deleted",
                    Path.GetFileName(pair.Key));
            }
        }
    }

    /// <summary>
    /// Ends whichever stored package would expire first (with every send that shares it) to make room.
    /// A package already being removed is skipped, so the caller's loop always makes progress.
    /// Must be called with reservationGate held.
    /// </summary>
    private bool EvictOldestPackage()
    {
        StoredPackage? candidate = null;
        var candidateEnd = DateTimeOffset.MaxValue;
        foreach (var package in packages.Values)
        {
            var sends = package.TransferIds
                .Select(id => transfers.TryGetValue(id, out var transfer) ? transfer : null)
                .ToList();
            if (sends.Count == 0 || sends.Any(transfer => transfer is null || transfer.RemovalRequested)) continue;
            var end = sends.Max(transfer => transfer!.RetainedUntil);
            if (end >= candidateEnd) continue;
            candidate = package;
            candidateEnd = end;
        }
        if (candidate is null) return false;
        candidate.Withdrawn = true;
        foreach (var id in candidate.TransferIds.ToList())
            _ = RequestRemoval(id, TransferRemovalReason.StoragePressure);
        return true;
    }

    /// <summary>Ends the oldest send that has finished offering. Must be called with reservationGate held.</summary>
    private bool EvictOldestFinishedTransfer()
    {
        var candidate = transfers.Values
            .Where(value => !value.HoldsActiveSlot && !value.RemovalRequested)
            .OrderBy(value => value.RetainedUntil)
            .FirstOrDefault();
        if (candidate is null) return false;
        _ = RequestRemoval(candidate.Id, TransferRemovalReason.RetentionLimit);
        return true;
    }

    /// <summary>
    /// The stored copy a new send of these exact bytes can use, or null when it must upload. A copy
    /// that a moderator blocked or deleted is never reused. Must be called with reservationGate held.
    /// </summary>
    private StoredPackage? FindReusablePackage(string sha256, long size)
    {
        if (!packages.TryGetValue(sha256, out var package) || package.Withdrawn || package.Size != size ||
            !File.Exists(package.Path))
            return null;
        foreach (var id in package.TransferIds)
        {
            if (!transfers.TryGetValue(id, out var other)) continue;
            lock (other.Gate)
                if (other.RecipientAccessBlocked || other.RemovalReason == TransferRemovalReason.AdminDeleted)
                    return null;
        }
        return package;
    }

    private DateTimeOffset RetentionEnd(Transfer transfer, DateTimeOffset storedAt)
    {
        var end = storedAt.Add(retentionPeriod);
        return end > transfer.OfferExpiresAt ? end : transfer.OfferExpiresAt;
    }

    private static IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> PendingOffers(Transfer transfer) =>
        transfer.RoomDetached || transfer.RecipientAccessBlocked
            ? []
            : transfer.Recipients
                .Where(pair => pair.Value.State == TransferRecipientState.Pending)
                .Select(pair => (pair.Key,
                    new ModTransferOfferDto(transfer.Id, transfer.ModName, transfer.SenderName, transfer.Size,
                        transfer.Sha256, pair.Value.Token, transfer.OfferExpiresAt, transfer.CatalogFingerprint)))
                .ToList();

    /// <summary>
    /// Frees the send's active-transfer slot once it no longer awaits an upload or offers the package
    /// to anyone. A finished send never becomes active again.
    /// </summary>
    private void ReleaseActiveSlotIfFinished(Transfer transfer)
    {
        bool finished;
        lock (transfer.Gate) finished = !IsActive(transfer, DateTimeOffset.UtcNow);
        if (!finished) return;
        lock (reservationGate) ReleaseActiveSlot(transfer);
    }

    /// <summary>Must be called with reservationGate held.</summary>
    private void ReleaseActiveSlot(Transfer transfer)
    {
        if (!transfer.HoldsActiveSlot) return;
        transfer.HoldsActiveSlot = false;
        activeTransferCount--;
        DecrementReservation(senderReservations, transfer.SenderId);
        DecrementReservation(roomReservations, transfer.RoomCode);
    }

    private static bool IsActive(Transfer transfer, DateTimeOffset now)
    {
        if (transfer.Removed || transfer.RemovalRequested) return false;
        if (!transfer.Uploaded) return transfer.OfferExpiresAt > now;
        return !transfer.RoomDetached && !transfer.RecipientAccessBlocked && transfer.OfferExpiresAt > now &&
               transfer.Recipients.Values.Any(value => value.State == TransferRecipientState.Pending);
    }

    private void ExpireOffers(Transfer transfer)
    {
        var changed = false;
        lock (transfer.Gate)
        {
            foreach (var recipient in transfer.Recipients.Values.Where(value =>
                         value.State == TransferRecipientState.Pending))
            {
                recipient.State = TransferRecipientState.Expired;
                recipient.HandledAt = transfer.OfferExpiresAt;
                recipient.Token = "";
                changed = true;
            }
        }
        if (changed) PublishEvent("offers-expired", transfer);
        if (changed || transfer.HoldsActiveSlot) ReleaseActiveSlotIfFinished(transfer);
    }

    private static void DecrementReservation(Dictionary<string, int> reservations, string key)
    {
        if (!reservations.TryGetValue(key, out var count)) return;
        if (count <= 1) reservations.Remove(key);
        else reservations[key] = count - 1;
    }

    private static string GetStatus(Transfer transfer)
    {
        var now = DateTimeOffset.UtcNow;
        if (transfer.Removed) return "removed";
        if (transfer.RemovalRequested || transfer.DeletionPending) return "deletion-pending";
        if (transfer.RetainedUntil <= now) return "expired";
        if (transfer.RecipientAccessBlocked) return "blocked";
        if (transfer.RoomDetached) return "room-detached";
        if (transfer.Uploaded)
            return transfer.OfferExpiresAt > now && transfer.Recipients.Values.Any(value =>
                value.State == TransferRecipientState.Pending)
                ? "available"
                : "completed";
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
            RetryPackageDeletions();
            var now = DateTimeOffset.UtcNow;
            foreach (var transfer in transfers.Values)
            {
                if (transfer.RemovalRequested)
                {
                    if (transfer.DeletionPending && !transfer.UploadInProgress) _ = TryFinalizeRemoval(transfer);
                }
                else if (transfer.Uploaded ? transfer.RetainedUntil <= now : transfer.OfferExpiresAt <= now)
                {
                    _ = RequestRemoval(transfer.Id, TransferRemovalReason.Expired);
                }
                else if (transfer.Uploaded && transfer.OfferExpiresAt <= now)
                {
                    ExpireOffers(transfer);
                }
            }
        }
    }

    // There is deliberately no shutdown cleanup: uploaded packages and their records survive a
    // restart. Sends still awaiting their upload end with their sender's connection, and their
    // partial files are removed by the next startup sweep.

    /// <summary>
    /// Reloads the retained sends recorded in SQLite whose package is still on disk, then deletes every
    /// other package or partial upload in the folder. Restored sends have no live recipients (rooms and
    /// connections do not survive a restart), so they come back detached from their room.
    /// </summary>
    private void RestoreRetainedTransfers()
    {
        IReadOnlyList<RetainedTransferRecord> records;
        try
        {
            records = retention.LoadRetainedTransfers();
        }
        catch (Exception exception)
        {
            // Without the records every package would look orphaned; keep the files for the next start.
            logger.LogError(exception,
                "Could not load retained transfers; stored packages are kept on disk but not offered");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restored = 0;
        var dropped = 0;
        foreach (var record in records)
        {
            var sha = NormalizeHash(record.PackageSha256);
            var path = Path.Combine(root, record.PackageFile);
            var usable = sha.Length == 64 && record.RetainedUntil > now && record.Size is > 0 and <= MaximumBytes &&
                         PackageFileRegex().IsMatch(record.PackageFile) &&
                         record.PackageFile.StartsWith(sha, StringComparison.OrdinalIgnoreCase) &&
                         File.Exists(path) && new FileInfo(path).Length == record.Size &&
                         !transfers.ContainsKey(record.TransferId);
            if (!usable)
            {
                dropped++;
                try { retention.DeleteRetainedTransfer(record.TransferId); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not delete the retained record of transfer {TransferId}",
                        record.TransferId);
                }
                continue;
            }

            // Two files with the same bytes can only come from an interrupted store; the first one wins
            // and the spare is swept below.
            if (!packages.TryGetValue(sha, out var package))
            {
                package = new StoredPackage(sha, record.Size, path);
                packages[sha] = package;
                reservedBytes += record.Size;
            }
            kept.Add(Path.GetFileName(package.Path));

            var transfer = new Transfer(
                record.TransferId,
                CleanName(record.ModName),
                record.ModNameHash,
                CleanCode(record.RoomCode),
                "",
                CleanDisplayName(record.SenderName),
                record.Size,
                sha,
                record.CreatedAt,
                Min(record.CreatedAt.Add(OfferLifetime), now),
                "",
                Path.Combine(root, record.TransferId + ".part"),
                RestoredRecipients(record),
                NormalizeHash(record.CatalogFingerprint))
            {
                Package = package,
                FromStoredCopy = record.FromStoredCopy,
                Restored = true,
                Uploaded = true,
                UploadedAt = record.UploadedAt,
                RetainedUntil = record.RetainedUntil,
                RoomDetached = true,
                RoomDetachedAt = record.RoomDetachedAt ?? now,
                RecipientAccessBlocked = record.AccessBlockedAt is not null,
                RecipientAccessBlockedAt = record.AccessBlockedAt,
                RecipientAccessBlockReason = record.AccessBlockReason
            };
            transfers[transfer.Id] = transfer;
            package.TransferIds.Add(transfer.Id);
            restored++;
            if (!Path.GetFileName(package.Path).Equals(record.PackageFile, StringComparison.OrdinalIgnoreCase))
                PersistRetained(transfer);
        }

        var swept = 0;
        foreach (var path in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(path);
            if (kept.Contains(name) ||
                !(name.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                File.Delete(path);
                swept++;
            }
            catch (Exception exception)
            {
                startupOrphans[path] = 0;
                logger.LogError(exception,
                    "Startup transfer sweep could not remove orphan package {FileName}; it will be retried", name);
            }
        }
        logger.LogInformation(
            "Restored {Restored} retained transfer(s) sharing {Packages} package(s) ({Bytes} bytes); " +
            "dropped {Dropped} expired or missing record(s); swept {Swept} orphan file(s), {Pending} pending retry",
            restored, packages.Count, reservedBytes, dropped, swept, startupOrphans.Count);
    }

    private static Dictionary<string, TransferRecipient> RestoredRecipients(RetainedTransferRecord record)
    {
        var recipients = new Dictionary<string, TransferRecipient>(StringComparer.Ordinal);
        void Add(int count, TransferRecipientState state)
        {
            for (var i = 0; i < Math.Clamp(count, 0, 64); i++)
                recipients["restored-" + recipients.Count] = new TransferRecipient("") { State = state };
        }
        Add(record.Downloaded, TransferRecipientState.Downloaded);
        Add(record.Declined, TransferRecipientState.Declined);
        Add(record.Closed, TransferRecipientState.RoomClosed);
        Add(record.AdminBlocked, TransferRecipientState.AdminBlocked);
        Add(record.DeliveryFailed, TransferRecipientState.DeliveryFailed);
        Add(record.Expired, TransferRecipientState.Expired);
        return recipients;
    }

    /// <summary>Older relays kept packages in the temporary folder; clear any they left behind.</summary>
    private void SweepLegacyTemporaryPackages()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "emotelink-transfers");
        if (!Directory.Exists(legacy) ||
            Path.GetFullPath(legacy).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            return;
        foreach (var path in Directory.EnumerateFiles(legacy, "*.pmp"))
        {
            try { File.Delete(path); }
            catch (Exception exception)
            {
                startupOrphans[path] = 0;
                logger.LogWarning(exception, "Could not remove legacy temporary package {FileName}",
                    Path.GetFileName(path));
            }
        }
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

    private static TimeSpan ReadRetentionPeriod()
    {
        var raw = Environment.GetEnvironmentVariable("EMOTELINK_TRANSFER_RETENTION_MINUTES");
        return int.TryParse(raw, out var minutes) && minutes is >= 1 and <= 7 * 24 * 60
            ? TimeSpan.FromMinutes(minutes)
            : DefaultRetention;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

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

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}-[0-9a-f]{16}\.pmp$")]
    private static partial Regex PackageFileRegex();

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

    private void PublishEvent(string eventType, Transfer transfer, string note = "")
    {
        PersistRetained(transfer);
        PublishEvent(eventType, ToAdminDto(transfer), note);
    }

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

    /// <summary>
    /// Records a stored send so it survives a restart. The write happens under the transfer's gate so
    /// it cannot land after the send's removal has deleted its record.
    /// </summary>
    private void PersistRetained(Transfer transfer)
    {
        lock (transfer.Gate)
        {
            if (!transfer.Uploaded || transfer.Package is not { } package || transfer.RemovalRequested ||
                transfer.Removed)
                return;
            var states = transfer.Recipients.Values.GroupBy(value => value.State)
                .ToDictionary(group => group.Key, group => group.Count());
            try
            {
                retention.SaveRetainedTransfer(new RetainedTransferRecord(
                    transfer.Id,
                    transfer.Sha256,
                    Path.GetFileName(package.Path),
                    transfer.Size,
                    transfer.ModName,
                    transfer.ModNameHash,
                    transfer.CatalogFingerprint,
                    transfer.RoomCode,
                    transfer.SenderName,
                    transfer.CreatedAt,
                    transfer.UploadedAt ?? transfer.CreatedAt,
                    transfer.RetainedUntil,
                    transfer.FromStoredCopy,
                    transfer.RoomDetachedAt,
                    transfer.RecipientAccessBlocked ? transfer.RecipientAccessBlockedAt : null,
                    transfer.RecipientAccessBlockReason,
                    states.GetValueOrDefault(TransferRecipientState.Downloaded),
                    states.GetValueOrDefault(TransferRecipientState.Declined),
                    // A recipient still pending at a restart can no longer be reached.
                    states.GetValueOrDefault(TransferRecipientState.RoomClosed) +
                    states.GetValueOrDefault(TransferRecipientState.Pending),
                    states.GetValueOrDefault(TransferRecipientState.AdminBlocked),
                    states.GetValueOrDefault(TransferRecipientState.DeliveryFailed),
                    states.GetValueOrDefault(TransferRecipientState.Expired)));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not record retained transfer {TransferId}", transfer.Id);
            }
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
        DateTimeOffset offerExpiresAt,
        string uploadToken,
        string uploadPath,
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
        // Recipients may accept, and the sender upload, until the offer expires.
        public DateTimeOffset OfferExpiresAt { get; } = offerExpiresAt;
        // The send and its review copy are removed at this time; set to the retention end once stored.
        public DateTimeOffset RetainedUntil { get; set; } = offerExpiresAt;
        public string UploadToken { get; set; } = uploadToken;
        // Where the upload is written before it is verified and moved into the package store.
        public string UploadPath { get; } = uploadPath;
        // The stored copy this send offers. Set under the store's reservation gate.
        public StoredPackage? Package { get; set; }
        // Bytes reserved for an upload that has not been stored yet. Guarded by the reservation gate.
        public long ReservedBytes { get; set; }
        // Whether the send still counts against the active-transfer limits. Guarded by the reservation gate.
        public bool HoldsActiveSlot { get; set; }
        public bool FromStoredCopy { get; set; }
        public bool Restored { get; set; }
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

    /// <summary>One uploaded package on disk, shared by every retained send of the same bytes.</summary>
    public sealed class StoredPackage(string sha256, long size, string path)
    {
        public string Sha256 { get; } = sha256;
        public long Size { get; } = size;
        public string Path { get; } = path;
        // The retained sends using this copy; the file is deleted when the last one ends.
        // Guarded by the store's reservation gate.
        public HashSet<string> TransferIds { get; } = new(StringComparer.Ordinal);
        // Set when a moderator deletes the package or storage pressure evicts it: no new send may use it.
        public bool Withdrawn { get; set; }
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
    DeliveryFailed,
    Expired
}

public enum TransferRemovalReason
{
    Expired,
    StoragePressure,
    AdminDeleted,
    Shutdown,
    UploadCleanupFailure,
    RetentionLimit
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

/// <summary>
/// The sender's reply. An empty UploadToken means there is nothing to upload: either everyone already
/// has the animation (empty TransferId) or the relay offered its stored copy (FromStoredCopy).
/// </summary>
public sealed record TransferUploadDto(
    string TransferId,
    string UploadToken,
    int PendingRecipients = 0,
    int AlreadyReceived = 0,
    bool FromStoredCopy = false);

public sealed record TransferBeginResult(
    TransferUploadDto Upload,
    IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> Offers);

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
    // When the send and its review copy are removed from the relay.
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
    string LastFailure,
    DateTimeOffset? OfferExpiresAt = null,
    bool FromStoredCopy = false,
    int ExpiredRecipients = 0,
    // How many retained sends share this package; deleting it ends all of them.
    int PackageSendCount = 0,
    bool Restored = false);

public sealed record TransferBlockRequest(string? Reason);

public sealed record TransferDownloadHandle(TransferStore.Transfer Transfer, FileStream Stream);

public sealed class TransferSharingBlockedException(TransferBanReasonCode reasonCode)
    : InvalidOperationException(reasonCode == TransferBanReasonCode.CreatorOptOut
        ? "This animation creator has opted out of relay sharing."
        : "This animation package is not allowed on the relay.")
{
    public TransferBanReasonCode ReasonCode { get; } = reasonCode;
}

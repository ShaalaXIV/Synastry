using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace EmoteLink.Relay;

public sealed class AnimationHub : Hub
{
    private const int PlayDelayMilliseconds = 1500;
    private static readonly TimeSpan EmptyRoomGracePeriod = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, Room> Rooms = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ConnectionRooms = new();
    private static readonly ConcurrentDictionary<string, LocalPresence> ConnectionLocalPresence = new();
    private static readonly ConcurrentDictionary<string, long> LastLocalAnimationTicks = new();
    private static readonly ConcurrentDictionary<string, string> ConnectionCommunityReporterIds = new();
    private static readonly ConcurrentDictionary<string, string> ConnectionCatalogReporterIds = new();
    private static readonly ConcurrentDictionary<string, int> ConnectionCatalogReportCounts = new();
    private static readonly ConcurrentDictionary<string, CatalogCreationBucket> CatalogCreationBuckets = new();
    private const int MaximumCatalogReportsPerConnection = 10_000;
    private const int MaximumNewArtifactsPerPeerBurst = 10_000;
    private const int MaximumCatalogCreationPeerBuckets = 100_000;
    private static readonly TimeSpan NewArtifactPeerRefillPeriod = TimeSpan.FromDays(1);
    private const int MaxMembers = 16;
    private readonly TransferStore transfers;
    private readonly CommunityRoleLabelStore communityRoles;
    private readonly AnimationCatalogStore animationCatalog;
    private readonly RelayStatisticsStore statistics;

    public AnimationHub(TransferStore transfers, CommunityRoleLabelStore communityRoles,
        AnimationCatalogStore animationCatalog, RelayStatisticsStore statistics)
    {
        this.transfers = transfers;
        this.communityRoles = communityRoles;
        this.animationCatalog = animationCatalog;
        this.statistics = statistics;
    }

    public int GetOnlineUserCount() => statistics.GetSnapshot().ActiveUsers;

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        var snapshot = statistics.ConnectionOpened();
        await Clients.All.SendAsync("OnlineUserCountChanged", snapshot.ActiveUsers);
    }

    public async Task<RoomStateDto> CreateRoom(string displayName)
    {
        await LeaveRoom();
        var code = CreateCode();
        var room = new Room(code);
        while (!Rooms.TryAdd(code, room)) { code = CreateCode(); room = new Room(code); }
        statistics.IncrementRoomsGenerated();
        lock (room.Gate)
            room.Members[Context.ConnectionId] = new Member(Context.ConnectionId, CleanName(displayName), true);
        ConnectionRooms[Context.ConnectionId] = code;
        await Groups.AddToGroupAsync(Context.ConnectionId, code);
        var state = Snapshot(room);
        await Clients.Group(code).SendAsync("RoomStateChanged", state);
        return state;
    }

    public async Task<RoomStateDto> JoinRoom(string roomCode, string displayName)
    {
        await LeaveRoom();
        var code = CleanCode(roomCode);
        if (!Rooms.TryGetValue(code, out var room)) throw new HubException("Room not found.");
        lock (room.Gate)
        {
            if (room.Members.Count >= MaxMembers) throw new HubException("Room is full.");
            room.Members[Context.ConnectionId] = new Member(
                Context.ConnectionId,
                CleanName(displayName),
                room.Members.Count == 0);
            ResetReady(room);
        }
        ConnectionRooms[Context.ConnectionId] = code;
        await Groups.AddToGroupAsync(Context.ConnectionId, code);
        var state = Snapshot(room);
        await Clients.Group(code).SendAsync("RoomStateChanged", state);
        return state;
    }

    public async Task SetCatalog(IReadOnlyList<string> fingerprints)
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.Catalog = fingerprints
                .Select(CleanFingerprint)
                .Where(value => value.Length == 64)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(1000)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        // The sender refreshes its own counts after SetCatalog returns. Notify only
        // the other members so one catalog action produces one refresh per client.
        await Clients.OthersInGroup(room.Code).SendAsync("CatalogChanged");
    }

    public async Task<int> AddCatalogFingerprint(string fingerprint)
    {
        var room = GetCurrentRoom();
        var clean = CleanFingerprint(fingerprint);
        if (clean.Length != 64) throw new HubException("A valid animation fingerprint is required.");

        int matches;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            if (!member.Catalog.Contains(clean) && member.Catalog.Count >= 1000)
                throw new HubException("The animation catalog is full.");
            member.Catalog.Add(clean);
            matches = room.Members.Values.Count(candidate => candidate.Catalog.Contains(clean));
        }
        await Clients.OthersInGroup(room.Code).SendAsync("CatalogFingerprintChanged", clean);
        return matches;
    }

    public Dictionary<string, int> GetMatchCounts(IReadOnlyList<string> fingerprints)
    {
        var room = GetCurrentRoom();
        var requested = fingerprints.Select(CleanFingerprint)
            .Where(value => value.Length == 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToList();
        lock (room.Gate)
            return requested.ToDictionary(
                fingerprint => fingerprint,
                fingerprint => room.Members.Values.Count(member => member.Catalog.Contains(fingerprint)),
                StringComparer.OrdinalIgnoreCase);
    }

    public TransferUploadDto BeginModTransfer(string modName, long size, string sha256)
    {
        try
        {
            var room = GetCurrentRoom();
            lock (room.Gate)
            {
                var sender = room.Members[Context.ConnectionId];
                var recipients = room.Members.Keys.Where(id => id != Context.ConnectionId).ToList();
                return transfers.Begin(room.Code, Context.ConnectionId, sender.DisplayName, modName, size, sha256,
                    recipients);
            }
        }
        catch (TransferSharingBlockedException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    public TransferUploadDto BeginModTransferV2(
        string modName,
        long size,
        string sha256,
        string catalogFingerprint)
    {
        try
        {
            var room = GetCurrentRoom();
            var fingerprint = CleanFingerprint(catalogFingerprint);
            if (fingerprint.Length != 64) throw new HubException("A valid animation fingerprint is required.");
            lock (room.Gate)
            {
                var sender = room.Members[Context.ConnectionId];
                var recipients = room.Members.Values
                    .Where(member => member.ConnectionId != Context.ConnectionId)
                    .ToList();
                if (recipients.Count == 0) throw new HubException("There is nobody else in the room.");
                var pending = recipients
                    .Where(member => !member.Catalog.Contains(fingerprint))
                    .Select(member => member.ConnectionId)
                    .ToList();
                var alreadyReceived = recipients.Count - pending.Count;
                return pending.Count == 0
                    ? new TransferUploadDto("", "", 0, alreadyReceived)
                    : transfers.Begin(room.Code, Context.ConnectionId, sender.DisplayName, modName, size, sha256,
                        pending, fingerprint, alreadyReceived);
            }
        }
        catch (TransferSharingBlockedException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    public Task CompleteModTransfer(string transferId)
    {
        if (transfers.MarkDownloaded(transferId, Context.ConnectionId))
            statistics.IncrementSharedAnimations();
        return Task.CompletedTask;
    }

    public Task DeclineModTransfer(string transferId)
    {
        transfers.Decline(transferId, Context.ConnectionId);
        return Task.CompletedTask;
    }

    public async Task SetOptionSelection(string modKey, string group, string option)
    {
        var room = GetCurrentRoom();
        OptionSelectionDto selection;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            selection = new OptionSelectionDto(member.DisplayName, CleanModKey(modKey), CleanLabel(group), CleanLabel(option));
            member.OptionSelections[selection.ModKey + "\n" + selection.Group] = selection;
        }
        await Clients.OthersInGroup(room.Code).SendAsync("OptionSelectionChanged", selection);
    }

    public IReadOnlyList<OptionSelectionDto> GetOptionSelections()
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
            return room.Members.Where(pair => pair.Key != Context.ConnectionId)
                .SelectMany(pair => pair.Value.OptionSelections.Values).ToList();
    }

    public async Task SetRoleLabel(string modKey, string group, string option, string label)
    {
        var room = GetCurrentRoom();
        RoleLabelDto? shared = null;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            var cleanModKey = CleanModKey(modKey);
            var cleanGroup = CleanLabel(group);
            var cleanOption = CleanLabel(option);
            var cleanRole = CleanRoleLabel(label);
            var key = cleanModKey + "\n" + cleanGroup + "\n" + cleanOption;
            if (cleanRole.Length == 0)
                member.RoleLabels.Remove(key);
            else
            {
                if (!member.RoleLabels.ContainsKey(key) && member.RoleLabels.Count >= 1000)
                    throw new HubException("Too many role labels are being shared.");
                shared = new RoleLabelDto(member.DisplayName, cleanModKey, cleanGroup, cleanOption, cleanRole);
                member.RoleLabels[key] = shared;
            }
        }
        if (shared is not null)
            await Clients.OthersInGroup(room.Code).SendAsync("RoleLabelChanged", shared);
    }

    public IReadOnlyList<RoleLabelDto> GetRoleLabels()
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
            return room.Members.Where(pair => pair.Key != Context.ConnectionId)
                .SelectMany(pair => pair.Value.RoleLabels.Values).ToList();
    }

    public IReadOnlyList<CommunityRoleLabelDto> GetCommunityRoleLabels(IReadOnlyList<string> fingerprints) =>
        communityRoles.Get(fingerprints.Select(CleanFingerprint)
            .Where(value => value.Length == 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToList());

    public async Task<CommunityRoleLabelDto?> SubmitCommunityRoleLabel(
        string fingerprint, string group, string option, string label, string reporterId)
        => await SubmitCommunityRoleLabelCore(fingerprint, group, option, label, reporterId, "", "");

    public async Task<CommunityRoleLabelDto?> SubmitCommunityRoleLabelV2(
        string fingerprint, string group, string option, string label, string reporterId,
        string modName, string animationName)
        => await SubmitCommunityRoleLabelCore(
            fingerprint, group, option, label, reporterId, modName, animationName);

    public void RegisterCommunityRoleMetadata(
        string fingerprint, string group, string option, string modName, string animationName)
    {
        var cleanFingerprint = CleanFingerprint(fingerprint);
        var cleanGroup = CleanLabel(group);
        var cleanOption = CleanLabel(option);
        if (cleanFingerprint.Length != 64 ||
            (cleanGroup != "$detected-pose" && cleanGroup != "$detected-emote"))
            throw new HubException("Invalid community role-label metadata.");
        communityRoles.RegisterMetadata(cleanFingerprint, cleanGroup, cleanOption,
            CleanDisplayMetadata(modName, 160), CleanDisplayMetadata(animationName, 120));
    }

    public IReadOnlyList<AnimationArtifactCatalogEntry> LookupAnimationArtifacts(
        IReadOnlyList<AnimationArtifactLookupKey> artifacts)
    {
        if (artifacts.Count > AnimationCatalogStore.MaximumBatchSize)
            throw new HubException($"Look up at most {AnimationCatalogStore.MaximumBatchSize} artifacts at once.");
        try
        {
            return animationCatalog.Lookup(artifacts);
        }
        catch (ArgumentException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    public IReadOnlyList<AnimationArtifactCatalogEntry> SubmitAnimationArtifactReports(
        string reporterId, IReadOnlyList<AnimationArtifactReportSubmission> reports)
    {
        if (reports.Count is < 1 or > AnimationCatalogStore.MaximumBatchSize)
            throw new HubException($"Submit 1-{AnimationCatalogStore.MaximumBatchSize} reports per batch.");
        var total = ConnectionCatalogReportCounts.AddOrUpdate(
            Context.ConnectionId, reports.Count, (_, current) => checked(current + reports.Count));
        if (total > MaximumCatalogReportsPerConnection)
        {
            ConnectionCatalogReportCounts.AddOrUpdate(
                Context.ConnectionId, 0, (_, current) => Math.Max(0, current - reports.Count));
            throw new HubException("This connection has reached its animation-report limit.");
        }
        var cleanReporter = BindReporterId(reporterId, ConnectionCatalogReporterIds);
        try
        {
            var lookupKeys = reports.Select(report =>
                    new AnimationArtifactLookupKey(report.SignatureAlgorithm, report.Signature))
                .ToList();
            var newArtifacts = animationCatalog.CountUnknownArtifacts(lookupKeys);
            if (newArtifacts > 0 && !TryConsumeCatalogCreationBudget(CatalogPeerKey(), newArtifacts))
                throw new HubException(
                    "This network peer has reached its daily new animation-artifact safety budget. " +
                    "Updates to existing artifacts remain available.");
            return animationCatalog.SubmitReports(cleanReporter, reports);
        }
        catch (ArgumentException exception)
        {
            throw new HubException(exception.Message);
        }
        catch (JsonException exception)
        {
            throw new HubException("Animation extraction payload JSON is invalid: " + exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    private string CatalogPeerKey() =>
        Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown-peer";

    private static bool TryConsumeCatalogCreationBudget(string peer, int count)
    {
        if (!CatalogCreationBuckets.TryGetValue(peer, out var bucket))
        {
            if (CatalogCreationBuckets.Count >= MaximumCatalogCreationPeerBuckets)
            {
                var staleBefore = DateTimeOffset.UtcNow - (NewArtifactPeerRefillPeriod * 2);
                var removed = 0;
                foreach (var candidate in CatalogCreationBuckets)
                {
                    var stale = false;
                    lock (candidate.Value.Gate) stale = candidate.Value.UpdatedUtc < staleBefore;
                    if (stale && CatalogCreationBuckets.TryRemove(candidate.Key, out _) && ++removed >= 2_000)
                        break;
                }
                if (CatalogCreationBuckets.Count >= MaximumCatalogCreationPeerBuckets) return false;
            }
            bucket = CatalogCreationBuckets.GetOrAdd(peer, _ => new CatalogCreationBucket());
        }
        lock (bucket.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = Math.Max(0, (now - bucket.UpdatedUtc).TotalSeconds);
            var refillPerSecond = MaximumNewArtifactsPerPeerBurst / NewArtifactPeerRefillPeriod.TotalSeconds;
            bucket.Tokens = Math.Min(MaximumNewArtifactsPerPeerBurst, bucket.Tokens + elapsed * refillPerSecond);
            bucket.UpdatedUtc = now;
            if (bucket.Tokens + 0.0001 < count) return false;
            bucket.Tokens -= count;
            return true;
        }
    }

    private async Task<CommunityRoleLabelDto?> SubmitCommunityRoleLabelCore(
        string fingerprint, string group, string option, string label, string reporterId,
        string modName, string animationName)
    {
        var cleanFingerprint = CleanFingerprint(fingerprint);
        var cleanGroup = CleanLabel(group);
        var cleanOption = CleanLabel(option);
        var cleanRole = CleanRoleLabel(label);
        var cleanReporter = BindReporterId(reporterId, ConnectionCommunityReporterIds);
        if (cleanFingerprint.Length != 64 || cleanRole.Length == 0 ||
            (cleanGroup != "$detected-pose" && cleanGroup != "$detected-emote"))
            throw new HubException("Invalid community role-label submission.");
        var (accepted, changed) = communityRoles.Submit(
            cleanFingerprint, cleanGroup, cleanOption, cleanRole, cleanReporter,
            CleanDisplayMetadata(modName, 160), CleanDisplayMetadata(animationName, 120));
        if (changed && accepted is not null)
            await Clients.All.SendAsync("CommunityRoleLabelChanged", accepted);
        return accepted;
    }

    private string BindReporterId(string reporterId, ConcurrentDictionary<string, string> bindings)
    {
        var cleanReporter = new string(reporterId.Where(Uri.IsHexDigit).Take(32).ToArray());
        if (cleanReporter.Length != 32) throw new HubException("Invalid installation reporter identifier.");
        if (bindings.TryGetValue(Context.ConnectionId, out var existingReporter) &&
            !existingReporter.Equals(cleanReporter, StringComparison.OrdinalIgnoreCase))
            throw new HubException("A connection cannot submit as multiple installations.");
        bindings[Context.ConnectionId] = cleanReporter;
        return cleanReporter;
    }

    public async Task DeclineAnimationSuggestion(string modKey, string suggestedBy)
    {
        var room = GetCurrentRoom();
        AnimationSuggestionDeclinedDto decline;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            var cleanModKey = CleanModKey(modKey);
            var cleanSuggestedBy = CleanLabel(suggestedBy);
            var suggestionExists = room.Members.Values.Any(candidate =>
                candidate.DisplayName.Equals(cleanSuggestedBy, StringComparison.OrdinalIgnoreCase) &&
                ((candidate.Ready && candidate.ModKey.Equals(cleanModKey, StringComparison.OrdinalIgnoreCase)) ||
                 candidate.OptionSelections.Values.Any(selection =>
                     selection.ModKey.Equals(cleanModKey, StringComparison.OrdinalIgnoreCase))));
            if (!suggestionExists) throw new HubException("That animation suggestion is no longer active.");
            decline = new AnimationSuggestionDeclinedDto(member.DisplayName, cleanSuggestedBy, cleanModKey);
        }
        await Clients.Group(room.Code).SendAsync("AnimationSuggestionDeclined", decline);
    }

    public async Task LeaveRoom()
    {
        if (!ConnectionRooms.TryRemove(Context.ConnectionId, out var code)) return;
        transfers.DetachRecipient(Context.ConnectionId);
        if (!Rooms.TryGetValue(code, out var room)) return;
        var removeRoom = false;
        lock (room.Gate)
        {
            room.Members.Remove(Context.ConnectionId);
            if (room.Members.Count == 0) removeRoom = true;
            else
            {
                if (!room.Members.Values.Any(member => member.IsLeader))
                    room.Members.Values.First().IsLeader = true;
                ResetReady(room);
            }
        }
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, code);
        if (removeRoom) _ = RemoveEmptyRoomAfterGracePeriodAsync(room);
        else
        {
            await Clients.Group(code).SendAsync("RoomStateChanged", Snapshot(room));
            await Clients.Group(code).SendAsync("CatalogChanged");
        }
    }

    private async Task RemoveEmptyRoomAfterGracePeriodAsync(Room room)
    {
        await Task.Delay(EmptyRoomGracePeriod);
        lock (room.Gate)
        {
            if (room.Members.Count != 0) return;
            Rooms.TryRemove(new KeyValuePair<string, Room>(room.Code, room));
            transfers.RemoveForRoom(room.Code);
        }
    }

    public async Task SetLocalPresence(string scope, string displayName, uint homeWorldId)
    {
        var cleanScope = CleanLocalScope(scope);
        if (cleanScope.Length == 0) throw new HubException("A valid local animation scope is required.");
        var next = new LocalPresence(cleanScope, CleanName(displayName), homeWorldId);
        if (ConnectionLocalPresence.TryGetValue(Context.ConnectionId, out var previous) &&
            !previous.Scope.Equals(next.Scope, StringComparison.OrdinalIgnoreCase))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, LocalGroup(previous.Scope));
        ConnectionLocalPresence[Context.ConnectionId] = next;
        await Groups.AddToGroupAsync(Context.ConnectionId, LocalGroup(next.Scope));
    }

    public async Task<LocalAnimationSignalDto> BroadcastLocalAnimation(
        string fingerprint,
        uint emoteId,
        int delayMilliseconds)
    {
        if (!ConnectionLocalPresence.TryGetValue(Context.ConnectionId, out var presence))
            throw new HubException("Set local animation presence first.");
        var cleanFingerprint = CleanFingerprint(fingerprint);
        if (cleanFingerprint.Length != 64 || emoteId == 0)
            throw new HubException("A valid animation fingerprint and emote are required.");

        var now = Environment.TickCount64;
        if (LastLocalAnimationTicks.TryGetValue(Context.ConnectionId, out var previous) && now - previous < 200)
            throw new HubException("Local animations are being started too quickly.");
        LastLocalAnimationTicks[Context.ConnectionId] = now;

        var delay = Math.Clamp(delayMilliseconds, 100, 3000);
        var signal = new LocalAnimationSignalDto(
            presence.DisplayName,
            presence.HomeWorldId,
            cleanFingerprint,
            emoteId,
            DateTimeOffset.UtcNow.AddMilliseconds(delay).ToUnixTimeMilliseconds(),
            Guid.NewGuid().ToString("N"),
            delay);
        await Clients.OthersInGroup(LocalGroup(presence.Scope)).SendAsync("LocalAnimation", signal);
        statistics.IncrementAnimationsPerformed(1);
        return signal;
    }

    public async Task<RoomStateDto> SetReady(string modKey)
    {
        var room = GetCurrentRoom();
        List<(string ConnectionId, PlaySignalDto Signal)> plays = [];
        RoomStateDto readyState;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.Ready = true;
            member.ModKey = CleanModKey(modKey);
            readyState = Snapshot(room);

            if (room.Members.Count >= 2 && room.Members.Values.All(value => value.Ready) &&
                room.Members.Values.All(value => !string.IsNullOrWhiteSpace(value.ModKey)))
            {
                var start = DateTimeOffset.UtcNow.AddMilliseconds(PlayDelayMilliseconds).ToUnixTimeMilliseconds();
                var sequence = Guid.NewGuid().ToString("N");
                plays = room.Members.Values.Select(value => (
                    value.ConnectionId,
                    new PlaySignalDto(value.ModKey, start, sequence, PlayDelayMilliseconds))).ToList();
                ResetReady(room);
            }
        }
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", readyState);
        if (plays.Count > 0)
        {
            await Task.WhenAll(plays.Select(play =>
                Clients.Client(play.ConnectionId).SendAsync("AnimationPlay", play.Signal)));
            statistics.IncrementAnimationsPerformed(plays.Count);
            await Clients.Group(room.Code).SendAsync("RoomStateChanged", Snapshot(room));
        }
        return readyState;
    }

    public async Task<RoomStateDto> CancelReady()
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.Ready = false;
            member.ModKey = "";
        }
        var state = Snapshot(room);
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", state);
        return state;
    }

    public async Task<RoomStateDto> ForceStart()
    {
        var room = GetCurrentRoom();
        List<(string ConnectionId, PlaySignalDto Signal)> plays;
        RoomStateDto state;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            if (!member.IsLeader) throw new HubException("Only the room host can force playback.");
            if (!member.Ready || string.IsNullOrWhiteSpace(member.ModKey))
                throw new HubException("Select an animation and ready it before forcing playback.");
            var start = DateTimeOffset.UtcNow.AddMilliseconds(PlayDelayMilliseconds).ToUnixTimeMilliseconds();
            var sequence = Guid.NewGuid().ToString("N");
            plays = room.Members.Values
                .Where(value => value.Ready && !string.IsNullOrWhiteSpace(value.ModKey))
                .Select(value => (
                    value.ConnectionId,
                    new PlaySignalDto(value.ModKey, start, sequence, PlayDelayMilliseconds)))
                .ToList();
            ResetReady(room);
            state = Snapshot(room);
        }
        await Task.WhenAll(plays.Select(play =>
            Clients.Client(play.ConnectionId).SendAsync("AnimationPlay", play.Signal)));
        statistics.IncrementAnimationsPerformed(plays.Count);
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", state);
        return state;
    }

    public async Task<RoomStateDto> RemoveMember(string connectionId)
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
        {
            var requester = room.Members[Context.ConnectionId];
            if (!requester.IsLeader) throw new HubException("Only the room host can remove members.");
            if (connectionId == Context.ConnectionId) throw new HubException("The host cannot remove themselves.");
            if (!room.Members.Remove(connectionId)) throw new HubException("That member is no longer in the room.");
            ConnectionRooms.TryRemove(connectionId, out _);
            ResetReady(room);
        }
        transfers.DetachRecipient(connectionId);
        await Groups.RemoveFromGroupAsync(connectionId, room.Code);
        await Clients.Client(connectionId).SendAsync("RemovedFromRoom", "The host removed you from the room.");
        var state = Snapshot(room);
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", state);
        await Clients.Group(room.Code).SendAsync("CatalogChanged");
        return state;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Adjust the live gauge first so cleanup failures cannot leave a stale active-user count.
        var relayStatistics = statistics.ConnectionClosed();
        ConnectionCommunityReporterIds.TryRemove(Context.ConnectionId, out _);
        ConnectionCatalogReporterIds.TryRemove(Context.ConnectionId, out _);
        ConnectionCatalogReportCounts.TryRemove(Context.ConnectionId, out _);
        if (ConnectionLocalPresence.TryRemove(Context.ConnectionId, out var localPresence))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, LocalGroup(localPresence.Scope));
        LastLocalAnimationTicks.TryRemove(Context.ConnectionId, out _);
        await LeaveRoom();
        await base.OnDisconnectedAsync(exception);
        await Clients.All.SendAsync("OnlineUserCountChanged", relayStatistics.ActiveUsers);
    }

    private Room GetCurrentRoom()
    {
        if (!ConnectionRooms.TryGetValue(Context.ConnectionId, out var code) || !Rooms.TryGetValue(code, out var room))
            throw new HubException("Join a room first.");
        return room;
    }

    private static RoomStateDto Snapshot(Room room)
    {
        lock (room.Gate)
            return new RoomStateDto(room.Code, room.Members.Values
                .Select(member => new RoomMemberDto(member.ConnectionId, member.DisplayName, member.IsLeader,
                    member.Ready, member.ModKey)).ToList());
    }

    private static void ResetReady(Room room)
    {
        foreach (var member in room.Members.Values) { member.Ready = false; member.ModKey = ""; }
    }

    private static string CreateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(6, bytes.ToArray(), (chars, values) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[values[i] % alphabet.Length];
        });
    }

    private static string CleanCode(string value) => new string(value.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToUpperInvariant();
    private static string CleanName(string value) => string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim()[..Math.Min(40, value.Trim().Length)];
    private static string CleanModKey(string value) => value.Trim()[..Math.Min(160, value.Trim().Length)];
    private static string CleanLabel(string value)
    {
        var clean = value.Trim();
        return clean[..Math.Min(120, clean.Length)];
    }
    private static string CleanRoleLabel(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean[..Math.Min(20, clean.Length)];
    }
    private static string CleanDisplayMetadata(string value, int maximumLength)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean[..Math.Min(maximumLength, clean.Length)];
    }
    private static string CleanFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Take(64).Select(char.ToUpperInvariant).ToArray());
    private static string CleanLocalScope(string value) =>
        new(value.Where(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_')
            .Take(80).ToArray());
    private static string LocalGroup(string scope) => "local:" + scope;

    private sealed class Room(string code)
    {
        public string Code { get; } = code;
        public object Gate { get; } = new();
        public Dictionary<string, Member> Members { get; } = [];
    }

    private sealed class Member(string connectionId, string displayName, bool leader)
    {
        public string ConnectionId { get; } = connectionId;
        public string DisplayName { get; } = displayName;
        public bool IsLeader { get; set; } = leader;
        public bool Ready { get; set; }
        public string ModKey { get; set; } = "";
        public HashSet<string> Catalog { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, OptionSelectionDto> OptionSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RoleLabelDto> RoleLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CatalogCreationBucket
    {
        public object Gate { get; } = new();
        public double Tokens { get; set; } = MaximumNewArtifactsPerPeerBurst;
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed record LocalPresence(string Scope, string DisplayName, uint HomeWorldId);
}

public sealed record RoomStateDto(string RoomCode, IReadOnlyList<RoomMemberDto> Members);
public sealed record RoomMemberDto(string ConnectionId, string DisplayName, bool IsLeader, bool Ready, string ModKey);
public sealed record PlaySignalDto(
    string ModKey,
    long StartUnixMilliseconds,
    string SequenceId,
    int DelayMilliseconds = 0);
public sealed record LocalAnimationSignalDto(
    string SenderName,
    uint SenderHomeWorldId,
    string Fingerprint,
    uint EmoteId,
    long StartUnixMilliseconds,
    string SequenceId,
    int DelayMilliseconds);
public sealed record OptionSelectionDto(string MemberName, string ModKey, string Group, string Option);
public sealed record RoleLabelDto(string MemberName, string ModKey, string Group, string Option, string Label);
public sealed record AnimationSuggestionDeclinedDto(string DeclinedBy, string SuggestedBy, string ModKey);

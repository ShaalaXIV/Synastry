using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const int MaxMembers = 16;
    private readonly TransferStore transfers;
    private readonly CommunityRoleLabelStore communityRoles;
    private readonly ContactMapStore contactMaps;
    private readonly RelayStatisticsStore statistics;

    public AnimationHub(TransferStore transfers, CommunityRoleLabelStore communityRoles,
        RelayStatisticsStore statistics, ContactMapStore contactMaps)
    {
        this.contactMaps = contactMaps;
        this.transfers = transfers;
        this.communityRoles = communityRoles;
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

    // A body profile is the member's measured shape (bone-to-skin distances, opening and shaft
    // sizes) as JSON the plugin defines. The relay only relays it within the room, in memory.
    private const int MaximumBodyProfileBytes = 32 * 1024;

    public async Task SetBodyProfile(string profileJson)
    {
        if (profileJson.Length > MaximumBodyProfileBytes)
            throw new HubException("This body profile is too large.");
        if (profileJson.Length > 0)
        {
            try { using var _ = JsonDocument.Parse(profileJson); }
            catch (JsonException) { throw new HubException("This body profile is not valid JSON."); }
        }
        var room = GetCurrentRoom();
        BodyProfileDto shared;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.BodyProfile = profileJson;
            shared = new BodyProfileDto(member.ConnectionId, member.DisplayName, profileJson);
        }
        await Clients.OthersInGroup(room.Code).SendAsync("BodyProfileChanged", shared);
    }

    public IReadOnlyList<BodyProfileDto> GetBodyProfiles()
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
            return room.Members.Values
                .Where(member => member.ConnectionId != Context.ConnectionId && member.BodyProfile.Length > 0)
                .Select(member => new BodyProfileDto(member.ConnectionId, member.DisplayName, member.BodyProfile))
                .ToList();
    }

    /// <summary>Contact maps for the animation files being played (at most 16 per call).</summary>
    public IReadOnlyList<ContactMapDto> GetContactMaps(IReadOnlyList<string> hashes) => contactMaps.Get(hashes);

    public IReadOnlyList<RoleLabelDto> GetRoleLabels()
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
            return room.Members.Where(pair => pair.Key != Context.ConnectionId)
                .SelectMany(pair => pair.Value.RoleLabels.Values).ToList();
    }

    public IReadOnlyList<CommunityRoleLabelDto> GetCommunityRoleLabels(IReadOnlyList<string> fingerprints) =>
        communityRoles.Get(CleanFingerprints(fingerprints));

    /// <summary>Like <see cref="GetCommunityRoleLabels"/>, plus moderator removals as empty labels.</summary>
    public IReadOnlyList<CommunityRoleLabelDto> GetCommunityRoleLabelsV2(IReadOnlyList<string> fingerprints) =>
        communityRoles.Get(CleanFingerprints(fingerprints), includeModerated: true);

    private static List<string> CleanFingerprints(IReadOnlyList<string> fingerprints) =>
        fingerprints.Select(CleanFingerprint)
            .Where(value => value.Length == 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToList();

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

    // Plugins before 1.0.78 still report every installed mod. The relay no longer keeps that
    // catalog, so these accept the call and answer that nothing is known.
    public IReadOnlyList<object> LookupAnimationArtifacts(JsonElement artifacts) => [];

    public IReadOnlyList<object> SubmitAnimationArtifactReports(string reporterId, JsonElement reports) => [];

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
        List<(string ConnectionId, PlaySignalDto Signal)> plays;
        RoomStateDto readyState;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.Ready = true;
            member.ModKey = CleanModKey(modKey);
            readyState = Snapshot(room);
            plays = TakePlaysIfEveryoneIsSet(room);
        }
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", readyState);
        await SendPlays(room, plays);
        return readyState;
    }

    /// <summary>
    /// The game path of the animation this member is about to play, announced before SetReady by
    /// clients that preload. Everyone else has to confirm they have it again.
    /// </summary>
    public async Task<RoomStateDto> SetAnimationAsset(string gamePath)
    {
        var room = GetCurrentRoom();
        RoomStateDto state;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.WaitsForAssets = true;
            member.AssetPath = CleanAssetPath(gamePath);
            member.AssetsReady = false;
            foreach (var other in room.Members.Values.Where(value => value != member)) other.AssetsReady = false;
            state = Snapshot(room);
        }
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", state);
        return state;
    }

    /// <summary>This member has every other ready member's animation files (or gave up waiting).</summary>
    public async Task<RoomStateDto> SetAssetsReady(bool ready)
    {
        var room = GetCurrentRoom();
        List<(string ConnectionId, PlaySignalDto Signal)> plays;
        RoomStateDto state;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.WaitsForAssets = true;
            member.AssetsReady = ready;
            state = Snapshot(room);
            plays = TakePlaysIfEveryoneIsSet(room);
        }
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", state);
        await SendPlays(room, plays);
        return state;
    }

    /// <summary>Everyone is ready and every preloading member has the others' files: start. Caller holds the gate.</summary>
    private static List<(string ConnectionId, PlaySignalDto Signal)> TakePlaysIfEveryoneIsSet(Room room)
    {
        if (room.Members.Count < 2 ||
            !room.Members.Values.All(value => value.Ready && !string.IsNullOrWhiteSpace(value.ModKey)) ||
            !room.Members.Values.All(value => !value.WaitsForAssets || value.AssetsReady))
            return [];
        var start = DateTimeOffset.UtcNow.AddMilliseconds(PlayDelayMilliseconds).ToUnixTimeMilliseconds();
        var sequence = Guid.NewGuid().ToString("N");
        var plays = room.Members.Values.Select(value => (
            value.ConnectionId,
            new PlaySignalDto(value.ModKey, start, sequence, PlayDelayMilliseconds))).ToList();
        ResetReady(room);
        return plays;
    }

    private async Task SendPlays(Room room, List<(string ConnectionId, PlaySignalDto Signal)> plays)
    {
        if (plays.Count == 0) return;
        await Task.WhenAll(plays.Select(play =>
            Clients.Client(play.ConnectionId).SendAsync("AnimationPlay", play.Signal)));
        statistics.IncrementAnimationsPerformed(plays.Count);
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", Snapshot(room));
    }

    public async Task<RoomStateDto> CancelReady()
    {
        var room = GetCurrentRoom();
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.Ready = false;
            member.ModKey = "";
            member.AssetPath = "";
            member.AssetsReady = false;
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

    public async Task<RoomStateDto> SetFreeUse(bool enabled)
    {
        var room = GetCurrentRoom();
        lock (room.Gate) room.Members[Context.ConnectionId].FreeUse = enabled;
        var state = Snapshot(room);
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", state);
        return state;
    }

    /// <summary>
    /// Sends one member's role choice to a room member who has opted into FREE USE. The relay
    /// stamps the sender's name and only delivers animations the target already advertises.
    /// </summary>
    public async Task DirectFreeUse(string targetConnectionId, FreeUseDirectionRequest request)
    {
        var room = GetCurrentRoom();
        FreeUseDirectiveDto directive;
        lock (room.Gate)
        {
            var sender = room.Members[Context.ConnectionId];
            if (targetConnectionId == Context.ConnectionId)
                throw new HubException("Choose another room member.");
            if (!room.Members.TryGetValue(targetConnectionId, out var target))
                throw new HubException("That member is no longer in the room.");
            if (!target.FreeUse)
                throw new HubException($"{target.DisplayName} is no longer in FREE USE mode.");
            var fingerprint = CleanFingerprint(request.Fingerprint);
            if (fingerprint.Length != 64)
                throw new HubException("A valid animation fingerprint is required.");
            if (!target.Catalog.Contains(fingerprint))
                throw new HubException($"{target.DisplayName} does not have this animation.");
            var trigger = request.Trigger.Trim();
            if (!FreeUseTrigger.IsMatch(trigger))
                throw new HubException("Choose a pose or emote role.");
            directive = new FreeUseDirectiveDto(
                sender.DisplayName,
                fingerprint,
                CleanModKey(request.ModKey),
                CleanDisplayMetadata(request.ModName, 160),
                trigger,
                CleanFreeUseOptions(request.Options));
        }
        await Clients.Client(targetConnectionId).SendAsync("FreeUseDirected", directive);
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
                    member.Ready, member.ModKey, member.FreeUse,
                    member.AssetPath, member.AssetsReady, member.WaitsForAssets)).ToList());
    }

    private static readonly Regex FreeUseTrigger = new(
        @"^(?:pose:(?:Idle|Sit|GroundSit|Doze):\d{1,3}|emote:\d{1,6})$",
        RegexOptions.CultureInvariant);

    private static Dictionary<string, List<string>> CleanFreeUseOptions(
        IReadOnlyDictionary<string, List<string>>? options)
    {
        var clean = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (options is null) return clean;
        foreach (var (group, selections) in options.Take(64))
        {
            var groupName = CleanOptionName(group);
            if (groupName.Length == 0) continue;
            clean[groupName] = (selections ?? [])
                .Select(CleanOptionName)
                .Where(option => option.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(64)
                .ToList();
        }
        return clean;
    }

    // Option names are matched exactly by Penumbra, so they are bounded but never trimmed.
    private static string CleanOptionName(string? value)
    {
        var clean = new string((value ?? "").Where(character => !char.IsControl(character)).ToArray());
        return clean[..Math.Min(160, clean.Length)];
    }

    private static void ResetReady(Room room)
    {
        foreach (var member in room.Members.Values)
        {
            member.Ready = false;
            member.ModKey = "";
            member.AssetPath = "";
            member.AssetsReady = false;
        }
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
    // Only a character animation path is ever announced: chara/... .pap, bounded.
    private static string CleanAssetPath(string? value)
    {
        var clean = (value ?? "").Trim().Replace('\\', '/').ToLowerInvariant();
        return clean.Length <= 200 && clean.StartsWith("chara/", StringComparison.Ordinal) &&
               clean.EndsWith(".pap", StringComparison.Ordinal) && !clean.Contains("..", StringComparison.Ordinal)
            ? clean
            : "";
    }
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
        public bool FreeUse { get; set; }
        public string ModKey { get; set; } = "";
        public string AssetPath { get; set; } = "";
        public bool AssetsReady { get; set; }
        // Set once the member's client takes part in preloading; older clients are never waited on.
        public bool WaitsForAssets { get; set; }
        public HashSet<string> Catalog { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, OptionSelectionDto> OptionSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RoleLabelDto> RoleLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string BodyProfile { get; set; } = "";
    }

    private sealed record LocalPresence(string Scope, string DisplayName, uint HomeWorldId);
}

public sealed record BodyProfileDto(string ConnectionId, string DisplayName, string ProfileJson);
public sealed record RoomStateDto(string RoomCode, IReadOnlyList<RoomMemberDto> Members);
public sealed record RoomMemberDto(
    string ConnectionId,
    string DisplayName,
    bool IsLeader,
    bool Ready,
    string ModKey,
    bool FreeUse = false,
    string AssetPath = "",
    bool AssetsReady = false,
    bool WaitsForAssets = false);
public sealed record FreeUseDirectionRequest(
    string Fingerprint,
    string ModKey,
    string ModName,
    string Trigger,
    Dictionary<string, List<string>>? Options);
public sealed record FreeUseDirectiveDto(
    string DirectedBy,
    string Fingerprint,
    string ModKey,
    string ModName,
    string Trigger,
    Dictionary<string, List<string>> Options);
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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;

namespace EmoteLink.Relay;

public sealed class AnimationHub : Hub
{
    private const int PlayDelayMilliseconds = 1500;
    private static readonly TimeSpan EmptyRoomGracePeriod = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, Room> Rooms = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ConnectionRooms = new();
    private static readonly ConcurrentDictionary<string, string> ConnectionReporterIds = new();
    private const int MaxMembers = 16;
    private readonly TransferStore transfers;
    private readonly CommunityRoleLabelStore communityRoles;

    public AnimationHub(TransferStore transfers, CommunityRoleLabelStore communityRoles)
    {
        this.transfers = transfers;
        this.communityRoles = communityRoles;
    }

    public async Task<RoomStateDto> CreateRoom(string displayName)
    {
        await LeaveRoom();
        var code = CreateCode();
        var room = new Room(code);
        while (!Rooms.TryAdd(code, room)) { code = CreateCode(); room = new Room(code); }
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
        await Clients.Group(room.Code).SendAsync("CatalogChanged");
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
        var room = GetCurrentRoom();
        lock (room.Gate)
        {
            var sender = room.Members[Context.ConnectionId];
            var recipients = room.Members.Keys.Where(id => id != Context.ConnectionId).ToList();
            return transfers.Begin(room.Code, Context.ConnectionId, sender.DisplayName, modName, size, sha256, recipients);
        }
    }

    public Task CompleteModTransfer(string transferId)
    {
        transfers.MarkDownloaded(transferId, Context.ConnectionId);
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
    {
        var cleanFingerprint = CleanFingerprint(fingerprint);
        var cleanGroup = CleanLabel(group);
        var cleanOption = CleanLabel(option);
        var cleanRole = CleanRoleLabel(label);
        var cleanReporter = new string(reporterId.Where(Uri.IsHexDigit).Take(32).ToArray());
        if (cleanFingerprint.Length != 64 || cleanRole.Length == 0 || cleanReporter.Length != 32 ||
            (cleanGroup != "$detected-pose" && cleanGroup != "$detected-emote"))
            throw new HubException("Invalid community role-label submission.");
        if (ConnectionReporterIds.TryGetValue(Context.ConnectionId, out var existingReporter) &&
            !existingReporter.Equals(cleanReporter, StringComparison.OrdinalIgnoreCase))
            throw new HubException("A connection cannot submit as multiple installations.");
        ConnectionReporterIds[Context.ConnectionId] = cleanReporter;
        var (accepted, changed) = communityRoles.Submit(
            cleanFingerprint, cleanGroup, cleanOption, cleanRole, cleanReporter);
        if (changed && accepted is not null)
            await Clients.All.SendAsync("CommunityRoleLabelChanged", accepted);
        return accepted;
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

    public async Task<RoomStateDto> SetReady(string modKey)
    {
        var room = GetCurrentRoom();
        PlaySignalDto? play = null;
        RoomStateDto readyState;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            member.Ready = true;
            member.ModKey = CleanModKey(modKey);
            readyState = Snapshot(room);

            var keys = room.Members.Values.Select(value => value.ModKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (room.Members.Count >= 2 && room.Members.Values.All(value => value.Ready) &&
                keys.Count == 1 && !string.IsNullOrWhiteSpace(keys[0]))
            {
                play = new PlaySignalDto(
                    keys[0],
                    DateTimeOffset.UtcNow.AddMilliseconds(PlayDelayMilliseconds).ToUnixTimeMilliseconds(),
                    Guid.NewGuid().ToString("N"),
                    PlayDelayMilliseconds);
                ResetReady(room);
            }
        }
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", readyState);
        if (play is not null)
        {
            await Clients.Group(room.Code).SendAsync("AnimationPlay", play);
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
        PlaySignalDto play;
        RoomStateDto state;
        lock (room.Gate)
        {
            var member = room.Members[Context.ConnectionId];
            if (!member.IsLeader) throw new HubException("Only the room host can force playback.");
            if (!member.Ready || string.IsNullOrWhiteSpace(member.ModKey))
                throw new HubException("Select an animation and ready it before forcing playback.");
            play = new PlaySignalDto(
                member.ModKey,
                DateTimeOffset.UtcNow.AddMilliseconds(PlayDelayMilliseconds).ToUnixTimeMilliseconds(),
                Guid.NewGuid().ToString("N"),
                PlayDelayMilliseconds);
            ResetReady(room);
            state = Snapshot(room);
        }
        await Clients.Group(room.Code).SendAsync("AnimationPlay", play);
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
        await Groups.RemoveFromGroupAsync(connectionId, room.Code);
        await Clients.Client(connectionId).SendAsync("RemovedFromRoom", "The host removed you from the room.");
        var state = Snapshot(room);
        await Clients.Group(room.Code).SendAsync("RoomStateChanged", state);
        await Clients.Group(room.Code).SendAsync("CatalogChanged");
        return state;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        ConnectionReporterIds.TryRemove(Context.ConnectionId, out _);
        await LeaveRoom();
        await base.OnDisconnectedAsync(exception);
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
    private static string CleanFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Take(64).Select(char.ToUpperInvariant).ToArray());

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
}

public sealed record RoomStateDto(string RoomCode, IReadOnlyList<RoomMemberDto> Members);
public sealed record RoomMemberDto(string ConnectionId, string DisplayName, bool IsLeader, bool Ready, string ModKey);
public sealed record PlaySignalDto(
    string ModKey,
    long StartUnixMilliseconds,
    string SequenceId,
    int DelayMilliseconds = 0);
public sealed record OptionSelectionDto(string MemberName, string ModKey, string Group, string Option);
public sealed record RoleLabelDto(string MemberName, string ModKey, string Group, string Option, string Label);
public sealed record AnimationSuggestionDeclinedDto(string DeclinedBy, string SuggestedBy, string ModKey);

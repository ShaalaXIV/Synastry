using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using System.Security.Cryptography;

namespace EmoteLink;

public sealed class AnimationSyncService : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, int> EmptyMatchCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private HubConnection? connection;
    private RoomStateDto? room;
    private IReadOnlyList<string> catalog = [];
    // Match-count snapshots are replaced as a unit and never mutated. Returning the
    // current snapshot avoids copying the entire catalog once per visible mod, per frame.
    private IReadOnlyDictionary<string, int> matchCounts = EmptyMatchCounts;
    private string? desiredRoomCode;
    private string desiredDisplayName = "Player";
    private string relayBaseUrl = "";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public event Action? StateChanged;
    public event Action<PlaySignalDto>? PlayReceived;
    public event Action<string, Exception?>? Diagnostic;
    public event Action<ModTransferOfferDto>? ModTransferOffered;
    public event Action<OptionSelectionDto>? OptionSelectionChanged;
    public event Action<RoleLabelDto>? RoleLabelChanged;
    public event Action<CommunityRoleLabelDto>? CommunityRoleLabelChanged;
    public event Action<AnimationSuggestionDeclinedDto>? AnimationSuggestionDeclined;
    public string Status { get; private set; } = "Disconnected";
    public bool IsConnected => connection?.State == HubConnectionState.Connected;
    public bool IsRoomLeader => Room?.Members.Any(member =>
        member.ConnectionId == connection?.ConnectionId && member.IsLeader) == true;
    public bool IsCurrentMember(string connectionId) => connection?.ConnectionId == connectionId;
    public RoomStateDto? Room => Volatile.Read(ref room);
    public IReadOnlyDictionary<string, int> MatchCounts => Volatile.Read(ref matchCounts);
    public bool IsInRoom => Volatile.Read(ref room) is not null;

    public async Task ConnectAsync(string baseUrl)
    {
        await DisconnectAsync();
        Status = "Connecting...";
        relayBaseUrl = baseUrl.Trim().TrimEnd('/');
        Notify();
        var hub = new HubConnectionBuilder()
            // Keep one quiet WebSocket open for real-time room/play events. Long polling
            // continuously replaces HTTP requests even when a room is idle.
            .WithUrl(baseUrl.Trim().TrimEnd('/') + "/animation", options =>
                options.Transports = HttpTransportType.WebSockets)
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .Build();
        hub.KeepAliveInterval = TimeSpan.FromSeconds(30);
        hub.ServerTimeout = TimeSpan.FromSeconds(90);
        hub.On<RoomStateDto>("RoomStateChanged", UpdateRoom);
        hub.On<PlaySignalDto>("AnimationPlay", signal => PlayReceived?.Invoke(signal));
        hub.On("CatalogChanged", () => _ = RefreshMatchCountsAsync());
        hub.On<ModTransferOfferDto>("ModTransferOffered", offer => ModTransferOffered?.Invoke(offer));
        hub.On<OptionSelectionDto>("OptionSelectionChanged", selection => OptionSelectionChanged?.Invoke(selection));
        hub.On<RoleLabelDto>("RoleLabelChanged", label => RoleLabelChanged?.Invoke(label));
        hub.On<CommunityRoleLabelDto>("CommunityRoleLabelChanged", label => CommunityRoleLabelChanged?.Invoke(label));
        hub.On<AnimationSuggestionDeclinedDto>("AnimationSuggestionDeclined",
            decline => AnimationSuggestionDeclined?.Invoke(decline));
        hub.On<string>("RemovedFromRoom", reason =>
        {
            lock (gate)
            {
                Volatile.Write(ref room, null);
                desiredRoomCode = null;
                Volatile.Write(ref matchCounts, EmptyMatchCounts);
            }
            Status = reason;
            Notify();
        });
        hub.Reconnecting += exception =>
        {
            if (!ReferenceEquals(connection, hub)) return Task.CompletedTask;
            Status = ConnectionStatus("Reconnecting", exception);
            Diagnostic?.Invoke("Relay connection interrupted; attempting to reconnect.", exception);
            Notify();
            return Task.CompletedTask;
        };
        hub.Reconnected += async connectionId =>
        {
            if (!ReferenceEquals(connection, hub)) return;
            Diagnostic?.Invoke($"Relay reconnected with connection ID {connectionId ?? "unknown"}.", null);
            await RecoverRoomAsync(hub);
        };
        hub.Closed += exception =>
        {
            if (!ReferenceEquals(connection, hub)) return Task.CompletedTask;
            Status = ConnectionStatus("Disconnected", exception);
            Volatile.Write(ref room, null);
            Volatile.Write(ref matchCounts, EmptyMatchCounts);
            Diagnostic?.Invoke("Relay connection closed after reconnect attempts were exhausted.", exception);
            Notify();
            return Task.CompletedTask;
        };
        connection = hub;
        try
        {
            await hub.StartAsync();
            Status = "Connected";
        }
        catch (Exception exception)
        {
            await hub.DisposeAsync();
            if (ReferenceEquals(connection, hub)) connection = null;
            Status = ConnectionStatus("Connection failed", exception);
            Diagnostic?.Invoke("Could not connect to the animation relay.", exception);
            Notify();
            throw;
        }
        Notify();
    }

    public async Task<RoomStateDto> CreateRoomAsync(string displayName, IReadOnlyList<string> fingerprints)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("CreateRoom", displayName);
        lock (gate) { desiredRoomCode = state.RoomCode; desiredDisplayName = CleanDisplayName(displayName); }
        UpdateRoom(state);
        await SetCatalogAsync(fingerprints);
        return state;
    }

    public async Task<RoomStateDto> JoinRoomAsync(string code, string displayName, IReadOnlyList<string> fingerprints)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("JoinRoom", code, displayName);
        lock (gate) { desiredRoomCode = state.RoomCode; desiredDisplayName = CleanDisplayName(displayName); }
        UpdateRoom(state);
        await SetCatalogAsync(fingerprints);
        return state;
    }

    public async Task SetCatalogAsync(IReadOnlyList<string> fingerprints)
    {
        lock (gate) catalog = fingerprints.Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        if (!IsInRoom) return;
        try
        {
            await RequireConnection().InvokeAsync("SetCatalog", catalog);
            await RefreshMatchCountsAsync();
        }
        catch
        {
            // Catalog matching is optional when connected to an older relay.
        }
    }

    public async Task SendModAsync(string modName, string packagePath, long size, string sha256)
    {
        var upload = await RequireConnection().InvokeAsync<TransferUploadDto>("BeginModTransfer", modName, size, sha256);
        await using var input = File.OpenRead(packagePath);
        using var content = new StreamContent(input);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await Http.PutAsync(
            $"{relayBaseUrl}/transfers/{Uri.EscapeDataString(upload.TransferId)}?token={Uri.EscapeDataString(upload.UploadToken)}",
            content);
        response.EnsureSuccessStatusCode();
    }

    public async Task DownloadModAsync(ModTransferOfferDto offer, string destination)
    {
        using var response = await Http.GetAsync(
            $"{relayBaseUrl}/transfers/{Uri.EscapeDataString(offer.TransferId)}?token={Uri.EscapeDataString(offer.DownloadToken)}",
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 75L * 1024 * 1024)
            throw new InvalidDataException("The mod exceeds the 75 MB transfer limit.");

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            total += read;
            if (total > 75L * 1024 * 1024) throw new InvalidDataException("The mod exceeds the 75 MB transfer limit.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        await output.FlushAsync();
        if (total != offer.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded mod failed checksum verification.");
    }

    public Task CompleteModTransferAsync(string transferId) =>
        RequireConnection().InvokeAsync("CompleteModTransfer", transferId);

    public Task DeclineModTransferAsync(string transferId) =>
        RequireConnection().InvokeAsync("DeclineModTransfer", transferId);

    public Task SetOptionSelectionAsync(string modKey, string group, string option) =>
        RequireConnection().InvokeAsync("SetOptionSelection", modKey, group, option);

    public async Task<IReadOnlyList<OptionSelectionDto>> GetOptionSelectionsAsync() =>
        await RequireConnection().InvokeAsync<IReadOnlyList<OptionSelectionDto>>("GetOptionSelections");

    public async Task SetRoleLabelAsync(string modKey, string group, string option, string label)
    {
        try { await RequireConnection().InvokeAsync("SetRoleLabel", modKey, group, option, label); }
        catch { /* Role labels are optional when connected to an older relay. */ }
    }

    public async Task<IReadOnlyList<RoleLabelDto>> GetRoleLabelsAsync()
    {
        try { return await RequireConnection().InvokeAsync<IReadOnlyList<RoleLabelDto>>("GetRoleLabels"); }
        catch { return []; }
    }

    public async Task<IReadOnlyList<CommunityRoleLabelDto>> GetCommunityRoleLabelsAsync(
        IReadOnlyList<string> fingerprints)
    {
        try
        {
            return await RequireConnection().InvokeAsync<IReadOnlyList<CommunityRoleLabelDto>>(
                "GetCommunityRoleLabels", fingerprints);
        }
        catch { return []; }
    }

    public async Task SubmitCommunityRoleLabelAsync(
        string fingerprint, string group, string option, string label, string reporterId)
    {
        try
        {
            await RequireConnection().InvokeAsync<CommunityRoleLabelDto?>(
                "SubmitCommunityRoleLabel", fingerprint, group, option, label, reporterId);
        }
        catch { /* Community labels are optional when connected to an older relay. */ }
    }

    public Task DeclineAnimationSuggestionAsync(string modKey, string suggestedBy) =>
        RequireConnection().InvokeAsync("DeclineAnimationSuggestion", modKey, suggestedBy);

    private async Task RefreshMatchCountsAsync()
    {
        try
        {
            IReadOnlyList<string> requested;
            lock (gate) requested = catalog;
            if (!IsInRoom || connection?.State != HubConnectionState.Connected) return;
            var counts = await connection.InvokeAsync<Dictionary<string, int>>("GetMatchCounts", requested);
            Volatile.Write(ref matchCounts,
                new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase));
            Notify();
        }
        catch
        {
            // Older relays do not expose private catalog matching; synchronization still works.
        }
    }

    public async Task LeaveRoomAsync()
    {
        lock (gate) desiredRoomCode = null;
        if (connection?.State == HubConnectionState.Connected) await connection.InvokeAsync("LeaveRoom");
        Volatile.Write(ref room, null);
        Volatile.Write(ref matchCounts, EmptyMatchCounts);
        Notify();
    }

    public async Task SetReadyAsync(string modKey)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("SetReady", modKey);
        UpdateRoom(state);
    }

    public async Task CancelReadyAsync()
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("CancelReady");
        UpdateRoom(state);
    }

    public async Task ForceStartAsync()
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("ForceStart");
        UpdateRoom(state);
    }

    public async Task RemoveMemberAsync(string connectionId)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("RemoveMember", connectionId);
        UpdateRoom(state);
    }

    public async Task DisconnectAsync()
    {
        var hub = connection;
        connection = null;
        lock (gate) desiredRoomCode = null;
        Volatile.Write(ref room, null);
        Volatile.Write(ref matchCounts, EmptyMatchCounts);
        if (hub is not null)
        {
            try { await hub.StopAsync(); } catch { }
            await hub.DisposeAsync();
        }
        Status = "Disconnected";
        Notify();
    }

    private HubConnection RequireConnection() => connection?.State == HubConnectionState.Connected
        ? connection
        : throw new InvalidOperationException("The relay is not connected.");

    private void UpdateRoom(RoomStateDto state)
    {
        Volatile.Write(ref room, state);
        Status = $"Room {state.RoomCode}";
        Notify();
    }

    private async Task RecoverRoomAsync(HubConnection hub)
    {
        string? code;
        string displayName;
        IReadOnlyList<string> fingerprints;
        lock (gate)
        {
            Volatile.Write(ref room, null);
            Volatile.Write(ref matchCounts, EmptyMatchCounts);
            code = desiredRoomCode;
            displayName = desiredDisplayName;
            fingerprints = catalog;
        }

        if (code is null)
        {
            Status = "Connected";
            Notify();
            return;
        }

        Status = $"Reconnected; rejoining room {code}...";
        Notify();
        try
        {
            var state = await hub.InvokeAsync<RoomStateDto>("JoinRoom", code, displayName);
            if (!ReferenceEquals(connection, hub)) return;
            UpdateRoom(state);
            await hub.InvokeAsync("SetCatalog", fingerprints);
            Diagnostic?.Invoke($"Automatically rejoined room {code} after reconnecting.", null);
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(connection, hub)) return;
            lock (gate) desiredRoomCode = null;
            Status = $"Reconnected, but room {code} could not be rejoined: {exception.GetBaseException().Message}";
            Diagnostic?.Invoke($"Could not automatically rejoin room {code}.", exception);
            Notify();
        }
    }

    private static string ConnectionStatus(string prefix, Exception? exception) => exception is null
        ? prefix
        : $"{prefix}: {exception.GetBaseException().Message}";

    private static string CleanDisplayName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim();

    private void Notify() => StateChanged?.Invoke();
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

public sealed record RoomStateDto(string RoomCode, IReadOnlyList<RoomMemberDto> Members);
public sealed record RoomMemberDto(string ConnectionId, string DisplayName, bool IsLeader, bool Ready, string ModKey);
public sealed record TransferUploadDto(string TransferId, string UploadToken);
public sealed record ModTransferOfferDto(string TransferId, string ModName, string SenderName, long Size,
    string Sha256, string DownloadToken, DateTimeOffset ExpiresAt);
public sealed record OptionSelectionDto(string MemberName, string ModKey, string Group, string Option);
public sealed record RoleLabelDto(string MemberName, string ModKey, string Group, string Option, string Label);
public sealed record CommunityRoleLabelDto(string Fingerprint, string Group, string Option, string Label);
public sealed record AnimationSuggestionDeclinedDto(string DeclinedBy, string SuggestedBy, string ModKey);
public sealed record PlaySignalDto(
    string ModKey,
    long StartUnixMilliseconds,
    string SequenceId,
    int DelayMilliseconds = 0);

using Microsoft.AspNetCore.SignalR.Client;

namespace EmoteLink;

public sealed class AnimationSyncService : IAsyncDisposable
{
    private readonly object gate = new();
    private HubConnection? connection;
    private RoomStateDto? room;
    private IReadOnlyList<string> catalog = [];
    private Dictionary<string, int> matchCounts = new(StringComparer.OrdinalIgnoreCase);

    public event Action? StateChanged;
    public event Action<PlaySignalDto>? PlayReceived;
    public string Status { get; private set; } = "Disconnected";
    public bool IsConnected => connection?.State == HubConnectionState.Connected;
    public RoomStateDto? Room { get { lock (gate) return room; } }
    public IReadOnlyDictionary<string, int> MatchCounts { get { lock (gate) return new Dictionary<string, int>(matchCounts); } }
    public bool IsInRoom => Room is not null;

    public async Task ConnectAsync(string baseUrl)
    {
        await DisconnectAsync();
        Status = "Connecting...";
        Notify();
        var hub = new HubConnectionBuilder()
            .WithUrl(baseUrl.Trim().TrimEnd('/') + "/animation")
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .Build();
        hub.On<RoomStateDto>("RoomStateChanged", UpdateRoom);
        hub.On<PlaySignalDto>("AnimationPlay", signal => PlayReceived?.Invoke(signal));
        hub.On("CatalogChanged", () => _ = RefreshMatchCountsAsync());
        hub.Reconnecting += _ => { Status = "Reconnecting..."; Notify(); return Task.CompletedTask; };
        hub.Reconnected += _ => { Status = "Connected (rejoin room if needed)"; lock (gate) { room = null; matchCounts.Clear(); } Notify(); return Task.CompletedTask; };
        hub.Closed += _ => { Status = "Disconnected"; lock (gate) { room = null; matchCounts.Clear(); } Notify(); return Task.CompletedTask; };
        connection = hub;
        try
        {
            await hub.StartAsync();
            Status = "Connected";
        }
        catch
        {
            await hub.DisposeAsync();
            if (ReferenceEquals(connection, hub)) connection = null;
            Status = "Connection failed";
            Notify();
            throw;
        }
        Notify();
    }

    public async Task<RoomStateDto> CreateRoomAsync(string displayName, IReadOnlyList<string> fingerprints)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("CreateRoom", displayName);
        UpdateRoom(state);
        await SetCatalogAsync(fingerprints);
        return state;
    }

    public async Task<RoomStateDto> JoinRoomAsync(string code, string displayName, IReadOnlyList<string> fingerprints)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("JoinRoom", code, displayName);
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

    private async Task RefreshMatchCountsAsync()
    {
        try
        {
            IReadOnlyList<string> requested;
            lock (gate) requested = catalog;
            if (!IsInRoom || connection?.State != HubConnectionState.Connected) return;
            var counts = await connection.InvokeAsync<Dictionary<string, int>>("GetMatchCounts", requested);
            lock (gate) matchCounts = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
            Notify();
        }
        catch
        {
            // Older relays do not expose private catalog matching; synchronization still works.
        }
    }

    public async Task LeaveRoomAsync()
    {
        if (connection?.State == HubConnectionState.Connected) await connection.InvokeAsync("LeaveRoom");
        lock (gate) { room = null; matchCounts.Clear(); }
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

    public async Task DisconnectAsync()
    {
        var hub = connection;
        connection = null;
        lock (gate) room = null;
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
        lock (gate) room = state;
        Status = $"Room {state.RoomCode}";
        Notify();
        _ = RefreshMatchCountsAsync();
    }

    private void Notify() => StateChanged?.Invoke();
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

public sealed record RoomStateDto(string RoomCode, IReadOnlyList<RoomMemberDto> Members);
public sealed record RoomMemberDto(string ConnectionId, string DisplayName, bool IsLeader, bool Ready, string ModKey);
public sealed record PlaySignalDto(
    string ModKey,
    long StartUnixMilliseconds,
    string SequenceId,
    int DelayMilliseconds = 0);

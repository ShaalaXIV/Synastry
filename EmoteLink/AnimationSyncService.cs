using Microsoft.AspNetCore.SignalR.Client;

namespace EmoteLink;

public sealed class AnimationSyncService : IAsyncDisposable
{
    private readonly object gate = new();
    private HubConnection? connection;
    private RoomStateDto? room;

    public event Action? StateChanged;
    public event Action<PlaySignalDto>? PlayReceived;
    public string Status { get; private set; } = "Disconnected";
    public bool IsConnected => connection?.State == HubConnectionState.Connected;
    public RoomStateDto? Room { get { lock (gate) return room; } }
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
        hub.Reconnecting += _ => { Status = "Reconnecting..."; Notify(); return Task.CompletedTask; };
        hub.Reconnected += _ => { Status = "Connected (rejoin room if needed)"; lock (gate) room = null; Notify(); return Task.CompletedTask; };
        hub.Closed += _ => { Status = "Disconnected"; lock (gate) room = null; Notify(); return Task.CompletedTask; };
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

    public async Task<RoomStateDto> CreateRoomAsync(string displayName)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("CreateRoom", displayName);
        UpdateRoom(state);
        return state;
    }

    public async Task<RoomStateDto> JoinRoomAsync(string code, string displayName)
    {
        var state = await RequireConnection().InvokeAsync<RoomStateDto>("JoinRoom", code, displayName);
        UpdateRoom(state);
        return state;
    }

    public async Task LeaveRoomAsync()
    {
        if (connection?.State == HubConnectionState.Connected) await connection.InvokeAsync("LeaveRoom");
        lock (gate) room = null;
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

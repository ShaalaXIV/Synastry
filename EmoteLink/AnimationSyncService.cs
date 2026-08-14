using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using System.Security.Cryptography;

namespace EmoteLink;

public sealed class AnimationSyncService : IAsyncDisposable
{
    private const string TransferCapabilityHeader = "X-Synastry-Transfer-Token";
    private static readonly IReadOnlyDictionary<string, int> EmptyMatchCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private HubConnection? connection;
    private RoomStateDto? room;
    private IReadOnlyList<string> catalog = [];
    // Match-count snapshots are replaced as a unit and never mutated. Returning the
    // current snapshot avoids copying the entire catalog once per visible mod, per frame.
    private IReadOnlyDictionary<string, int> matchCounts = EmptyMatchCounts;
    private int onlineUserCount = -1;
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
    public int? OnlineUserCount => Volatile.Read(ref onlineUserCount) is var count && count >= 0 ? count : null;
    public string RelayConnectionStatus => OnlineUserCount is { } count
        ? $"Connected to animation relay. {count:N0} users online"
        : "Connected to animation relay.";
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
        hub.On<string>("CatalogFingerprintChanged", fingerprint => _ = RefreshMatchCountAsync(fingerprint));
        hub.On<ModTransferOfferDto>("ModTransferOffered", offer => ModTransferOffered?.Invoke(offer));
        hub.On<OptionSelectionDto>("OptionSelectionChanged", selection => OptionSelectionChanged?.Invoke(selection));
        hub.On<RoleLabelDto>("RoleLabelChanged", label => RoleLabelChanged?.Invoke(label));
        hub.On<CommunityRoleLabelDto>("CommunityRoleLabelChanged", label => CommunityRoleLabelChanged?.Invoke(label));
        hub.On<int>("OnlineUserCountChanged", UpdateOnlineUserCount);
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
            Interlocked.Exchange(ref onlineUserCount, -1);
            Diagnostic?.Invoke("Relay connection interrupted; attempting to reconnect.", exception);
            Notify();
            return Task.CompletedTask;
        };
        hub.Reconnected += async connectionId =>
        {
            if (!ReferenceEquals(connection, hub)) return;
            Diagnostic?.Invoke($"Relay reconnected with connection ID {connectionId ?? "unknown"}.", null);
            await RefreshOnlineUserCountAsync(hub);
            await RecoverRoomAsync(hub);
        };
        hub.Closed += exception =>
        {
            if (!ReferenceEquals(connection, hub)) return Task.CompletedTask;
            Status = ConnectionStatus("Disconnected", exception);
            Interlocked.Exchange(ref onlineUserCount, -1);
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
            await RefreshOnlineUserCountAsync(hub);
            Status = RelayConnectionStatus;
        }
        catch (Exception exception)
        {
            await hub.DisposeAsync();
            if (ReferenceEquals(connection, hub)) connection = null;
            Interlocked.Exchange(ref onlineUserCount, -1);
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

    public async Task AddCatalogFingerprintAsync(string fingerprint)
    {
        var clean = CleanFingerprint(fingerprint);
        if (clean.Length != 64) return;

        IReadOnlyList<string> snapshot;
        lock (gate)
        {
            catalog = catalog
                .Append(clean)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(1000)
                .ToList();
            snapshot = catalog;
        }
        if (!IsInRoom) return;

        try
        {
            var count = await RequireConnection().InvokeAsync<int>("AddCatalogFingerprint", clean);
            UpdateMatchCount(clean, count);
        }
        catch
        {
            // Fall back to replacing the catalog when connected to an older relay.
            await SetCatalogAsync(snapshot);
        }
    }

    public async Task<ModTransferSendResult> SendModAsync(
        string modName,
        string packagePath,
        long size,
        string sha256,
        string catalogFingerprint)
    {
        var upload = await RequireConnection().InvokeAsync<TransferUploadDto>(
            "BeginModTransferV2", modName, size, sha256, catalogFingerprint);
        if (string.IsNullOrWhiteSpace(upload.TransferId) || string.IsNullOrWhiteSpace(upload.UploadToken))
            return new ModTransferSendResult(upload.PendingRecipients, upload.AlreadyReceived);
        await using var input = File.OpenRead(packagePath);
        using var content = new StreamContent(input);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{relayBaseUrl}/transfers/{Uri.EscapeDataString(upload.TransferId)}")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation(TransferCapabilityHeader, upload.UploadToken);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new ModTransferSendResult(upload.PendingRecipients, upload.AlreadyReceived);
    }

    public async Task DownloadModAsync(ModTransferOfferDto offer, string destination)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{relayBaseUrl}/transfers/{Uri.EscapeDataString(offer.TransferId)}");
        request.Headers.TryAddWithoutValidation(TransferCapabilityHeader, offer.DownloadToken);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
        string fingerprint, string group, string option, string label, string reporterId,
        string modName, string animationName)
    {
        try
        {
            await RequireConnection().InvokeAsync<CommunityRoleLabelDto?>(
                "SubmitCommunityRoleLabelV2", fingerprint, group, option, label, reporterId,
                modName, animationName);
        }
        catch
        {
            try
            {
                await RequireConnection().InvokeAsync<CommunityRoleLabelDto?>(
                    "SubmitCommunityRoleLabel", fingerprint, group, option, label, reporterId);
            }
            catch { /* Community labels are optional when connected to an older relay. */ }
        }
    }

    public async Task RegisterCommunityRoleMetadataAsync(
        string fingerprint, string group, string option, string modName, string animationName)
    {
        try
        {
            await RequireConnection().InvokeAsync(
                "RegisterCommunityRoleMetadata", fingerprint, group, option, modName, animationName);
        }
        catch { /* Display metadata is optional when connected to an older relay. */ }
    }

    public async Task<IReadOnlyList<AnimationArtifactCatalogEntryDto>?> LookupAnimationArtifactsAsync(
        IReadOnlyList<AnimationArtifactLookupKeyDto> artifacts,
        CancellationToken cancellationToken = default)
    {
        if (artifacts.Count == 0) return [];
        var hub = connection;
        if (hub?.State != HubConnectionState.Connected) return null;
        try
        {
            return await hub.InvokeCoreAsync<IReadOnlyList<AnimationArtifactCatalogEntryDto>>(
                "LookupAnimationArtifacts", [artifacts], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Catalog acceleration is additive. An offline or older relay follows the exact
            // same local extraction path as releases that predate the shared catalog.
            return null;
        }
    }

    public async Task<IReadOnlyList<AnimationArtifactCatalogEntryDto>?> SubmitAnimationArtifactReportsAsync(
        string reporterId,
        IReadOnlyList<AnimationArtifactReportSubmissionDto> reports,
        CancellationToken cancellationToken = default)
    {
        if (reports.Count == 0) return [];
        var hub = connection;
        if (hub?.State != HubConnectionState.Connected) return null;
        try
        {
            return await hub.InvokeCoreAsync<IReadOnlyList<AnimationArtifactCatalogEntryDto>>(
                "SubmitAnimationArtifactReports", [reporterId, reports], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
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

    private async Task RefreshMatchCountAsync(string fingerprint)
    {
        try
        {
            var clean = CleanFingerprint(fingerprint);
            IReadOnlyList<string> requested;
            lock (gate)
                requested = catalog.Contains(clean, StringComparer.OrdinalIgnoreCase) ? [clean] : [];
            if (requested.Count == 0 || !IsInRoom || connection?.State != HubConnectionState.Connected) return;
            var counts = await connection.InvokeAsync<Dictionary<string, int>>("GetMatchCounts", requested);
            if (counts.TryGetValue(clean, out var count)) UpdateMatchCount(clean, count);
        }
        catch
        {
            // Incremental catalog updates are optional when connected to an older relay.
        }
    }

    private void UpdateMatchCount(string fingerprint, int count)
    {
        lock (gate)
        {
            var updated = new Dictionary<string, int>(matchCounts, StringComparer.OrdinalIgnoreCase)
            {
                [fingerprint] = count
            };
            Volatile.Write(ref matchCounts, updated);
        }
        Notify();
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
        Interlocked.Exchange(ref onlineUserCount, -1);
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
            Status = RelayConnectionStatus;
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

    private static string CleanFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Take(64).Select(char.ToUpperInvariant).ToArray());

    private async Task RefreshOnlineUserCountAsync(HubConnection hub)
    {
        try
        {
            UpdateOnlineUserCount(await hub.InvokeAsync<int>("GetOnlineUserCount"));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref onlineUserCount, -1);
            Diagnostic?.Invoke("The connected relay does not expose an online-user count.", exception);
        }
    }

    private void UpdateOnlineUserCount(int count)
    {
        Interlocked.Exchange(ref onlineUserCount, Math.Max(0, count));
        if (IsConnected && !IsInRoom) Status = RelayConnectionStatus;
        Notify();
    }

    private void Notify() => StateChanged?.Invoke();
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

public sealed record RoomStateDto(string RoomCode, IReadOnlyList<RoomMemberDto> Members);
public sealed record RoomMemberDto(string ConnectionId, string DisplayName, bool IsLeader, bool Ready, string ModKey);
public sealed record TransferUploadDto(
    string TransferId,
    string UploadToken,
    int PendingRecipients = 0,
    int AlreadyReceived = 0);
public sealed record ModTransferOfferDto(string TransferId, string ModName, string SenderName, long Size,
    string Sha256, string DownloadToken, DateTimeOffset ExpiresAt, string CatalogFingerprint = "");
public sealed record ModTransferSendResult(int PendingRecipients, int AlreadyReceived);
public sealed record OptionSelectionDto(string MemberName, string ModKey, string Group, string Option);
public sealed record RoleLabelDto(string MemberName, string ModKey, string Group, string Option, string Label);
public sealed record CommunityRoleLabelDto(string Fingerprint, string Group, string Option, string Label);
public enum AnimationArtifactClassificationDto
{
    Unknown = 0,
    Animation = 1,
    NonAnimation = 2
}
public enum AnimationSharingPolicyDto
{
    Default = 0,
    Allowed = 1,
    CatalogOnlyBlocked = 2
}
public sealed record AnimationArtifactLookupKeyDto(string SignatureAlgorithm, string Signature);
public sealed record PortableAnimationPayloadSubmissionDto(int SchemaVersion, string ExtractorVersion, string Json);
public sealed record AnimationArtifactReportSubmissionDto(
    string Signature,
    string SignatureAlgorithm,
    string DisplayName,
    AnimationArtifactClassificationDto Classification,
    int ManifestFileCount,
    long ManifestBytes,
    PortableAnimationPayloadSubmissionDto? Payload = null);
public sealed record PortableAnimationPayloadDto(
    int SchemaVersion,
    string ExtractorVersion,
    string Sha256,
    string Json,
    int VerificationReports);
public sealed record AnimationArtifactCatalogEntryDto(
    string ArtifactKey,
    string Signature,
    string SignatureAlgorithm,
    IReadOnlyList<string> Names,
    int ManifestFileCount,
    long ManifestBytes,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    AnimationArtifactClassificationDto ConsensusClassification,
    AnimationArtifactClassificationDto EffectiveClassification,
    double Confidence,
    int AnimationReports,
    int NonAnimationReports,
    AnimationSharingPolicyDto SharingPolicy,
    string OverrideReasonCode,
    string OverrideNote,
    PortableAnimationPayloadDto? Payload,
    bool IsModeratorVerified = false,
    bool IsPayloadModeratorVerified = false);
public sealed record AnimationSuggestionDeclinedDto(string DeclinedBy, string SuggestedBy, string ModKey);
public sealed record PlaySignalDto(
    string ModKey,
    long StartUnixMilliseconds,
    string SequenceId,
    int DelayMilliseconds = 0);

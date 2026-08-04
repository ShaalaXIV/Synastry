using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;

var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://127.0.0.1:25080";
await using var first = new HubConnectionBuilder().WithUrl(baseUrl + "/animation", options =>
    options.Transports = HttpTransportType.LongPolling).Build();
await using var second = new HubConnectionBuilder().WithUrl(baseUrl + "/animation", options =>
    options.Transports = HttpTransportType.LongPolling).Build();
await using var third = new HubConnectionBuilder().WithUrl(baseUrl + "/animation", options =>
    options.Transports = HttpTransportType.LongPolling).Build();
await using var fourth = new HubConnectionBuilder().WithUrl(baseUrl + "/animation", options =>
    options.Transports = HttpTransportType.LongPolling).Build();
await using var fifth = new HubConnectionBuilder().WithUrl(baseUrl + "/animation", options =>
    options.Transports = HttpTransportType.LongPolling).Build();
var firstPlay = new TaskCompletionSource<PlaySignalDto>(TaskCreationOptions.RunContinuationsAsynchronously);
var secondPlay = new TaskCompletionSource<PlaySignalDto>(TaskCreationOptions.RunContinuationsAsynchronously);
first.On<PlaySignalDto>("AnimationPlay", signal => firstPlay.TrySetResult(signal));
second.On<PlaySignalDto>("AnimationPlay", signal => secondPlay.TrySetResult(signal));
await first.StartAsync();
await second.StartAsync();
await third.StartAsync();
await fourth.StartAsync();
await fifth.StartAsync();
var room = await first.InvokeAsync<RoomStateDto>("CreateRoom", "Top");
await second.InvokeAsync<RoomStateDto>("JoinRoom", room.RoomCode, "Bottom");
var sharedFingerprint = new string('A', 64);
var firstOnlyFingerprint = new string('B', 64);
var secondOnlyFingerprint = new string('C', 64);
await first.InvokeAsync("SetCatalog", new[] { sharedFingerprint, firstOnlyFingerprint });
await second.InvokeAsync("SetCatalog", new[] { sharedFingerprint, secondOnlyFingerprint });
var firstMatches = await first.InvokeAsync<Dictionary<string, int>>(
    "GetMatchCounts", new[] { sharedFingerprint, firstOnlyFingerprint });
if (firstMatches.Count != 2 || firstMatches[sharedFingerprint] != 2 || firstMatches[firstOnlyFingerprint] != 1 ||
    firstMatches.ContainsKey(secondOnlyFingerprint))
    throw new InvalidOperationException("Private catalog match counts were incorrect.");
const string modKey = "deep plaps:0123456789ABCDEF";
var suggestionDeclined = new TaskCompletionSource<AnimationSuggestionDeclinedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
first.On<AnimationSuggestionDeclinedDto>("AnimationSuggestionDeclined", decline => suggestionDeclined.TrySetResult(decline));
await first.InvokeAsync("SetOptionSelection", modKey, "Actor", "Chair Sit 1");
var receivedRole = new TaskCompletionSource<RoleLabelDto>(TaskCreationOptions.RunContinuationsAsynchronously);
second.On<RoleLabelDto>("RoleLabelChanged", label => receivedRole.TrySetResult(label));
await first.InvokeAsync("SetRoleLabel", modKey, "$detected-pose", "GroundSit:1", "Driver");
var sharedRole = await receivedRole.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (sharedRole.MemberName != "Top" || sharedRole.ModKey != modKey || sharedRole.Group != "$detected-pose" ||
    sharedRole.Option != "GroundSit:1" || sharedRole.Label != "Driver")
    throw new InvalidOperationException("Role label was not broadcast correctly.");
var storedRoles = await second.InvokeAsync<IReadOnlyList<RoleLabelDto>>("GetRoleLabels");
if (storedRoles.Count != 1 || storedRoles[0] != sharedRole)
    throw new InvalidOperationException("Role label was not retained for room members.");
Console.WriteLine("PASS role-label broadcast and retrieval");
var communityChanged = new TaskCompletionSource<CommunityRoleLabelDto>(TaskCreationOptions.RunContinuationsAsynchronously);
second.On<CommunityRoleLabelDto>("CommunityRoleLabelChanged", label => communityChanged.TrySetResult(label));
var communityFingerprint = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var firstReporter = Guid.NewGuid().ToString("N");
var secondReporter = Guid.NewGuid().ToString("N");
var thirdReporter = Guid.NewGuid().ToString("N");
var fourthReporter = Guid.NewGuid().ToString("N");
var fifthReporter = Guid.NewGuid().ToString("N");
await first.InvokeAsync<CommunityRoleLabelDto?>("SubmitCommunityRoleLabel", communityFingerprint,
    "$detected-pose", "GroundSit:1", "Passenger", firstReporter);
var acceptedCommunityRole = await communityChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (acceptedCommunityRole.Fingerprint != communityFingerprint || acceptedCommunityRole.Label != "Passenger")
    throw new InvalidOperationException("The first community role label was not accepted immediately.");
var communityRoles = await second.InvokeAsync<IReadOnlyList<CommunityRoleLabelDto>>(
    "GetCommunityRoleLabels", new[] { communityFingerprint });
if (communityRoles.Count != 1 || communityRoles[0] != acceptedCommunityRole)
    throw new InvalidOperationException("Accepted community role label was not persisted.");
var correctionChanged = new TaskCompletionSource<CommunityRoleLabelDto>(TaskCreationOptions.RunContinuationsAsynchronously);
second.On<CommunityRoleLabelDto>("CommunityRoleLabelChanged", label =>
{
    if (label.Fingerprint == communityFingerprint && label.Label == "Camera") correctionChanged.TrySetResult(label);
});
await first.InvokeAsync<CommunityRoleLabelDto?>("SubmitCommunityRoleLabel", communityFingerprint,
    "$detected-pose", "GroundSit:1", "Camera", firstReporter);
await second.InvokeAsync<CommunityRoleLabelDto?>("SubmitCommunityRoleLabel", communityFingerprint,
    "$detected-pose", "GroundSit:1", "Camera", secondReporter);
await third.InvokeAsync<CommunityRoleLabelDto?>("SubmitCommunityRoleLabel", communityFingerprint,
    "$detected-pose", "GroundSit:1", "Camera", thirdReporter);
await fourth.InvokeAsync<CommunityRoleLabelDto?>("SubmitCommunityRoleLabel", communityFingerprint,
    "$detected-pose", "GroundSit:1", "Camera", fourthReporter);
await fifth.InvokeAsync<CommunityRoleLabelDto?>("SubmitCommunityRoleLabel", communityFingerprint,
    "$detected-pose", "GroundSit:1", "Camera", fifthReporter);
await correctionChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("PASS community role-label consensus and retrieval");
await second.InvokeAsync("DeclineAnimationSuggestion", modKey, "Top");
var decline = await suggestionDeclined.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (decline.DeclinedBy != "Bottom" || decline.SuggestedBy != "Top" || decline.ModKey != modKey)
    throw new InvalidOperationException("Animation suggestion decline was not broadcast correctly.");
await first.InvokeAsync<RoomStateDto>("SetReady", modKey);
try
{
    await second.InvokeAsync<RoomStateDto>("ForceStart");
    throw new InvalidOperationException("A non-host member was allowed to force playback.");
}
catch (Microsoft.AspNetCore.SignalR.HubException)
{
    // Expected: only the room host can force playback.
}
await first.InvokeAsync<RoomStateDto>("ForceStart");
var forcedPlays = await Task.WhenAll(firstPlay.Task.WaitAsync(TimeSpan.FromSeconds(5)),
    secondPlay.Task.WaitAsync(TimeSpan.FromSeconds(5)));
if (forcedPlays[0].SequenceId != forcedPlays[1].SequenceId || forcedPlays[0].ModKey != modKey)
    throw new InvalidOperationException("Forced playback did not reach the room consistently.");

firstPlay = new TaskCompletionSource<PlaySignalDto>(TaskCreationOptions.RunContinuationsAsynchronously);
secondPlay = new TaskCompletionSource<PlaySignalDto>(TaskCreationOptions.RunContinuationsAsynchronously);
await first.InvokeAsync<RoomStateDto>("SetReady", modKey);
await second.InvokeAsync<RoomStateDto>("SetReady", modKey);
var plays = await Task.WhenAll(firstPlay.Task.WaitAsync(TimeSpan.FromSeconds(5)),
    secondPlay.Task.WaitAsync(TimeSpan.FromSeconds(5)));
if (plays[0].SequenceId != plays[1].SequenceId ||
    plays[0].StartUnixMilliseconds != plays[1].StartUnixMilliseconds ||
    plays[0].DelayMilliseconds != plays[1].DelayMilliseconds)
    throw new InvalidOperationException("Clients received different play signals.");
if (plays[0].DelayMilliseconds <= 0)
    throw new InvalidOperationException("Relay did not provide a relative play countdown.");
var removed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
second.On<string>("RemovedFromRoom", reason => removed.TrySetResult(reason));
var afterRemoval = await first.InvokeAsync<RoomStateDto>("RemoveMember", second.ConnectionId!);
await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (afterRemoval.Members.Count != 1 || !afterRemoval.Members[0].IsLeader)
    throw new InvalidOperationException("Host removal did not update the room correctly.");
try
{
    await second.InvokeAsync<RoomStateDto>("SetReady", modKey);
    throw new InvalidOperationException("A removed member could still ready in the room.");
}
catch (Microsoft.AspNetCore.SignalR.HubException)
{
    // Expected: the removed connection is no longer associated with the room.
}
await first.StopAsync();
await second.StopAsync();
await using var recovered = new HubConnectionBuilder().WithUrl(baseUrl + "/animation", options =>
    options.Transports = HttpTransportType.LongPolling).Build();
await recovered.StartAsync();
var recoveredRoom = await recovered.InvokeAsync<RoomStateDto>("JoinRoom", room.RoomCode, "Top");
if (recoveredRoom.Members.Count != 1 || !recoveredRoom.Members[0].IsLeader)
    throw new InvalidOperationException("An empty room was not retained for reconnect recovery.");
Console.WriteLine($"PASS room={room.RoomCode} sequence={plays[0].SequenceId} delay={plays[0].DelayMilliseconds}ms");

public sealed record RoomStateDto(string RoomCode, IReadOnlyList<RoomMemberDto> Members);
public sealed record RoomMemberDto(string ConnectionId, string DisplayName, bool IsLeader, bool Ready, string ModKey);
public sealed record PlaySignalDto(
    string ModKey,
    long StartUnixMilliseconds,
    string SequenceId,
    int DelayMilliseconds = 0);
public sealed record AnimationSuggestionDeclinedDto(string DeclinedBy, string SuggestedBy, string ModKey);
public sealed record RoleLabelDto(string MemberName, string ModKey, string Group, string Option, string Label);
public sealed record CommunityRoleLabelDto(string Fingerprint, string Group, string Option, string Label);

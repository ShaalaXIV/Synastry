using Microsoft.AspNetCore.SignalR.Client;

var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://127.0.0.1:25080";
await using var first = new HubConnectionBuilder().WithUrl(baseUrl + "/animation").Build();
await using var second = new HubConnectionBuilder().WithUrl(baseUrl + "/animation").Build();
var firstPlay = new TaskCompletionSource<PlaySignalDto>(TaskCreationOptions.RunContinuationsAsynchronously);
var secondPlay = new TaskCompletionSource<PlaySignalDto>(TaskCreationOptions.RunContinuationsAsynchronously);
first.On<PlaySignalDto>("AnimationPlay", signal => firstPlay.TrySetResult(signal));
second.On<PlaySignalDto>("AnimationPlay", signal => secondPlay.TrySetResult(signal));
await first.StartAsync();
await second.StartAsync();
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
await first.StopAsync();
await second.StopAsync();
await using var recovered = new HubConnectionBuilder().WithUrl(baseUrl + "/animation").Build();
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

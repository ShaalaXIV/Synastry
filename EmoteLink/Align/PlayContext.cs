namespace EmoteLink.Align;

/// <summary>
/// The animation your character is playing through Synastry, from the moment it really started (a
/// room can be ready long before). Trigger is "pose:Kind:Index" or "emote:Id"; Selections are the mod
/// options it was played with.
/// </summary>
public sealed record PlayContext(
    string Directory,
    string ModName,
    string Fingerprint,
    string SyncKey,
    string Trigger,
    string Animation,
    IReadOnlyDictionary<string, List<string>> Selections,
    long StartedAt);

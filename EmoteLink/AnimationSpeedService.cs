using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace EmoteLink;

/// <summary>
/// Holds a character's animation speed where Synastry set it: the game works the speed out again as
/// the animation plays, and this puts Synastry's back on top each time. Works for you and for room
/// partners whose speed their Synastry shared. LivePose (inside Simple Heels) hooks the same
/// calculation for its own speed, so a LivePose speed on the same character can win over this one.
/// </summary>
public sealed unsafe class AnimationSpeedService : IDisposable
{
    private delegate void CalculateAndApplyOverallSpeedDelegate(TimelineContainer* timeline);

    [Signature("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83",
        DetourName = nameof(CalculateAndApplyOverallSpeedDetour))]
    private Hook<CalculateAndApplyOverallSpeedDelegate>? calculateAndApplyOverallSpeedHook { get; init; }

    private readonly IObjectTable objects;
    private readonly Func<nint, float?> getSpeedOverride;
    private readonly HashSet<uint> adjustableTimelines =
    [
        3, 3124, 3126, 3182, 3184, 7405, 7407
    ];

    public AnimationSpeedService(
        IGameInteropProvider interop,
        IObjectTable objects,
        IDataManager data,
        Func<nint, float?> getSpeedOverride)
    {
        this.objects = objects;
        this.getSpeedOverride = getSpeedOverride;

        foreach (var emote in data.GetExcelSheet<Emote>())
        foreach (var timeline in emote.ActionTimeline)
            if (timeline is { RowId: > 0, IsValid: true } && timeline.Value.IsLoop)
                adjustableTimelines.Add(timeline.RowId);

        interop.InitializeFromAttributes(this);
        if (calculateAndApplyOverallSpeedHook is null)
            throw new InvalidOperationException("The animation-speed calculation hook could not be resolved.");
        calculateAndApplyOverallSpeedHook.Enable();
    }

    private void CalculateAndApplyOverallSpeedDetour(TimelineContainer* timeline)
    {
        calculateAndApplyOverallSpeedHook!.Original(timeline);
        if (timeline is null || timeline->OwnerObject is null) return;
        if (getSpeedOverride((nint)timeline->OwnerObject) is not { } speed) return;
        // Only a loop or a standing pose: speeding up walking or a one-off action would break it. A
        // Synastry animation can play on a timeline no emote lists (a carrier, a direct playback), so
        // being in a looping emote or seat counts as well.
        var character = timeline->OwnerObject;
        if (!adjustableTimelines.Contains(timeline->TimelineSequencer.TimelineIds[0]) &&
            !adjustableTimelines.Contains(timeline->BaseOverride) &&
            character->Mode is not (CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)) return;
        timeline->OverallSpeed = Math.Clamp(speed, -2f, 2f);
    }

    public void Dispose() => calculateAndApplyOverallSpeedHook?.Dispose();
}

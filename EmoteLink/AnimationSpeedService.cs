using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace EmoteLink;

public sealed unsafe class AnimationSpeedService : IDisposable
{
    private delegate void CalculateAndApplyOverallSpeedDelegate(TimelineContainer* timeline);

    [Signature("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83",
        DetourName = nameof(CalculateAndApplyOverallSpeedDetour))]
    private Hook<CalculateAndApplyOverallSpeedDelegate>? calculateAndApplyOverallSpeedHook { get; init; }

    private readonly IObjectTable objects;
    private readonly Func<float?> getSpeedOverride;
    private readonly HashSet<uint> adjustableTimelines =
    [
        3, 3124, 3126, 3182, 3184, 7405, 7407
    ];

    public AnimationSpeedService(
        IGameInteropProvider interop,
        IObjectTable objects,
        IDataManager data,
        Func<float?> getSpeedOverride)
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

        var player = objects.LocalPlayer;
        if (player is null || (nint)timeline->OwnerObject != player.Address) return;
        if (!adjustableTimelines.Contains(timeline->TimelineSequencer.TimelineIds[0])) return;

        if (getSpeedOverride() is { } speed)
            timeline->OverallSpeed = Math.Clamp(speed, -2f, 2f);
    }

    public void Dispose() => calculateAndApplyOverallSpeedHook?.Dispose();
}

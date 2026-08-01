using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace EmoteLink;

public enum PoseKind { Idle, Sit, GroundSit, Doze }
public sealed record PoseTarget(PoseKind Kind, byte Index);

public sealed unsafe class PoseService
{
    private readonly IObjectTable objects;

    public PoseService(IObjectTable objects) => this.objects = objects;

    public void SetIndex(PoseTarget target)
    {
        var state = PlayerState.Instance();
        if (state is null) return;
        state->SelectedPoses[(int)ToGameType(target.Kind)] = target.Index;
    }

    public PoseKind? CurrentKind()
    {
        var character = (Character*)(objects.LocalPlayer?.Address ?? 0);
        if (character is null) return null;
        if (character->Mode == CharacterModes.Normal) return PoseKind.Idle;
        if (character->Mode != CharacterModes.InPositionLoop) return null;
        return character->ModeParam switch
        {
            1 => PoseKind.GroundSit,
            2 => PoseKind.Sit,
            3 => PoseKind.Doze,
            _ => null
        };
    }

    public byte CurrentIndex()
    {
        var character = (Character*)(objects.LocalPlayer?.Address ?? 0);
        return character is null ? (byte)0 : character->EmoteController.CPoseState;
    }

    private static EmoteController.PoseType ToGameType(PoseKind kind) => kind switch
    {
        PoseKind.Sit => EmoteController.PoseType.Sit,
        PoseKind.GroundSit => EmoteController.PoseType.GroundSit,
        PoseKind.Doze => EmoteController.PoseType.Doze,
        _ => EmoteController.PoseType.Idle
    };
}

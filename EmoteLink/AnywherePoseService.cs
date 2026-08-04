using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace EmoteLink;

public sealed unsafe class AnywherePoseService : IDisposable
{
    [Signature("E8 ?? ?? ?? ?? 40 84 ED 74 ?? 48 8B 4B ?? 48 8B 01 FF 90")]
    private readonly delegate* unmanaged<nint, ushort, nint, byte, byte, void> useEmote = null!;

    private delegate byte ShouldSnap(Character* character, SnapPosition* snapPosition);

    [Signature("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 4C 8D 74 24", DetourName = nameof(ShouldSnapDetour))]
    private Hook<ShouldSnap>? shouldSnapHook { get; init; }

    [Signature("48 83 EC 38 F3 0F 10 05 ?? ?? ?? ?? 45 33 C9", DetourName = nameof(ShouldSnapUnsitDetour))]
    private Hook<ShouldSnap>? shouldSnapUnsitHook { get; init; }

    private readonly IObjectTable objects;
    private bool suppressSnap;
    private System.Numerics.Vector3? savedSitPosition;
    private float? savedSitRotation;

    public AnywherePoseService(IGameInteropProvider interop, IObjectTable objects)
    {
        this.objects = objects;
        interop.InitializeFromAttributes(this);
        shouldSnapHook?.Enable();
        shouldSnapUnsitHook?.Enable();
    }

    public void EnterDozePose()
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Emote);
        if (agent is null) return;
        suppressSnap = true;
        try
        {
            useEmote((nint)agent, 88, nint.Zero, 0, 0);
        }
        finally
        {
            suppressSnap = false;
        }
    }

    public void EnterChairPose()
    {
        var player = (Character*)(objects.LocalPlayer?.Address ?? nint.Zero);
        if (player is null) return;
        savedSitPosition = player->GameObject.Position;
        savedSitRotation = player->GameObject.Rotation;
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Emote);
        if (agent is null) return;
        useEmote((nint)agent, 96, nint.Zero, 0, 0);
    }

    private byte ShouldSnapDetour(Character* character, SnapPosition* snapPosition) =>
        suppressSnap ? (byte)0 : shouldSnapHook!.Original(character, snapPosition);

    private byte ShouldSnapUnsitDetour(Character* player, SnapPosition* snapPosition)
    {
        var result = shouldSnapUnsitHook!.Original(player, snapPosition);
        if (result != 0 && savedSitPosition is { } position && savedSitRotation is { } rotation)
        {
            if (Vector3.Distance(player->GameObject.Position, position) < 3f)
            {
                snapPosition->PositionB = position;
                snapPosition->RotationB = rotation;
            }
            savedSitPosition = null;
            savedSitRotation = null;
        }
        return result;
    }

    public void Dispose()
    {
        shouldSnapHook?.Dispose();
        shouldSnapUnsitHook?.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x38)]
    private struct SnapPosition
    {
        [FieldOffset(0x00)] public Vector3 PositionA;
        [FieldOffset(0x10)] public float RotationA;
        [FieldOffset(0x20)] public Vector3 PositionB;
        [FieldOffset(0x30)] public float RotationB;
    }
}

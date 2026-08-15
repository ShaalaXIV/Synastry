using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System.Runtime.InteropServices;

namespace EmoteLink;

/// <summary>
/// Plays an emote's real action timeline without asking the game server to execute
/// the corresponding unlocked emote. The parameter layout and playback sequence are
/// adapted from Aspher0/BypassEmote and Brio under AGPL-compatible licensing.
/// </summary>
internal static unsafe class ActionTimelinePlayback
{
    [StructLayout(LayoutKind.Explicit, Size = 0x60)]
    private struct AnimationParameters
    {
        [FieldOffset(0x24)] public float Intensity;
        [FieldOffset(0x2C)] public float Unknown1C;
        [FieldOffset(0x38)] public ulong TargetObjectId;
        [FieldOffset(0x44)] public uint Priority;
        [FieldOffset(0x48)] public int Unknown38;
        [FieldOffset(0x4C)] public byte Unknown3C;
    }

    public static bool Play(nint actorAddress, ushort timelineId, uint priority = 0)
    {
        if (actorAddress == 0 || timelineId == 0) return false;
        var actor = (Character*)actorAddress;
        if (actor is null) return false;

        var parameters = (AnimationParameters*)NativeMemory.AllocZeroed((nuint)sizeof(AnimationParameters));
        try
        {
            parameters->Intensity = 1f;
            parameters->Unknown1C = -1f;
            parameters->TargetObjectId = 0xE0000000;
            parameters->Priority = priority;
            parameters->Unknown38 = -1;
            parameters->Unknown3C = timelineId == 3123 ? (byte)0 : (byte)0xFF;
            actor->Timeline.TimelineSequencer.PlayTimeline(timelineId, parameters);
            return true;
        }
        finally
        {
            NativeMemory.Free(parameters);
        }
    }

    public static bool Start(nint actorAddress, EmotePlayback playback, out ushort originalBaseOverride)
    {
        originalBaseOverride = 0;
        if (actorAddress == 0 || playback.MainTimeline == 0) return false;
        var actor = (Character*)actorAddress;
        if (actor is null) return false;
        originalBaseOverride = actor->Timeline.BaseOverride;

        if (!playback.IsLoop) return Play(actorAddress, playback.MainTimeline);
        if (playback.IntroTimeline != 0) Play(actorAddress, playback.IntroTimeline, 1);
        actor->Timeline.BaseOverride = playback.MainTimeline;
        return true;
    }

    public static void Stop(nint actorAddress, ushort originalBaseOverride)
    {
        if (actorAddress == 0) return;
        var actor = (Character*)actorAddress;
        if (actor is null) return;
        actor->Timeline.BaseOverride = originalBaseOverride;
        Play(actorAddress, 3);
    }
}

internal sealed record EmotePlayback(uint EmoteId, ushort MainTimeline, ushort IntroTimeline, bool IsLoop);

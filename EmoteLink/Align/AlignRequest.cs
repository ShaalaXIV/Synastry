using System.Numerics;

namespace EmoteLink.Align;

/// <summary>Where a bone align came from: a button, alignment memory, or the check the second of two
/// remembered partners makes before moving.</summary>
internal enum AlignOrigin { Manual, Memory, Check }

/// <summary>Align moves you; Measure only reads how you are placed now.</summary>
internal enum AlignMode { Align, Measure }

/// <summary>What one bone align should do.</summary>
/// <param name="RelativeYaw">Your drawn facing minus your partner's, turned to before measuring; null
/// keeps the facing you have.</param>
/// <param name="ContactOffset">Where your part belongs relative to your partner's, in their drawn
/// frame; null stops <paramref name="Gap"/> short of theirs.</param>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
internal sealed record AlignRequest(
    BodyPart Self,
    BodyPart Partner,
    float Gap,
    float? RelativeYaw,
    Vector3? ContactOffset,
    AlignOrigin Origin,
    AlignMode Mode = AlignMode.Align)
{
    public static AlignRequest FromMemory(AlignmentEntry entry) => new(
        entry.Self, entry.Partner, entry.Gap, entry.RelativeYaw, entry.ContactOffset.ToVector3(), AlignOrigin.Memory);
}

/// <summary>How an align or measure ended, relative to your partner.</summary>
/// <param name="RelativeYaw">Your drawn facing minus your partner's, in radians.</param>
/// <param name="ContactOffset">Your part minus your partner's, in their drawn frame.</param>
internal sealed record AlignResult(float RelativeYaw, Vector3 ContactOffset);

using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using EmoteLink.Align;

namespace EmoteLink.Places;

/// <summary>
/// Follows the partner of a place while they settle into their animation, and stops for good once they
/// leave it or walk off: you stay where you're drawn instead of being dragged along. Uses their real
/// position, which their own offsets don't change.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class PartnerTracker
{
    // Smaller changes are treated as network jitter.
    public const float PositionThreshold = 0.02f;
    public const float RotationThreshold = MathF.PI / 180f;
    // Keeps a moving partner from sending a Simple Heels command every frame.
    private const long ReapplyIntervalMs = 250;
    // Covers a partner who starts a beat later.
    private const long SettleGraceMs = 5000;
    // Inside a looping animation the real position stays put, so more than this means they walked off.
    private const float SettledMoveThreshold = 0.5f;

    private (Vector3 Partner, float PartnerYaw, Vector3 Own, float OwnYaw)? last;
    private Vector3? settled;
    private long foundAt;
    private long appliedAt;

    /// <summary>Following has ended for this play.</summary>
    public bool Frozen { get; set; }

    public static IPlayerCharacter? Find(IObjectTable objects, string name, uint worldId)
    {
        var local = objects.LocalPlayer;
        foreach (var candidate in objects)
            if (candidate is IPlayerCharacter player && player.Address != local?.Address &&
                player.HomeWorld.RowId == worldId && player.Name.TextValue.Equals(name, StringComparison.Ordinal))
                return player;
        return null;
    }

    /// <summary>Where you both were when the place was last worked out.</summary>
    public void Remember(IGameObject partner, IGameObject own, long now)
    {
        last = (partner.Position, partner.Rotation, own.Position, own.Rotation);
        appliedAt = now;
        if (foundAt == 0) foundAt = now;
    }

    /// <summary>
    /// Freezes once the partner has left their animation. One never seen looping within the grace period
    /// counts as left too: what they're doing isn't something to stay matched to. Check this before
    /// following, so the frame they start walking doesn't take you with them.
    /// </summary>
    public bool CheckLeft(ICharacter partner, long now)
    {
        if (Frozen) return true;
        var looping = DrawnModel.IsLooping(partner);
        if (settled is { } spot)
            return Frozen = !looping || Vector3.Distance(partner.Position, spot) > SettledMoveThreshold;

        // Settled only once they're also still, so a slide into a seat lands before the spot is taken.
        if (looping && last is { } seen && Vector3.Distance(partner.Position, seen.Partner) <= PositionThreshold)
        {
            settled = partner.Position;
            return false;
        }
        return Frozen = foundAt != 0 && now - foundAt >= SettleGraceMs;
    }

    /// <summary>Either of you moved since the place was last worked out, and that was long enough ago.</summary>
    public bool ShouldFollow(IGameObject partner, IGameObject own, long now) =>
        now - appliedAt >= ReapplyIntervalMs && last is { } seen &&
        (Vector3.Distance(partner.Position, seen.Partner) > PositionThreshold ||
         Vector3.Distance(own.Position, seen.Own) > PositionThreshold ||
         MathF.Abs(DrawnModel.Wrap(partner.Rotation - seen.PartnerYaw)) > RotationThreshold ||
         MathF.Abs(DrawnModel.Wrap(own.Rotation - seen.OwnYaw)) > RotationThreshold);
}

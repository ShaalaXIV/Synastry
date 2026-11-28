using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using EmoteLink.Ik;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace EmoteLink.Align;

/// <summary>A body part bone align can line up. Saved by value in the settings, so only append.</summary>
public enum BodyPart
{
    Penis,
    Vagina,
    Anus,
    LeftButtock,
    RightButtock,
    Mouth,
    LeftHand,
    RightHand,
    LeftFingers,
    RightFingers,
    LeftFoot,
    RightFoot,
    LeftToes,
    RightToes,
}

/// <summary>Where each body part is: its name in the panel and the bones it is measured at.</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.
/// Reads through Synastry's SkeletonView, and adds the other shaft chain and older mouth bones.</remarks>
internal static class BodyParts
{
    // YAS gives every body the full set; a shaft shorter than this is one it doesn't use, collapsed
    // onto one point.
    private const float MinShaftLength = 0.05f;

    /// <summary>List order.</summary>
    public static readonly BodyPart[] All = Enum.GetValues<BodyPart>();

    // Tried in order; the first group with any bone present is averaged. iv_* bones come from the
    // IVCS skeleton, j_* from the game's own.
    private static readonly Dictionary<BodyPart, string[][]> Groups = new()
    {
        // The tip, on either chain a body may use.
        [BodyPart.Penis] = [["iv_ochinko_f"], ["iv_ochinko_e"], ["iv_funyachin_phy_d"], ["iv_funyachin_phy_c"]],
        [BodyPart.Vagina] = [["iv_omanko"]],
        [BodyPart.Anus] = [["iv_koumon"]],
        [BodyPart.LeftButtock] = [["iv_shiri_l"], ["j_shiri_l"]],
        [BodyPart.RightButtock] = [["iv_shiri_r"], ["j_shiri_r"]],
        // The middle of the lips. Most faces have four lip bones; some older faces use a single pair.
        [BodyPart.Mouth] =
        [
            ["j_f_ulip_01_l", "j_f_ulip_01_r", "j_f_dlip_01_l", "j_f_dlip_01_r"],
            ["j_f_ulip_a", "j_f_dlip_a"],
            ["j_f_lip_l", "j_f_lip_r"],
        ],
        [BodyPart.LeftHand] = [["j_te_l"]],
        [BodyPart.RightHand] = [["j_te_r"]],
        // Between the index and middle fingertips.
        [BodyPart.LeftFingers] = [["iv_hito_c_l", "iv_naka_c_l"], ["j_hito_b_l", "j_naka_b_l"]],
        [BodyPart.RightFingers] = [["iv_hito_c_r", "iv_naka_c_r"], ["j_hito_b_r", "j_naka_b_r"]],
        // The middle of the sole, between ankle and toe base.
        [BodyPart.LeftFoot] = [["j_asi_d_l", "j_asi_e_l"]],
        [BodyPart.RightFoot] = [["j_asi_d_r", "j_asi_e_r"]],
        // The five toe tips averaged.
        [BodyPart.LeftToes] =
        [
            ["iv_asi_oya_b_l", "iv_asi_hito_b_l", "iv_asi_naka_b_l", "iv_asi_kusu_b_l", "iv_asi_ko_b_l"],
            ["j_asi_e_l"],
        ],
        [BodyPart.RightToes] =
        [
            ["iv_asi_oya_b_r", "iv_asi_hito_b_r", "iv_asi_naka_b_r", "iv_asi_kusu_b_r", "iv_asi_ko_b_r"],
            ["j_asi_e_r"],
        ],
    };

    private static readonly Dictionary<BodyPart, string[]> Wanted = Groups.ToDictionary(
        pair => pair.Key,
        pair => (pair.Key == BodyPart.Penis
            ? BodyMeasurer.ShaftChains.SelectMany(chain => chain)
            : pair.Value.SelectMany(group => group)).Distinct().ToArray());

    public static string DisplayName(BodyPart part) => part switch
    {
        BodyPart.LeftButtock => "Left buttock",
        BodyPart.RightButtock => "Right buttock",
        BodyPart.LeftHand => "Left hand",
        BodyPart.RightHand => "Right hand",
        BodyPart.LeftFingers => "Left fingers",
        BodyPart.RightFingers => "Right fingers",
        BodyPart.LeftFoot => "Left foot",
        BodyPart.RightFoot => "Right foot",
        BodyPart.LeftToes => "Left toes",
        BodyPart.RightToes => "Right toes",
        _ => part.ToString(),
    };

    /// <summary>The name inside a sentence.</summary>
    public static string Words(BodyPart part) => DisplayName(part).ToLowerInvariant();

    /// <summary>Where the part is drawn in the world. Reads the skeleton once.</summary>
    public static unsafe bool TryLocate(ICharacter character, BodyPart part, out Vector3 world)
    {
        world = default;
        var native = (Character*)character.Address;
        if (native is null || !Wanted.TryGetValue(part, out var bones)) return false;
        var view = SkeletonView.Read((nint)native->DrawObject, character.Name.TextValue, character.GameObjectId, bones);
        if (view is null) return false;
        if (part == BodyPart.Penis && TryShaftTip(view, out world)) return true;
        foreach (var group in Groups[part])
        {
            var present = group.Where(view.Has).ToList();
            if (present.Count == 0) continue;
            world = present.Aggregate(Vector3.Zero, (sum, bone) => sum + view.World(bone)) / present.Count;
            return true;
        }
        return false;
    }

    /// <summary>Whether the body has a real shaft, not the collapsed set every YAS body carries.</summary>
    public static unsafe bool HasShaft(ICharacter character)
    {
        var native = (Character*)character.Address;
        if (native is null) return false;
        var view = SkeletonView.Read((nint)native->DrawObject, character.Name.TextValue, character.GameObjectId,
            Wanted[BodyPart.Penis]);
        return view is not null && TryShaftTip(view, out _);
    }

    /// <summary>The tip of the chain this body really uses, when one isn't collapsed.</summary>
    private static bool TryShaftTip(SkeletonView view, out Vector3 tip)
    {
        tip = default;
        foreach (var chain in BodyMeasurer.ShaftChains)
        {
            var points = chain.Where(view.Has).Select(view.World).ToList();
            if (points.Count < 2) continue;
            var length = points.Zip(points.Skip(1), Vector3.Distance).Sum();
            if (length < MinShaftLength) continue;
            tip = points[^1];
            return true;
        }
        return false;
    }
}

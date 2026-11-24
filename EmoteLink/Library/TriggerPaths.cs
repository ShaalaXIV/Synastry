using System.Text.RegularExpressions;

namespace EmoteLink.Library;

/// <summary>Which pose an animation file belongs to. Emotes are matched by their timelines instead
/// (Plugin.Library.cs).</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal static class TriggerPaths
{
    private static readonly Regex SeatPose = new(@"([jsl])_pose(\d+)", RegexOptions.Compiled);

    // Standing idles live under bt_common; weapon-stance folders hold battle idles, which /cpose
    // doesn't reach.
    private static readonly Regex StandingIdle =
        new(@"/bt_common/emote/pose0([1-6])_(?:loop|start)\.pap$", RegexOptions.Compiled);

    private static readonly Regex SlashCommand = new(@"\(/([a-zA-Z]+)\)", RegexOptions.Compiled);

    /// <summary>The pose a lower-case game path plays; null for anything else.</summary>
    public static PoseTarget? PoseOf(string path)
    {
        // Pose 0 of each seat isn't a numbered file; the game uses these instead.
        if (path.EndsWith("/emote/jmn.pap", StringComparison.Ordinal)) return new PoseTarget(PoseKind.GroundSit, 0);
        if (path.EndsWith("/emote/sit.pap", StringComparison.Ordinal)) return new PoseTarget(PoseKind.Sit, 0);
        if (path.EndsWith("/emote/bed_liedown_loop.pap", StringComparison.Ordinal)) return new PoseTarget(PoseKind.Doze, 0);
        if (path.EndsWith("/bt_common/resident/idle.pap", StringComparison.Ordinal)) return new PoseTarget(PoseKind.Idle, 0);

        var seat = SeatPose.Match(path);
        if (seat.Success && byte.TryParse(seat.Groups[2].Value, out var index) && index <= PoseService.MaxPoseIndex)
            return new PoseTarget(seat.Groups[1].Value switch
            {
                "j" => PoseKind.GroundSit,
                "s" => PoseKind.Sit,
                _ => PoseKind.Doze
            }, index);

        var idle = StandingIdle.Match(path);
        return idle.Success ? new PoseTarget(PoseKind.Idle, byte.Parse(idle.Groups[1].Value)) : null;
    }

    /// <summary>The command an author wrote into a name as "(/dance)", without the slash.</summary>
    public static string? CommandIn(string name) =>
        SlashCommand.Match(name) is { Success: true } match ? match.Groups[1].Value : null;
}

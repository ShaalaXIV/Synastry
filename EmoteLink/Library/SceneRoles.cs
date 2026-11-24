using System.Text.RegularExpressions;

namespace EmoteLink.Library;

/// <summary>One side of a scene: its role as the name labels it, and the trigger that plays it.</summary>
internal readonly record struct SceneRole(string Label, string Trigger);

/// <summary>A two-role animation whose name says which trigger is whose.</summary>
internal sealed record Scene(SceneRole Dominant, SceneRole Other);

/// <summary>
/// Reads roles from a name such as "(Dom-Confirm/Sub-Sweep)" or "[Sub-Gsit1, Dom-Gsit2]". A scene needs
/// exactly one trigger on each side; a longer list ("Dom-A,B/Sub-C") only labels the chips it can.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal static class SceneRoles
{
    private static readonly Regex Enclosed = new(@"[\(\[]([^()\[\]]*)[\)\]]", RegexOptions.Compiled);

    // A space also separates role and pose: authors drop the hyphen ("Dom-BedPose0/Sub BedPose1"), and
    // the pose must still resolve to one of the option's triggers.
    private static readonly Regex RolePart = new(@"^\s*(f?dom\d*|top|hugger|sub\d*|bot|hugged)(?:\s*-\s*|\s+)(.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PoseToken = new(@"^(csit|chairsit|chair|sit|gsit|groundsit|bedpose|bed|doze)0*(\d)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Shorthand mod authors use for emote commands.</summary>
    private static readonly Dictionary<string, string> EmoteAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bdance"] = "balldance",
        ["hdance"] = "harvestdance",
        ["sdance"] = "stepdance",
        ["gdance"] = "golddance",
        ["tdance"] = "thavnairiandance",
        ["beeknees"] = "beesknees",
    };

    /// <summary>The two roles the names state, most specific name first. A mod's name counts because
    /// standalone mods put the roles there; the triggers must still match.</summary>
    public static Scene? TryParse(IEnumerable<string> names, IReadOnlyList<string> triggers, Func<string, uint?> emoteOf)
    {
        foreach (var name in names)
        foreach (Match enclosed in Enclosed.Matches(name))
        {
            var parts = enclosed.Groups[1].Value.Split('/', ',');
            if (parts.Length != 2) continue;
            var first = RolePart.Match(parts[0]);
            var second = RolePart.Match(parts[1]);
            if (!first.Success || !second.Success) continue;

            var firstIsDominant = IsDominant(first.Groups[1].Value);
            if (firstIsDominant == IsDominant(second.Groups[1].Value)) continue;
            if (Resolve(first.Groups[2].Value, triggers, emoteOf) is not { } firstTrigger ||
                Resolve(second.Groups[2].Value, triggers, emoteOf) is not { } secondTrigger ||
                firstTrigger == secondTrigger) continue;

            var firstRole = new SceneRole(LabelOf(first.Groups[1].Value), firstTrigger);
            var secondRole = new SceneRole(LabelOf(second.Groups[1].Value), secondTrigger);
            return firstIsDominant ? new Scene(firstRole, secondRole) : new Scene(secondRole, firstRole);
        }
        return null;
    }

    /// <summary>The role each trigger plays, as far as the names say. Only for labels: a trigger listed
    /// for both roles gets none.</summary>
    public static IReadOnlyDictionary<string, string> Labels(IEnumerable<string> names, IReadOnlyList<string> triggers,
        Func<string, uint?> emoteOf)
    {
        foreach (var name in names)
        foreach (Match enclosed in Enclosed.Matches(name))
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in enclosed.Groups[1].Value.Split('/'))
            {
                var match = RolePart.Match(part);
                if (!match.Success) continue;
                var label = LabelOf(match.Groups[1].Value);
                foreach (var token in match.Groups[2].Value.Split([',', '&'], StringSplitOptions.TrimEntries))
                    if (Resolve(token, triggers, emoteOf) is { } trigger)
                        labels[trigger] = labels.TryGetValue(trigger, out var other) && other != label ? "" : label;
            }
            foreach (var trigger in labels.Where(pair => pair.Value.Length == 0).Select(pair => pair.Key).ToList())
                labels.Remove(trigger);
            if (labels.Count > 0) return labels;
        }
        return new Dictionary<string, string>();
    }

    private static bool IsDominant(string label) =>
        label.StartsWith("dom", StringComparison.OrdinalIgnoreCase) ||
        label.StartsWith("fdom", StringComparison.OrdinalIgnoreCase) ||
        label.Equals("top", StringComparison.OrdinalIgnoreCase) ||
        label.Equals("hugger", StringComparison.OrdinalIgnoreCase);

    private static string LabelOf(string label)
    {
        var plain = label.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return plain.Equals("fdom", StringComparison.OrdinalIgnoreCase)
            ? "FDom"
            : char.ToUpperInvariant(plain[0]) + plain[1..].ToLowerInvariant();
    }

    private static string? Resolve(string token, IReadOnlyList<string> triggers, Func<string, uint?> emoteOf)
    {
        var pose = PoseToken.Match(token);
        if (pose.Success)
        {
            var kind = pose.Groups[1].Value.ToLowerInvariant() switch
            {
                "gsit" or "groundsit" => PoseKind.GroundSit,
                "bedpose" or "bed" or "doze" => PoseKind.Doze,
                _ => PoseKind.Sit,
            };
            return Find($"pose:{kind}:{pose.Groups[2].Value}");
        }
        if (EmoteAliases.TryGetValue(token, out var alias) && emoteOf(alias) is { } aliased &&
            Find($"emote:{aliased}") is { } found) return found;
        return emoteOf(token) is { } emote ? Find($"emote:{emote}") : null;

        string? Find(string trigger) => triggers.Contains(trigger) ? trigger : null;
    }
}

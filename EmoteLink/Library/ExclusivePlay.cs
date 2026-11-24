namespace EmoteLink.Library;

/// <summary>
/// Which selections make a picked option the one that plays. Packs reuse a few emote slots for
/// everything, so options left on by earlier plays claim the same files.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal static class ExclusivePlay
{
    private static readonly string[] OffNames = ["none", "off", "disabled"];

    /// <summary>The groups whose selection must change for this option to win, with their new
    /// selections. A mod's always-active files can't be switched off, so they're skipped.</summary>
    public static Dictionary<string, List<string>> SelectionChanges(
        IReadOnlyList<AnimGroup> groups,
        AnimGroup target,
        AnimOption picked,
        Func<string, IReadOnlyList<string>> selectedIn)
    {
        var changes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var paths = picked.PapPaths.ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.IsImplicit) continue;
            var selected = selectedIn(group.Name);
            List<string> selection;
            if (ReferenceEquals(group, target))
            {
                selection = group.IsMulti
                    ? selected.Where(name => Same(name, picked.Name) || !Conflicts(group, name, paths)).ToList()
                    : [];
                if (!selection.Any(name => Same(name, picked.Name))) selection.Add(picked.Name);
            }
            else
            {
                selection = selected.Where(name => !Conflicts(group, name, paths)).ToList();
                if (selection.Count == selected.Count) continue;
                // Penumbra can't leave a single-select group empty: use its off option, or leave it be.
                if (!group.IsMulti)
                {
                    if (OffOption(group) is not { } off) continue;
                    selection = [off.Name];
                }
            }

            if (selection.Count != selected.Count || selection.Any(name => !selected.Any(other => Same(name, other))))
                changes[group.Name] = selection;
        }
        return changes;
    }

    private static bool Conflicts(AnimGroup group, string optionName, HashSet<string> paths) =>
        group.Options.FirstOrDefault(option => Same(option.Name, optionName)) is { } option &&
        option.PapPaths.Any(paths.Contains);

    /// <summary>An animation-free option named as off, else any animation-free one.</summary>
    private static AnimOption? OffOption(AnimGroup group)
    {
        var free = group.Options.Where(option => option.PapPaths.Count == 0).ToList();
        return free.FirstOrDefault(option => OffNames.Contains(option.Name, StringComparer.OrdinalIgnoreCase)) ??
               free.FirstOrDefault();
    }

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}

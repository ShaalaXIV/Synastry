using System.Security.Cryptography;
using System.Text;

namespace EmoteLink.Library;

/// <summary>One option of a mod. Triggers are what plays it ("pose:Kind:Index" or "emote:Id"), PapPaths
/// the animation game paths it replaces.</summary>
internal sealed record AnimOption(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> PapPaths);

/// <summary>One option group in the author's order. Implicit is the mod's always-active files, which
/// aren't a group in Penumbra.</summary>
internal sealed record AnimGroup(
    string Name,
    string Description,
    bool IsMulti,
    bool IsImplicit,
    IReadOnlyList<AnimOption> Options);

/// <summary>
/// One way to play a row. A renamed role saves under NoteGroup and NoteOption. That is the mod's shared
/// role tag, unless so many rows use this trigger that one tag would name them all: then the row keeps
/// its own (IsRowNote), and the shared tag stays as a fallback.
/// </summary>
internal sealed record LibraryTrigger(
    string Trigger,
    string Name,
    string NoteGroup,
    string NoteOption,
    string TagGroup,
    string TagOption)
{
    public bool IsRowNote => !NoteGroup.Equals(TagGroup, StringComparison.Ordinal);
}

/// <summary>One playable option in the list.</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class LibraryRow
{
    private readonly Func<string, uint?> emoteOf;
    private (Scene? Scene, IReadOnlyDictionary<string, string> Labels)? roles;
    private bool? isCouple;
    private string? searchText;

    public LibraryRow(ModLibrary mod, AnimGroup group, AnimOption option, IReadOnlyList<LibraryTrigger> triggers,
        Func<string, uint?> emoteOf)
    {
        Mod = mod;
        Group = group;
        Option = option;
        Triggers = triggers;
        this.emoteOf = emoteOf;
        Id = $"{mod.Directory}\n{group.Name}\n{option.Name}";
        PickKey = $"{ModLibrary.Hash(group.Name)}:{ModLibrary.Hash(option.Name)}";
    }

    public ModLibrary Mod { get; }
    public AnimGroup Group { get; }
    public AnimOption Option { get; }
    public IReadOnlyList<LibraryTrigger> Triggers { get; }

    /// <summary>Names the row in favourites, recents and ImGui IDs.</summary>
    public string Id { get; }
    /// <summary>Names the row to the room without sending the mod's own text.</summary>
    public string PickKey { get; }

    /// <summary>The always-active files have no option name, so they go by the mod's.</summary>
    public string Name => Group.IsImplicit ? Mod.Name : Option.Name;
    public string Caption => Group.IsImplicit ? Mod.Name : $"{Mod.Name} › {Group.Name}";

    /// <summary>The names that can state roles, most specific first.</summary>
    private string[] Names => Group.IsImplicit ? [Mod.Name] : [Option.Name, Group.Name, Mod.Name];

    public Scene? Scene => Roles.Scene;

    /// <summary>The role a trigger plays according to the row's own names.</summary>
    public string? RoleFromName(string trigger) => Roles.Labels.GetValueOrDefault(trigger);

    public bool IsCouple => isCouple ??= CoupleNames.IsCouple(Group.IsImplicit ? [Mod.Name] : [Option.Name, Group.Name]);

    /// <summary>Lower-case option, group and trigger names.</summary>
    public string SearchText => searchText ??=
        $"{Option.Name}\n{Group.Name}\n{string.Join('\n', Triggers.Select(trigger => trigger.Name))}".ToLowerInvariant();

    // Read when first drawn or searched: a big library has thousands of rows.
    private (Scene? Scene, IReadOnlyDictionary<string, string> Labels) Roles => roles ??= ReadRoles();

    private (Scene?, IReadOnlyDictionary<string, string>) ReadRoles()
    {
        var scene = SceneRoles.TryParse(Names, Option.Triggers, emoteOf);
        return (scene, scene is null
            ? SceneRoles.Labels(Names, Option.Triggers, emoteOf)
            : new Dictionary<string, string>
            {
                [scene.Dominant.Trigger] = scene.Dominant.Label,
                [scene.Other.Trigger] = scene.Other.Label
            });
    }
}

/// <summary>An option group that holds animations. Off are a single-select group's options that play
/// nothing, such as "Disabled".</summary>
internal sealed record LibraryList(
    AnimGroup Group,
    string Key,
    string Title,
    string Hash,
    IReadOnlyList<LibraryRow> Rows,
    IReadOnlyList<AnimOption> Off);

/// <summary>A mod's playable options, built once when the mod is indexed.</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class ModLibrary
{
    // Rows that share a trigger in a small mod are variants of the same roles; more make it a pack.
    private const int MaxVariants = 4;

    private readonly Dictionary<string, LibraryRow> rowsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LibraryRow> rowsByPick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> triggerUses = new(StringComparer.OrdinalIgnoreCase);

    public ModLibrary(string directory, string name, IReadOnlyList<AnimGroup> groups,
        Func<string, string> nameOf, Func<string, uint?> emoteOf)
    {
        Directory = directory;
        Name = name;
        Groups = groups;
        foreach (var trigger in groups.SelectMany(group => group.Options).SelectMany(option => option.Triggers))
            triggerUses[trigger] = triggerUses.GetValueOrDefault(trigger) + 1;

        var lists = new List<LibraryList>();
        foreach (var group in groups)
        {
            var rows = group.Options.Where(option => option.Triggers.Count > 0)
                .Select(option => new LibraryRow(this, group, option,
                    option.Triggers.Select(trigger => TriggerOf(group, option, trigger, nameOf)).ToList(), emoteOf))
                .ToList();
            if (rows.Count == 0) continue;
            foreach (var row in rows)
            {
                rowsById[row.Id] = row;
                rowsByPick[row.PickKey] = row;
            }
            lists.Add(new LibraryList(group, $"{directory}\n{group.Name}", group.IsImplicit ? "Always on" : group.Name, Hash(group.Name), rows,
                group.IsMulti ? [] : group.Options.Where(option => option.Triggers.Count == 0).ToList()));
        }
        Lists = lists;
        RowCount = lists.Sum(list => list.Rows.Count);
        Settings = SettingsOf(groups);
    }

    public string Directory { get; }
    public string Name { get; }
    /// <summary>Every group, animations or not.</summary>
    public IReadOnlyList<AnimGroup> Groups { get; }
    /// <summary>The groups with animations.</summary>
    public IReadOnlyList<LibraryList> Lists { get; }
    /// <summary>What plays nothing: whole single-select groups, and the toggles of multi-select ones.</summary>
    public IReadOnlyList<ModOptionGroup> Settings { get; }
    public int RowCount { get; }

    /// <summary>The mod opens as lists of animations. One that only has always-active files has nothing
    /// to choose between and keeps its plain role chips.</summary>
    public bool UsesRows => RowCount > 1 || RowCount == 1 && !Lists[0].Group.IsImplicit;

    public bool Plays(string trigger) => triggerUses.ContainsKey(trigger);

    /// <summary>Few enough rows use this trigger that naming it says which roles are meant.</summary>
    public bool IsDistinct(string trigger) => triggerUses.GetValueOrDefault(trigger) <= MaxVariants;

    public LibraryRow? FindRow(string id) => rowsById.GetValueOrDefault(id);

    /// <summary>The row someone in the room picked: by what they shared, or, from an older Synastry that
    /// shares only the trigger, the one row that has it.</summary>
    public LibraryRow? FindPick(RemotePick pick)
    {
        if (pick.PickKey.Length > 0) return rowsByPick.GetValueOrDefault(pick.PickKey);
        return triggerUses.GetValueOrDefault(pick.Trigger) == 1
            ? Lists.SelectMany(list => list.Rows).First(row => row.Option.Triggers.Contains(pick.Trigger, StringComparer.OrdinalIgnoreCase))
            : null;
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    private LibraryTrigger TriggerOf(AnimGroup group, AnimOption option, string trigger, Func<string, string> nameOf)
    {
        var colon = trigger.IndexOf(':');
        var tagGroup = trigger.StartsWith("pose:", StringComparison.Ordinal) ? "$detected-pose" : "$detected-emote";
        var tagOption = trigger[(colon + 1)..];
        return IsDistinct(trigger)
            ? new LibraryTrigger(trigger, nameOf(trigger), tagGroup, tagOption, tagGroup, tagOption)
            : new LibraryTrigger(trigger, nameOf(trigger), $"$row:{group.Name}\u001f{option.Name}", trigger, tagGroup, tagOption);
    }

    private static List<ModOptionGroup> SettingsOf(IReadOnlyList<AnimGroup> groups)
    {
        var settings = new List<ModOptionGroup>();
        foreach (var group in groups)
        {
            if (group.IsImplicit) continue;
            var free = group.Options.Where(option => option.Triggers.Count == 0).Select(option => option.Name).ToList();
            // A single-select group mixing both keeps its off options beside its animations.
            if (free.Count > 0 && (group.IsMulti || free.Count == group.Options.Count))
                settings.Add(new ModOptionGroup(group.Name, free, group.IsMulti));
        }
        return settings;
    }
}

/// <summary>What someone in the room picked in a mod. PickKey is empty from an older Synastry.</summary>
internal readonly record struct RemotePick(string Member, string Trigger, string PickKey);

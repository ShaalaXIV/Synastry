using LibraryScope = EmoteLink.MainWindow.LibraryScope;

namespace EmoteLink.Library;

internal enum LibraryKind { All, Solo, Couple }

/// <summary>One mod in the list, with the option groups that match the search and filter. ListsMatch:
/// what the search found is inside them, so they open with the mod.</summary>
internal sealed record ModBlock(
    (string Directory, string Name) Mod,
    string? CategoryId,
    IReadOnlyList<LibraryList> Lists,
    bool OpenedBySearch,
    bool ListsMatch);

/// <summary>
/// What the list shows: the folder's mods with their options, or the favourites or recents, narrowed by
/// the search and the All / Solo / Couple filter. Kept until something it depends on changes, since it
/// spans thousands of options.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class LibraryView(Plugin plugin)
{
    private (int Library, int Marks, LibraryScope Scope, string? Category, string Search, LibraryKind Kind)? built;
    private long builtAt;
    private readonly Dictionary<string, string> categoryByMod = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> folderCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModBlock> blocksByMod = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ModBlock> Blocks { get; private set; } = [];
    /// <summary>The same mods in the same order, for selecting a range.</summary>
    public IReadOnlyList<(string Directory, string Name)> Order { get; private set; } = [];
    /// <summary>The favourites or recents, when those are showing.</summary>
    public IReadOnlyList<LibraryRow> Rows { get; private set; } = [];
    public bool IsFlat { get; private set; }
    public bool IsSearching { get; private set; }
    /// <summary>Changes with each new search or filter, so what matches opens once.</summary>
    public string Reason { get; private set; } = "";
    public int PrivateCount { get; private set; }
    public int FavoriteCount { get; private set; }
    public int RecentCount { get; private set; }

    public int FolderCount(string categoryId) => folderCounts.GetValueOrDefault(categoryId);
    public ModBlock? BlockOf(string directory) => blocksByMod.GetValueOrDefault(directory);

    public void Refresh(LibraryScope scope, string? categoryId, string search, LibraryKind kind)
    {
        var text = search.Trim().ToLowerInvariant();
        var key = (plugin.LibraryRevision, plugin.LibraryMarksRevision, scope, categoryId, text, kind);
        // Role names and members leaving the room have no revision of their own.
        if (built == key && Environment.TickCount64 - builtAt < 1000) return;
        built = key;
        builtAt = Environment.TickCount64;
        IsSearching = text.Length > 0;
        Reason = $"{text}|{kind}";
        IsFlat = !IsSearching && scope is LibraryScope.Favorites or LibraryScope.Recent;

        CountFolders();
        Blocks = IsFlat ? [] : IsSearching ? FindBlocks(text, kind) : FolderBlocks(scope, categoryId, kind);
        Order = Blocks.Select(block => block.Mod).ToList();
        blocksByMod.Clear();
        foreach (var block in Blocks) blocksByMod[block.Mod.Directory] = block;
        Rows = !IsFlat ? [] : (scope == LibraryScope.Favorites ? Favorites() : Recents()).Where(row => Fits(row, kind)).ToList();
    }

    private void CountFolders()
    {
        categoryByMod.Clear();
        folderCounts.Clear();
        foreach (var category in plugin.Categories)
        {
            folderCounts[category.Id] = plugin.GetCategoryModCount(category.Id);
            foreach (var directory in category.ModDirectories) categoryByMod.TryAdd(directory, category.Id);
        }
        PrivateCount = plugin.Mods.Count(mod => plugin.IsModPrivate(mod.Directory));
        FavoriteCount = plugin.FavoriteOptions.Count(id => plugin.FindRow(id) is not null);
        RecentCount = plugin.RecentOptions.Count(id => plugin.FindRow(id) is not null);
    }

    private List<LibraryRow> Favorites() => plugin.FavoriteOptions.Select(plugin.FindRow).OfType<LibraryRow>()
        .OrderBy(row => row.Caption, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private List<LibraryRow> Recents() => plugin.RecentOptions.Select(plugin.FindRow).OfType<LibraryRow>().ToList();

    private List<ModBlock> FolderBlocks(LibraryScope scope, string? categoryId, LibraryKind kind)
    {
        IEnumerable<(string Directory, string Name)> mods = scope switch
        {
            LibraryScope.Category when categoryId is not null => plugin.GetOrganizedMods(categoryId),
            LibraryScope.Uncategorized => plugin.GetOrganizedMods(null),
            LibraryScope.Private => plugin.Mods.Where(mod => plugin.IsModPrivate(mod.Directory)),
            _ => plugin.Mods
        };
        return Ordered(mods).Select(mod => Block(mod, kind)).OfType<ModBlock>().ToList();
    }

    /// <summary>Mods whose name matches first, then those with a match inside, which open to show it.</summary>
    private List<ModBlock> FindBlocks(string text, LibraryKind kind)
    {
        var tags = plugin.TagsMatching(text);
        var named = new List<(string Directory, string Name)>();
        var others = new List<(string Directory, string Name)>();
        foreach (var mod in plugin.Mods)
            (mod.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             mod.Directory.Contains(text, StringComparison.OrdinalIgnoreCase)
                ? named
                : others).Add(mod);

        var blocks = Ordered(named).Select(mod => Block(mod, kind)).OfType<ModBlock>().ToList();
        foreach (var mod in Ordered(others))
        {
            var modTags = tags.GetValueOrDefault(mod.Directory, []);
            if (plugin.LibraryOf(mod.Directory) is { UsesRows: true })
            {
                if (Block(mod, kind, row => Matches(row, text, modTags)) is { } block) blocks.Add(block);
            }
            else if (MatchesWhole(mod, text, modTags) && Block(mod, kind) is { } whole)
            {
                blocks.Add(whole);
            }
        }
        // The only result has nothing to be compared with, so it opens as well.
        return blocks is [{ OpenedBySearch: false } single] ? [single with { OpenedBySearch = true }] : blocks;
    }

    private ModBlock? Block((string Directory, string Name) mod, LibraryKind kind, Func<LibraryRow, bool>? match = null)
    {
        var category = categoryByMod.GetValueOrDefault(mod.Directory);
        var library = plugin.LibraryOf(mod.Directory);
        if (library is not { UsesRows: true })
            return Fits(mod, library, kind) ? new ModBlock(mod, category, [], false, false) : null;
        if (match is null && kind == LibraryKind.All) return new ModBlock(mod, category, library.Lists, false, false);
        var lists = library.Lists
            .Select(list => list with
            {
                Rows = list.Rows.Where(row => Fits(row, kind) && (match is null || match(row))).ToList(),
                Off = []
            })
            .Where(list => list.Rows.Count > 0)
            .ToList();
        return lists.Count > 0 ? new ModBlock(mod, category, lists, match is not null, match is not null) : null;
    }

    private static bool Matches(LibraryRow row, string text, IReadOnlyList<(string Group, string Option)> tags) =>
        row.SearchText.Contains(text, StringComparison.Ordinal) ||
        tags.Count > 0 && row.Triggers.Any(trigger => tags.Any(tag =>
            tag.Group == trigger.NoteGroup && tag.Option == trigger.NoteOption ||
            trigger.IsRowNote && tag.Group == trigger.TagGroup && tag.Option == trigger.TagOption &&
            row.RoleFromName(trigger.Trigger) is null));

    /// <summary>A mod without rows of its own: its options, emotes and role names.</summary>
    private bool MatchesWhole((string Directory, string Name) mod, string text, IReadOnlyList<(string Group, string Option)> tags) =>
        tags.Count > 0 ||
        plugin.LibraryOf(mod.Directory) is { } library && library.Lists.Any(list => list.Rows.Any(row => Matches(row, text, tags))) ||
        plugin.GetDetectedEmotes(mod.Directory).Any(emote => emote.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                                             emote.Command.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
        plugin.GetOptionGroups(mod.Directory).Any(group => group.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            group.Options.Any(option => option.Contains(text, StringComparison.OrdinalIgnoreCase)));

    private static bool Fits(LibraryRow row, LibraryKind kind) => kind == LibraryKind.All || row.IsCouple == (kind == LibraryKind.Couple);

    private static bool Fits((string Directory, string Name) mod, ModLibrary? library, LibraryKind kind) =>
        kind == LibraryKind.All ||
        (library is { RowCount: > 0 } ? library.Lists[0].Rows[0].IsCouple : CoupleNames.IsCouple([mod.Name])) == (kind == LibraryKind.Couple);

    private List<(string Directory, string Name)> Ordered(IEnumerable<(string Directory, string Name)> mods) =>
        plugin.OrderModsForLibrary(mods.DistinctBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase));
}

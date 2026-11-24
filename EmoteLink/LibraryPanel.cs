using System.Numerics;
using Dalamud.Bindings.ImGui;
using EmoteLink.Library;

namespace EmoteLink;

/// <summary>
/// What the animation list shows under its mod rows: each option group as a collapsed list, its options
/// as cards with a button per way to play them, and the favourites and recents as the same cards. The
/// mod rows themselves, folders and selection stay in <see cref="MainWindow"/>.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class LibraryPanel(Plugin plugin, MainWindow window)
{
    /// <summary>Where a card is drawn. Its height depends on it.</summary>
    private enum Place { List, Flat, Suggestion }

    /// <summary>A mod with few animations shows them at once instead of behind collapsed lists.</summary>
    private const int SmallMod = 12;

    private static readonly Vector4 Gold = new(0.91f, 0.76f, 0.36f, 1f);
    private static readonly LibraryKind[] Kinds = [LibraryKind.All, LibraryKind.Solo, LibraryKind.Couple];

    private readonly LibraryView view = new(plugin);
    private readonly Dictionary<string, string> noteBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> correctionBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> openMods = new(StringComparer.OrdinalIgnoreCase);
    // Lists flipped from how they start out, kept apart for the list, a search and a room pick, so
    // clearing a search brings back what was open before it.
    private readonly HashSet<string> flippedLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> flippedBySearch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> flippedInSuggestion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> openHeights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Place Place, string Row), (float Width, float Height)> cardHeights = [];
    private LibraryKind kind;
    private string searchReason = "";
    // The role this player last readied, shown as theirs while the room still has them ready.
    private string? readyRoleKey;

    // ---- The list -----------------------------------------------------------------------------

    public LibraryView Refresh(MainWindow.LibraryScope scope, string? categoryId, string search)
    {
        view.Refresh(scope, categoryId, search, kind);
        if (searchReason != view.Reason)
        {
            searchReason = view.Reason;
            flippedBySearch.Clear();
        }
        return view;
    }

    /// <summary>All, Solo or Couple.</summary>
    public void DrawKindFilter()
    {
        foreach (var option in Kinds)
        {
            if (option != LibraryKind.All) ImGui.SameLine(0, 2f * Theme.Scale);
            if (Theme.Text(option.ToString(), option == kind ? Theme.Bone : Theme.Faint)) kind = option;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(option switch
                {
                    LibraryKind.Solo => "Only animations whose names don't mention a second role.",
                    LibraryKind.Couple => "Only animations whose names mention two or more roles.",
                    _ => "Every animation."
                });
        }
    }

    public bool IsOpen(ModBlock block) => view.IsSearching
        ? block.OpenedBySearch != flippedBySearch.Contains(block.Mod.Directory)
        : openMods.Contains(block.Mod.Directory);

    public void Toggle(ModBlock block) => Flip(view.IsSearching ? flippedBySearch : openMods, block.Mod.Directory);

    /// <summary>Takes up the room of a mod row that isn't on screen instead of drawing it. Not while
    /// dragging: the dragged row has to stay even when scrolled away.</summary>
    public bool SkipHidden(ModBlock block, float rowHeight)
    {
        var height = rowHeight;
        if (IsOpen(block) && !openHeights.TryGetValue(block.Mod.Directory, out height) ||
            !ImGui.GetDragDropPayload().IsNull) return false;
        var size = new Vector2(1f, height);
        if (ImGui.IsRectVisible(size)) return false;
        ImGui.Dummy(size);
        return true;
    }

    /// <summary>How tall an opened mod was when last drawn; 0 before that.</summary>
    public float OpenHeight(string directory) => openHeights.GetValueOrDefault(directory);

    public void RememberOpenHeight(string directory, float height) => openHeights[directory] = height;

    public string Summary(ModBlock block)
    {
        var directory = block.Mod.Directory;
        var converted = plugin.IsModConverted(directory);
        if (block.Lists.Count > 0)
        {
            var rows = block.Lists.Sum(list => list.Rows.Count);
            var animations = $"{rows:N0} animation{(rows == 1 ? "" : "s")}";
            return (block.Lists.Count > 1 ? $"{block.Lists.Count} lists, {animations}" : animations) + (converted ? ", carrier" : "");
        }
        if (converted) return "carrier";
        var emotes = plugin.GetDetectedEmotes(directory).Count;
        var poses = plugin.GetDetectedPoses(directory).Count;
        if (emotes > 0) return $"{emotes} emote{(emotes == 1 ? "" : "s")}";
        if (poses > 0) return $"{poses} pose{(poses == 1 ? "" : "s")}";
        var groups = plugin.GetOptionGroups(directory).Count;
        return groups > 0 ? $"{groups} option{(groups == 1 ? "" : "s")}" : "";
    }

    /// <summary>The favourites or recents.</summary>
    public void DrawFlat()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f) * Theme.Scale);
        foreach (var row in view.Rows) DrawCard(row, Place.Flat);
        ImGui.PopStyleVar();
    }

    /// <summary>An opened animation mod. Returns true when a role was chosen (the suggestion prompt
    /// closes then).</summary>
    public bool DrawDetails((string Directory, string Name) mod, bool suggestionMode)
    {
        var library = plugin.LibraryOf(mod.Directory);
        if (library is not { UsesRows: true }) return DrawWholeMod(mod, library, suggestionMode);

        plugin.EnsureDefaultOptionSelections(mod.Directory);
        if (suggestionMode) return DrawSuggestion(mod, library);

        var block = view.BlockOf(mod.Directory);
        var lists = block is { Lists.Count: > 0 } ? block.Lists : library.Lists;
        DrawSettings(library, Place.List);
        ImGui.BeginGroup();
        var activated = false;
        foreach (var list in lists)
            activated |= DrawList(list, Place.List, block?.ListsMatch == true || library.RowCount <= SmallMod);
        ImGui.EndGroup();
        window.HighlightForGuide();
        return activated | DrawOtherRoles(mod, library, Place.List);
    }

    /// <summary>Someone's pick first, then the rest of the mod.</summary>
    private bool DrawSuggestion((string Directory, string Name) mod, ModLibrary library)
    {
        LibraryRow? theirs = null;
        foreach (var pick in plugin.RemotePicksIn(mod.Directory))
            if ((theirs = library.FindPick(pick)) is not null) break;

        var activated = false;
        if (theirs is null)
        {
            DrawSettings(library, Place.Suggestion);
            foreach (var list in library.Lists)
                activated |= DrawList(list, Place.Suggestion, library.RowCount <= SmallMod);
            return activated | DrawOtherRoles(mod, library, Place.Suggestion);
        }

        var theirList = library.Lists.First(list => ReferenceEquals(list.Group, theirs.Group));
        var rest = library.Lists.Where(list => !ReferenceEquals(list, theirList)).ToList();
        // A mod that is one scene can keep each role in a list of its own, so the lists that play
        // something else are likely yours. In a pack every list is its own animations.
        var yours = rest.Count > 2 ? [] : rest.Where(list => list.Rows.Any(row => row.Triggers.Any(trigger =>
            theirs.Triggers.All(other => other.Trigger != trigger.Trigger)))).ToList();
        rest.RemoveAll(yours.Contains);

        // One option with several triggers already covers both roles; otherwise the options beside it
        // may hold the other one.
        var siblings = theirList.Rows.Where(row => !ReferenceEquals(row, theirs)).ToList();
        var showSiblings = theirs.Triggers.Count == 1 && siblings.Count <= SmallMod;
        Theme.Wrapped(theirList.Title, Theme.Ash);
        activated |= DrawCard(theirs, Place.Suggestion);
        if (showSiblings)
            foreach (var row in siblings) activated |= DrawCard(row, Place.Suggestion);
        else if (siblings.Count > 0)
            rest.Insert(0, theirList with { Rows = siblings, Off = [] });

        foreach (var list in yours)
        {
            Theme.Wrapped(list.Title, Theme.Ash);
            foreach (var row in list.Rows) activated |= DrawCard(row, Place.Suggestion);
        }

        DrawSettings(library, Place.Suggestion);
        if (rest.Count > 0 && DrawHeader("##others", "Other animations", rest.Sum(list => list.Rows.Count), Place.Suggestion,
                $"{mod.Directory}\n$others", false))
        {
            ImGui.Indent(14f * Theme.Scale);
            foreach (var list in rest) activated |= DrawList(list, Place.Suggestion, false);
            ImGui.Unindent(14f * Theme.Scale);
        }
        return activated | DrawOtherRoles(mod, library, Place.Suggestion);
    }

    /// <summary>Everything that plays nothing, as the combos and checkboxes it always was.</summary>
    private void DrawSettings(ModLibrary library, Place place)
    {
        if (library.Settings.Count == 0) return;
        var count = library.Settings.Sum(group => group.IsMultiSelect ? group.Options.Count : 1);
        if (!DrawHeader("##settings", "Settings", count, place, $"{library.Directory}\n$settings", false)) return;
        ImGui.Indent(14f * Theme.Scale);
        ImGui.BeginGroup();
        MainWindow.DrawOptionGroups(plugin, library.Directory, library.Settings);
        ImGui.EndGroup();
        ImGui.Unindent(14f * Theme.Scale);
        ImGui.Dummy(new Vector2(0, 2f * Theme.Scale));
    }

    private bool DrawList(LibraryList list, Place place, bool startsOpen)
    {
        ImGui.PushID(list.Key);
        var activated = false;
        if (DrawHeader("##list", list.Title, list.Rows.Count, place, list.Key, startsOpen, list))
        {
            var s = Theme.Scale;
            ImGui.Indent(14f * s);
            for (var index = 0; index < list.Off.Count; index++)
            {
                var off = list.Off[index];
                if (SmallRadio($"{off.Name}##off{index}", plugin.IsOptionSelected(list.Rows[0].Mod.Directory, list.Group.Name, off.Name)))
                    plugin.SetOptionSelected(list.Rows[0].Mod.Directory, list.Group.Name, off.Name, true, false, broadcastSelection: false);
                DescriptionTooltip(off.Description);
            }
            foreach (var row in list.Rows) activated |= DrawCard(row, place);
            ImGui.Unindent(14f * s);
        }
        ImGui.PopID();
        return activated;
    }

    /// <summary>Roles the mod has that no option's files explain, so nothing found before is lost.</summary>
    private bool DrawOtherRoles((string Directory, string Name) mod, ModLibrary library, Place place)
    {
        if (view.IsSearching && place == Place.List) return false;
        var (poses, emotes) = plugin.OtherRoles(library);
        if (poses.Count + emotes.Count == 0 ||
            !DrawHeader("##other-roles", "Other roles", poses.Count + emotes.Count, place, $"{mod.Directory}\n$other-roles", false))
            return false;
        ImGui.Indent(14f * Theme.Scale);
        ImGui.BeginGroup();
        var activated = DrawRoleChips(mod, null, poses, emotes, place == Place.Suggestion);
        ImGui.EndGroup();
        ImGui.Unindent(14f * Theme.Scale);
        return activated;
    }

    /// <summary>The line that opens and closes a list: arrow, name (wrapped, packs use long ones), count,
    /// and a dot when someone in the room picked something inside. Returns whether it's open.</summary>
    private bool DrawHeader(string id, string title, int count, Place place, string key, bool startsOpen, LibraryList? list = null)
    {
        var flipped = place == Place.Suggestion ? flippedInSuggestion : view.IsSearching ? flippedBySearch : flippedLists;
        var open = startsOpen != flipped.Contains(key);
        var s = Theme.Scale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - 12f * s;
        var pickedBy = list is null ? null : PickedIn(list);
        var countText = count.ToString("N0");
        float countWidth;
        using (Theme.NumberFont()) countWidth = ImGui.CalcTextSize(countText).X;
        var textWidth = MathF.Max(40f * s, width - 22f * s - countWidth - 30f * s);
        var height = ImGui.CalcTextSize(title, false, textWidth).Y + 10f * s;
        var size = new Vector2(width, height);
        if (!ImGui.IsRectVisible(size))
        {
            ImGui.Dummy(size);
            return open;
        }

        if (ImGui.Selectable(id, false, ImGuiSelectableFlags.DontClosePopups, size)) Flip(flipped, key);
        var hovered = ImGui.IsItemHovered();
        if (hovered && pickedBy is not null) ImGui.SetTooltip($"{pickedBy} picked an animation in here.");
        else if (hovered && list is not null) DescriptionTooltip(list.Group.Description);

        var draw = ImGui.GetWindowDrawList();
        var lineY = start.Y + 5f * s + ImGui.GetTextLineHeight() * 0.5f;
        MainWindow.Chevron(draw, new Vector2(start.X + 9f * s, lineY), open, ImGui.GetColorU32(hovered ? Theme.Soft : Theme.Faint));
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), start + new Vector2(22f * s, 5f * s),
            ImGui.GetColorU32(pickedBy is not null ? Theme.AzureText : open || hovered ? Theme.Bone : Theme.Soft), title, textWidth);
        if (pickedBy is not null)
            Theme.Dot(draw, new Vector2(start.X + width - countWidth - 22f * s, lineY), Theme.AzureText, true);
        using (Theme.NumberFont())
            draw.AddText(new Vector2(start.X + width - 8f * s - countWidth, lineY - ImGui.GetTextLineHeight() * 0.5f),
                ImGui.GetColorU32(Theme.Faint), countText);
        return open;
    }

    // ---- One animation ------------------------------------------------------------------------

    /// <summary>An option as a card: star, name as the author wrote it, who plays what, and a chip per
    /// way to play it. Returns true when a role was chosen.</summary>
    private bool DrawCard(LibraryRow row, Place place)
    {
        var s = Theme.Scale;
        var width = ImGui.GetContentRegionAvail().X - 12f * s;
        // A card that hasn't been on screen yet takes a guess at its height, so a search that opens
        // thousands of them doesn't lay them all out at once.
        cardHeights.TryGetValue((place, row.Id), out var known);
        var size = new Vector2(width, known.Height > 0f ? known.Height : 62f * s);
        if (!ImGui.IsRectVisible(size))
        {
            ImGui.Dummy(size);
            return false;
        }

        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var pad = new Vector2(10f, 8f) * s;
        var directory = row.Mod.Directory;
        var favorite = plugin.IsFavorite(row);
        var chosen = !row.Group.IsImplicit && plugin.IsOptionSelected(directory, row.Group.Name, row.Option.Name);
        // Away from its mod, a card carries the mod's colour itself.
        var availability = place == Place.Flat ? window.AvailabilityOf(directory) : Availability.Solo;
        var highlight = place != Place.Flat ? null
            : plugin.GetRemoteModSelector(directory) is not null ? Theme.AzureText : Theme.AvailabilityColor(availability);

        // Drawn behind the contents, at the height they took last time.
        if (known.Width == width)
        {
            draw.AddRectFilled(start, start + size, ImGui.GetColorU32(
                highlight is { } wash ? Theme.WithAlpha(wash, 0.07f)
                : favorite ? Theme.WithAlpha(Gold, 0.07f)
                : Theme.WithAlpha(Theme.Bone, 0.035f)), 4f * s);
            if ((highlight ?? (chosen ? Theme.Rose : null)) is { } bar)
                draw.AddRectFilled(start, start + new Vector2(2f * s, size.Y), ImGui.GetColorU32(bar), 1f * s);
        }

        ImGui.PushID(row.Id);
        ImGui.SetCursorScreenPos(start + pad);
        ImGui.BeginGroup();
        DrawStar(row, favorite);
        ImGui.SameLine(0, 8f * s);
        ImGui.BeginGroup();
        var contents = ImGui.GetCursorScreenPos();
        var right = start.X + width - pad.X;
        var nameRight = right;
        if (!row.Group.IsImplicit)
        {
            var mark = ImGui.GetTextLineHeight() + 4f * s;
            nameRight -= mark + 8f * s;
            ImGui.SetCursorScreenPos(new Vector2(right - mark, contents.Y - 2f * s));
            DrawChosenMark(row, chosen);
            ImGui.SetCursorScreenPos(contents);
        }

        ImGui.PushTextWrapPos(nameRight - ImGui.GetWindowPos().X);
        if (place == Place.Flat)
        {
            Theme.AvailabilityMark(draw, contents + new Vector2(8f * s, ImGui.GetTextLineHeight() * 0.5f), availability);
            ImGui.SetCursorScreenPos(contents + new Vector2(20f * s, 0));
            ImGui.PushStyleColor(ImGuiCol.Text, highlight ?? (availability == Availability.Private ? Theme.Ash : Theme.Soft));
            ImGui.TextUnformatted(row.Caption);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Theme.AvailabilityText(availability));
        }
        ImGui.PushStyleColor(ImGuiCol.Text, favorite ? Gold : Theme.Bone);
        ImGui.TextUnformatted(row.Name);
        ImGui.PopStyleColor();
        DescriptionTooltip(row.Option.Description);
        if (row.Scene is { } scene)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Ash);
            ImGui.TextUnformatted($"{scene.Dominant.Label}: {plugin.TriggerName(scene.Dominant.Trigger)} · " +
                                  $"{scene.Other.Label}: {plugin.TriggerName(scene.Other.Trigger)}");
            ImGui.PopStyleColor();
        }
        ImGui.PopTextWrapPos();

        var activated = DrawTriggers(row, place, right);
        ImGui.EndGroup();
        ImGui.EndGroup();
        var height = ImGui.GetItemRectMax().Y - start.Y + pad.Y;
        cardHeights[(place, row.Id)] = (width, height);
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(Vector2.Zero);
        ImGui.PopID();
        return activated;
    }

    private bool DrawTriggers(LibraryRow row, Place place, float right)
    {
        var suggestionMode = place == Place.Suggestion;
        var directory = row.Mod.Directory;
        var flow = new Flow(right);
        var activated = false;
        foreach (var trigger in row.Triggers)
        {
            ImGui.PushID(trigger.Trigger);
            var role = RoleOf(row, trigger);
            var key = $"{row.Id}\n{trigger.Trigger}";
            var chip = new RoleChipText(directory, trigger.NoteGroup, trigger.NoteOption, trigger.Name, role ?? trigger.Name,
                role is null ? trigger.Name : $"{role} · {trigger.Name}", PickedBy(row, trigger), key);
            var chosen = RoleChip(chip, suggestionMode, true, ref flow, out var solo);
            if (chosen)
            {
                plugin.SelectForPlay(row);
                if (suggestionMode || window.GuideAllowsPick(directory, row.Name))
                {
                    plugin.PlayOption(row, trigger.Trigger, false);
                    RememberReady(key);
                    activated = true;
                }
            }
            if (solo) plugin.PlayOption(row, trigger.Trigger, true);
            ImGui.PopID();
        }
        return activated;
    }

    /// <summary>A trigger's role in this row: the tag given here, else the role its name states, else the
    /// mod's shared tag for that trigger.</summary>
    private string? RoleOf(LibraryRow row, LibraryTrigger trigger)
    {
        var directory = row.Mod.Directory;
        var note = plugin.GetOptionNote(directory, trigger.NoteGroup, trigger.NoteOption);
        if (note.Length > 0) return note;
        if (row.RoleFromName(trigger.Trigger) is { } named) return named;
        var tag = trigger.IsRowNote ? plugin.GetOptionNote(directory, trigger.TagGroup, trigger.TagOption) : "";
        return tag.Length > 0 ? tag : null;
    }

    private string? PickedBy(LibraryRow row, LibraryTrigger trigger)
    {
        foreach (var pick in plugin.RemotePicksIn(row.Mod.Directory))
            if (pick.Trigger.Equals(trigger.Trigger, StringComparison.OrdinalIgnoreCase) &&
                (pick.PickKey.Length > 0
                    ? pick.PickKey.Equals(row.PickKey, StringComparison.OrdinalIgnoreCase)
                    : row.Mod.IsDistinct(trigger.Trigger)))
                return pick.Member;
        return null;
    }

    private string? PickedIn(LibraryList list)
    {
        var library = list.Rows[0].Mod;
        foreach (var pick in plugin.RemotePicksIn(library.Directory))
            if (library.FindPick(pick) is { } row && ReferenceEquals(row.Group, list.Group))
                return pick.Member;
        return null;
    }

    /// <summary>Shows whether the option is on, and changes that without playing it.</summary>
    private void DrawChosenMark(LibraryRow row, bool chosen)
    {
        var directory = row.Mod.Directory;
        if (row.Group.IsMulti)
        {
            var on = chosen;
            if (SmallCheckbox("##chosen", ref on))
                plugin.SetOptionSelected(directory, row.Group.Name, row.Option.Name, on, true, broadcastSelection: false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(chosen ? "On. Click to turn it off." : "Off. Playing it turns it on.");
            return;
        }
        if (SmallRadio("##chosen", chosen) && !chosen)
            plugin.SetOptionSelected(directory, row.Group.Name, row.Option.Name, true, false, broadcastSelection: false);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(chosen ? "Chosen in this list." : "Choose it without playing it.");
    }

    private void DrawStar(LibraryRow row, bool favorite)
    {
        var size = ImGui.GetTextLineHeight();
        var start = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##favorite", new Vector2(size, size))) plugin.ToggleFavorite(row);
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetTooltip(favorite ? "Remove from Favorites" : "Add to Favorites");

        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(favorite ? Gold : hovered ? Theme.Soft : Theme.Faint);
        var center = start + new Vector2(size, size) * 0.5f;
        Vector2 Point(int index)
        {
            var angle = -MathF.PI * 0.5f + index * MathF.PI / 5f;
            return center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * size * (index % 2 == 0 ? 0.5f : 0.22f);
        }
        for (var index = 0; index < 10; index++)
        {
            if (favorite) draw.AddTriangleFilled(center, Point(index), Point(index + 1), color);
            else draw.AddLine(Point(index), Point(index + 1), color, 1.2f * Theme.Scale);
        }
    }

    // ---- A mod without rows -------------------------------------------------------------------

    /// <summary>A mod that is one animation, or whose options aren't what plays it: its roles as chips,
    /// then its options.</summary>
    private bool DrawWholeMod((string Directory, string Name) mod, ModLibrary? library, bool suggestionMode)
    {
        var poses = plugin.GetDetectedPoses(mod.Directory);
        var emotes = plugin.GetDetectedEmotes(mod.Directory);
        var groups = plugin.GetOptionGroups(mod.Directory);
        var row = library is { RowCount: 1 } ? library.Lists[0].Rows[0] : null;
        var activated = false;

        if (poses.Count + emotes.Count > 0)
        {
            if (row is not null && !suggestionMode)
            {
                DrawStar(row, plugin.IsFavorite(row));
                ImGui.SameLine(0, 8f * Theme.Scale);
            }
            Theme.Label(plugin.Sync.IsInRoom && !suggestionMode ? "Roles" : "Animations");
            ImGui.BeginGroup();
            activated = DrawRoleChips(mod, row, poses, emotes, suggestionMode);
            ImGui.EndGroup();
            if (!suggestionMode) window.HighlightForGuide();
        }
        else
        {
            Theme.Quiet("No emote or pose found in this mod's current options.");
        }

        if (groups.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4f * Theme.Scale));
            MainWindow.DrawOptionGroups(plugin, mod.Directory, groups);
        }
        return activated;
    }

    private bool DrawRoleChips(
        (string Directory, string Name) mod,
        LibraryRow? row,
        IReadOnlyList<PoseTarget> poses,
        IReadOnlyList<EmoteTarget> emotes,
        bool suggestionMode)
    {
        var activated = false;
        var flow = new Flow(ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X);
        foreach (var pose in poses)
        {
            ImGui.PushID($"pose-{pose.Kind}-{pose.Index}");
            var chip = WholeModChip(mod.Directory, "$detected-pose", $"{pose.Kind}:{pose.Index}", MainWindow.PoseDisplayName(pose));
            var chosen = RoleChip(chip, suggestionMode, false, ref flow, out var solo);
            if ((chosen || solo) && row is not null) plugin.RememberRecent(row);
            if (chosen && (suggestionMode || window.GuideAllowsPick(mod.Directory, chip.AnimationName)))
            {
                plugin.ActivateDetectedPose(mod.Directory, mod.Name, pose);
                RememberReady(chip.Key);
                activated = true;
            }
            if (solo) plugin.ActivateDetectedPoseSolo(mod.Directory, mod.Name, pose);
            ImGui.PopID();
        }

        foreach (var emote in emotes)
        {
            ImGui.PushID($"emote-{emote.Id}");
            var chip = WholeModChip(mod.Directory, "$detected-emote", emote.Id.ToString(), emote.Name);
            var chosen = RoleChip(chip, suggestionMode, false, ref flow, out var solo);
            if ((chosen || solo) && row is not null) plugin.RememberRecent(row);
            if (chosen && (suggestionMode || window.GuideAllowsPick(mod.Directory, chip.AnimationName)))
            {
                plugin.ActivateDetectedEmote(mod.Directory, mod.Name, emote);
                RememberReady(chip.Key);
                activated = true;
            }
            if (solo) plugin.ActivateDetectedEmoteSolo(mod.Directory, mod.Name, emote);
            ImGui.PopID();
        }
        return activated;
    }

    private RoleChipText WholeModChip(string directory, string group, string option, string animationName)
    {
        var note = plugin.GetOptionNote(directory, group, option);
        var role = string.IsNullOrWhiteSpace(note) ? animationName : note;
        return new RoleChipText(directory, group, option, animationName, role, role,
            plugin.GetRemoteDetectedTriggerSelector(directory, group, option), NoteKey(directory, group, option));
    }

    // ---- Role chips ---------------------------------------------------------------------------

    /// <summary>What a chip says: the role's name, the whole label, who in the room has it, and what it
    /// is remembered by once readied. A rename saves under the note group and option.</summary>
    private readonly record struct RoleChipText(
        string Directory,
        string NoteGroup,
        string NoteOption,
        string AnimationName,
        string Role,
        string Label,
        string? PickedBy,
        string Key);

    /// <summary>Puts chips on one line until the next doesn't fit.</summary>
    private struct Flow(float right)
    {
        private bool started;

        public void Place(float width)
        {
            if (started)
            {
                ImGui.SameLine(0, 8f * Theme.Scale);
                if (ImGui.GetCursorScreenPos().X + width > right) ImGui.NewLine();
            }
            started = true;
        }
    }

    /// <summary>A role chip. Its right-click menu renames the role, reports a bad shared name and,
    /// in a room, plays it just for this player.</summary>
    private bool RoleChip(in RoleChipText chip, bool suggestionMode, bool compact, ref Flow flow, out bool solo)
    {
        solo = false;
        var (directory, group, option, animationName, role, _, pickedBy, _) = chip;
        var key = NoteKey(directory, group, option);
        var savedNote = plugin.GetOptionNote(directory, group, option);
        if (!noteBuffers.TryGetValue(key, out var note) ||
            (string.IsNullOrWhiteSpace(note) && !string.IsNullOrWhiteSpace(savedNote))) note = savedNote;
        noteBuffers[key] = note;
        var inRoom = plugin.Sync.IsInRoom;
        var mine = inRoom && !suggestionMode && readyRoleKey == chip.Key && IsLocallyReady();

        var (label, state) = pickedBy is not null
            ? ($"{chip.Label}, {pickedBy}", suggestionMode ? ChipState.Taken : ChipState.Theirs)
            : mine
                ? ($"{chip.Label}, you're ready", ChipState.Mine)
                : (chip.Label, ChipState.Open);
        var showSolo = inRoom && !suggestionMode;
        flow.Place(ImGui.CalcTextSize(label).X + (compact ? 22f : 30f) * Theme.Scale +
                   (showSolo ? ImGui.CalcTextSize("Solo").X + 16f * Theme.Scale : 0f));
        var clicked = Theme.Chip($"{label}##chip", state, compact);
        var chipHeight = ImGui.GetItemRectSize().Y;
        if (ImGui.IsItemHovered())
        {
            var action = suggestionMode ? $"Ready as {role}" : inRoom ? $"Get ready as {role}" : $"Play {animationName}";
            var who = pickedBy is not null ? $"\n{pickedBy} picked this role." : "";
            var name = !role.Equals(animationName, StringComparison.Ordinal) ? $"\n{animationName}" : "";
            ImGui.SetTooltip($"{action}{name}{who}\nRight-click to rename.");
        }
        if (ImGui.BeginPopupContextItem("roleMenu"))
        {
            var shared = group.StartsWith("$detected-", StringComparison.Ordinal);
            ImGui.TextUnformatted(animationName);
            if (inRoom && !suggestionMode && ImGui.MenuItem("Play just for me"))
                solo = true;
            ImGui.Separator();
            Theme.Label(shared ? "Role name" : "Role name, for this animation only");
            ImGui.SetNextItemWidth(220f * Theme.Scale);
            var submit = ImGui.InputTextWithHint("##role", "Lead, Follow, Top…", ref note, 21,
                ImGuiInputTextFlags.EnterReturnsTrue);
            noteBuffers[key] = note;
            if (Theme.Primary("Save") || submit)
            {
                plugin.SaveOptionNote(directory, group, option, note);
                noteBuffers[key] = plugin.GetOptionNote(directory, group, option);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (Theme.Text("Clear"))
            {
                plugin.SaveOptionNote(directory, group, option, "");
                noteBuffers[key] = "";
                ImGui.CloseCurrentPopup();
            }
            if (shared && !string.IsNullOrWhiteSpace(savedNote))
            {
                ImGui.Separator();
                Theme.Label("Wrong shared name? Suggest a better one");
                if (!correctionBuffers.TryGetValue(key, out var correction)) correction = "";
                ImGui.SetNextItemWidth(220f * Theme.Scale);
                ImGui.InputTextWithHint("##correction", "Better name", ref correction, 21);
                correctionBuffers[key] = correction;
                if (Theme.Outline("Suggest") && !string.IsNullOrWhiteSpace(correction))
                {
                    plugin.ReportBadRoleLabel(directory, group, option, correction);
                    noteBuffers[key] = plugin.GetOptionNote(directory, group, option);
                    correctionBuffers[key] = "";
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Changes it for you now. Five matching suggestions change it for everyone.");
            }
            ImGui.EndPopup();
        }

        // After the chip's right-click menu, which attaches to the item drawn just before it.
        if (showSolo)
        {
            // Back from before the redesign: play this role just for you, without readying the room.
            ImGui.SameLine(0, 2f * Theme.Scale);
            if (!compact) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (chipHeight - ImGui.GetFrameHeight()) * 0.5f);
            if (Theme.Text("Solo", Theme.Ash)) solo = true;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Play {animationName} just for you, without the room.");
        }
        return clicked;
    }

    private void RememberReady(string key)
    {
        if (plugin.Sync.IsInRoom) readyRoleKey = key;
    }

    private bool IsLocallyReady()
    {
        var room = plugin.Sync.Room;
        return room is not null && room.Members.Any(member => plugin.Sync.IsCurrentMember(member.ConnectionId) && member.Ready);
    }

    // ---- Small helpers ------------------------------------------------------------------------

    private static void Flip(HashSet<string> set, string key)
    {
        if (!set.Remove(key)) set.Add(key);
    }

    private static string NoteKey(string directory, string group, string option) =>
        directory + "\n" + group + "\n" + option;

    private static void DescriptionTooltip(string description)
    {
        if (description.Length == 0 || !ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(360f * Theme.Scale);
        ImGui.TextUnformatted(description);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static bool SmallCheckbox(string label, ref bool value)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2f, 2f) * Theme.Scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Ink);
        var changed = ImGui.Checkbox(label, ref value);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        return changed;
    }

    private static bool SmallRadio(string label, bool on)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2f, 2f) * Theme.Scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Ink);
        ImGui.PushStyleColor(ImGuiCol.Text, on ? Theme.Bone : Theme.Soft);
        var clicked = ImGui.RadioButton(label, on);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        return clicked;
    }
}

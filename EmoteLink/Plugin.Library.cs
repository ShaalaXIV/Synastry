using EmoteLink.Library;

namespace EmoteLink;

/// <summary>
/// The per-option animation library: what each option of a mod plays, playing one option on its own,
/// telling the room which one it was, and the player's favourites and recents.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
public sealed unsafe partial class Plugin
{
    private const string PickedOptionGroup = "$picked-option";
    private const string NoPickedOption = "none";
    private const int MaxRecentOptions = 20;
    private const uint ChangePoseEmote = 90;

    private readonly Dictionary<string, ModLibrary> modLibraries = new(StringComparer.OrdinalIgnoreCase);
    // A timeline's file name to the emotes that play it, with the timeline's whole key.
    private readonly Dictionary<string, List<(string Suffix, uint EmoteId)>> emotesByTimeline = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (string Name, PoseKind? Seat)> emoteDetails = [];
    private readonly Dictionary<string, IReadOnlyList<RemotePick>> remotePicks = new(StringComparer.OrdinalIgnoreCase);
    private int remotePicksRevision = -1;
    private long remotePicksReadAt;
    // The row being played, until its role is shared; and the mods the room still knows a row for.
    private LibraryRow? pickToShare;
    private readonly HashSet<string> sharedPicks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes whenever the library, the room or a pick does.</summary>
    internal int LibraryRevision => Volatile.Read(ref libraryOrderRevision);

    /// <summary>Changes with the favourites and recents.</summary>
    internal int LibraryMarksRevision { get; private set; }

    internal ModLibrary? LibraryOf(string directory) => modLibraries.GetValueOrDefault(directory);

    // ---- Indexing -----------------------------------------------------------------------------

    /// <summary>Which emote plays which timeline, built once with the emote names.</summary>
    private void BuildTimelineLookup()
    {
        foreach (var info in emotePlaybackById.Values.OrderBy(info => info.EmoteId))
        {
            if (info.EmoteId == ChangePoseEmote) continue;
            foreach (var timeline in info.Timelines)
            {
                var name = timeline.Key[(timeline.Key.LastIndexOf('/') + 1)..];
                if (!emotesByTimeline.TryGetValue(name, out var emotes)) emotesByTimeline[name] = emotes = [];
                emotes.Add(($"/{timeline.Key}.pap", info.EmoteId));
            }
        }
    }

    /// <summary>Penumbra's option groups in the author's order.</summary>
    private static List<ModOptionGroup> OptionGroupsOf(OptionManifest manifest) => manifest.Groups
        .Where(group => !group.IsImplicit)
        .Select(group => new ModOptionGroup(group.Name, group.Options.Select(option => option.Name).ToList(), group.IsMulti))
        .ToList();

    private void IndexLibrary((string Directory, string Name) mod, OptionManifest? manifest)
    {
        modLibraries.Remove(mod.Directory);
        if (manifest is null || manifest.Version != OptionManifest.CurrentVersion) return;

        var converted = inPlaceEmoteConverter.TryGetCarrier(
            penumbra.GetModRoot(), mod.Directory, out var convertedEmote, out var carrierEmote);
        var triggers = manifest.PapPaths.Select(TriggerForPath).ToArray();
        var groups = manifest.Groups.Select(group => new AnimGroup(
            group.Name, group.Description, group.IsMulti, group.IsImplicit,
            group.Options.Select(option =>
            {
                var found = TriggersOf(group, option, triggers);
                // A conversion adds its carrier beside the emote it stands in for.
                if (converted && found.Contains($"emote:{convertedEmote}")) found.Remove($"emote:{carrierEmote}");
                return new AnimOption(option.Name, option.Description, found,
                    option.Paths.Select(path => manifest.PapPaths[path]).ToArray());
            }).ToList())).ToList();
        modLibraries[mod.Directory] = new ModLibrary(mod.Directory, mod.Name, groups, TriggerName, EmoteIdOf);
    }

    private string? TriggerForPath(string path)
    {
        if (TriggerPaths.PoseOf(path) is { } pose) return $"pose:{pose.Kind}:{pose.Index}";
        var name = path[(path.LastIndexOf('/') + 1)..^".pap".Length];
        if (!emotesByTimeline.TryGetValue(name, out var candidates)) return null;
        foreach (var (suffix, emoteId) in candidates)
            if (path.EndsWith(suffix, StringComparison.Ordinal)) return $"emote:{emoteId}";
        return null;
    }

    private List<string> TriggersOf(ManifestGroup group, ManifestOption option, string?[] pathTriggers)
    {
        var found = option.Paths.Select(path => pathTriggers[path]).OfType<string>().Distinct().ToList();
        var poses = found.Where(trigger => trigger.StartsWith("pose:", StringComparison.Ordinal))
            .OrderBy(trigger => trigger, StringComparer.Ordinal).ToList();
        // A seat's own emote only plays whichever of its poses you used last, so the poses cover it.
        var emotes = found.Where(trigger => !trigger.StartsWith("pose:", StringComparison.Ordinal) &&
            !(SeatOf(trigger) is { } seat && poses.Any(pose => pose.StartsWith($"pose:{seat}:", StringComparison.Ordinal))));
        poses.AddRange(emotes);
        if (poses.Count == 0 && option.Paths.Count > 0 &&
            (TriggerPaths.CommandIn(option.Name) ?? TriggerPaths.CommandIn(group.Name)) is { } command &&
            EmoteIdOf(command) is { } emoteId)
            poses.Add($"emote:{emoteId}");
        return poses;
    }

    private uint? EmoteIdOf(string command) =>
        emotePlaybackByCommand.TryGetValue("/" + command, out var info) ? info.EmoteId : null;

    private PoseKind? SeatOf(string trigger) =>
        uint.TryParse(trigger["emote:".Length..], out var emoteId) ? EmoteDetails(emoteId).Seat : null;

    private (string Name, PoseKind? Seat) EmoteDetails(uint emoteId)
    {
        if (emoteDetails.TryGetValue(emoteId, out var known)) return known;
        var row = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRowOrDefault(emoteId);
        var name = row?.Name.ExtractText() ?? "";
        if (name.Length == 0) name = emotePlaybackById.TryGetValue(emoteId, out var info) ? info.Command : $"Emote {emoteId}";
        PoseKind? seat = row?.EmoteMode.RowId switch
        {
            1 => PoseKind.GroundSit,
            2 => PoseKind.Sit,
            3 => PoseKind.Doze,
            _ => null
        };
        return emoteDetails[emoteId] = (name, seat);
    }

    /// <summary>A trigger's own name: "Ground sit 1" or the emote's.</summary>
    internal string TriggerName(string trigger)
    {
        if (TryParsePoseTrigger(trigger, out var pose)) return MainWindow.PoseDisplayName(pose);
        return uint.TryParse(trigger["emote:".Length..], out var emoteId) ? EmoteDetails(emoteId).Name : trigger;
    }

    /// <summary>
    /// Roles found for the mod as a whole that no option's own files explain, such as an emote changed
    /// only by its effects. The seats' own emotes aren't worth a button: the rows list their poses.
    /// </summary>
    internal (IReadOnlyList<PoseTarget> Poses, IReadOnlyList<EmoteTarget> Emotes) OtherRoles(ModLibrary library) =>
        (GetDetectedPoses(library.Directory).Where(pose => !library.Plays($"pose:{pose.Kind}:{pose.Index}")).ToList(),
            GetDetectedEmotes(library.Directory).Where(emote => emote.Id != ChangePoseEmote &&
                EmoteDetails(emote.Id).Seat is null && !library.Plays($"emote:{emote.Id}")).ToList());

    // ---- Playing ------------------------------------------------------------------------------

    /// <summary>Selects one option, switches off what would play instead of it in the same mod, then
    /// plays (or, in a room, readies) one of its triggers.</summary>
    internal void PlayOption(LibraryRow row, string trigger, bool solo)
    {
        if (!modsByDirectory.TryGetValue(row.Mod.Directory, out var mod))
        {
            Status = $"{row.Mod.Name} is no longer in the animation library.";
            return;
        }
        SelectForPlay(row);
        RememberRecent(row);

        pickToShare = solo ? null : row;
        if (TryParsePoseTrigger(trigger, out var pose))
        {
            if (solo) ActivateDetectedPoseSolo(mod.Directory, mod.Name, pose);
            else ActivateDetectedPose(mod.Directory, mod.Name, pose);
        }
        else if (uint.TryParse(trigger["emote:".Length..], out var emoteId) &&
                 emotePlaybackById.TryGetValue(emoteId, out var info))
        {
            var emote = new EmoteTarget(emoteId, EmoteDetails(emoteId).Name, info.Command);
            if (solo) ActivateDetectedEmoteSolo(mod.Directory, mod.Name, emote);
            else ActivateDetectedEmote(mod.Directory, mod.Name, emote);
        }
        pickToShare = null;
    }

    internal void SelectForPlay(LibraryRow row)
    {
        var directory = row.Mod.Directory;
        EnsureDefaultOptionSelections(directory);
        var saved = configuration.ModOptionSelections.GetValueOrDefault(directory);
        var changes = ExclusivePlay.SelectionChanges(row.Mod.Groups, row.Group, row.Option,
            group => saved?.GetValueOrDefault(group) ?? []);
        if (changes.Count == 0) return;
        saved ??= configuration.ModOptionSelections[directory] = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, selection) in changes) saved[group] = selection;
        configuration.Save(PluginInterface);
    }

    /// <summary>Tells the room which row a role belongs to, just before the role itself. A role picked
    /// any other way takes an earlier row back.</summary>
    private void SharePickedOption(string directory, string modKey)
    {
        var row = pickToShare;
        pickToShare = null;
        if (IsModPrivate(directory)) return;
        if (row is null ? !sharedPicks.Remove(modKey) : row.Group.IsImplicit && !row.Mod.UsesRows) return;
        if (row is not null) sharedPicks.Add(modKey);
        _ = sync.SetOptionSelectionAsync(modKey, PickedOptionGroup, row?.PickKey ?? NoPickedOption).ContinueWith(
            task => Log.Debug(task.Exception!.GetBaseException(), "Could not share the picked option."),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>What the room picked in a mod.</summary>
    internal IReadOnlyList<RemotePick> RemotePicksIn(string directory)
    {
        if (!sync.IsInRoom || !modSyncKeys.TryGetValue(directory, out var modKey)) return [];
        var now = Environment.TickCount64;
        var revision = LibraryRevision;
        // Asked for by every visible row. Picks of members who left go without a revision of their own.
        if (revision != remotePicksRevision || now - remotePicksReadAt > 500)
        {
            remotePicksRevision = revision;
            remotePicksReadAt = now;
            remotePicks.Clear();
            foreach (var picks in remoteOptionSelections.Values
                         .Where(selection => selection.Group is "$detected-trigger" or PickedOptionGroup)
                         .GroupBy(selection => selection.ModKey, StringComparer.OrdinalIgnoreCase))
                remotePicks[picks.Key] = picks.GroupBy(selection => selection.MemberName, StringComparer.OrdinalIgnoreCase)
                    .Select(member => new RemotePick(member.Key,
                        member.FirstOrDefault(selection => selection.Group == "$detected-trigger")?.Option ?? "",
                        member.FirstOrDefault(selection => selection.Group == PickedOptionGroup)?.Option is { } picked &&
                        picked != NoPickedOption ? picked : ""))
                    .Where(pick => pick.Trigger.Length > 0)
                    .ToList();
        }
        return remotePicks.GetValueOrDefault(modKey, []);
    }

    /// <summary>The saved role names that contain a search, by mod.</summary>
    internal Dictionary<string, List<(string Group, string Option)>> TagsMatching(string text)
    {
        var tags = new Dictionary<string, List<(string Group, string Option)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, note) in configuration.OptionNotes)
        {
            if (!note.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = key.Split('\n', 3);
            if (parts.Length != 3) continue;
            if (!tags.TryGetValue(parts[0], out var found)) tags[parts[0]] = found = [];
            found.Add((parts[1], parts[2]));
        }
        return tags;
    }

    // ---- Favourites and recents ---------------------------------------------------------------

    internal IReadOnlyCollection<string> FavoriteOptions => configuration.FavoriteOptions;
    internal IReadOnlyList<string> RecentOptions => configuration.RecentOptions;
    internal bool IsFavorite(LibraryRow row) => configuration.FavoriteOptions.Contains(row.Id);

    internal void ToggleFavorite(LibraryRow row)
    {
        if (!configuration.FavoriteOptions.Remove(row.Id)) configuration.FavoriteOptions.Add(row.Id);
        LibraryMarksRevision++;
        configuration.Save(PluginInterface);
    }

    /// <summary>The row an id names, while its mod is installed and still has it.</summary>
    internal LibraryRow? FindRow(string id) => LibraryOf(id[..Math.Max(0, id.IndexOf('\n'))])?.FindRow(id);

    /// <summary>Saved with the activation that follows.</summary>
    internal void RememberRecent(LibraryRow row)
    {
        var recent = configuration.RecentOptions;
        recent.RemoveAll(id => id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, row.Id);
        if (recent.Count > MaxRecentOptions) recent.RemoveRange(MaxRecentOptions, recent.Count - MaxRecentOptions);
        LibraryMarksRevision++;
    }
}

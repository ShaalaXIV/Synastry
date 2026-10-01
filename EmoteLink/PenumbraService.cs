using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Text.Json;

namespace EmoteLink;

public sealed class PenumbraService : IDisposable
{
    private const string Source = "EmoteLink";
    // Penumbra's type-erased ModProperty indices for API 5.
    private const int ModNameProperty = 2;
    private const int ModIdentifierProperty = 3;
    private const int ModFolderProperty = 10;
    private const int ModFullPathProperty = 11;
    private readonly IPluginLog log;
    private readonly string? organizationFile;
    private readonly ICallGateSubscriber<int> apiVersion;
    private readonly ICallGateSubscriber<Dictionary<string, string>> getModList;
    private readonly ICallGateSubscriber<IDisposable> getModListAdapter;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> getCollectionForObject;
    private readonly ICallGateSubscriber<Guid, string, string, bool,
        (int, (bool, int, Dictionary<string, List<string>>, bool)?)> getSettings;
    private readonly ICallGateSubscriber<Guid, string, string,
        (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int> setTemporary;
    private readonly ICallGateSubscriber<Guid, string, string, int, int> removeTemporary;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>> getChangedItems;
    private readonly ICallGateSubscriber<string> getModDirectory;
    private readonly ICallGateSubscriber<string, string,
        IReadOnlyDictionary<string, (string[] Options, int GroupType)>?> getAvailableSettings;
    private readonly ICallGateSubscriber<string, string, int> reloadMod;
    private readonly ICallGateSubscriber<string, int> addMod;
    private readonly ICallGateSubscriber<string, int> installMod;
    private readonly ICallGateSubscriber<string, string, string, int> setModPath;
    private readonly ICallGateSubscriber<string, object?> modAdded;
    private readonly ICallGateSubscriber<string, string> resolvePlayerPath;
    private readonly ICallGateSubscriber<string, int, string> resolveGameObjectPath;

    public event Action<string>? ModAdded;

    public PenumbraService(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        organizationFile = pi.ConfigDirectory.Parent is { } configRoot
            ? Path.Combine(configRoot.FullName, "Penumbra", "mod_filesystem", "organization.json")
            : null;
        apiVersion = pi.GetIpcSubscriber<int>("Penumbra.ApiVersion");
        getModList = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        getModListAdapter = pi.GetIpcSubscriber<IDisposable>("Penumbra.GetModListAdapter");
        getCollectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        getSettings = pi.GetIpcSubscriber<Guid, string, string, bool,
            (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5");
        setTemporary = pi.GetIpcSubscriber<Guid, string, string,
            (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int>("Penumbra.SetTemporaryModSettings.V5");
        removeTemporary = pi.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.RemoveTemporaryModSettings.V5");
        getChangedItems = pi.GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems.V5");
        getModDirectory = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
        getAvailableSettings = pi.GetIpcSubscriber<string, string,
            IReadOnlyDictionary<string, (string[] Options, int GroupType)>?>("Penumbra.GetAvailableModSettings.V5");
        reloadMod = pi.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5");
        addMod = pi.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5");
        installMod = pi.GetIpcSubscriber<string, int>("Penumbra.InstallMod.V5");
        setModPath = pi.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath.V5");
        modAdded = pi.GetIpcSubscriber<string, object?>("Penumbra.ModAdded");
        resolvePlayerPath = pi.GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath.V5");
        resolveGameObjectPath = pi.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath.V5");
        modAdded.Subscribe(OnModAdded);
    }

    public bool IsAvailable
    {
        get
        {
            try { return apiVersion.InvokeFunc() >= 5; }
            catch { return false; }
        }
    }

    public IReadOnlyList<(string Directory, string Name)> GetMods()
    {
        try
        {
            return getModList.InvokeFunc()
                .Select(pair => (pair.Key, pair.Value))
                .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read Penumbra's mod list.");
            return [];
        }
    }

    public (Guid Id, string Name)? GetPlayerCollection()
        => GetCollectionForObject(0);

    public (Guid Id, string Name)? GetCollectionForObject(int objectIndex)
    {
        try
        {
            var (valid, _, collection) = getCollectionForObject.InvokeFunc(objectIndex);
            return valid && collection.Item1 != Guid.Empty ? collection : null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read Penumbra collection for object index {ObjectIndex}.", objectIndex);
            return null;
        }
    }

    public (bool Success, string Error) Activate(Guid collectionId, string directory, string name,
        IReadOnlyDictionary<string, List<string>> selectedOptions, int priority = 9999)
    {
        try
        {
            var (code, current) = getSettings.InvokeFunc(collectionId, directory, name, false);
            if (code != 0)
            {
                log.Warning("Could not read Penumbra settings for {Mod}; error code {ErrorCode}.", name, code);
                return (false, DescribeActivationError(code));
            }

            // Penumbra rejects the whole request when any group or option name is not an exact
            // match, including a single-select group sent with no option. Saved selections can
            // drift from the installed mod (trimmed names, updated packs, older Synastry builds),
            // so every name is resolved against the mod's current option list first.
            var available = GetAvailableGroups(directory, name);
            var options = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var dropped = new List<string>();
            // A newly installed mod can legitimately have no collection settings yet.
            // Penumbra's temporary-settings API creates default settings in that case and
            // emits the cache refresh needed to make the mod's PAP files immediately usable.
            if (current is { } currentSettings)
                foreach (var (group, selections) in currentSettings.Item3)
                    AddResolvedSelection(options, available, group, selections, null);
            foreach (var (group, selections) in selectedOptions)
                AddResolvedSelection(options, available, group, selections, dropped);
            if (dropped.Count > 0)
                log.Warning("Ignored saved options for {Mod} that no longer match Penumbra: {Dropped}",
                    name, string.Join("; ", dropped));

            var settings = (false, true, priority, (IReadOnlyDictionary<string, IReadOnlyList<string>>)options);
            var result = setTemporary.InvokeFunc(collectionId, directory, name, settings, Source, 0);
            if (result is 0 or 1) return (true, "");
            log.Warning("Penumbra rejected temporary activation for {Mod}; error code {ErrorCode}.", name, result);
            return (false, DescribeActivationError(result));
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not temporarily activate {Mod}.", name);
            return (false, $"Penumbra could not be reached: {ex.GetBaseException().Message}");
        }
    }

    private static void AddResolvedSelection(
        IDictionary<string, IReadOnlyList<string>> options,
        IReadOnlyDictionary<string, PenumbraOptionGroup>? available,
        string group,
        IReadOnlyList<string> selections,
        List<string>? dropped)
    {
        if (available is null)
        {
            // Without Penumbra's option list the request is sent unchanged, as before.
            options[group] = selections.ToList();
            return;
        }

        var target = MatchName(available.Keys, group);
        if (target is null)
        {
            dropped?.Add($"group '{group}'");
            return;
        }

        var definition = available[target];
        var resolved = new List<string>(selections.Count);
        foreach (var selection in selections)
        {
            var option = MatchName(definition.Options, selection);
            if (option is null) dropped?.Add($"'{group}' option '{selection}'");
            else if (!resolved.Contains(option, StringComparer.Ordinal)) resolved.Add(option);
        }

        if (!definition.IsMultiSelect)
        {
            // Penumbra cannot leave a single-select group empty. Keep the collection's or the
            // mod's default choice instead of sending a request it will reject.
            if (resolved.Count == 0) return;
            resolved.RemoveRange(1, resolved.Count - 1);
        }
        options[target] = resolved;
    }

    private static string? MatchName(IEnumerable<string> candidates, string requested)
    {
        var list = candidates as IReadOnlyCollection<string> ?? candidates.ToList();
        return list.FirstOrDefault(candidate => candidate.Equals(requested, StringComparison.Ordinal)) ??
               list.FirstOrDefault(candidate => candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)) ??
               list.FirstOrDefault(candidate =>
                   candidate.Trim().Equals(requested.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeActivationError(int code) => code switch
    {
        2 => "Penumbra could not find your character's collection (CollectionMissing).",
        3 => "Penumbra no longer has this mod under that folder. Refresh the library and try again (ModMissing).",
        4 or 5 => "A saved option no longer exists in this mod. Re-select its options and try again " +
                  $"({(code == 4 ? "OptionGroupMissing" : "OptionMissing")}).",
        17 => "Penumbra is shutting down (SystemDisposed).",
        19 => "Another plugin has locked this mod's temporary settings in your collection " +
              "(TemporarySettingDisallowed). Clear that plugin's override or its temporary settings in Penumbra.",
        20 => "Your character's collection cannot hold temporary settings, for example the None or a " +
              "temporary collection (TemporarySettingImpossible).",
        22 => "Your character's collection is not active in Penumbra (CollectionInactive).",
        _ => $"Penumbra rejected the temporary activation (error code {code})."
    };

    /// <summary>The file a game path loads from for this player, or null if Penumbra can't say.</summary>
    public string? ResolvePlayerPath(string gamePath)
    {
        try { return resolvePlayerPath.InvokeFunc(gamePath); }
        catch { return null; }
    }

    /// <summary>The file a game path loads from for another character, including a sync plugin's
    /// temporary collection for them, or null if Penumbra can't say.</summary>
    public string? ResolveGameObjectPath(string gamePath, int objectIndex)
    {
        try { return resolveGameObjectPath.InvokeFunc(gamePath, objectIndex); }
        catch { return null; }
    }

    public string? GetModRoot()
    {
        try { return getModDirectory.InvokeFunc(); }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read Penumbra's mod directory.");
            return null;
        }
    }

    /// <summary>
    /// Penumbra's exact group and option names. Only <c>Single</c> (0) groups are single-select;
    /// <c>Multi</c> (1), <c>Imc</c> (2), and <c>Combining</c> (3) all accept several options.
    /// </summary>
    public IReadOnlyDictionary<string, PenumbraOptionGroup>? GetAvailableGroups(string directory, string name)
    {
        try
        {
            return getAvailableSettings.InvokeFunc(directory, name)?.ToDictionary(
                group => group.Key,
                group => new PenumbraOptionGroup(group.Value.Options, group.Value.GroupType != 0),
                StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read options for {Mod}.", name);
            return null;
        }
    }

    public IReadOnlyList<string> GetChangedItemNames(string directory, string name)
    {
        try { return getChangedItems.InvokeFunc(directory, name).Keys.ToList(); }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not inspect changed items for {Mod}.", name);
            return [];
        }
    }

    public (bool Success, string Error) Reload(string directory, string name)
    {
        try
        {
            var result = reloadMod.InvokeFunc(directory, name);
            return result is 0 or 1
                ? (true, "")
                : (false, PenumbraError(result));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not reload converted Penumbra mod {ModName}.", name);
            return (false, ex.GetBaseException().Message);
        }
    }

    public (bool Success, string Error) AddMod(string directory)
    {
        try
        {
            var result = addMod.InvokeFunc(directory);
            return result is 0 or 1
                ? (true, "")
                : (false, PenumbraError(result));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not register managed Penumbra mod {ModDirectory}.", directory);
            return (false, ex.GetBaseException().Message);
        }
    }

    public (bool Success, string Error) InstallMod(string packagePath)
    {
        try
        {
            var result = installMod.InvokeFunc(packagePath);
            return result == 0
                ? (true, "")
                : (false, result switch
                {
                    9 => "Penumbra could not find the downloaded file",
                    11 => "Penumbra rejected the package path",
                    17 => "Penumbra is shutting down",
                    _ => $"Penumbra error code {result}"
                });
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not queue transferred mod for Penumbra installation.");
            return (false, ex.GetBaseException().Message);
        }
    }

    public IReadOnlyList<string> GetModFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in GetModListEntries())
            AddFolderAndParents(folders, mod.Folder);
        AddSavedOrganizationFolders(folders);

        return folders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public (bool Success, string Folder, string Error) EnsureModFolder(string requestedFolder)
    {
        if (!TryNormalizeModListFolder(requestedFolder, out var folder, out var error))
            return (false, "", error);
        return (true, folder, "");
    }

    public (bool Success, string FullPath, string Error) MoveModToFolder(
        string directory,
        string name,
        string requestedFolder)
    {
        var folderResult = EnsureModFolder(requestedFolder);
        if (!folderResult.Success) return (false, directory, folderResult.Error);

        try
        {
            var entries = GetModListEntries();
            var matches = entries.Where(mod =>
                    mod.Identifier.Equals(directory, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                matches = entries.Where(mod => mod.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                return (false, directory, matches.Count == 0
                    ? "Penumbra did not expose the installed mod in its organized mod list."
                    : "More than one Penumbra mod matched that received animation.");

            var entry = matches[0];
            var currentPath = NormalizeModListPath(entry.FullPath);
            var separator = currentPath.LastIndexOf('/');
            var nodeName = separator >= 0 ? currentPath[(separator + 1)..] : currentPath;
            if (string.IsNullOrWhiteSpace(nodeName)) nodeName = entry.Name;

            var existing = entries.Where(mod => !mod.Identifier.Equals(entry.Identifier,
                    StringComparison.OrdinalIgnoreCase))
                .Select(mod => NormalizeModListPath(mod.FullPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidate = folderResult.Folder.Length == 0
                ? nodeName
                : $"{folderResult.Folder}/{nodeName}";
            for (var suffix = 2; existing.Contains(candidate); suffix++)
                candidate = folderResult.Folder.Length == 0
                    ? $"{nodeName} ({suffix})"
                    : $"{folderResult.Folder}/{nodeName} ({suffix})";

            if (candidate.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                return (true, candidate, "");

            var result = setModPath.InvokeFunc(directory, name, candidate);
            return result == 0
                ? (true, candidate, "")
                : (false, directory, $"Penumbra error code {result}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not move received mod {ModName} into Penumbra folder {Folder}.",
                name, requestedFolder);
            return (false, directory, ex.GetBaseException().Message);
        }
    }

    private IReadOnlyList<PenumbraModListEntry> GetModListEntries()
    {
        try
        {
            using var adapter = getModListAdapter.InvokeFunc();
            var mods = (IReadOnlyList<IDisposable>)adapter;
            var entries = new List<PenumbraModListEntry>(mods.Count);
            foreach (var modAdapter in mods)
            {
                try
                {
                    var properties = (IReadOnlyList<object?>)modAdapter;
                    if (properties.Count <= ModFullPathProperty) continue;
                    entries.Add(new PenumbraModListEntry(
                        properties[ModIdentifierProperty] as string ?? "",
                        properties[ModNameProperty] as string ?? "",
                        properties[ModFolderProperty] as string ?? "",
                        properties[ModFullPathProperty] as string ?? ""));
                }
                finally
                {
                    modAdapter.Dispose();
                }
            }
            return entries;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read Penumbra's organized mod-list folders.");
            return [];
        }
    }

    private void AddSavedOrganizationFolders(ISet<string> folders)
    {
        if (organizationFile is null || !File.Exists(organizationFile)) return;
        try
        {
            using var stream = new FileStream(organizationFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Folders", out var savedFolders) ||
                savedFolders.ValueKind != JsonValueKind.Object) return;

            foreach (var folder in savedFolders.EnumerateObject())
            {
                if (folder.Value.ValueKind == JsonValueKind.Object &&
                    folder.Value.TryGetProperty("IsSeparator", out var isSeparator) &&
                    isSeparator.ValueKind == JsonValueKind.True) continue;
                AddFolderAndParents(folders, folder.Name);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read Penumbra's saved mod-list folder organization.");
        }
    }

    private static void AddFolderAndParents(ISet<string> folders, string path)
    {
        var folder = NormalizeModListPath(path);
        while (folder.Length > 0)
        {
            folders.Add(folder);
            var separator = folder.LastIndexOf('/');
            folder = separator < 0 ? "" : folder[..separator];
        }
    }

    private bool TryNormalizeModListFolder(
        string requestedFolder,
        out string relativeFolder,
        out string error)
    {
        relativeFolder = "";
        error = "";
        if (!IsAvailable)
        {
            error = "Penumbra is unavailable.";
            return false;
        }

        var normalized = NormalizeModListPath(requestedFolder);
        if (normalized.Length == 0) return true;
        if (Path.IsPathRooted(requestedFolder) || normalized.Split('/')
                .Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            error = "Choose a valid relative folder in Penumbra's mod list.";
            return false;
        }
        if (normalized.Length > 160)
        {
            error = "Penumbra mod-list folders must be 160 characters or fewer.";
            return false;
        }
        relativeFolder = normalized;
        return true;
    }

    private static string NormalizeModListPath(string path) =>
        path.Replace('\\', '/').Trim('/');

    public bool Remove(TemporaryAssignment assignment)
    {
        try
        {
            return removeTemporary.InvokeFunc(
                assignment.CollectionId, assignment.ModDirectory, assignment.ModName, 0) is 0 or 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not remove temporary settings for {Mod}.", assignment.ModName);
            return false;
        }
    }

    private static string PenumbraError(int code) => code switch
    {
        2 => "CollectionMissing (code 2)",
        3 => "ModMissing (code 3)",
        9 => "FileMissing (code 9)",
        11 => "InvalidArgument (code 11)",
        17 => "SystemDisposed (code 17)",
        _ => $"Penumbra error code {code}"
    };

    private void OnModAdded(string directory) => ModAdded?.Invoke(directory);

    public void Dispose() => modAdded.Unsubscribe(OnModAdded);
}

internal sealed record PenumbraModListEntry(string Identifier, string Name, string Folder, string FullPath);

public sealed record PenumbraOptionGroup(IReadOnlyList<string> Options, bool IsMultiSelect);

public sealed record ModOptionGroup(string Name, IReadOnlyList<string> Options, bool IsMultiSelect);

using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace EmoteLink;

public sealed class PenumbraService : IDisposable
{
    private const string Source = "EmoteLink";
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<int> apiVersion;
    private readonly ICallGateSubscriber<Dictionary<string, string>> getModList;
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
    private readonly ICallGateSubscriber<string, int> installMod;
    private readonly ICallGateSubscriber<string, object?> modAdded;

    public event Action<string>? ModAdded;

    public PenumbraService(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = pi.GetIpcSubscriber<int>("Penumbra.ApiVersion");
        getModList = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
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
        installMod = pi.GetIpcSubscriber<string, int>("Penumbra.InstallMod.V5");
        modAdded = pi.GetIpcSubscriber<string, object?>("Penumbra.ModAdded");
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
    {
        try
        {
            var (valid, _, collection) = getCollectionForObject.InvokeFunc(0);
            return valid && collection.Item1 != Guid.Empty ? collection : null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read the player's Penumbra collection.");
            return null;
        }
    }

    public bool Activate(Guid collectionId, string directory, string name,
        IReadOnlyDictionary<string, List<string>> selectedOptions)
    {
        try
        {
            var (code, current) = getSettings.InvokeFunc(collectionId, directory, name, false);
            if (code != 0 || current is null)
                return false;

            var (_, _, options, _) = current.Value;
            foreach (var (group, selections) in selectedOptions)
                options[group] = selections.ToList();
            var readonlyOptions = options.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value);
            var settings = (false, true, 9999,
                (IReadOnlyDictionary<string, IReadOnlyList<string>>)readonlyOptions);
            var result = setTemporary.InvokeFunc(collectionId, directory, name, settings, Source, 0);
            return result is 0 or 1;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not temporarily activate {Mod}.", name);
            return false;
        }
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

    public IReadOnlyList<ModOptionGroup> GetOptionGroups(string directory, string name)
    {
        try
        {
            var groups = getAvailableSettings.InvokeFunc(directory, name);
            return groups?.Select(group => new ModOptionGroup(
                    group.Key, group.Value.Options, group.Value.GroupType == 2))
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read options for {Mod}.", name);
            return [];
        }
    }

    public Dictionary<string, List<string>> GetCurrentOptions(Guid collectionId, string directory, string name)
    {
        try
        {
            var (code, settings) = getSettings.InvokeFunc(collectionId, directory, name, false);
            return code == 0 && settings is not null
                ? settings.Value.Item3.ToDictionary(pair => pair.Key, pair => pair.Value.ToList())
                : [];
        }
        catch { return []; }
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

    private void OnModAdded(string directory) => ModAdded?.Invoke(directory);

    public void Dispose() => modAdded.Unsubscribe(OnModAdded);
}

public sealed record ModOptionGroup(string Name, IReadOnlyList<string> Options, bool IsMultiSelect);

using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EmoteLink;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/emotelink";
    private const float MaxAlignDistance = 2f;
    private const string RelayUrl = "http://74.208.141.184:5080";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static ITargetManager Targets { get; set; } = null!;
    [PluginService] private static IGameInteropProvider Interop { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;

    private readonly Configuration configuration;
    private readonly PenumbraService penumbra;
    private readonly MovementService movement;
    private readonly PoseService poses;
    private readonly AnimationSyncService sync;
    private readonly WindowSystem windows = new("EmoteLink");
    private readonly MainWindow mainWindow;
    private bool waitingForAnimation;
    private long activationTime;
    private readonly Dictionary<string, string> emoteCommandsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ModOptionGroup>> optionGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private string? pendingCommand;
    private long pendingCommandTime;
    private PoseTarget? pendingPose;
    private readonly Dictionary<string, PoseTarget> optionPoses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> optionGroupMulti = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modSyncKeys = new(StringComparer.OrdinalIgnoreCase);
    private PoseTarget? cyclingPose;
    private long nextPoseCycleTime;
    private int poseCycleAttempts;
    private long movementTrackingStart;
    private System.Numerics.Vector3 movementSample;
    private bool hasMovementSample;
    private int movementFrames;
    private readonly ConcurrentQueue<PlaySignalDto> syncPlaySignals = new();
    private string? preparedModKey;
    private string? preparedCommand;
    private PoseTarget? preparedPose;

    public IReadOnlyList<(string Directory, string Name)> Mods { get; private set; } = [];
    public IReadOnlyList<ModCategory> Categories => configuration.Categories;
    public bool PenumbraAvailable => penumbra.IsAvailable;
    public bool IsAligning => movement.IsWalking;
    public string Status { get; private set; } = "Ready.";
    public AnimationSyncService Sync => sync;
    public string SyncDisplayName => configuration.SyncDisplayName;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        penumbra = new PenumbraService(PluginInterface, Log);
        movement = new MovementService(Interop, Objects);
        poses = new PoseService(Objects);
        sync = new AnimationSyncService();
        sync.PlayReceived += signal => syncPlaySignals.Enqueue(signal);
        mainWindow = new MainWindow(this);
        BuildEmoteLookup();
        windows.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        Framework.Update += OnUpdate;
        Commands.AddHandler(Command, new CommandInfo((_, _) => ToggleWindow())
        {
            HelpMessage = "Open EmoteLink."
        });

        // Recover from an unload/crash that left our tracked overrides behind.
        ClearTemporaryAssignments();
        RefreshMods();
    }

    public void RefreshMods()
    {
        if (!penumbra.IsAvailable)
        {
            Mods = [];
            Status = "Penumbra is unavailable.";
            return;
        }

        var root = penumbra.GetModRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Mods = [];
            Status = "Penumbra's mod directory could not be read.";
            return;
        }

        var allMods = penumbra.GetMods();
        var animationMods = new List<(string Directory, string Name)>();
        optionGroups.Clear();
        optionPoses.Clear();
        optionGroupMulti.Clear();
        modSyncKeys.Clear();
        foreach (var mod in allMods)
        {
            var path = Path.Combine(root, mod.Directory);
            try
            {
                if (!Directory.Exists(path) ||
                    !Directory.EnumerateFiles(path, "*.pap", SearchOption.AllDirectories).Any())
                    continue;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not scan {ModDirectory} for PAP files.", mod.Directory);
                continue;
            }

            animationMods.Add(mod);
            modSyncKeys[mod.Directory] = BuildModSyncKey(path, mod.Name);
            IndexPoseOptions(path, mod.Directory);
            var groups = penumbra.GetOptionGroups(mod.Directory, mod.Name)
                .Select(group => optionGroupMulti.TryGetValue(OptionGroupKey(mod.Directory, group.Name), out var multi)
                    ? group with { IsMultiSelect = multi }
                    : group)
                .ToList();
            optionGroups[mod.Directory] = groups;
            NormalizeSelections(mod.Directory, groups);
            InitializeOptionSelections(mod, groups);
        }
        Mods = animationMods;
        Status = $"Loaded {Mods.Count} mod(s) containing PAP animations.";
        NormalizeOrganization();
    }

    public IReadOnlyList<ModOptionGroup> GetOptionGroups(string directory) =>
        optionGroups.TryGetValue(directory, out var groups) ? groups : [];

    public bool IsOptionSelected(string directory, string group, string option) =>
        configuration.ModOptionSelections.TryGetValue(directory, out var groups) &&
        groups.TryGetValue(group, out var selected) && selected.Contains(option, StringComparer.OrdinalIgnoreCase);

    public void SetOptionSelected(string directory, string group, string option, bool selected, bool multiSelect)
    {
        if (!configuration.ModOptionSelections.TryGetValue(directory, out var groups))
            configuration.ModOptionSelections[directory] = groups = new(StringComparer.OrdinalIgnoreCase);
        if (!groups.TryGetValue(group, out var selections)) groups[group] = selections = [];

        if (!multiSelect)
        {
            if (selected || selections.Count == 0)
            {
                selections.Clear();
                selections.Add(option);
            }
        }
        else if (selected)
        {
            if (!selections.Contains(option, StringComparer.OrdinalIgnoreCase)) selections.Add(option);
        }
        else
        {
            selections.RemoveAll(item => item.Equals(option, StringComparison.OrdinalIgnoreCase));
        }
        SaveOrganization();
    }

    public void ApplyOption(string directory, string name, string group, string option, bool selected)
    {
        var pose = selected ? GetOptionPose(directory, group, option) : null;
        ActivateInternal(directory, name, pose);
    }

    public void ActivateOption(
        string directory,
        string name,
        string group,
        string option,
        bool multiSelect)
    {
        SetOptionSelected(directory, group, option, true, multiSelect);
        ActivateInternal(directory, name, GetOptionPose(directory, group, option));
    }

    public void SaveSyncSettings(string displayName)
    {
        configuration.SyncDisplayName = displayName.Trim();
        SaveOrganization();
    }

    public void ConnectSync(string displayName)
    {
        SaveSyncSettings(displayName);
        RunSync(sync.ConnectAsync(RelayUrl), "Connected to animation relay.");
    }

    public void DisconnectSync() => RunSync(sync.DisconnectAsync(), "Disconnected from animation relay.");
    public void CreateSyncRoom(string displayName)
    {
        SaveSyncSettings(displayName);
        RunSync(sync.CreateRoomAsync(configuration.SyncDisplayName), "Created group-play room.");
    }

    public void JoinSyncRoom(string code, string displayName)
    {
        SaveSyncSettings(displayName);
        RunSync(sync.JoinRoomAsync(code, configuration.SyncDisplayName), "Joined group-play room.");
    }

    public void LeaveSyncRoom() => RunSync(sync.LeaveRoomAsync(), "Left group-play room.");
    public void CancelSyncReady()
    {
        preparedModKey = null;
        preparedCommand = null;
        preparedPose = null;
        RunSync(sync.CancelReadyAsync(), "Group-play readiness cancelled.");
    }

    private void RunSync(Task operation, string success)
    {
        _ = operation.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) Status = success;
            else if (task.Exception is not null)
            {
                var ex = task.Exception.GetBaseException();
                Status = $"Group play: {ex.Message}";
                Log.Warning(ex, "Group-play operation failed.");
            }
        }, TaskScheduler.Default);
    }

    public PoseTarget? GetOptionPose(string directory, string group, string option)
    {
        var key = OptionPoseKey(directory, group, option);
        if (configuration.ManualPoseAssignments.TryGetValue(key, out var manual))
            return new PoseTarget(manual.Kind, PoseService.ClampIndex(manual.Index));
        return optionPoses.TryGetValue(key, out var pose) ? pose : null;
    }

    public bool HasManualPose(string directory, string group, string option) =>
        configuration.ManualPoseAssignments.ContainsKey(OptionPoseKey(directory, group, option));

    public void SetManualPose(string directory, string group, string option, PoseTarget? pose)
    {
        var key = OptionPoseKey(directory, group, option);
        if (pose is null) configuration.ManualPoseAssignments.Remove(key);
        else configuration.ManualPoseAssignments[key] = new ManualPoseAssignment
        {
            Kind = pose.Kind,
            Index = PoseService.ClampIndex(pose.Index)
        };
        SaveOrganization();
    }

    private void NormalizeSelections(string directory, IReadOnlyList<ModOptionGroup> groups)
    {
        if (!configuration.ModOptionSelections.TryGetValue(directory, out var selections)) return;
        foreach (var group in groups.Where(group => !group.IsMultiSelect))
            if (selections.TryGetValue(group.Name, out var selected) && selected.Count > 1)
                selected.RemoveRange(1, selected.Count - 1);
    }

    private void InitializeOptionSelections((string Directory, string Name) mod, IReadOnlyList<ModOptionGroup> groups)
    {
        if (configuration.ModOptionSelections.ContainsKey(mod.Directory)) return;
        var collection = penumbra.GetPlayerCollection();
        var current = collection is null ? [] : penumbra.GetCurrentOptions(collection.Value.Id, mod.Directory, mod.Name);
        var selections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var valid = group.Options.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selections[group.Name] = current.TryGetValue(group.Name, out var chosen)
                ? chosen.Where(valid.Contains).ToList()
                : [];
        }
        configuration.ModOptionSelections[mod.Directory] = selections;
    }

    public IReadOnlyList<(string Directory, string Name)> GetOrganizedMods(string? categoryId)
    {
        var order = categoryId is null
            ? configuration.UncategorizedOrder
            : configuration.Categories.FirstOrDefault(folder => folder.Id == categoryId)?.ModDirectories ?? [];
        var byDirectory = Mods.ToDictionary(mod => mod.Directory, StringComparer.OrdinalIgnoreCase);
        return order.Where(byDirectory.ContainsKey).Select(directory => byDirectory[directory]).ToList();
    }

    public void CreateCategory(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        configuration.Categories.Add(new ModCategory { Name = name });
        SaveOrganization();
    }

    public void DeleteCategory(string categoryId)
    {
        var category = configuration.Categories.FirstOrDefault(item => item.Id == categoryId);
        if (category is null) return;
        configuration.UncategorizedOrder.AddRange(category.ModDirectories);
        configuration.Categories.Remove(category);
        NormalizeOrganization();
    }

    public void MoveMod(string directory, string? targetCategoryId, string? beforeDirectory = null)
    {
        configuration.UncategorizedOrder.RemoveAll(item => item.Equals(directory, StringComparison.OrdinalIgnoreCase));
        foreach (var category in configuration.Categories)
            category.ModDirectories.RemoveAll(item => item.Equals(directory, StringComparison.OrdinalIgnoreCase));

        var target = targetCategoryId is null
            ? configuration.UncategorizedOrder
            : configuration.Categories.FirstOrDefault(item => item.Id == targetCategoryId)?.ModDirectories;
        if (target is null) return;
        var index = beforeDirectory is null
            ? -1
            : target.FindIndex(item => item.Equals(beforeDirectory, StringComparison.OrdinalIgnoreCase));
        if (index < 0) target.Add(directory); else target.Insert(index, directory);
        SaveOrganization();
    }

    public void MoveCategory(string sourceId, string beforeId)
    {
        if (sourceId == beforeId) return;
        var source = configuration.Categories.FirstOrDefault(item => item.Id == sourceId);
        var targetIndex = configuration.Categories.FindIndex(item => item.Id == beforeId);
        if (source is null || targetIndex < 0) return;
        configuration.Categories.Remove(source);
        targetIndex = configuration.Categories.FindIndex(item => item.Id == beforeId);
        configuration.Categories.Insert(targetIndex, source);
        SaveOrganization();
    }

    private void NormalizeOrganization()
    {
        var available = Mods.Select(mod => mod.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in configuration.Categories)
            category.ModDirectories.RemoveAll(directory => !available.Contains(directory) || !seen.Add(directory));
        configuration.UncategorizedOrder.RemoveAll(directory => !available.Contains(directory) || !seen.Add(directory));
        configuration.UncategorizedOrder.AddRange(Mods.Select(mod => mod.Directory).Where(seen.Add));
        SaveOrganization();
    }

    private void SaveOrganization() => configuration.Save(PluginInterface);

    public void Activate(string directory, string name) => ActivateInternal(directory, name, null);

    private void ActivateInternal(string directory, string name, PoseTarget? requestedPose)
    {
        ClearTemporaryAssignmentsInternal(false);
        var collection = penumbra.GetPlayerCollection();
        var selections = configuration.ModOptionSelections.TryGetValue(directory, out var savedOptions)
            ? savedOptions
            : new Dictionary<string, List<string>>();
        if (collection is null || !penumbra.Activate(collection.Value.Id, directory, name, selections))
        {
            Log.Warning("Temporary activation failed for {Mod}.", name);
            Status = $"Could not activate {name}.";
            return;
        }

        configuration.ActiveAssignments.Add(new TemporaryAssignment(collection.Value.Id, directory, name));
        configuration.Save(PluginInterface);
        waitingForAnimation = true;
        activationTime = Environment.TickCount64;
        movementTrackingStart = activationTime + 1200;
        hasMovementSample = false;
        movementFrames = 0;

        if (requestedPose is not null)
        {
            if (PrepareForGroupPlay(directory, name, null, requestedPose)) return;
            SchedulePose(name, requestedPose, 300);
            return;
        }

        var command = DetectEmoteCommand(directory, name);
        if (command is null)
        {
            Status = $"Activated {name}, but no emote command was detected.";
            Chat.PrintError($"[EmoteLink] {name} was activated, but its emote could not be detected.");
            return;
        }

        if (PrepareForGroupPlay(directory, name, command, null)) return;
        ScheduleCommand(name, command, 300);
    }

    private bool PrepareForGroupPlay(string directory, string modName, string? command, PoseTarget? pose)
    {
        if (!sync.IsInRoom) return false;
        preparedModKey = modSyncKeys.TryGetValue(directory, out var key) ? key : NormalizeModKey(modName);
        preparedCommand = command;
        preparedPose = pose;
        Status = $"Prepared {modName}; waiting for everyone in room {sync.Room!.RoomCode}.";
        RunSync(sync.SetReadyAsync(preparedModKey), $"Ready with {modName}; waiting for the group.");
        return true;
    }

    private void SchedulePose(string modName, PoseTarget pose, int delayMs)
    {
        Status = $"Activated {modName}; switching to {PoseLabel(pose)}.";
        pendingPose = pose;
        pendingCommandTime = Environment.TickCount64 + delayMs;
    }

    private void ScheduleCommand(string modName, string command, int delayMs)
    {
        Status = $"Activated {modName}; starting {command}.";
        pendingCommand = command;
        pendingCommandTime = Environment.TickCount64 + delayMs;
    }

    private static string NormalizeModKey(string modName) =>
        string.Join(' ', modName.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string BuildModSyncKey(string modPath, string modName)
    {
        var gamePaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(modPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                CollectPapGamePaths(document.RootElement, gamePaths);
            }
            catch { }
        }
        var identity = NormalizeModKey(modName) + "\n" + string.Join('\n', gamePaths.Select(path => path.ToLowerInvariant()));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        return $"{NormalizeModKey(modName)}:{hash}";
    }

    private static void CollectPapGamePaths(JsonElement element, ISet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("Files") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var file in property.Value.EnumerateObject())
                        if (file.Name.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                            paths.Add(file.Name.Replace('\\', '/'));
                }
                else CollectPapGamePaths(property.Value, paths);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectPapGamePaths(item, paths);
        }
    }

    private void IndexPoseOptions(string modPath, string directory)
    {
        foreach (var file in Directory.EnumerateFiles(modPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (file.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith("default_mod.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (!root.TryGetProperty("Name", out var groupNameElement) ||
                    !root.TryGetProperty("Options", out var optionsElement)) continue;
                var groupName = groupNameElement.GetString();
                if (string.IsNullOrWhiteSpace(groupName) || optionsElement.ValueKind != JsonValueKind.Array) continue;
                var isMulti = root.TryGetProperty("Type", out var typeElement) &&
                    string.Equals(typeElement.GetString(), "Multi", StringComparison.OrdinalIgnoreCase);
                optionGroupMulti[OptionGroupKey(directory, groupName)] = isMulti;

                foreach (var optionElement in optionsElement.EnumerateArray())
                {
                    if (!optionElement.TryGetProperty("Name", out var optionNameElement) ||
                        !optionElement.TryGetProperty("Files", out var filesElement)) continue;
                    var optionName = optionNameElement.GetString();
                    if (string.IsNullOrWhiteSpace(optionName) || filesElement.ValueKind != JsonValueKind.Object) continue;
                    var paths = filesElement.EnumerateObject().Select(property => property.Name).ToList();
                    var pose = DetectPoseTarget(paths, optionName);
                    if (pose is not null) optionPoses[OptionPoseKey(directory, groupName, optionName)] = pose;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not inspect pose options in {File}.", file);
            }
        }
    }

    private static PoseTarget? DetectPoseTarget(IReadOnlyList<string> paths, string optionName)
    {
        foreach (var rawPath in paths.Where(path => path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)))
        {
            var path = rawPath.Replace('\\', '/').ToLowerInvariant();
            var candidates = new[]
            {
                (Kind: PoseKind.GroundSit, Pattern: @"j_pose(\d+)"),
                (Kind: PoseKind.Sit, Pattern: @"s_pose(\d+)"),
                (Kind: PoseKind.Doze, Pattern: @"l_pose(\d+)"),
                (Kind: PoseKind.Idle, Pattern: @"(?:^|[/_])pose(\d+)")
            };
            foreach (var candidate in candidates)
            {
                var match = Regex.Match(path, candidate.Pattern, RegexOptions.IgnoreCase);
                if (match.Success && byte.TryParse(match.Groups[1].Value, out var index) &&
                    index <= PoseService.MaxPoseIndex)
                    return new PoseTarget(candidate.Kind, index);
            }
            if (path.Contains("/resident/idle.pap")) return new PoseTarget(PoseKind.Idle, 0);

            PoseKind? kind = path.Contains("/jmn/") ? PoseKind.GroundSit
                : path.Contains("/sit/") ? PoseKind.Sit
                : path.Contains("/doze/") ? PoseKind.Doze
                : null;
            if (kind is not null)
            {
                var labelIndex = Regex.Match(optionName, @"(\d+)(?!.*\d)");
                return new PoseTarget(kind.Value,
                    labelIndex.Success && byte.TryParse(labelIndex.Value, out var index)
                        ? PoseService.ClampIndex(index)
                        : (byte)0);
            }
        }
        return null;
    }

    private static string OptionPoseKey(string directory, string group, string option) =>
        $"{directory}\u001f{group}\u001f{option}";

    private static string OptionGroupKey(string directory, string group) => $"{directory}\u001f{group}";

    private static string PoseLabel(PoseTarget pose) => pose.Kind switch
    {
        PoseKind.GroundSit => $"ground-sit pose {pose.Index}",
        PoseKind.Sit => $"chair-sit pose {pose.Index}",
        PoseKind.Doze => $"doze pose {pose.Index}",
        _ => $"idle pose {pose.Index}"
    };

    private void BuildEmoteLookup()
    {
        var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        if (sheet is null) return;
        foreach (var row in sheet)
        {
            var textCommand = row.TextCommand.ValueNullable;
            if (textCommand is null) continue;
            var command = textCommand.Value.Command.ToString();
            var name = row.Name.ExtractText();
            if (command.Length == 0 || name.Length == 0) continue;
            AddEmoteName(name, command);
        }
        Log.Information("Loaded {Count} emote names for automatic playback.", emoteCommandsByName.Count);
    }

    private void AddEmoteName(string name, string command)
    {
        var normalized = NormalizeEmoteName(name);
        if (normalized.Length == 0) return;
        emoteCommandsByName.TryAdd(normalized, command);
        emoteCommandsByName.TryAdd(normalized.Replace("-", ""), command);
    }

    private string? DetectEmoteCommand(string directory, string name)
    {
        foreach (var changedItem in penumbra.GetChangedItemNames(directory, name))
        {
            var item = changedItem.Trim();
            if ((item.Contains('/') || item.Contains('\\')) && item.Contains('.')) continue;
            var colon = item.IndexOf(':');
            if (colon >= 0) item = item[(colon + 1)..];
            var normalized = NormalizeEmoteName(item);
            if (emoteCommandsByName.TryGetValue(normalized, out var direct)) return direct;
            if (emoteCommandsByName.TryGetValue(normalized.Replace("-", ""), out direct)) return direct;

            if (!changedItem.Contains("emote", StringComparison.OrdinalIgnoreCase) &&
                !changedItem.Contains("action", StringComparison.OrdinalIgnoreCase)) continue;
            var partial = emoteCommandsByName
                .Where(pair => pair.Key.Length > 3 &&
                    (normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(pair => pair.Key.Length)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(partial.Value)) return partial.Value;
        }
        return null;
    }

    private static string NormalizeEmoteName(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static void ExecuteCommand(string command)
    {
        var ui = UIModule.Instance();
        if (ui is null) return;
        using var text = new Utf8String(command);
        ui->ProcessChatBoxEntry(&text);
    }

    public void ClearTemporaryAssignments() => ClearTemporaryAssignmentsInternal(true);

    private void ClearTemporaryAssignmentsInternal(bool cancelGroupReady)
    {
        foreach (var assignment in configuration.ActiveAssignments.ToList())
            if (penumbra.Remove(assignment)) configuration.ActiveAssignments.Remove(assignment);

        configuration.Save(PluginInterface);
        waitingForAnimation = false;
        pendingCommand = null;
        pendingPose = null;
        cyclingPose = null;
        hasMovementSample = false;
        movementFrames = 0;
        if (cancelGroupReady && sync.IsInRoom)
        {
            preparedModKey = null;
            preparedCommand = null;
            preparedPose = null;
            RunSync(sync.CancelReadyAsync(), "Temporary animation and group readiness cleared.");
        }
        Status = "Temporary animation assignments cleared.";
    }

    public void ToggleAlignment()
    {
        if (movement.IsWalking) { movement.Cancel(); return; }
        var target = Targets.Target ?? Targets.SoftTarget;
        var player = (Character*)(Objects.LocalPlayer?.Address ?? 0);
        if (target is null || player is null || player->Mode != CharacterModes.Normal) return;
        if (System.Numerics.Vector3.Distance(Objects.LocalPlayer!.Position, target.Position) > MaxAlignDistance) return;

        var position = target.Position;
        var rotation = target.Rotation;
        movement.WalkTo(position, () =>
        {
            player->GameObject.SetPosition(position.X, position.Y, position.Z);
            player->GameObject.SetRotation(rotation);
        });
    }

    private void OnUpdate(IFramework _)
    {
        ProcessSyncPlaySignals();
        if (pendingPose is not null && Environment.TickCount64 >= pendingCommandTime)
        {
            var pose = pendingPose;
            pendingPose = null;
            ExecutePose(pose);
        }
        if (pendingCommand is not null && Environment.TickCount64 >= pendingCommandTime)
        {
            var command = pendingCommand;
            pendingCommand = null;
            ExecuteCommand(command);
        }
        UpdatePoseCycling();
        if (!waitingForAnimation) return;
        UpdateMovementCleanup();
    }

    private void ProcessSyncPlaySignals()
    {
        while (syncPlaySignals.TryDequeue(out var signal))
        {
            if (preparedModKey is null || !signal.ModKey.Equals(preparedModKey, StringComparison.OrdinalIgnoreCase))
            {
                Status = "Group play signal ignored because the prepared mod did not match.";
                continue;
            }
            // New relays send a relative countdown so different PC clock settings cannot
            // turn into multi-second start skew. Keep the UTC calculation only for old relays.
            var delay = signal.DelayMilliseconds > 0
                ? signal.DelayMilliseconds
                : Math.Max(0, signal.StartUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            pendingCommandTime = Environment.TickCount64 + delay;
            pendingCommand = preparedCommand;
            pendingPose = preparedPose;
            Status = $"Group ready. Starting in {delay / 1000f:F1}s.";
            Log.Information(
                "Group play {SequenceId} received; scheduling {ModKey} in {DelayMilliseconds} ms ({TimingMode}).",
                signal.SequenceId,
                signal.ModKey,
                delay,
                signal.DelayMilliseconds > 0 ? "relay countdown" : "legacy UTC");
            preparedModKey = null;
            preparedCommand = null;
            preparedPose = null;
        }
    }

    private void UpdateMovementCleanup()
    {
        if (Environment.TickCount64 < movementTrackingStart) return;
        var player = Objects.LocalPlayer;
        if (player is null) return;
        var position = player.Position;
        if (!hasMovementSample)
        {
            movementSample = position;
            hasMovementSample = true;
            return;
        }

        var dx = position.X - movementSample.X;
        var dz = position.Z - movementSample.Z;
        movementSample = position;
        // Requiring several consecutive translated frames ignores rotation,
        // network jitter, redraws, and one-frame furniture/pose snaps.
        if (dx * dx + dz * dz > 0.000025f)
        {
            if (++movementFrames >= 3)
            {
                ClearTemporaryAssignments();
                Status = "Movement detected; temporary animation assignment cleared.";
            }
        }
        else
        {
            movementFrames = 0;
        }
    }

    private void ExecutePose(PoseTarget pose)
    {
        var alreadyInPose = poses.CurrentKind() == pose.Kind;
        if (alreadyInPose)
        {
            ExecuteCommand("/penumbra redraw self");
            BeginPoseCycling(pose, 150);
            return;
        }

        poses.SetIndex(pose);
        ExecuteCommand("/penumbra redraw self");
        switch (pose.Kind)
        {
            case PoseKind.GroundSit: ExecuteCommand("/groundsit"); break;
            case PoseKind.Sit: ExecuteCommand("/sit"); break;
            case PoseKind.Doze: ExecuteCommand("/doze"); break;
            case PoseKind.Idle: BeginPoseCycling(pose, 150); break;
        }
        if (pose.Kind != PoseKind.Idle) BeginPoseCycling(pose, 500);
    }

    private void BeginPoseCycling(PoseTarget pose, int delayMs)
    {
        cyclingPose = pose;
        poseCycleAttempts = 0;
        nextPoseCycleTime = Environment.TickCount64 + delayMs;
    }

    private void UpdatePoseCycling()
    {
        if (cyclingPose is null || Environment.TickCount64 < nextPoseCycleTime) return;
        if (poses.CurrentKind() != cyclingPose.Kind)
        {
            if (++poseCycleAttempts >= 8) cyclingPose = null;
            else nextPoseCycleTime = Environment.TickCount64 + 100;
            return;
        }
        if (poses.CurrentIndex() == cyclingPose.Index)
        {
            cyclingPose = null;
            return;
        }
        ExecuteCommand("/cpose");
        if (++poseCycleAttempts >= 8) cyclingPose = null;
        else nextPoseCycleTime = Environment.TickCount64 + 100;
    }

    private void ToggleWindow() => mainWindow.Toggle();

    public void Dispose()
    {
        ClearTemporaryAssignments();
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        Commands.RemoveHandler(Command);
        movement.Dispose();
        sync.DisposeAsync().AsTask().GetAwaiter().GetResult();
        windows.RemoveAllWindows();
    }
}

using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;

namespace EmoteLink;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/emotelink";
    private const float MaxAlignDistance = 2f;
    private const int LobbyEmoteRefreshDelayMs = 5000;
    private const string RelayUrl = "https://emotelink.aethercast.org";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static ITargetManager Targets { get; set; } = null!;
    [PluginService] private static IGameInteropProvider Interop { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static IContextMenu ContextMenu { get; set; } = null!;

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
    private readonly Dictionary<string, EmoteTarget> emoteTargetsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ModOptionGroup>> optionGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private string? pendingCommand;
    private long pendingCommandTime;
    private PoseTarget? pendingPose;
    private string? pendingSelectionModKey;
    private long lobbyEmoteRefreshTime;
    private readonly Dictionary<string, PoseTarget> optionPoses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<PoseTarget>> modPoses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<EmoteTarget>> modEmotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> optionGroupMulti = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modSyncKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modCatalogKeys = new(StringComparer.OrdinalIgnoreCase);
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
    private nint alignmentTargetAddress;
    private int alignmentFramesRemaining;
    private int alignmentStableFrames;
    private readonly ConcurrentQueue<ModTransferOfferDto> transferOffers = new();
    private readonly ConcurrentQueue<(ModTransferOfferDto Offer, string Path, Exception? Error)> completedDownloads = new();
    private readonly ConcurrentDictionary<string, OptionSelectionDto> remoteOptionSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AnimationSuggestion> activeAnimationSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, DalamudLinkPayload> inviteLinks = [];
    private string? remoteSelectionRoom;

    public IReadOnlyList<(string Directory, string Name)> Mods { get; private set; } = [];
    public IReadOnlyList<ModCategory> Categories => configuration.Categories;
    public bool PenumbraAvailable => penumbra.IsAvailable;
    public bool IsAligning => movement.IsWalking || alignmentFramesRemaining > 0;
    public string Status { get; private set; } = "Ready.";
    public AnimationSyncService Sync => sync;
    public string SyncDisplayName => CurrentCharacterName() ?? "Unavailable";

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        penumbra = new PenumbraService(PluginInterface, Log);
        movement = new MovementService(Interop, Objects);
        poses = new PoseService(Objects);
        sync = new AnimationSyncService();
        sync.PlayReceived += signal => syncPlaySignals.Enqueue(signal);
        sync.ModTransferOffered += offer => transferOffers.Enqueue(offer);
        sync.OptionSelectionChanged += RememberOptionSelection;
        sync.AnimationSuggestionDeclined += OnAnimationSuggestionDeclined;
        sync.StateChanged += OnSyncStateChanged;
        sync.Diagnostic += (message, exception) =>
        {
            if (exception is null) Log.Information("{Message}", message);
            else Log.Warning(exception, "{Message}", message);
        };
        mainWindow = new MainWindow(this);
        BuildEmoteLookup();
        windows.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        Framework.Update += OnUpdate;
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Chat.ChatMessage += OnChatMessage;
        Commands.AddHandler(Command, new CommandInfo((_, arguments) =>
        {
            var match = Regex.Match(arguments, @"^\s*join\s+([A-Za-z0-9]{4,8})\s*$", RegexOptions.IgnoreCase);
            if (match.Success) JoinSyncRoom(match.Groups[1].Value);
            else ToggleWindow();
        })
        {
            HelpMessage = "Open EmoteLink, or join with /emotelink join ROOMCODE."
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
        modPoses.Clear();
        modEmotes.Clear();
        optionGroupMulti.Clear();
        modSyncKeys.Clear();
        modCatalogKeys.Clear();
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
            modCatalogKeys[mod.Directory] = CatalogFingerprint(modSyncKeys[mod.Directory]);
            IndexPoseOptions(path, mod.Directory);
            IndexDetectedEmotes(mod.Directory, mod.Name);
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
        if (sync.IsInRoom) _ = sync.SetCatalogAsync(GetCatalogFingerprints());
    }

    public IReadOnlyList<ModOptionGroup> GetOptionGroups(string directory) =>
        optionGroups.TryGetValue(directory, out var groups) ? groups : [];

    public (int Matches, int Members) GetModMatch(string directory)
    {
        var members = sync.Room?.Members.Count ?? 0;
        if (!modCatalogKeys.TryGetValue(directory, out var fingerprint) ||
            !sync.MatchCounts.TryGetValue(fingerprint, out var matches)) return (0, members);
        return (matches, members);
    }

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
        if (selected && sync.IsInRoom && modSyncKeys.TryGetValue(directory, out var modKey))
            RunSync(sync.SetOptionSelectionAsync(modKey, group, option), $"Selected {option} for the room.");
    }

    public string? GetRemoteOptionSelector(string directory, string group, string option)
    {
        if (!modSyncKeys.TryGetValue(directory, out var modKey)) return null;
        return remoteOptionSelections.Values.FirstOrDefault(value =>
            value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase) &&
            value.Group.Equals(group, StringComparison.OrdinalIgnoreCase) &&
            value.Option.Equals(option, StringComparison.OrdinalIgnoreCase))?.MemberName;
    }

    public string? GetRemoteGroupSelector(string directory, string group)
    {
        if (!modSyncKeys.TryGetValue(directory, out var modKey)) return null;
        return remoteOptionSelections.Values.FirstOrDefault(value =>
            value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase) &&
            value.Group.Equals(group, StringComparison.OrdinalIgnoreCase))?.MemberName;
    }

    public string? GetRemoteModSelector(string directory)
    {
        if (!modSyncKeys.TryGetValue(directory, out var modKey)) return null;
        return remoteOptionSelections.Values.FirstOrDefault(value =>
                   value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase))?.MemberName ??
               activeAnimationSuggestions.Values.FirstOrDefault(value =>
                   value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase))?.SuggestedBy;
    }

    public string GetOptionNote(string directory, string group, string option) =>
        configuration.OptionNotes.TryGetValue(OptionNoteKey(directory, group, option), out var note) ? note : "";

    public void SaveOptionNote(string directory, string group, string option, string note)
    {
        var key = OptionNoteKey(directory, group, option);
        if (string.IsNullOrWhiteSpace(note)) configuration.OptionNotes.Remove(key);
        else configuration.OptionNotes[key] = note.Trim();
        configuration.Save(PluginInterface);
    }

    public bool IsModPrivate(string directory) => configuration.PrivateMods.Contains(directory);

    public void SetModPrivate(string directory, bool isPrivate)
    {
        if (isPrivate) configuration.PrivateMods.Add(directory);
        else configuration.PrivateMods.Remove(directory);
        configuration.Save(PluginInterface);
        if (sync.IsInRoom) _ = sync.SetCatalogAsync(GetCatalogFingerprints());
        Status = isPrivate
            ? "Mod marked private. It will not be advertised or sent in group play."
            : "Mod is available to group play again.";
    }

    public void ApplyOption(string directory, string name, string group, string option, bool selected)
    {
        var pose = selected ? GetOptionPose(directory, group, option) : null;
        var command = selected && pose is null ? DetectEmoteCommandFromLabel(option) : null;
        ActivateInternal(directory, name, pose, requestedCommand: command);
    }

    public void ActivateOption(
        string directory,
        string name,
        string group,
        string option,
        bool multiSelect)
    {
        SetOptionSelected(directory, group, option, true, multiSelect);
        var pose = GetOptionPose(directory, group, option);
        ActivateInternal(directory, name, pose, requestedCommand: pose is null ? DetectEmoteCommandFromLabel(option) : null);
    }

    public void ActivateOptionSolo(
        string directory,
        string name,
        string group,
        string option,
        bool multiSelect)
    {
        SetOptionSelected(directory, group, option, true, multiSelect);
        CancelGroupReadinessForSolo();
        var pose = GetOptionPose(directory, group, option);
        ActivateInternal(directory, name, pose, false, pose is null ? DetectEmoteCommandFromLabel(option) : null);
    }

    public void ConnectSync()
    {
        if (!RequireCharacterName(out _)) return;
        RunSync(sync.ConnectAsync(RelayUrl), "Connected to animation relay.");
    }

    public void DisconnectSync() => RunSync(sync.DisconnectAsync(), "Disconnected from animation relay.");
    public void CreateSyncRoom()
    {
        if (!RequireCharacterName(out var characterName)) return;
        RunSync(sync.CreateRoomAsync(characterName, GetCatalogFingerprints()), "Created group-play room.");
    }

    public void JoinSyncRoom(string code)
    {
        if (!RequireCharacterName(out var characterName)) return;
        RunSync(sync.JoinRoomAsync(code, characterName, GetCatalogFingerprints()), "Joined group-play room.");
    }

    private static string? CurrentCharacterName()
    {
        var name = Objects.LocalPlayer?.Name.TextValue.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private bool RequireCharacterName(out string characterName)
    {
        characterName = CurrentCharacterName() ?? "";
        if (characterName.Length > 0) return true;
        Status = "Your character must be logged in before connecting to group play.";
        return false;
    }

    public bool TryTakeTransferOffer(out ModTransferOfferDto offer) => transferOffers.TryDequeue(out offer!);

    public void SendMod(string directory, string name)
    {
        if (IsModPrivate(directory))
        {
            Status = $"{name} is private and cannot be sent.";
            return;
        }
        if (!sync.IsInRoom)
        {
            Status = "Join a room before sending a mod.";
            return;
        }
        var root = penumbra.GetModRoot();
        var source = root is null ? null : Path.Combine(root, directory);
        if (source is null || !Directory.Exists(source))
        {
            Status = $"Could not find {name} on disk.";
            return;
        }

        Status = $"Packaging {name} for the room...";
        _ = Task.Run(() =>
        {
            var package = Path.Combine(Path.GetTempPath(), $"EmoteLink-{Guid.NewGuid():N}.pmp");
            try
            {
                ZipFile.CreateFromDirectory(source, package, CompressionLevel.Optimal, false);
                var size = new FileInfo(package).Length;
                if (size > 75L * 1024 * 1024)
                    throw new InvalidDataException($"The packaged mod is {size / 1024f / 1024f:F1} MB; the limit is 75 MB.");
                using var packageInput = File.OpenRead(package);
                var hash = Convert.ToHexString(SHA256.HashData(packageInput));
                Status = $"Uploading {name} ({size / 1024f / 1024f:F1} MB)...";
                sync.SendModAsync(name, package, size, hash).GetAwaiter().GetResult();
                Status = $"Sent {name} to the room.";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not send animation mod {ModName}.", name);
                Status = $"Could not send {name}: {ex.GetBaseException().Message}";
            }
            finally
            {
                try { File.Delete(package); } catch { }
            }
        });
    }

    public void AcceptModTransfer(ModTransferOfferDto offer)
    {
        Status = $"Downloading {offer.ModName} from {offer.SenderName}...";
        _ = Task.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"EmoteLink-{Guid.NewGuid():N}.pmp");
            try
            {
                sync.DownloadModAsync(offer, path).GetAwaiter().GetResult();
                completedDownloads.Enqueue((offer, path, null));
            }
            catch (Exception ex)
            {
                try { File.Delete(path); } catch { }
                completedDownloads.Enqueue((offer, path, ex));
            }
        });
    }

    public void DeclineModTransfer(ModTransferOfferDto offer)
    {
        RunSync(sync.DeclineModTransferAsync(offer.TransferId), $"Declined {offer.ModName} from {offer.SenderName}.");
    }

    public void LeaveSyncRoom() => RunSync(sync.LeaveRoomAsync(), "Left group-play room.");
    public void CancelSyncReady()
    {
        preparedModKey = null;
        preparedCommand = null;
        preparedPose = null;
        RunSync(sync.CancelReadyAsync(), "Group-play readiness cancelled.");
    }

    public void ForceSyncStart() =>
        RunSync(sync.ForceStartAsync(), "Forced the prepared animation to start for matching room members.");

    public void RemoveSyncMember(RoomMemberDto member) =>
        RunSync(sync.RemoveMemberAsync(member.ConnectionId), $"Removed {member.DisplayName} from the room.");

    private void CancelGroupReadinessForSolo()
    {
        preparedModKey = null;
        preparedCommand = null;
        preparedPose = null;
        if (!sync.IsInRoom) return;
        _ = sync.CancelReadyAsync().ContinueWith(task =>
        {
            if (task.Exception is not null)
                Log.Warning(task.Exception.GetBaseException(), "Could not cancel readiness before solo playback.");
        }, TaskScheduler.Default);
    }

    public void NotifyRoomCodeCopied(string roomCode)
    {
        Status = $"Copied room code {roomCode}.";
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        var roomCode = sync.Room?.RoomCode;
        if (roomCode is null || args.Target is not MenuTargetDefault target ||
            string.IsNullOrWhiteSpace(target.TargetName)) return;
        if (target.TargetContentId == 0 &&
            target.TargetObject is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter) return;

        var targetName = target.TargetName;
        var worldName = "";
        try
        {
            if (target.TargetHomeWorld.RowId != 0)
                worldName = DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()
                    .GetRow(target.TargetHomeWorld.RowId).Name.ExtractText();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not resolve invite target's home world.");
        }
        args.AddMenuItem(new MenuItem
        {
            Name = "Invite to EmoteLink",
            PrefixChar = 'E',
            OnClicked = _ => SendRoomInvite(targetName, worldName, roomCode)
        });
    }

    private void SendRoomInvite(string targetName, string worldName, string roomCode)
    {
        var cleanName = Regex.Replace(targetName, @"[^\p{L}'\- ]", "").Trim();
        var cleanWorld = Regex.Replace(worldName, @"[^\p{L}\d\-]", "");
        if (cleanName.Length == 0) return;
        var recipient = cleanWorld.Length == 0 ? cleanName : $"{cleanName}@{cleanWorld}";
        ExecuteCommand($"/tell {recipient} EmoteLink room code: {roomCode}");
        Status = $"Invited {cleanName} to room {roomCode}.";
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var match = Regex.Match(chatMessage.Message.TextValue, @"^EmoteLink room code:\s*([A-Za-z0-9]{4,8})\s*$", RegexOptions.IgnoreCase);
        if (!match.Success) return;
        var code = match.Groups[1].Value.ToUpperInvariant();
        var commandId = 0xE1000000u | (uint)(code.GetHashCode(StringComparison.OrdinalIgnoreCase) & 0x00FFFFFF);
        if (!inviteLinks.TryGetValue(commandId, out var link))
        {
            link = Chat.AddChatLinkHandler(commandId, (_, _) =>
            {
                if (!sync.IsConnected)
                {
                    Status = $"Connect to Group Play, then click the room invite again to join {code}.";
                    mainWindow.IsOpen = true;
                    return;
                }
                JoinSyncRoom(code);
                mainWindow.IsOpen = true;
            });
            inviteLinks[commandId] = link;
        }
        chatMessage.Message = new SeStringBuilder()
            .AddText("EmoteLink invitation: ")
            .Add(link)
            .AddText($"Join room {code}")
            .Add(RawPayload.LinkTerminator)
            .Build();
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
        return optionPoses.TryGetValue(OptionPoseKey(directory, group, option), out var pose) ? pose : null;
    }

    public IReadOnlyList<PoseTarget> GetDetectedPoses(string directory) =>
        modPoses.TryGetValue(directory, out var poses) ? poses : [];

    public IReadOnlyList<EmoteTarget> GetDetectedEmotes(string directory) =>
        modEmotes.TryGetValue(directory, out var emotes) ? emotes : [];

    public void ActivateDetectedPose(string directory, string name, PoseTarget pose) =>
        ActivateInternal(directory, name, pose);

    public void ActivateDetectedPoseSolo(string directory, string name, PoseTarget pose)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, pose, false);
    }

    public void ActivateDetectedEmote(string directory, string name, EmoteTarget emote) =>
        ActivateInternal(directory, name, null, requestedCommand: emote.Command);

    public void ActivateDetectedEmoteSolo(string directory, string name, EmoteTarget emote)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, null, false, emote.Command);
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
        return order.Where(byDirectory.ContainsKey)
            .Select(directory => byDirectory[directory])
            // LINQ's stable ordering preserves the user's manual order inside each
            // match tier while promoting purple, green, then orange, within every folder.
            .OrderBy(mod => GetMatchSortTier(mod.Directory))
            .ToList();
    }

    private int GetMatchSortTier(string directory)
    {
        if (GetRemoteModSelector(directory) is not null) return 0; // Purple: suggested.
        var (matches, members) = GetModMatch(directory);
        if (members > 1 && matches >= members) return 1; // Green: everyone has it.
        if (members > 1 && matches > 1) return 2;        // Orange: some members have it.
        return 3;                                        // White: no shared match.
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

    public void ActivateSolo(string directory, string name)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, null, false);
    }

    private void ActivateInternal(
        string directory,
        string name,
        PoseTarget? requestedPose,
        bool allowGroupPlay = true,
        string? requestedCommand = null)
    {
        if (requestedPose is null && modPoses.TryGetValue(directory, out var detected) && detected.Count == 1)
            requestedPose = detected[0];
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
            if (allowGroupPlay && PrepareForGroupPlay(directory, name, null, requestedPose)) return;
            SchedulePose(name, requestedPose, 300);
            return;
        }

        var command = requestedCommand ?? DetectEmoteCommand(directory, name);
        if (command is null)
        {
            Status = $"Activated {name}, but no emote command was detected.";
            Chat.PrintError($"[EmoteLink] {name} was activated, but its emote could not be detected.");
            return;
        }

        if (allowGroupPlay && PrepareForGroupPlay(directory, name, command, null)) return;
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

    private IReadOnlyList<string> GetCatalogFingerprints() => modCatalogKeys
        .Where(pair => !IsModPrivate(pair.Key))
        .Select(pair => pair.Value)
        .Distinct()
        .ToList();

    private static string CatalogFingerprint(string modSyncKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modSyncKey)));

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
        var modPapPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(modPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (file.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                CollectPapGamePaths(root, modPapPaths);
                if (file.EndsWith("default_mod.json", StringComparison.OrdinalIgnoreCase)) continue;
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
        modPoses[directory] = DetectPoseTargets(modPapPaths);
    }

    private static PoseTarget? DetectPoseTarget(IReadOnlyList<string> paths, string optionName)
        => DetectPoseTargets(paths, optionName).FirstOrDefault();

    private static IReadOnlyList<PoseTarget> DetectPoseTargets(IEnumerable<string> paths, string optionName = "")
    {
        var poses = new List<PoseTarget>();
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
                {
                    var pose = new PoseTarget(candidate.Kind, index);
                    if (!poses.Contains(pose)) poses.Add(pose);
                    break;
                }
            }
            if (path.Contains("/resident/idle.pap") && !poses.Contains(new PoseTarget(PoseKind.Idle, 0)))
                poses.Add(new PoseTarget(PoseKind.Idle, 0));

            PoseKind? kind = path.Contains("/jmn/") ? PoseKind.GroundSit
                : path.Contains("/sit/") ? PoseKind.Sit
                : path.Contains("/doze/") ? PoseKind.Doze
                : null;
            if (kind is not null)
            {
                var labelIndex = Regex.Match(optionName, @"(\d+)(?!.*\d)");
                var pose = new PoseTarget(kind.Value,
                    labelIndex.Success && byte.TryParse(labelIndex.Value, out var index)
                        ? PoseService.ClampIndex(index)
                        : (byte)0);
                if (!poses.Contains(pose)) poses.Add(pose);
            }
        }
        return poses
            .OrderBy(pose => pose.Kind)
            .ThenBy(pose => pose.Index)
            .ToList();
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
            AddEmoteName(row.RowId, name, command);
        }
        Log.Information("Loaded {Count} emote names for automatic playback.", emoteCommandsByName.Count);
    }

    private void AddEmoteName(uint id, string name, string command)
    {
        var normalized = NormalizeEmoteName(name);
        if (normalized.Length == 0) return;
        var target = new EmoteTarget(id, name, command);
        emoteCommandsByName.TryAdd(normalized, command);
        emoteCommandsByName.TryAdd(normalized.Replace("-", ""), command);
        emoteTargetsByName.TryAdd(normalized, target);
        emoteTargetsByName.TryAdd(normalized.Replace("-", ""), target);
    }

    private void IndexDetectedEmotes(string directory, string name)
    {
        modEmotes[directory] = penumbra.GetChangedItemNames(directory, name)
            .Select(FindEmoteTarget)
            .Where(target => target is not null)
            .Select(target => target!)
            .DistinctBy(target => target.Id)
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private EmoteTarget? FindEmoteTarget(string changedItem)
    {
        var item = changedItem.Trim();
        if ((item.Contains('/') || item.Contains('\\')) && item.Contains('.')) return null;
        var colon = item.IndexOf(':');
        if (colon >= 0) item = item[(colon + 1)..];
        var normalized = NormalizeEmoteName(item);
        if (emoteTargetsByName.TryGetValue(normalized, out var direct)) return direct;
        if (emoteTargetsByName.TryGetValue(normalized.Replace("-", ""), out direct)) return direct;
        var partial = emoteTargetsByName
            .Where(pair => pair.Key.Length > 3 && normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();
        return partial.Value;
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

    private string? DetectEmoteCommandFromLabel(string option)
    {
        var target = FindEmoteTarget(option);
        if (target is not null) return target.Command;
        var normalized = NormalizeEmoteName(option);
        if (emoteCommandsByName.TryGetValue(normalized, out var direct)) return direct;
        if (emoteCommandsByName.TryGetValue(normalized.Replace("-", ""), out direct)) return direct;
        var partial = emoteCommandsByName
            .Where(pair => pair.Key.Length > 3 &&
                (normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                 pair.Key.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(partial.Value) ? null : partial.Value;
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
        if (IsAligning)
        {
            movement.Cancel();
            alignmentFramesRemaining = 0;
            alignmentStableFrames = 0;
            Status = "Alignment cancelled.";
            return;
        }
        var target = Targets.Target ?? Targets.SoftTarget;
        var player = (Character*)(Objects.LocalPlayer?.Address ?? 0);
        if (target is null || player is null || player->Mode != CharacterModes.Normal) return;
        if (System.Numerics.Vector3.Distance(Objects.LocalPlayer!.Position, target.Position) > MaxAlignDistance) return;

        var position = target.Position;
        var rotation = target.Rotation;
        var targetAddress = target.Address;
        Status = "Aligning position and facing direction...";
        movement.WalkTo(position, () =>
        {
            alignmentTargetAddress = targetAddress;
            alignmentFramesRemaining = 12;
            alignmentStableFrames = 0;
            ApplyAlignment(position, rotation);
        });
    }

    private void OnUpdate(IFramework _)
    {
        UpdateAlignment();
        ProcessCompletedDownloads();
        ProcessSyncPlaySignals();
        var initializedPendingMod = false;
        if (pendingPose is not null && Environment.TickCount64 >= pendingCommandTime)
        {
            var pose = pendingPose;
            pendingPose = null;
            ExecutePose(pose);
            initializedPendingMod = true;
        }
        if (pendingCommand is not null && Environment.TickCount64 >= pendingCommandTime)
        {
            var command = pendingCommand;
            pendingCommand = null;
            ExecuteCommand(command);
            initializedPendingMod = true;
        }
        if (initializedPendingMod && pendingSelectionModKey is not null)
        {
            ClearRemoteSelections(pendingSelectionModKey);
            pendingSelectionModKey = null;
            lobbyEmoteRefreshTime = Environment.TickCount64 + LobbyEmoteRefreshDelayMs;
        }
        if (lobbyEmoteRefreshTime > 0 && Environment.TickCount64 >= lobbyEmoteRefreshTime)
        {
            lobbyEmoteRefreshTime = 0;
            RefreshLobbyEmotes();
        }
        UpdatePoseCycling();
        if (!waitingForAnimation) return;
        UpdateMovementCleanup();
    }

    private void ProcessCompletedDownloads()
    {
        while (completedDownloads.TryDequeue(out var result))
        {
            if (result.Error is not null)
            {
                Status = $"Could not download {result.Offer.ModName}: {result.Error.GetBaseException().Message}";
                Log.Warning(result.Error, "Transferred mod download failed.");
                continue;
            }
            var installation = penumbra.InstallMod(result.Path);
            if (!installation.Success)
            {
                Status = $"Downloaded {result.Offer.ModName}, but installation failed: {installation.Error}.";
                continue;
            }
            RunSync(sync.CompleteModTransferAsync(result.Offer.TransferId),
                $"Downloaded {result.Offer.ModName}; Penumbra is installing it.");
        }
    }

    private void RememberOptionSelection(OptionSelectionDto selection)
    {
        remoteOptionSelections[selection.MemberName + "\n" + selection.ModKey + "\n" + selection.Group] = selection;
        QueueAnimationSuggestion(selection.MemberName, selection.ModKey);
    }

    private void QueueAnimationSuggestion(string memberName, string modKey)
    {
        var suggestionKey = SuggestionKey(memberName, modKey);
        var mod = Mods.FirstOrDefault(candidate => modSyncKeys.TryGetValue(candidate.Directory, out var key) &&
            key.Equals(modKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(mod.Directory)) return;
        var suggestion = new AnimationSuggestion(memberName, modKey, mod.Directory, mod.Name);
        activeAnimationSuggestions[suggestionKey] = suggestion;
        Log.Information("Marked animation suggestion from {MemberName}: {ModName}.", memberName, mod.Name);
    }

    private void OnAnimationSuggestionDeclined(AnimationSuggestionDeclinedDto decline)
    {
        if (decline.SuggestedBy.Equals(CurrentCharacterName(), StringComparison.OrdinalIgnoreCase))
            Status = $"{decline.DeclinedBy} declined your animation suggestion.";
    }

    private static string SuggestionKey(string memberName, string modKey) => memberName + "\n" + modKey;

    private void OnSyncStateChanged()
    {
        var roomCode = sync.Room?.RoomCode;
        if (roomCode is null)
        {
            remoteSelectionRoom = null;
            remoteOptionSelections.Clear();
            activeAnimationSuggestions.Clear();
            return;
        }
        var roomChanged = !roomCode.Equals(remoteSelectionRoom, StringComparison.OrdinalIgnoreCase);
        if (roomChanged)
        {
            remoteSelectionRoom = roomCode;
            remoteOptionSelections.Clear();
            activeAnimationSuggestions.Clear();
        }
        var currentMembers = sync.Room!.Members.Select(member => member.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in remoteOptionSelections.Where(pair => !currentMembers.Contains(pair.Value.MemberName)))
            remoteOptionSelections.TryRemove(pair.Key, out _);
        foreach (var pair in activeAnimationSuggestions.Where(pair => !currentMembers.Contains(pair.Value.SuggestedBy)))
            activeAnimationSuggestions.TryRemove(pair.Key, out _);
        foreach (var member in sync.Room.Members.Where(member =>
                     !sync.IsCurrentMember(member.ConnectionId) && member.Ready && !string.IsNullOrWhiteSpace(member.ModKey)))
            QueueAnimationSuggestion(member.DisplayName, member.ModKey);
        if (!roomChanged) return;
        _ = sync.GetOptionSelectionsAsync().ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            foreach (var selection in task.Result) RememberOptionSelection(selection);
        }, TaskScheduler.Default);
    }

    private static string OptionNoteKey(string directory, string group, string option) =>
        directory + "\n" + group + "\n" + option;

    private void UpdateAlignment()
    {
        if (alignmentFramesRemaining <= 0) return;

        var target = Objects.FirstOrDefault(gameObject => gameObject.Address == alignmentTargetAddress);
        if (target is null || Objects.LocalPlayer is null)
        {
            alignmentFramesRemaining = 0;
            alignmentStableFrames = 0;
            Status = "Alignment stopped because the target was lost.";
            return;
        }

        var positionMatched = System.Numerics.Vector3.Distance(Objects.LocalPlayer.Position, target.Position) <= 0.01f;
        var rotationDelta = MathF.Abs(MathF.IEEERemainder(Objects.LocalPlayer.Rotation - target.Rotation, MathF.Tau));
        alignmentStableFrames = positionMatched && rotationDelta <= 0.01f ? alignmentStableFrames + 1 : 0;

        ApplyAlignment(target.Position, target.Rotation);
        alignmentFramesRemaining--;
        if (alignmentStableFrames < 3 && alignmentFramesRemaining > 0) return;

        alignmentFramesRemaining = 0;
        Status = "Aligned with target: position and facing direction match.";
    }

    private static void ApplyAlignment(System.Numerics.Vector3 position, float rotation)
    {
        var player = (Character*)(Objects.LocalPlayer?.Address ?? 0);
        if (player is null) return;
        player->GameObject.SetPosition(position.X, position.Y, position.Z);
        player->GameObject.SetRotation(rotation);
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
            pendingSelectionModKey = signal.ModKey;
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

    private void ClearRemoteSelections(string modKey)
    {
        foreach (var pair in remoteOptionSelections.Where(pair =>
                     pair.Value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase)))
            remoteOptionSelections.TryRemove(pair.Key, out _);
        foreach (var pair in activeAnimationSuggestions.Where(pair =>
                     pair.Value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase)))
            activeAnimationSuggestions.TryRemove(pair.Key, out _);
    }

    private void RefreshLobbyEmotes()
    {
        var room = sync.Room;
        var localPlayer = Objects.LocalPlayer;
        if (room is null || localPlayer is null) return;

        var lobbyNames = room.Members.Select(member => member.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refreshed = 0;
        foreach (var actor in Objects.OfType<IPlayerCharacter>())
        {
            // The local player is necessarily a lobby member. Remote actors are included
            // only when their character name is one of the room's displayed identities.
            if (actor.Address != localPlayer.Address && !lobbyNames.Contains(actor.Name.TextValue)) continue;
            if (RefreshActorEmote(actor.Address)) refreshed++;
        }
        Log.Debug("Refreshed synchronized emote time for {ActorCount} visible lobby actors.", refreshed);
    }

    private static bool RefreshActorEmote(nint address)
    {
        var character = (Character*)address;
        if (character is null || character->DrawObject is null ||
            character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return false;
        if (character->Mode is not (CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)) return false;
        var characterBase = (CharacterBase*)character->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return false;
        var skeleton = ((Human*)character->DrawObject)->Skeleton;
        if (skeleton is null || skeleton->PartialSkeletonCount < 1) return false;
        var animatedSkeleton = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animatedSkeleton is null || animatedSkeleton->AnimationControls.Length < 1) return false;
        var control = animatedSkeleton->AnimationControls[0].Value;
        if (control is null) return false;
        control->hkaAnimationControl.LocalTime = 0f;
        return true;
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
            // Match Encore's active-pose path: redraw to apply the new mod files, but do
            // not write SelectedPoses first. Writing it also changes CPoseState, which
            // would make the cycling code believe the actor is already on that variant.
            ExecuteCommand("/penumbra redraw self");
            BeginPoseCycling(pose, 150);
            return;
        }

        // Match Encore's initial-pose path. Select the Sit/GroundSit/Doze slot before
        // entering the state and do not redraw afterward; a redraw between these two
        // operations can reset/reapply actor state and cause the game to enter a
        // different pose variant than the PAP slot replaced by the mod.
        poses.SetIndex(pose);
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
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        Chat.ChatMessage -= OnChatMessage;
        Chat.RemoveChatLinkHandler();
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        Commands.RemoveHandler(Command);
        movement.Dispose();
        sync.DisposeAsync().AsTask().GetAwaiter().GetResult();
        windows.RemoveAllWindows();
    }
}

public sealed record AnimationSuggestion(string SuggestedBy, string ModKey, string Directory, string ModName);
public sealed record EmoteTarget(uint Id, string Name, string Command);

using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Chat;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using NoireLib;
using NoireLib.Hooking;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Diagnostics;

namespace EmoteLink;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string PrimaryCommand = "/syn";
    private const string FallbackCommand = "/synastry";
    private const float MaxAlignDistance = 2f;
    private const float MaxMidEmoteAlignDistance = 0.5f;
    private const int LobbyEmoteRefreshDelayMs = 6000;
    private const double RefreshFrameBudgetMilliseconds = 4;
    private static readonly TimeSpan StaleTransferPackageAge = TimeSpan.FromHours(24);
    private const string PublicRelayUrl = "https://emotelink.aethercast.org";

    private sealed record PendingPenumbraInstall(ModTransferOfferDto Offer, string Path, string ReceiveFolder);
    private sealed record EmoteTimelineInfo(int Slot, uint RowId, string Key, bool IsPersistentLoop);
    private sealed record EmotePlaybackInfo(
        uint EmoteId,
        string Command,
        IReadOnlyList<EmoteTimelineInfo> Timelines);
    private sealed record PendingDirectPlayback(nint ActorAddress, EmotePlayback Playback, string ModName);
    private sealed record CarrierPlayback(uint EmoteId, string Command, string ModName);
    private sealed record PendingCarrierPlayback(
        CarrierPlayback Playback,
        long NextAttempt,
        long Deadline);
    private static readonly HashSet<string> GroundLoopCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/playdead",
        "/pushups",
        "/situps",
        "/slump",
    };
    private static readonly HashSet<string> PropLoopCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/sweep",
        "/shakedrink",
        "/bouquet",
        "/tomescroll",
        "/study",
        "/gridaniangulp",
        "/uldahngulp",
        "/lominsangulp",
        "/savortea",
        "/pen",
        "/carrybook",
        "/conduct",
        "/devourtaco",
    };
    private sealed record ActiveDirectPlayback(nint ActorAddress, ushort OriginalBaseOverride);
    private sealed record PendingRemotePlayback(
        nint ActorAddress,
        EmotePlayback Playback,
        TemporaryAssignment Assignment,
        string ModName,
        long StartAt);
    private sealed record ActiveRemotePlayback(
        nint ActorAddress,
        ushort OriginalBaseOverride,
        TemporaryAssignment Assignment,
        System.Numerics.Vector3 StartPosition,
        bool IsLoop,
        long CleanupAt);

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static ITargetManager Targets { get; set; } = null!;
    [PluginService] private static IGameInteropProvider Interop { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static ISigScanner SigScanner { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IUnlockState UnlockState { get; set; } = null!;

    private readonly Configuration configuration;
    private readonly AnimationIndexCache animationIndexCache;
    private readonly PenumbraService penumbra;
    private readonly InPlaceEmoteConverter inPlaceEmoteConverter;
    private readonly VanillaEmoteRedirectService vanillaEmoteRedirect;
    private readonly MovementService movement;
    private readonly ContactAlignService contactAlign;
    private readonly AnimationPreloader preloader;
    private bool simpleHeelsLoaded;
    private long simpleHeelsCheckedAt;
    private readonly PoseService poses;
    private readonly AnywherePoseService? anywherePoses;
    private readonly AnimationSpeedService? animationSpeedController;
    private readonly AnimationSyncService sync;
    private readonly WindowSystem windows = new("Synastry");
    private readonly MainWindow mainWindow;
    private readonly MiniPlayerWindow miniPlayerWindow;
    private readonly Ik.ContactIkService? contactIk;
    private readonly Ik.ContactMapResolver contactMaps;
    private readonly TestPartner.TestPartnerService testPartner;
    // Body profiles shared by room partners, and ones measured here for partners who haven't run
    // setup, by character name.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Ik.BodyProfile> sharedBodies =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Ik.BodyProfile> measuredBodies =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> measuringBodies = new(StringComparer.OrdinalIgnoreCase);
    private Ik.BodyProfile? ownBody;
    private string ownBodyName = "";
    private bool bodySyncPending;
    private bool bodySetupRunning;

    /// <summary>The animation this player last started, until it's cleared (moving or another start).</summary>
    public NowPlayingInfo? NowPlaying { get; private set; }
    private readonly SettingsWindow settingsWindow;
    private readonly CustomCommandsWindow customCommandsWindow;
    private readonly TypedEmoteChooserWindow typedEmoteChooserWindow;
    private readonly NoireHook<AgentEmote.Delegates.ExecuteEmote>? agentExecuteEmoteHook;
    private readonly HashSet<string> registeredCustomCommands = new(StringComparer.OrdinalIgnoreCase);
    private bool waitingForAnimation;
    private long activationTime;
    private readonly Dictionary<string, string> emoteCommandsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmoteTarget> emoteTargetsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmotePlaybackInfo> emotePlaybackByCommand =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, EmotePlaybackInfo> emotePlaybackById = [];
    private readonly Dictionary<string, IReadOnlyList<ModOptionGroup>> optionGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private string? pendingCommand;
    private PendingDirectPlayback? pendingDirectPlayback;
    private PendingCarrierPlayback? pendingCarrierPlayback;
    private long pendingCommandTime;
    private PoseTarget? pendingPose;
    private string? pendingSelectionModKey;
    private long lobbyEmoteRefreshTime;
    private readonly Dictionary<string, PoseTarget> optionPoses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<PoseTarget>> modPoses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<EmoteTarget>> modEmotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> optionDefaultsChecked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modSyncKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modCatalogKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> convertedMods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Directory, string Name)> modsByDirectory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<(string Directory, string Name)>> organizedModsCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const string UncategorizedCacheKey = "\0";
    private int libraryOrderRevision;
    private int cachedLibraryOrderRevision = -1;
    private PoseTarget? cyclingPose;
    private long nextPoseCycleTime;
    private int poseCycleAttempts;
    private long movementTrackingStart;
    private System.Numerics.Vector3 movementSample;
    private bool hasMovementSample;
    private int movementFrames;
    private readonly ConcurrentQueue<PlaySignalDto> syncPlaySignals = new();
    private readonly ConcurrentQueue<LocalAnimationSignalDto> localAnimationSignals = new();
    private readonly ConcurrentQueue<uint> typedEmoteRequests = new();
    private readonly ConcurrentDictionary<uint, byte> queuedTypedEmoteRequests = new();
    private readonly List<PendingRemotePlayback> pendingRemotePlaybacks = [];
    private readonly List<ActiveRemotePlayback> activeRemotePlaybacks = [];
    private readonly HashSet<TemporaryAssignment> remoteAssignments = [];
    private string? preparedModKey;
    private string? preparedCatalogFingerprint;
    private string? preparedCommand;
    private EmotePlayback? preparedDirectPlayback;
    private CarrierPlayback? preparedCarrierPlayback;
    private PoseTarget? preparedPose;
    private ActiveDirectPlayback? activeDirectPlayback;
    private nint alignmentTargetAddress;
    private int alignmentFramesRemaining;
    private int alignmentStableFrames;
    private float? animationSpeedOverride;
    private System.Numerics.Vector3? animationSpeedPosition;
    private nint animationSpeedMatchTargetAddress;
    private string animationSpeedMatchTargetName = "";
    private readonly ConcurrentQueue<ModTransferOfferDto> incomingTransferOffers = new();
    private readonly ConcurrentQueue<ModTransferOfferDto> transferOffers = new();
    private readonly ConcurrentQueue<RoomInvite> roomInvites = new();
    private readonly ConcurrentQueue<(ModTransferOfferDto Offer, string Path, Exception? Error)> completedDownloads = new();
    private readonly List<PendingPenumbraInstall> pendingPenumbraInstalls = [];
    private readonly ConcurrentQueue<string> addedModDirectories = new();
    private readonly ConcurrentDictionary<string, byte> queuedAddedModDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OptionSelectionDto> remoteOptionSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<RoleLabelDto> receivedRoleLabels = new();
    private readonly ConcurrentQueue<CommunityRoleLabelDto> receivedCommunityRoleLabels = new();
    private readonly ConcurrentDictionary<string, AnimationSuggestion> activeAnimationSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> ignoredAnimationSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<AnimationSuggestion> incomingAnimationSuggestions = new();
    private readonly ConcurrentQueue<FreeUseDirectiveDto> freeUseDirectives = new();
    private readonly ConcurrentDictionary<string, string> remoteReadyModKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? remoteSelectionRoom;
    private bool roleSyncPending;
    private bool communityRoleSyncPending;
    private bool communityRelayConnected;
    private string localPresenceScope = "";
    private bool localPresenceUpdatePending;
    private long nextLocalPresenceAttempt;
    private readonly ConcurrentQueue<ModRefreshResult> modRefreshResults = new();
    private readonly SemaphoreSlim modScanFramePermit = new(0, 1);
    private Queue<((string Directory, string Name) Mod, CachedAnimationMod Cached)>? provisionalCachedMods;
    private CancellationTokenSource? modRefreshCancellation;
    private Task? modRefreshWorker;
    private volatile bool refreshFastMode;
    private bool refreshPriorityMode;
    private int refreshGeneration;
    private bool refreshWorkerCompleted;
    private IReadOnlyList<(string Directory, string Name)>? refreshAllMods;
    private int refreshTotalMods;
    private int refreshProcessedMods;
    private int refreshCachedMods;
    private int refreshScannedMods;
    private HashSet<string>? refreshCurrentDirectories;

    public IReadOnlyList<(string Directory, string Name)> Mods { get; private set; } = [];
    public IReadOnlyList<ModCategory> Categories => configuration.Categories;
    public bool PenumbraAvailable => penumbra.IsAvailable;
    public bool SimpleHeelsAvailable
    {
        get
        {
            try
            {
                return PluginInterface.InstalledPlugins.Any(plugin =>
                    plugin.IsLoaded && plugin.InternalName.Equals("SimpleHeels", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
    public bool IsAligning => movement.IsWalking || alignmentFramesRemaining > 0;
    public bool AutomaticEmoteSyncEnabled => configuration.AutomaticEmoteSync;
    public bool SitDozeAnywhereEnabled => configuration.SitDozeAnywhere;
    public bool SitDozeAnywhereAvailable => anywherePoses is not null;
    public bool DozeAnywhereOnlyEnabled => configuration.DozeAnywhereOnly;
    public bool IsRefreshingMods => modRefreshCancellation is not null;
    public string ReceivedModFolder => (configuration.ReceivedModFolder ?? "").Replace('\\', '/').Trim('/');
    public string Status { get; private set; } = "Ready.";
    public AnimationSyncService Sync => sync;
    public string SyncDisplayName => CurrentCharacterName() ?? "Unavailable";

    public Plugin()
    {
        NoireLibMain.Initialize(PluginInterface, this);
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var upgradedConfiguration = configuration.Version < 9;
        if (upgradedConfiguration) configuration.Version = 9;
        configuration.CustomAnimationCommands ??= [];
        configuration.TypedEmoteDefaults ??= [];
        animationIndexCache = AnimationIndexCache.Load(
            Path.Combine(PluginInterface.ConfigDirectory.FullName, "animation-index.json"), Log);
        _ = Task.Run(SweepStaleTransferPackages);
        var generatedReporterIdentity = false;
        if (!IsReporterId(configuration.CommunityReporterId))
        {
            configuration.CommunityReporterId = Guid.NewGuid().ToString("N");
            generatedReporterIdentity = true;
        }
        // 1.0.86 makes line-up and bone bending experimental: off for everyone once, then the
        // player's choice.
        if (!configuration.ExperimentalFeaturesReset)
        {
            configuration.AutomaticLineUp = false;
            configuration.BendShaft = false;
            configuration.BendOpenings = false;
            configuration.BendHands = false;
            configuration.ExperimentalFeaturesReset = true;
            upgradedConfiguration = true;
        }
        if (generatedReporterIdentity || upgradedConfiguration) configuration.Save(PluginInterface);
        penumbra = new PenumbraService(PluginInterface, Log);
        inPlaceEmoteConverter = new InPlaceEmoteConverter(
            DataManager,
            Log,
            PluginInterface.ConfigDirectory.FullName);
        vanillaEmoteRedirect = new VanillaEmoteRedirectService(DataManager, Log);
        penumbra.ModAdded += OnPenumbraModAdded;
        movement = new MovementService(Interop, Objects);
        poses = new PoseService(Objects);
        try
        {
            anywherePoses = new AnywherePoseService(Interop, Objects);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Anywhere pose hooks could not be initialized; normal-pose fallbacks remain available.");
        }
        try
        {
            animationSpeedController = new AnimationSpeedService(
                Interop, Objects, DataManager, GetAnimationSpeedHookOverride);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Animation-speed hook could not be initialized.");
        }
        sync = new AnimationSyncService();
        try
        {
            contactIk = new Ik.ContactIkService(SigScanner, Interop, Log, BodyProfileOf);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Contact IK could not start; couple animations line up the old way.");
        }
        testPartner = new TestPartner.TestPartnerService(PluginInterface, Objects, penumbra, DataManager, Log);
        testPartner.Released += name =>
        {
            LeaveTestPartnerRoom();
            measuredBodies.TryRemove(name, out _);
            measuringBodies.Remove(name);
        };
        contactMaps = new Ik.ContactMapResolver(DataManager, penumbra, hashes => sync.GetContactMapsAsync(hashes), Log);
        sync.BodyProfileChanged += shared =>
        {
            if (Ik.BodyProfile.FromJson(shared.ProfileJson) is { } profile) sharedBodies[shared.DisplayName] = profile;
            else sharedBodies.TryRemove(shared.DisplayName, out _);
        };
        // A test partner has no Synastry of its own, so your side always does the lining up.
        contactAlign = new ContactAlignService(Objects, Targets, Log,
            name => IsRoomMemberNamed(name) && !(testPartner.Active && name.Equals(testPartner.Name, StringComparison.OrdinalIgnoreCase)),
            ExecuteCommand,
            () => configuration.LineUpPreference, TestPartnerCharacter, MappedOpeningOf);
        preloader = new AnimationPreloader(PluginInterface, Objects, penumbra, sync, Log);
        sync.PlayReceived += signal => syncPlaySignals.Enqueue(signal);
        sync.LocalAnimationReceived += signal => localAnimationSignals.Enqueue(signal);
        sync.ModTransferOffered += offer => incomingTransferOffers.Enqueue(offer);
        sync.OptionSelectionChanged += RememberOptionSelection;
        sync.RoleLabelChanged += label => receivedRoleLabels.Enqueue(label);
        sync.CommunityRoleLabelChanged += label => receivedCommunityRoleLabels.Enqueue(label);
        sync.AnimationSuggestionDeclined += OnAnimationSuggestionDeclined;
        sync.FreeUseDirected += directive => freeUseDirectives.Enqueue(directive);
        sync.StateChanged += OnSyncStateChanged;
        sync.Diagnostic += (message, exception) =>
        {
            if (exception is null) Log.Information("{Message}", message);
            else Log.Warning(exception, "{Message}", message);
        };
        Theme.Initialize(PluginInterface.UiBuilder);
        mainWindow = new MainWindow(this);
        miniPlayerWindow = new MiniPlayerWindow(this);
        settingsWindow = new SettingsWindow(this);
        customCommandsWindow = new CustomCommandsWindow(this);
        typedEmoteChooserWindow = new TypedEmoteChooserWindow(this);
        BuildEmoteLookup();
        try
        {
            agentExecuteEmoteHook = new NoireHook<AgentEmote.Delegates.ExecuteEmote>(
                DetourAgentExecuteEmote,
                true,
                "Synastry typed emote interception");
            Log.Information(
                "Typed locked-emote interception initialized (enabled: {Enabled}).",
                agentExecuteEmoteHook.IsEnabled);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Typed locked-emote interception could not be initialized.");
        }
        windows.AddWindow(mainWindow);
        windows.AddWindow(miniPlayerWindow);
        windows.AddWindow(settingsWindow);
        windows.AddWindow(customCommandsWindow);
        windows.AddWindow(typedEmoteChooserWindow);

        if (!configuration.HasSeenHowTo)
        {
            configuration.HasSeenHowTo = true;
            configuration.Save(PluginInterface);
            mainWindow.StartTutorial();
        }

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        Framework.Update += OnUpdate;
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Chat.ChatMessage += OnChatMessage;
        Commands.AddHandler(PrimaryCommand, new CommandInfo(HandleCommand)
        {
            HelpMessage = "Open Synastry, start the guide with /syn tutorial, join with /syn join ROOMCODE, or select a localhost dev relay with /syn relay URL."
        });
        Commands.AddHandler(FallbackCommand, new CommandInfo(HandleCommand)
        {
            HelpMessage = "Fallback command for Synastry. The shorter /syn command is also available."
        });
        RegisterConfiguredAnimationCommands();

        // Recover from an unload/crash that left our tracked overrides behind.
        ClearTemporaryAssignments();
        RefreshMods();
    }

    private void HandleCommand(string _, string arguments)
    {
        if (Regex.IsMatch(arguments, @"^\s*tutorial\s*$", RegexOptions.IgnoreCase))
        {
            OpenHowTo();
            return;
        }
        var match = Regex.Match(arguments, @"^\s*join\s+([A-Za-z0-9]{4,8})\s*$", RegexOptions.IgnoreCase);
        if (match.Success) JoinSyncRoom(match.Groups[1].Value);
        else if (Regex.IsMatch(arguments, @"^\s*relay\s+(?:default|reset)\s*$", RegexOptions.IgnoreCase))
            SetLocalRelayOverride("");
        else if (Regex.Match(arguments, @"^\s*relay\s+(\S+)\s*$", RegexOptions.IgnoreCase) is
                 { Success: true } relayMatch)
            SetLocalRelayOverride(relayMatch.Groups[1].Value);
        else ToggleWindow();
    }

    private void DetourAgentExecuteEmote(
        AgentEmote* agent,
        ushort emoteId,
        EmoteController.PlayEmoteOption* playEmoteOption,
        bool addToHistory,
        bool liveUpdateHistory)
    {
        var hook = agentExecuteEmoteHook;
        if (hook is null) return;
        try
        {
            if (Objects.LocalPlayer is not null &&
                emotePlaybackById.ContainsKey(emoteId) &&
                !IsEmoteUnlocked(emoteId))
            {
                QueueTypedEmoteRequest(emoteId);
                return;
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not inspect emote {EmoteId}; allowing the game to handle it normally.", emoteId);
        }

        hook.Original(agent, emoteId, playEmoteOption, addToHistory, liveUpdateHistory);
    }

    private void ProcessTypedEmoteRequest()
    {
        if (!typedEmoteRequests.TryDequeue(out var emoteId)) return;
        queuedTypedEmoteRequests.TryRemove(emoteId, out _);
        if (!emotePlaybackById.TryGetValue(emoteId, out var info)) return;

        // The unlock state can change between the hook and the next framework update.
        // If it did, send the command back through the normal game path.
        if (IsEmoteUnlocked(emoteId))
        {
            ExecuteCommand(info.Command);
            return;
        }

        ActivateVanillaEmoteRedirect(info);
    }

    private void QueueTypedEmoteRequest(uint emoteId)
    {
        if (queuedTypedEmoteRequests.TryAdd(emoteId, 0))
            typedEmoteRequests.Enqueue(emoteId);
    }

    private List<TypedEmoteCandidate> GetTypedEmoteCandidates(uint emoteId)
    {
        var candidates = new List<TypedEmoteCandidate>();
        foreach (var mod in Mods)
        {
            if (!modEmotes.TryGetValue(mod.Directory, out var emotes)) continue;
            var emote = emotes.FirstOrDefault(candidate => candidate.Id == emoteId);
            if (emote is not null)
                candidates.Add(new TypedEmoteCandidate(mod.Directory, mod.Name, emote));
        }
        return candidates;
    }

    public void ActivateTypedEmote(TypedEmoteCandidate candidate)
    {
        if (!modsByDirectory.TryGetValue(candidate.Directory, out var mod) ||
            !modEmotes.TryGetValue(candidate.Directory, out var emotes) ||
            emotes.FirstOrDefault(emote => emote.Id == candidate.Emote.Id) is not { } currentEmote)
        {
            ReportPlaybackFailure(candidate.ModName, "This animation is no longer in the current Synastry index.");
            return;
        }

        RememberTypedEmoteDefault(currentEmote.Id, mod.Directory);
        PublishDetectedTriggerSelection(mod.Directory, $"emote:{currentEmote.Id}");
        ActivateInternal(mod.Directory, mod.Name, null, requestedCommand: currentEmote.Command);
    }

    public void IgnoreTypedEmote(string command)
    {
        Status = $"Ignored locked emote {command}.";
    }

    private void RememberTypedEmoteDefault(uint emoteId, string directory)
    {
        if (configuration.TypedEmoteDefaults.TryGetValue(emoteId, out var current) &&
            current.Equals(directory, StringComparison.OrdinalIgnoreCase))
            return;
        configuration.TypedEmoteDefaults[emoteId] = directory;
        configuration.Save(PluginInterface);
    }

    public void RefreshMods()
    {
        if (modRefreshCancellation is not null)
        {
            Status = "An animation-library refresh is already in progress.";
            return;
        }

        if (!penumbra.IsAvailable)
        {
            Status = Mods.Count == 0
                ? "Penumbra is unavailable."
                : $"Penumbra is unavailable; keeping the last valid library of {Mods.Count} animation mod(s).";
            return;
        }

        var root = penumbra.GetModRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Status = Mods.Count == 0
                ? "Penumbra's mod directory could not be read."
                : $"Penumbra's mod directory could not be read; keeping {Mods.Count} known animation mod(s).";
            return;
        }

        var allMods = penumbra.GetMods()
            .Where(mod => !vanillaEmoteRedirect.IsManagedMod(root, mod.Directory))
            .ToList();
        convertedMods.Clear();
        foreach (var mod in allMods)
            if (inPlaceEmoteConverter.IsConverted(root, mod.Directory))
                convertedMods.Add(mod.Directory);
        var currentDirectories = allMods.Select(mod => mod.Directory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in modsByDirectory.Keys.Where(directory => !currentDirectories.Contains(directory)).ToList())
            RemoveModIndex(directory);
        Mods = Mods.Where(mod => currentDirectories.Contains(mod.Directory)).ToList();
        RebuildModDirectoryLookup();

        refreshAllMods = allMods;
        refreshTotalMods = allMods.Count;
        refreshProcessedMods = 0;
        refreshCachedMods = 0;
        refreshScannedMods = 0;
        refreshCurrentDirectories = currentDirectories;
        refreshWorkerCompleted = false;
        while (modRefreshResults.TryDequeue(out _)) { }
        provisionalCachedMods = new Queue<((string Directory, string Name), CachedAnimationMod)>();
        var work = new List<ModRefreshWorkItem>(allMods.Count);
        var alreadyVisible = Mods.Select(mod => mod.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in allMods)
        {
            animationIndexCache.TryGetLastKnown(mod.Directory, out var cached);
            if (cached is { IsAnimationMod: true } && !alreadyVisible.Contains(mod.Directory))
                provisionalCachedMods.Enqueue((mod, cached));
            work.Add(new ModRefreshWorkItem(mod, Path.Combine(root, mod.Directory), cached));
        }

        var generation = ++refreshGeneration;
        modRefreshCancellation = new CancellationTokenSource();
        while (modScanFramePermit.Wait(0)) { }
        refreshPriorityMode = false;
        refreshFastMode = mainWindow.IsOpen;
        Status = $"Refreshing animation library: 0 of {allMods.Count} mods checked...";
        var worker = new AnimationCatalogRefreshWorker(
            WaitForModScanSlotAsync,
            modRefreshResults.Enqueue);
        modRefreshWorker = Task.Run(
            () => worker.RunAsync(generation, work, modRefreshCancellation.Token),
            modRefreshCancellation.Token);
        if (allMods.Count == 0)
            modRefreshResults.Enqueue(new ModRefreshResult(generation, ModRefreshResultKind.Completed, default));
    }

    private void ProcessModRefresh()
    {
        if (modRefreshCancellation is null) return;
        refreshFastMode = refreshPriorityMode || mainWindow.IsOpen;
        // The worker consumes at most one permit for each recursive mod scan. Keeping the
        // semaphore bounded at one prevents permits from accumulating while disk I/O is busy.
        if (modScanFramePermit.CurrentCount == 0)
        {
            try { modScanFramePermit.Release(); }
            catch (SemaphoreFullException) { }
        }
        var frameStart = Stopwatch.GetTimestamp();
        while (provisionalCachedMods?.TryDequeue(out var provisional) == true)
        {
            try
            {
                if (!modsByDirectory.ContainsKey(provisional.Mod.Directory))
                {
                    RemoveModIndex(provisional.Mod.Directory);
                    if (RestoreCachedAnimationMod(provisional.Mod, provisional.Cached))
                        AddOrUpdateAnimationMod(provisional.Mod);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not provisionally restore cached animation mod {ModDirectory}.",
                    provisional.Mod.Directory);
            }
            if (Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds >= RefreshFrameBudgetMilliseconds) return;
        }

        while (modRefreshResults.TryDequeue(out var result))
        {
            if (result.Generation != refreshGeneration) continue;
            if (result.Kind == ModRefreshResultKind.Completed)
            {
                refreshWorkerCompleted = true;
                if (result.Error.Length > 0)
                    Log.Warning("Animation refresh worker stopped early: {Error}", result.Error);
                continue;
            }
            ApplyModRefreshResult(result);
            refreshProcessedMods++;
            if (result.CacheHit) refreshCachedMods++;
            else refreshScannedMods++;
            Status = $"Refreshing animation library: {refreshProcessedMods} of {refreshTotalMods} mods checked...";

            // Refresh application is now in-memory only. Emote enrichment is lazy when a row
            // is expanded, so large libraries are governed by elapsed work instead of a fixed
            // one-result-per-frame minimum.
            if (Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds >= RefreshFrameBudgetMilliseconds) break;
        }

        if (refreshWorkerCompleted && modRefreshResults.IsEmpty && provisionalCachedMods?.Count == 0)
            FinishModRefresh();
    }

    private Task WaitForModScanSlotAsync(CancellationToken cancellationToken)
    {
        if (!refreshFastMode)
            return Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

        return modScanFramePermit.WaitAsync(cancellationToken);
    }

    private void FinishModRefresh()
    {
        var current = refreshCurrentDirectories ?? [];
        foreach (var directory in modsByDirectory.Keys.Where(directory => !current.Contains(directory)).ToList())
            RemoveModIndex(directory);
        Mods = (refreshAllMods ?? [])
            .Where(mod => modsByDirectory.ContainsKey(mod.Directory))
            .ToList();
        RebuildModDirectoryLookup();
        animationIndexCache.RemoveExcept(refreshCurrentDirectories ?? []);
        _ = animationIndexCache.SaveInBackgroundAsync();
        modRefreshCancellation?.Dispose();
        modRefreshCancellation = null;
        modRefreshWorker = null;
        provisionalCachedMods = null;
        refreshAllMods = null;
        refreshPriorityMode = false;
        refreshCurrentDirectories = null;
        refreshTotalMods = 0;
        refreshProcessedMods = 0;
        Status = $"Loaded {Mods.Count} animation mod(s): {refreshCachedMods} cached, " +
                 $"{refreshScannedMods} validated in the background.";
        refreshCachedMods = 0;
        refreshScannedMods = 0;
        NormalizeOrganization();
        if (sync.IsInRoom) _ = sync.SetCatalogAsync(GetCatalogFingerprints());
    }

    private void ApplyModRefreshResult(ModRefreshResult result)
    {
        if (result.Kind == ModRefreshResultKind.Failed)
        {
            Log.Warning("Could not validate animation mod {ModDirectory}: {Error}. Keeping its last valid state.",
                result.Mod.Directory, result.Error);
            return;
        }

        RemoveModIndex(result.Mod.Directory);
        if (result.Kind == ModRefreshResultKind.Cached && result.Cached is not null)
        {
            if (RestoreCachedAnimationMod(result.Mod, result.Cached))
            {
                AddOrUpdateAnimationMod(result.Mod);
                CompletePendingPenumbraInstall(result.Mod.Name, modCatalogKeys[result.Mod.Directory]);
            }
            else
                RemoveAnimationMod(result.Mod.Directory);
            return;
        }

        if (result.Kind == ModRefreshResultKind.PortableAnimation && result.Payload is not null)
        {
            ApplyPortableAnimationMod(result.Mod, result.Payload);
            var cached = CaptureAnimationMod(result.Mod, result.SourceStamp, true);
            cached.ManifestSignature = result.Signature;
            cached.SignatureAlgorithm = AnimationManifestScanner.SignatureAlgorithm;
            cached.ManifestFileCount = result.ManifestFileCount;
            cached.ManifestBytes = result.ManifestBytes;
            cached.PortablePayloadJson = result.PortablePayloadJson;
            animationIndexCache.Set(cached);
            AddOrUpdateAnimationMod(result.Mod);
            CompletePendingPenumbraInstall(result.Mod.Name, modCatalogKeys[result.Mod.Directory]);
            return;
        }

        var negative = new CachedAnimationMod
        {
            Directory = result.Mod.Directory,
            SourceStamp = result.SourceStamp,
            ManifestSignature = result.Signature,
            SignatureAlgorithm = AnimationManifestScanner.SignatureAlgorithm,
            ManifestFileCount = result.ManifestFileCount,
            ManifestBytes = result.ManifestBytes,
            IsAnimationMod = false
        };
        animationIndexCache.Set(negative);
        RemoveAnimationMod(result.Mod.Directory);
    }

    private void ApplyPortableAnimationMod(
        (string Directory, string Name) mod,
        PortableAnimationIndexPayload payload)
    {
        var computedSyncKey = BuildModSyncKey(mod.Name, payload.PapGamePaths);
        var identity = ResolveCatalogIdentity(mod.Directory, computedSyncKey);
        modSyncKeys[mod.Directory] = identity.SyncKey;
        modCatalogKeys[mod.Directory] = identity.Fingerprint;
        foreach (var optionPose in payload.OptionPoses)
            optionPoses[OptionPoseKey(mod.Directory, optionPose.Group, optionPose.Option)] =
                new PoseTarget(optionPose.Kind, optionPose.Index);
        modPoses[mod.Directory] = payload.Poses;
        var groups = payload.OptionGroups
            .Select(group => new ModOptionGroup(group.Name, group.Options, group.IsMultiSelect))
            .ToList();
        optionGroups[mod.Directory] = groups;
        NormalizeSelections(mod.Directory, groups);
    }

    private CachedAnimationMod CaptureAnimationMod(
        (string Directory, string Name) mod,
        string sourceStamp,
        bool isAnimationMod)
    {
        var cached = new CachedAnimationMod
        {
            Directory = mod.Directory,
            SourceStamp = sourceStamp,
            IsAnimationMod = isAnimationMod
        };
        if (!isAnimationMod) return cached;

        cached.SyncKey = modSyncKeys[mod.Directory];
        cached.OptionGroups = optionGroups.GetValueOrDefault(mod.Directory, [])
            .Select(group => new CachedOptionGroup
            {
                Name = group.Name,
                Options = group.Options.ToList(),
                IsMultiSelect = group.IsMultiSelect
            })
            .ToList();
        var prefix = mod.Directory + "\u001f";
        cached.OptionPoses = optionPoses
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair =>
            {
                var parts = pair.Key[prefix.Length..].Split('\u001f', 2);
                return new CachedOptionPose
                {
                    Group = parts[0],
                    Option = parts[1],
                    Kind = pair.Value.Kind,
                    Index = pair.Value.Index
                };
            })
            .ToList();
        cached.Poses = modPoses.GetValueOrDefault(mod.Directory, []).ToList();
        cached.EmotesIndexed = modEmotes.ContainsKey(mod.Directory);
        cached.Emotes = modEmotes.GetValueOrDefault(mod.Directory, []).ToList();
        return cached;
    }

    private bool RestoreCachedAnimationMod(
        (string Directory, string Name) mod,
        CachedAnimationMod cached)
    {
        if (!cached.IsAnimationMod) return false;

        var identity = ResolveCatalogIdentity(mod.Directory, cached.SyncKey);
        modSyncKeys[mod.Directory] = identity.SyncKey;
        modCatalogKeys[mod.Directory] = identity.Fingerprint;
        foreach (var optionPose in cached.OptionPoses)
            optionPoses[OptionPoseKey(mod.Directory, optionPose.Group, optionPose.Option)] =
                new PoseTarget(optionPose.Kind, optionPose.Index);
        modPoses[mod.Directory] = cached.Poses;
        if (cached.EmotesIndexed || cached.Emotes.Count > 0)
            modEmotes[mod.Directory] = cached.Emotes;
        var groups = cached.OptionGroups
            .Select(group => new ModOptionGroup(group.Name, group.Options, group.IsMultiSelect))
            .ToList();
        optionGroups[mod.Directory] = groups;
        NormalizeSelections(mod.Directory, groups);
        return true;
    }

    private void AddOrUpdateAnimationMod((string Directory, string Name) mod)
    {
        var existing = Mods.ToList();
        var index = existing.FindIndex(candidate =>
            candidate.Directory.Equals(mod.Directory, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) existing[index] = mod;
        else existing.Add(mod);
        Mods = existing;
        modsByDirectory[mod.Directory] = mod;
        InvalidateLibraryOrder();
    }

    private void RemoveAnimationMod(string directory)
    {
        if (!modsByDirectory.Remove(directory)) return;
        Mods = Mods.Where(mod => !mod.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)).ToList();
        InvalidateLibraryOrder();
    }

    private void OnPenumbraModAdded(string directory)
    {
        if (directory.Equals(VanillaEmoteRedirectService.ModDirectory, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(directory) || !queuedAddedModDirectories.TryAdd(directory, 0)) return;
        addedModDirectories.Enqueue(directory);
    }

    private void OrganizeReceivedMod((string Directory, string Name) mod)
    {
        var matches = pendingPenumbraInstalls.Where(pending =>
                pending.Offer.ModName.Equals(mod.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return;
        if (matches.Count > 1)
        {
            Log.Warning(
                "Could not choose a receive folder for {ModName}; {MatchCount} transfers with that name are pending.",
                mod.Name, matches.Count);
            return;
        }

        var moved = penumbra.MoveModToFolder(mod.Directory, mod.Name, matches[0].ReceiveFolder);
        var destination = matches[0].ReceiveFolder.Length == 0
            ? "the top level of Penumbra's mod list"
            : $"Penumbra folder {matches[0].ReceiveFolder}";
        if (!moved.Success)
        {
            Status = $"Installed {mod.Name}, but could not organize it in {destination}: {moved.Error}.";
            Log.Warning("Could not place received mod {ModName} in {ReceiveFolder}: {Error}",
                mod.Name, matches[0].ReceiveFolder, moved.Error);
            return;
        }

        Status = $"Installed {mod.Name} in {destination}.";
        Log.Information("Organized received mod {ModName} at Penumbra mod-list path {FullPath}.",
            mod.Name, moved.FullPath);
    }

    private void ProcessAddedMod()
    {
        if (modRefreshCancellation is not null || !addedModDirectories.TryDequeue(out var directory)) return;
        queuedAddedModDirectories.TryRemove(directory, out _);
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { directory };
        while (addedModDirectories.TryDequeue(out var additional))
        {
            requested.Add(additional);
            queuedAddedModDirectories.TryRemove(additional, out _);
        }

        try
        {
            var root = penumbra.GetModRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            var allMods = penumbra.GetMods().ToList();
            var targets = allMods.Where(mod => requested.Contains(mod.Directory)).ToList();
            if (targets.Count == 0)
            {
                Log.Warning("Penumbra reported added mod {ModDirectory}, but it was not present in the mod list.",
                    directory);
                return;
            }

            foreach (var mod in targets) OrganizeReceivedMod(mod);

            var currentDirectories = allMods.Select(mod => mod.Directory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in modsByDirectory.Keys.Where(item => !currentDirectories.Contains(item)).ToList())
                RemoveModIndex(stale);
            Mods = Mods.Where(mod => currentDirectories.Contains(mod.Directory)).ToList();
            RebuildModDirectoryLookup();

            refreshAllMods = allMods;
            refreshTotalMods = targets.Count;
            refreshProcessedMods = 0;
            refreshCachedMods = 0;
            refreshScannedMods = 0;
            refreshCurrentDirectories = currentDirectories;
            refreshWorkerCompleted = false;
            while (modRefreshResults.TryDequeue(out _)) { }
            provisionalCachedMods = new Queue<((string Directory, string Name), CachedAnimationMod)>();
            var work = new List<ModRefreshWorkItem>(targets.Count);
            foreach (var mod in targets)
            {
                animationIndexCache.TryGetLastKnown(mod.Directory, out var cached);
                work.Add(new ModRefreshWorkItem(mod, Path.Combine(root, mod.Directory), cached));
            }

            var generation = ++refreshGeneration;
            modRefreshCancellation = new CancellationTokenSource();
            while (modScanFramePermit.Wait(0)) { }
            refreshPriorityMode = true;
            refreshFastMode = true;
            Status = targets.Count == 1
                ? $"Validating newly installed mod {targets[0].Name}..."
                : $"Validating {targets.Count} newly installed mods...";
            var worker = new AnimationCatalogRefreshWorker(
                WaitForModScanSlotAsync,
                modRefreshResults.Enqueue);
            modRefreshWorker = Task.Run(
                () => worker.RunAsync(generation, work, modRefreshCancellation.Token),
                modRefreshCancellation.Token);
            Log.Information("Scheduled {Count} newly added Penumbra mod(s) for priority validation.", targets.Count);
        }
        catch (Exception ex)
        {
            Status = $"Could not validate the newly installed mod: {ex.GetBaseException().Message}";
            Log.Warning(ex, "Could not schedule newly installed Penumbra mods for validation.");
        }
    }

    private void RemoveModIndex(string directory)
    {
        var hasIndexedState = optionGroups.ContainsKey(directory) || modPoses.ContainsKey(directory) ||
            modEmotes.ContainsKey(directory) || modSyncKeys.ContainsKey(directory) ||
            modCatalogKeys.ContainsKey(directory);
        if (!hasIndexedState) return;

        if (optionGroups.TryGetValue(directory, out var groups))
        {
            foreach (var group in groups)
            {
                foreach (var option in group.Options)
                    optionPoses.Remove(OptionPoseKey(directory, group.Name, option));
            }
        }
        optionGroups.Remove(directory);
        optionDefaultsChecked.Remove(directory);
        modPoses.Remove(directory);
        modEmotes.Remove(directory);
        modSyncKeys.Remove(directory);
        modCatalogKeys.Remove(directory);
    }

    private void RebuildModDirectoryLookup()
    {
        modsByDirectory.Clear();
        foreach (var mod in Mods) modsByDirectory[mod.Directory] = mod;
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

    public void SetOptionSelected(
        string directory,
        string group,
        string option,
        bool selected,
        bool multiSelect,
        bool broadcastSelection = true)
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
        if (broadcastSelection && selected && sync.IsInRoom && modSyncKeys.TryGetValue(directory, out var modKey))
            RunSync(sync.SetOptionSelectionAsync(modKey, group, option), $"Selected {option} for the room.");
    }

    public string? GetRemoteOptionSelector(string directory, string group, string option)
    {
        if (!sync.IsInRoom || !modSyncKeys.TryGetValue(directory, out var modKey)) return null;
        foreach (var pair in remoteOptionSelections)
        {
            var value = pair.Value;
            if (value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase) &&
                value.Group.Equals(group, StringComparison.OrdinalIgnoreCase) &&
                value.Option.Equals(option, StringComparison.OrdinalIgnoreCase)) return value.MemberName;
        }
        return null;
    }

    public string? GetRemoteDetectedTriggerSelector(string directory, string group, string option)
    {
        var trigger = group.Equals("$detected-pose", StringComparison.OrdinalIgnoreCase)
            ? "pose:" + option
            : group.Equals("$detected-emote", StringComparison.OrdinalIgnoreCase)
                ? "emote:" + option
                : "";
        return trigger.Length == 0 ? null : GetRemoteOptionSelector(directory, "$detected-trigger", trigger);
    }

    public string? GetRemoteGroupSelector(string directory, string group)
    {
        if (!sync.IsInRoom || !modSyncKeys.TryGetValue(directory, out var modKey)) return null;
        foreach (var pair in remoteOptionSelections)
        {
            var value = pair.Value;
            if (value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase) &&
                value.Group.Equals(group, StringComparison.OrdinalIgnoreCase)) return value.MemberName;
        }
        return null;
    }

    public string? GetRemoteModSelector(string directory)
    {
        if (!sync.IsInRoom || !modSyncKeys.TryGetValue(directory, out var modKey)) return null;
        foreach (var pair in remoteOptionSelections)
            if (pair.Value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase)) return pair.Value.MemberName;
        foreach (var pair in activeAnimationSuggestions)
            if (pair.Value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase)) return pair.Value.SuggestedBy;
        return null;
    }

    public string GetOptionNote(string directory, string group, string option) =>
        configuration.OptionNotes.TryGetValue(OptionNoteKey(directory, group, option), out var note) ? note : "";

    public void SaveOptionNote(string directory, string group, string option, string note)
    {
        var key = OptionNoteKey(directory, group, option);
        var clean = note.Trim();
        configuration.CommunityRoleKeys.Remove(key);
        if (clean.Length == 0) configuration.OptionNotes.Remove(key);
        else configuration.OptionNotes[key] = clean;
        configuration.Save(PluginInterface);
        if (sync.IsInRoom && IsSynchronizedRoleGroup(group) && !IsModPrivate(directory) &&
            modSyncKeys.TryGetValue(directory, out var modKey))
            _ = sync.SetRoleLabelAsync(modKey, group, option, clean);
        if (sync.IsConnected && clean.Length > 0 && IsSynchronizedRoleGroup(group) && !IsModPrivate(directory) &&
            modCatalogKeys.TryGetValue(directory, out var fingerprint))
        {
            var metadata = GetCommunityRoleMetadata(directory, group, option);
            _ = sync.SubmitCommunityRoleLabelAsync(
                fingerprint, group, option, clean, configuration.CommunityReporterId,
                metadata.ModName, metadata.AnimationName);
        }
    }

    public void ReportBadRoleLabel(string directory, string group, string option, string correction)
    {
        SaveOptionNote(directory, group, option, correction);
        Status = "Your correction was applied locally and submitted to the community database.";
    }

    public bool IsModPrivate(string directory) => configuration.PrivateMods.Contains(directory);

    public bool IsModConverted(string directory) => convertedMods.Contains(directory);
    public int ConvertedAnimationCount => convertedMods.Count;

    public void RestoreConvertedMod(string directory, string name)
    {
        var result = inPlaceEmoteConverter.Restore(penumbra.GetModRoot(), directory, name);
        if (!result.Success)
        {
            ReportPlaybackFailure(name, result.Message);
            return;
        }
        convertedMods.Remove(directory);
        var reload = penumbra.Reload(directory, name);
        if (!reload.Success)
        {
            ReportPlaybackFailure(
                name,
                $"The original files were restored, but Penumbra could not reload the mod: {reload.Error}. " +
                "Use Rediscover Mods in Penumbra.");
            return;
        }
        Status = result.Message + " Refreshing the animation library...";
        RefreshMods();
    }

    public void RestoreAllConvertedAnimations()
    {
        var targets = convertedMods
            .Select(directory => modsByDirectory.TryGetValue(directory, out var mod)
                ? mod
                : (Directory: directory, Name: directory))
            .ToList();
        if (targets.Count == 0)
        {
            Status = "No converted animations need to be restored.";
            return;
        }

        var restored = 0;
        var failures = new List<string>();
        foreach (var mod in targets)
        {
            var result = inPlaceEmoteConverter.Restore(
                penumbra.GetModRoot(),
                mod.Directory,
                mod.Name);
            if (!result.Success)
            {
                failures.Add($"{mod.Name}: {result.Message}");
                continue;
            }

            convertedMods.Remove(mod.Directory);
            restored++;
            var reload = penumbra.Reload(mod.Directory, mod.Name);
            if (!reload.Success)
                failures.Add($"{mod.Name}: restored, but Penumbra reload failed ({reload.Error})");
        }

        if (restored > 0)
            RefreshMods();

        if (failures.Count == 0)
        {
            Status = $"Restored {restored:N0} converted animation(s) to their original files. Library refresh started.";
            Chat.Print($"[Synastry] {Status}");
            return;
        }

        Status =
            $"Restored {restored:N0} converted animation(s); {failures.Count:N0} could not be fully restored. " +
            "Check /xllog for details.";
        Chat.PrintError($"[Synastry] {Status}");
        foreach (var failure in failures)
            Log.Warning("Bulk animation restore issue: {Failure}", failure);
    }

    public void SetModPrivate(string directory, bool isPrivate)
    {
        if (isPrivate) configuration.PrivateMods.Add(directory);
        else configuration.PrivateMods.Remove(directory);
        configuration.Save(PluginInterface);
        if (sync.IsInRoom) _ = sync.SetCatalogAsync(GetCatalogFingerprints());
        InvalidateLibraryOrder();
        Status = isPrivate
            ? "Mod marked private. It will not be advertised or sent in group play."
            : "Mod is available to group play again.";
    }

    public void SetModsPrivate(IReadOnlyCollection<string> directories, bool isPrivate)
    {
        var changed = 0;
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var didChange = isPrivate
                ? configuration.PrivateMods.Add(directory)
                : configuration.PrivateMods.Remove(directory);
            if (didChange) changed++;
        }
        if (changed == 0)
        {
            Status = isPrivate ? "The selected mods are already private." : "The selected mods are already public.";
            return;
        }
        configuration.Save(PluginInterface);
        if (sync.IsInRoom) _ = sync.SetCatalogAsync(GetCatalogFingerprints());
        InvalidateLibraryOrder();
        Status = isPrivate
            ? $"Marked {changed} selected mod(s) private."
            : $"Marked {changed} selected mod(s) public.";
    }

    public void MarkAllModsPrivate()
    {
        var added = 0;
        foreach (var mod in Mods)
            if (configuration.PrivateMods.Add(mod.Directory)) added++;
        configuration.Save(PluginInterface);
        if (sync.IsInRoom) _ = sync.SetCatalogAsync(GetCatalogFingerprints());
        InvalidateLibraryOrder();
        Status = added == 0
            ? "All animation mods are already private."
            : $"Marked {added} animation mod(s) private. Unhide individual mods from their right-click menu.";
    }

    public void SetDozeAnywhereOnly(bool enabled)
    {
        configuration.DozeAnywhereOnly = enabled;
        configuration.Save(PluginInterface);
        Status = enabled
            ? "Doze anywhere only: doze plays in place; sitting uses normal game placement."
            : "Sit and doze anywhere: both play in place.";
    }

    public void SetSitDozeAnywhere(bool enabled)
    {
        if (enabled && anywherePoses is null)
        {
            Status = "Sit/doze anywhere is unavailable because its game hooks could not be initialized.";
            return;
        }
        configuration.SitDozeAnywhere = enabled;
        configuration.Save(PluginInterface);
        Status = enabled
            ? "Sit/doze anywhere enabled. Chair-sit and doze animations will play in place."
            : "Sit/doze anywhere disabled. Chair-sit and doze will use normal game placement.";
    }

    public bool AutomaticLineUpEnabled => configuration.AutomaticLineUp;
    public ContactPreference LineUpPreference => configuration.LineUpPreference;

    public void SetLineUpPreference(ContactPreference preference)
    {
        configuration.LineUpPreference = preference;
        configuration.Save(PluginInterface);
    }
    public string LineUpStatus => contactAlign.Status;
    public bool IsLiningUp => contactAlign.IsMeasuring;

    public void SetAutomaticLineUp(bool enabled)
    {
        configuration.AutomaticLineUp = enabled;
        configuration.Save(PluginInterface);
        Status = enabled
            ? "Automatic line-up enabled. Couple animations line up through Simple Heels when they start."
            : "Automatic line-up disabled. The Line up button still works.";
    }

    public void LineUpNow() => contactAlign.LineUpNow(IsSimpleHeelsLoadedCached());

    /// <summary>Whether this character name is someone else in the current room, and so runs Synastry.</summary>
    // ---- Bending bones and body setup --------------------------------------------------------

    public bool ContactIkAvailable => contactIk?.Available == true;
    public string ContactIkStatus => contactIk?.Status ?? "Bending bones couldn't start on this game version.";
    public bool BendShaftEnabled => configuration.BendShaft;
    public bool BendOpeningsEnabled => configuration.BendOpenings;
    public bool BendHandsEnabled => configuration.BendHands;
    public bool BodySetupRunning => bodySetupRunning;
    internal Ik.BodyProfile? OwnBodyProfile => OwnBody();

    public void SetBendOptions(bool shaft, bool openings, bool hands)
    {
        configuration.BendShaft = shaft;
        configuration.BendOpenings = openings;
        configuration.BendHands = hands;
        configuration.Save(PluginInterface);
    }

    private const string DefaultMeshName = "My body";
    private long nextMeshCheck;

    /// <summary>This character's saved meshes, in the order they were made.</summary>
    public IReadOnlyList<string> BodyMeshNames =>
        CurrentCharacterName() is { } name ? MeshesOf(name).Keys.ToList() : [];

    public string ActiveBodyMeshName =>
        CurrentCharacterName() is { } name && configuration.ActiveBodyMesh.TryGetValue(name, out var mesh) &&
        MeshesOf(name).ContainsKey(mesh)
            ? mesh
            : CurrentCharacterName() is { } other ? MeshesOf(other).Keys.FirstOrDefault() ?? "" : "";

    /// <summary>Switches to a saved mesh and shares it with the room.</summary>
    public void SelectBodyMesh(string mesh, bool automatic = false)
    {
        if (CurrentCharacterName() is not { } name || !MeshesOf(name).ContainsKey(mesh)) return;
        configuration.ActiveBodyMesh[name] = mesh;
        configuration.Save(PluginInterface);
        ownBody = null;
        if (sync.IsInRoom && OwnBody() is { } profile) _ = sync.SetBodyProfileAsync(profile.ToJson());
        Status = automatic ? $"Your body changed; using the {mesh} mesh." : $"Using the {mesh} mesh.";
    }

    /// <summary>Renames a saved mesh, keeping its place in the list and whether it's in use.</summary>
    public bool RenameBodyMesh(string mesh, string newName)
    {
        var clean = newName.Trim();
        clean = clean[..Math.Min(24, clean.Length)];
        if (clean.Length == 0 || CurrentCharacterName() is not { } name) return false;
        var meshes = MeshesOf(name);
        if (!meshes.ContainsKey(mesh)) return false;
        if (!clean.Equals(mesh, StringComparison.OrdinalIgnoreCase) && meshes.ContainsKey(clean))
        {
            Status = $"You already have a mesh called {clean}.";
            return false;
        }
        // Rebuild in order so the renamed mesh keeps its place.
        var renamed = meshes.ToList();
        meshes.Clear();
        foreach (var (key, json) in renamed)
            meshes[key.Equals(mesh, StringComparison.OrdinalIgnoreCase) ? clean : key] = json;
        if (configuration.ActiveBodyMesh.TryGetValue(name, out var active) && active.Equals(mesh, StringComparison.OrdinalIgnoreCase))
            configuration.ActiveBodyMesh[name] = clean;
        configuration.Save(PluginInterface);
        Status = $"Renamed {mesh} to {clean}.";
        return true;
    }

    public void DeleteBodyMesh(string mesh)
    {
        if (CurrentCharacterName() is not { } name || !MeshesOf(name).Remove(mesh)) return;
        if (configuration.ActiveBodyMesh.TryGetValue(name, out var active) && active.Equals(mesh, StringComparison.OrdinalIgnoreCase))
            configuration.ActiveBodyMesh.Remove(name);
        configuration.Save(PluginInterface);
        ownBody = null;
        if (sync.IsInRoom) _ = sync.SetBodyProfileAsync(OwnBody()?.ToJson() ?? "");
    }

    /// <summary>Measures this character's body from its loaded models into a saved mesh (the current
    /// one, or a new one when a name is given), uses it, and shares it with the room.</summary>
    public void SetUpBody(string? newMesh = null)
    {
        if (bodySetupRunning) return;
        if (Objects.LocalPlayer is not { } local || CurrentCharacterName() is not { } name)
        {
            Status = "Log in to a character before setting up your body.";
            return;
        }
        var mesh = (newMesh ?? "").Trim();
        if (mesh.Length == 0) mesh = ActiveBodyMeshName.Length > 0 ? ActiveBodyMeshName : DefaultMeshName;
        mesh = mesh[..Math.Min(24, mesh.Length)];
        var capture = ReadBody(local.Address, local.ObjectIndex);
        if (capture is null)
        {
            Status = "Couldn't read your body. Make sure Penumbra is running and your character is drawn.";
            return;
        }
        bodySetupRunning = true;
        Status = $"Measuring your {mesh} mesh...";
        var tuning = MeshesOf(name).TryGetValue(mesh, out var existing) ? Ik.BodyProfile.FromJson(existing)?.Tuning : null;
        _ = Task.Run(() => Ik.BodyMeasurer.Measure(capture, DataManager, Log)).ContinueWith(task =>
        {
            bodySetupRunning = false;
            if (!task.IsCompletedSuccessfully)
            {
                Status = "Body setup failed: " + task.Exception?.GetBaseException().Message;
                return;
            }
            var profile = task.Result;
            if (tuning is not null) profile.Tuning = tuning;
            Framework.RunOnFrameworkThread(() =>
            {
                MeshesOf(name)[mesh] = profile.ToJson();
                configuration.ActiveBodyMesh[name] = mesh;
                configuration.Save(PluginInterface);
                ownBody = profile;
                ownBodyName = name;
                if (sync.IsInRoom) _ = sync.SetBodyProfileAsync(profile.ToJson());
                Status = $"{mesh} mesh set up: {DescribeBody(profile)}.";
            });
        }, TaskScheduler.Default);
    }

    public void SetBodyTuning(float contactGap, float gripStrength, float openingAmount)
    {
        if (OwnBody() is not { } profile || CurrentCharacterName() is not { } name) return;
        profile.Tuning.ContactGap = contactGap;
        profile.Tuning.GripStrength = gripStrength;
        profile.Tuning.OpeningAmount = openingAmount;
        MeshesOf(name)[ActiveBodyMeshName] = profile.ToJson();
        configuration.Save(PluginInterface);
    }

    /// <summary>Shares the finished tuning with the room; called when a slider is let go.</summary>
    public void ShareBodyProfile()
    {
        if (sync.IsInRoom && OwnBody() is { } profile) _ = sync.SetBodyProfileAsync(profile.ToJson());
    }

    /// <summary>A character's saved meshes. A single measurement from 1.0.80-1.0.81 becomes "My body".</summary>
    private Dictionary<string, string> MeshesOf(string character)
    {
        if (!configuration.BodyMeshes.TryGetValue(character, out var meshes))
            configuration.BodyMeshes[character] = meshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (meshes.Count == 0 && configuration.BodyProfiles.Remove(character, out var legacy))
        {
            meshes[DefaultMeshName] = legacy;
            configuration.ActiveBodyMesh[character] = DefaultMeshName;
            configuration.Save(PluginInterface);
        }
        return meshes;
    }

    /// <summary>Every few seconds, when the loaded models match a saved mesh exactly (the body, gear
    /// and everything else that was on when it was measured), switch to that mesh.</summary>
    private void RememberBodyMesh(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter local)
    {
        var now = Environment.TickCount64;
        if (now < nextMeshCheck || bodySetupRunning || CurrentCharacterName() is not { } name) return;
        nextMeshCheck = now + 5000;
        var meshes = MeshesOf(name);
        if (meshes.Count < 2) return;
        var models = penumbra.GetLoadedModels(local.ObjectIndex);
        if (models.Count == 0) return;
        var signature = Ik.BodyMeasurer.SignatureOf(models);
        var active = ActiveBodyMeshName;
        foreach (var (mesh, json) in meshes)
        {
            if (mesh.Equals(active, StringComparison.OrdinalIgnoreCase)) continue;
            if (Ik.BodyProfile.FromJson(json)?.BodySignature == signature)
            {
                SelectBodyMesh(mesh, true);
                return;
            }
        }
    }

    internal static string DescribeBody(Ik.BodyProfile profile)
    {
        var parts = new List<string>();
        if (profile.Shaft is not null) parts.Add("shaft");
        parts.AddRange(profile.Openings.Keys.OrderBy(key => key));
        if (profile.Hands.Count > 0) parts.Add("hands");
        return parts.Count == 0
            ? $"{profile.SurfaceRadius.Count} body surfaces"
            : $"{profile.SurfaceRadius.Count} body surfaces, {string.Join(", ", parts)}";
    }

    private Ik.BodyProfile? OwnBody()
    {
        var name = CurrentCharacterName();
        if (name is null) return null;
        if (ownBody is not null && ownBodyName.Equals(name, StringComparison.OrdinalIgnoreCase)) return ownBody;
        ownBodyName = name;
        var mesh = ActiveBodyMeshName;
        ownBody = mesh.Length > 0 && MeshesOf(name).TryGetValue(mesh, out var json) ? Ik.BodyProfile.FromJson(json) : null;
        return ownBody;
    }

    private Ik.BodyProfile? BodyProfileOf(string name)
    {
        if (name.Equals(ownBodyName, StringComparison.OrdinalIgnoreCase) && ownBody is not null) return ownBody;
        if (sharedBodies.TryGetValue(name, out var shared)) return shared;
        return measuredBodies.TryGetValue(name, out var measured) ? measured : null;
    }

    private unsafe Ik.BodyCapture? ReadBody(nint address, ushort objectIndex)
    {
        var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        if (gameObject is null || gameObject->DrawObject is null) return null;
        return Ik.BodyMeasurer.Capture((nint)gameObject->DrawObject, objectIndex, penumbra);
    }

    private void StartBodyProfileSync()
    {
        bodySyncPending = false;
        if (!sync.IsInRoom) return;
        if (OwnBody() is { } profile) _ = sync.SetBodyProfileAsync(profile.ToJson());
        _ = sync.GetBodyProfilesAsync().ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            foreach (var shared in task.Result)
                if (Ik.BodyProfile.FromJson(shared.ProfileJson) is { } parsed) sharedBodies[shared.DisplayName] = parsed;
        }, TaskScheduler.Default);
    }

    /// <summary>Hands the bone bender the characters to work on: you and the room partners near you
    /// (or your target when you aren't in a room), and measures a partner who hasn't shared a body.</summary>
    private unsafe void UpdateContactIk()
    {
        if (contactIk is null) return;
        var settings = new Ik.IkSettings(configuration.BendShaft, configuration.BendOpenings, configuration.BendHands,
            configuration.LineUpPreference);
        if (Objects.LocalPlayer is not { } local || (!settings.Shaft && !settings.Openings && !settings.Hands) ||
            !IsSynastryAnimationPlaying())
        {
            contactIk.Update([], settings);
            return;
        }
        OwnBody();
        RememberBodyMesh(local);
        var nearby = new List<Ik.IkActor> { WithMap(ToIkActor(local, true), local) };
        foreach (var player in Objects.OfType<Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter>())
        {
            if (player.Address == local.Address || System.Numerics.Vector3.Distance(player.Position, local.Position) > 3f) continue;
            var name = player.Name.TextValue;
            // Your room partners and your target; never a stranger nearby.
            var partner = IsRoomMemberNamed(name) || Targets.Target?.Address == player.Address;
            if (!partner) continue;
            nearby.Add(WithMap(ToIkActor(player, false), player));
            if (!sharedBodies.ContainsKey(name) && !measuredBodies.ContainsKey(name) && measuringBodies.Add(name))
                MeasurePartner(player, name);
        }
        // Your test partner (a dressed minion) counts as a partner too.
        if (testPartner.Active)
        {
            var (hash, map) = contactMaps.Resolve(testPartner.Address, testPartner.ObjectId, testPartner.ObjectIndex, false);
            nearby.Add(new Ik.IkActor(testPartner.Address, testPartner.ObjectId, testPartner.Name, false,
                testPartner.Playing, hash, map));
        }
        contactIk.Update(nearby, settings);
    }

    /// <summary>Adds the playing animation's file hash and contact map to a character.</summary>
    private Ik.IkActor WithMap(Ik.IkActor actor, Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        var (hash, map) = contactMaps.Resolve(player.Address, player.GameObjectId, player.ObjectIndex, actor.IsLocal);
        return actor with { Hash = hash, Map = map };
    }

    private static unsafe Ik.IkActor ToIkActor(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, bool isLocal)
    {
        var character = (Character*)player.Address;
        var looping = character is not null &&
                      character->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop;
        return new Ik.IkActor(player.Address, player.GameObjectId, player.Name.TextValue, isLocal, looping);
    }

    // ---- Test partner --------------------------------------------------------------------

    public bool TestPartnerActive => testPartner.Active;
    public string TestPartnerRoomStatus =>
        !testPartner.Active ? "" :
        partnerSync is { IsInRoom: true } ? $"In your room as {testPartner.Name}, Free Use on." :
        partnerJoining ? "Joining your room..." :
        sync.IsInRoom ? "Couldn't join your room; see /xllog." : "Join or create a room and it joins you in Free Use mode.";
    public bool TestPartnerMeasured => testPartner.Active && measuredBodies.ContainsKey(testPartner.Name);
    private long nextCollectionCheck;
    private bool hasTestPartnerCollection;

    /// <summary>Whether the player has made the "Synastry" collection (checked every few seconds).</summary>
    public bool HasTestPartnerCollection
    {
        get
        {
            if (Environment.TickCount64 >= nextCollectionCheck)
            {
                hasTestPartnerCollection = testPartner.HasCollection();
                nextCollectionCheck = Environment.TickCount64 + 3000;
            }
            return hasTestPartnerCollection;
        }
    }
    public bool TestPartnerLoading => testPartner.Loading;
    public bool TestPartnerPlaying => testPartner.Playing;
    public string TestPartnerStatus => testPartner.Status;
    public string TestPartnerDescription => testPartner.Active ? $"{testPartner.Name} as {testPartner.FileName}" : "";

    public void LoadTestPartner(string mcdfPath) => testPartner.Load(mcdfPath);
    public void ReleaseTestPartner()
    {
        LeaveTestPartnerRoom();
        testPartner.Release();
    }
    public void StopTestPartner() => testPartner.StopAnimation();

    /// <summary>The other roles of the animation you're playing, for the test partner to take.</summary>
    public IReadOnlyList<(string Label, Action Play)> TestPartnerRoles()
    {
        if (NowPlaying is not { } playing || !testPartner.Active) return [];
        var roles = new List<(string, Action)>();
        foreach (var pose in GetDetectedPoses(playing.Directory))
        {
            var label = PoseDisplayName(pose);
            var note = GetOptionNote(playing.Directory, "$detected-pose", $"{pose.Kind}:{pose.Index}");
            roles.Add((note.Length > 0 ? $"{note} ({label})" : label, () =>
            {
                partnerPick = (playing.Directory, $"pose:{pose.Kind}:{pose.Index}");
                PlayTestPartnerPose(playing, pose);
            }));
        }
        foreach (var emote in GetDetectedEmotes(playing.Directory))
        {
            var note = GetOptionNote(playing.Directory, "$detected-emote", emote.Id.ToString());
            roles.Add((note.Length > 0 ? $"{note} ({emote.Name})" : emote.Name, () =>
            {
                partnerPick = (playing.Directory, $"emote:{emote.Id}");
                PlayTestPartnerEmote(playing, emote);
            }));
        }
        return roles;
    }

    private void PlayTestPartnerPose(NowPlayingInfo playing, PoseTarget pose,
        IReadOnlyDictionary<string, List<string>>? selections = null)
    {
        var prefix = pose.Kind switch
        {
            PoseKind.GroundSit => "j_pose",
            PoseKind.Sit => "s_pose",
            PoseKind.Doze => "l_pose",
            _ => null
        };
        if (prefix is null)
        {
            Status = "The test partner can't play standing idle poses yet.";
            return;
        }
        var loop = TimelineByKey($"emote/{prefix}{pose.Index:D2}_loop");
        if (loop == 0)
        {
            Status = $"Couldn't find the game animation for {PoseDisplayName(pose)}.";
            return;
        }
        var start = TimelineByKey($"emote/{prefix}{pose.Index:D2}_start");
        testPartner.Play(playing.Directory, playing.ModName, selections ?? GetActivationSelections(playing.Directory),
            new EmotePlayback(0, loop, start, true));
    }

    private void PlayTestPartnerCommand(NowPlayingInfo playing, string command,
        IReadOnlyDictionary<string, List<string>> selections)
    {
        if (!TryCreatePlayback(command, out var playback))
        {
            Status = $"{command} has no animation the test partner can play.";
            return;
        }
        testPartner.Play(playing.Directory, playing.ModName, selections, playback);
    }

    private void PlayTestPartnerEmote(NowPlayingInfo playing, EmoteTarget emote)
    {
        if (!TryCreatePlayback(emote.Command, out var playback))
        {
            Status = $"{emote.Command} has no animation the test partner can play.";
            return;
        }
        testPartner.Play(playing.Directory, playing.ModName, GetActivationSelections(playing.Directory), playback);
    }

    private ushort TimelineByKey(string key)
    {
        var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
        if (sheet is null) return 0;
        foreach (var row in sheet)
            if (row.Key.ExtractText().Equals(key, StringComparison.OrdinalIgnoreCase))
                return row.RowId <= ushort.MaxValue ? (ushort)row.RowId : (ushort)0;
        return 0;
    }

    // ---- The test partner as a room member, in Free Use mode ---------------------------------

    private AnimationSyncService? partnerSync;
    private readonly System.Collections.Concurrent.ConcurrentQueue<FreeUseDirectiveDto> partnerDirectives = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<PlaySignalDto> partnerStarts = new();
    private bool partnerJoining;
    private string partnerRoom = "";
    private string partnerProfileShared = "";
    private Action? partnerReplay;
    private long partnerPlayAt;
    /// <summary>The role last picked for the test partner ("pose:Kind:Index" or "emote:Id") and its mod.</summary>
    private (string Directory, string Trigger)? partnerPick;

    /// <summary>
    /// When you ready an animation in the room, your test partner readies the other role (or the one
    /// you last picked for it in this mod) with your options, and starts with the room.
    /// </summary>
    private void FollowWithTestPartner(string directory, string ownTrigger)
    {
        if (partnerSync is not { IsInRoom: true } || !testPartner.Active ||
            !modCatalogKeys.TryGetValue(directory, out var fingerprint) ||
            !modSyncKeys.TryGetValue(directory, out var modKey) ||
            !modsByDirectory.TryGetValue(directory, out var mod)) return;
        var triggers = GetDetectedPoses(directory).Select(pose => $"pose:{pose.Kind}:{pose.Index}")
            .Concat(GetDetectedEmotes(directory).Select(emote => $"emote:{emote.Id}")).ToList();
        var trigger = partnerPick is { } pick && pick.Directory == directory && triggers.Contains(pick.Trigger) &&
                      !pick.Trigger.Equals(ownTrigger, StringComparison.OrdinalIgnoreCase)
            ? pick.Trigger
            : triggers.FirstOrDefault(candidate => !candidate.Equals(ownTrigger, StringComparison.OrdinalIgnoreCase)) ?? ownTrigger;
        var options = GetActivationSelections(directory).ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        partnerDirectives.Enqueue(new FreeUseDirectiveDto("you", fingerprint, modKey, mod.Name, trigger, options));
    }

    /// <summary>
    /// While a test partner exists and you're in a room, it joins that room through a second relay
    /// connection under its own name, with Free Use on, so every room feature reaches it like a second
    /// player: Free Use picks play on the minion, it readies, and room starts restart its animation.
    /// It shares its temporary measurements and leaves when released.
    /// </summary>
    private void UpdateTestPartnerRoom()
    {
        var room = sync.Room;
        var wanted = testPartner.Active && room is not null && sync.IsConnected;
        if (!wanted)
        {
            if (partnerSync is not null && !partnerJoining) LeaveTestPartnerRoom();
            return;
        }

        if (!partnerJoining && !partnerRoom.Equals(room!.RoomCode, StringComparison.OrdinalIgnoreCase))
        {
            partnerJoining = true;
            var code = room.RoomCode;
            var name = testPartner.Name;
            var fingerprints = GetCatalogFingerprints();
            var url = EffectiveRelayUrl();
            if (partnerSync is null)
            {
                var connection = new AnimationSyncService();
                connection.FreeUseDirected += directive => partnerDirectives.Enqueue(directive);
                connection.PlayReceived += signal => partnerStarts.Enqueue(signal);
                partnerSync = connection;
            }
            TestPartner.TestPartnerRoom.Join(partnerSync, url, code, name, fingerprints).ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                    Log.Information("Test partner {Name} joined room {Room} in Free Use mode.", name, code);
                else
                    Log.Warning(task.Exception?.GetBaseException(), "The test partner couldn't join the room.");
                partnerRoom = code;   // on failure too: releasing or a new room tries again
                partnerProfileShared = "";
                partnerJoining = false;
            }, TaskScheduler.Default);
            return;
        }

        if (partnerSync is not { IsInRoom: true } partner) return;

        // Its temporary measurements, shared like any member's.
        if (measuredBodies.TryGetValue(testPartner.Name, out var body))
        {
            var json = body.ToJson();
            if (json != partnerProfileShared)
            {
                partnerProfileShared = json;
                _ = partner.SetBodyProfileAsync(json);
            }
        }

        // Free Use picks play on the minion, and it readies for the room.
        while (partnerDirectives.TryDequeue(out var directive))
        {
            var directory = modCatalogKeys.FirstOrDefault(pair =>
                pair.Value.Equals(directive.Fingerprint, StringComparison.OrdinalIgnoreCase)).Key;
            if (string.IsNullOrWhiteSpace(directory) || !modsByDirectory.TryGetValue(directory, out var mod) ||
                !TryResolveFreeUseTrigger(directory, directive.Trigger, out var pose, out var command, out var animationName))
            {
                Status = $"{testPartner.Name} couldn't play what {directive.DirectedBy} chose.";
                continue;
            }
            var selections = GetActivationSelections(directory).ToDictionary(
                pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var (group, options) in directive.Options) selections[group] = options.ToList();
            var playing = new NowPlayingInfo(directory, mod.Name, animationName);
            Action play = pose is not null
                ? () => PlayTestPartnerPose(playing, pose, selections)
                : () => PlayTestPartnerCommand(playing, command!, selections);
            partnerReplay = play;
            if (directive.DirectedBy != "you") partnerPick = (directory, directive.Trigger);
            _ = partner.SetReadyAsync(directive.ModKey);
            Status = directive.DirectedBy == "you"
                ? $"{testPartner.Name} is ready as {animationName}."
                : $"{directive.DirectedBy} chose {animationName} for {testPartner.Name}.";
        }

        // The room started: play on the same countdown as everyone else.
        while (partnerStarts.TryDequeue(out var start))
        {
            var delay = start.DelayMilliseconds > 0
                ? start.DelayMilliseconds
                : Math.Max(0, start.StartUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            partnerPlayAt = Environment.TickCount64 + delay;
        }
        if (partnerPlayAt != 0 && Environment.TickCount64 >= partnerPlayAt)
        {
            partnerPlayAt = 0;
            partnerReplay?.Invoke();
        }
    }

    private void LeaveTestPartnerRoom()
    {
        var connection = partnerSync;
        partnerSync = null;
        partnerRoom = "";
        partnerReplay = null;
        partnerPlayAt = 0;
        partnerProfileShared = "";
        while (partnerDirectives.TryDequeue(out _)) { }
        while (partnerStarts.TryDequeue(out _)) { }
        if (connection is null) return;
        _ = TestPartner.TestPartnerRoom.Leave(connection);
    }

    /// <summary>The opening a character's playing animation is mapped to aim at, if it has a map.</summary>
    private string? MappedOpeningOf(Dalamud.Game.ClientState.Objects.Types.ICharacter character)
    {
        var isLocal = Objects.LocalPlayer?.Address == character.Address;
        return contactMaps.Resolve(character.Address, character.GameObjectId, character.ObjectIndex, isLocal).Map?.Shaft?.Opening;
    }

    private Dalamud.Game.ClientState.Objects.Types.ICharacter? TestPartnerCharacter() =>
        testPartner.Active ? Objects.CreateObjectReference(testPartner.Address) as Dalamud.Game.ClientState.Objects.Types.ICharacter : null;

    /// <summary>Measures the test partner once its models have loaded; forgotten when it's released.</summary>
    private void MeasureTestPartner()
    {
        if (!testPartner.ReadyToMeasure) return;
        var name = testPartner.Name;
        if (measuredBodies.ContainsKey(name) || !measuringBodies.Add(name)) return;
        var capture = ReadBody(testPartner.Address, testPartner.ObjectIndex);
        if (capture is null)
        {
            measuringBodies.Remove(name);
            return;
        }
        _ = Task.Run(() => Ik.BodyMeasurer.Measure(capture, DataManager, Log)).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && testPartner.Active && testPartner.Name == name)
                measuredBodies[name] = task.Result;
        }, TaskScheduler.Default);
    }

    private void MeasurePartner(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, string name)
    {
        var capture = ReadBody(player.Address, player.ObjectIndex);
        if (capture is null) return;
        _ = Task.Run(() => Ik.BodyMeasurer.Measure(capture, DataManager, Log)).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) measuredBodies[name] = task.Result;
        }, TaskScheduler.Default);
    }

    private long playingSince;
    private string? playingSignature;
    // Set when the emote really starts: a room animation is activated when you ready up but plays
    // only when the room starts.
    private bool playingStarted;

    /// <summary>
    /// True while an animation Synastry started is still playing: something was activated (from the
    /// list, a room sync, Free Use, a slash command) and you haven't moved or switched to another
    /// emote since. Line-up and bone bending only ever act then, never on ordinary sitting or emotes.
    /// </summary>
    private bool IsSynastryAnimationPlaying()
    {
        if (NowPlaying is null || !playingStarted || Objects.LocalPlayer is not { } local) return false;
        var looping = IsLoopingNow(local);
        if (playingSignature is null)
        {
            // Remember the loop it settles into once the emote has started and any pose-variant
            // cycling is over; until then, any loop counts (it may still be the old sit).
            if (looping && Environment.TickCount64 - playingSince > 3000)
                playingSignature = ContactAlignService.Signature(local);
            return looping && Environment.TickCount64 - playingSince < 30000;
        }
        return looping && ContactAlignService.Signature(local) == playingSignature;
    }

    private static unsafe bool IsLoopingNow(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        var character = (Character*)player.Address;
        return character is not null &&
               character->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop;
    }

    private bool IsRoomMemberNamed(string name) =>
        sync.IsInRoom && sync.Room is { } room && room.Members.Any(member =>
            !sync.IsCurrentMember(member.ConnectionId) &&
            member.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));

    // Checking the plugin list every frame is wasteful; once a second is plenty.
    private bool IsSimpleHeelsLoadedCached()
    {
        var now = Environment.TickCount64;
        if (now - simpleHeelsCheckedAt < 1000) return simpleHeelsLoaded;
        simpleHeelsCheckedAt = now;
        simpleHeelsLoaded = SimpleHeelsAvailable;
        return simpleHeelsLoaded;
    }

    public void SetAutomaticEmoteSync(bool enabled)
    {
        configuration.AutomaticEmoteSync = enabled;
        if (!enabled) lobbyEmoteRefreshTime = 0;
        configuration.Save(PluginInterface);
        Status = enabled
            ? "Automatic room EmoteSync enabled. It will run six seconds after synchronized playback starts."
            : "Automatic room EmoteSync disabled. The footer EmoteSync button remains available.";
    }

    public IReadOnlyList<string> GetPenumbraModFolders() => penumbra.GetModFolders();

    public bool SetReceivedModFolder(string folder)
    {
        var result = penumbra.EnsureModFolder(folder);
        if (!result.Success)
        {
            Status = $"Could not use that Penumbra folder: {result.Error}";
            return false;
        }

        configuration.ReceivedModFolder = result.Folder;
        configuration.Save(PluginInterface);
        Status = result.Folder.Length == 0
            ? "Received animations will remain at the top level of Penumbra's mod list."
            : $"Received animations will be organized in Penumbra mod-list folder {result.Folder}.";
        return true;
    }

    private (string SyncKey, string Fingerprint) ResolveCatalogIdentity(
        string directory,
        string computedSyncKey)
    {
        if (inPlaceEmoteConverter.TryGetOriginalIdentity(
                penumbra.GetModRoot(),
                directory,
                out var originalSyncKey,
                out var originalFingerprint))
            return (originalSyncKey, originalFingerprint);
        return (computedSyncKey, CatalogFingerprint(computedSyncKey));
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
        RunSync(sync.ConnectAsync(EffectiveRelayUrl()), () => sync.RelayConnectionStatus);
    }

    public void DisconnectSync() => RunSync(sync.DisconnectAsync(), "Disconnected from animation relay.");

    public void DownloadCommunityTags()
    {
        if (IsRefreshingMods)
        {
            Status = "Wait for the animation-library refresh to finish before downloading community labels.";
            return;
        }
        if (!sync.IsConnected)
        {
            Status = "Connect to Group Play before downloading community tags.";
            return;
        }

        var fingerprints = modCatalogKeys
            .Where(pair => !IsModPrivate(pair.Key))
            .Select(pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fingerprints.Count == 0)
        {
            Status = "No public animation fingerprints are available to check.";
            return;
        }

        Status = $"Checking community tags for {fingerprints.Count:N0} animation mods...";
        var downloads = fingerprints.Chunk(1000)
            .Select(batch => sync.GetCommunityRoleLabelsAsync(batch))
            .ToArray();
        _ = Task.WhenAll(downloads).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully)
            {
                Status = "Community tag download failed.";
                if (task.Exception is not null)
                    Log.Warning(task.Exception.GetBaseException(), "Community tag download failed.");
                return;
            }
            var downloaded = task.Result.SelectMany(batch => batch).ToList();
            foreach (var label in downloaded) receivedCommunityRoleLabels.Enqueue(label);
            Status = downloaded.Count == 0
                ? "No accepted community tags matched your installed animation mods."
                : $"Downloaded {downloaded.Count:N0} community tags.";
        }, TaskScheduler.Default);
    }

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

    public void AcceptRoomInvite(RoomInvite invite)
    {
        if (!RequireCharacterName(out var characterName)) return;
        Status = $"Accepting {invite.SenderName}'s invitation to room {invite.RoomCode}...";
        var join = sync.IsConnected
            ? sync.JoinRoomAsync(invite.RoomCode, characterName, GetCatalogFingerprints())
            : sync.ConnectAsync(EffectiveRelayUrl()).ContinueWith(task =>
            {
                task.GetAwaiter().GetResult();
                return sync.JoinRoomAsync(invite.RoomCode, characterName, GetCatalogFingerprints());
            }, TaskScheduler.Default).Unwrap();
        RunSync(join, $"Joined {invite.SenderName} in room {invite.RoomCode}.");
    }

    public void DeclineRoomInvite(RoomInvite invite) =>
        Status = $"Declined {invite.SenderName}'s room invitation.";

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

    public bool TryTakeRoomInvite(out RoomInvite invite) => roomInvites.TryDequeue(out invite!);

    public void SendMod(string directory, string name)
    {
        Log.Information("Animation send requested for {ModName} ({ModDirectory}).", name, directory);
        if (IsModPrivate(directory))
        {
            Status = $"{name} is private and cannot be sent.";
            return;
        }
        if (IsModConverted(directory))
        {
            Status =
                $"{name} is locally converted and cannot be transferred in this beta. " +
                "Restore it first so recipients receive the creator's original files.";
            Chat.PrintError($"[Synastry] {Status}");
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
        if (!modCatalogKeys.TryGetValue(directory, out var fingerprint))
        {
            Status = $"The animation fingerprint for {name} is not available yet. Refresh the library and try again.";
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
                var sent = sync.SendModAsync(name, package, size, hash, fingerprint).GetAwaiter().GetResult();
                Status = sent.PendingRecipients == 0 && sent.AlreadyReceived > 0
                    ? $"Everyone else in the room already has {name}; no transfer was stored."
                    : sent.PendingRecipients > 0 && sent.AlreadyReceived > 0
                        ? $"Sent {name} to {sent.PendingRecipients} member(s); {sent.AlreadyReceived} already had it."
                        : sent.PendingRecipients > 0
                            ? $"Sent {name} to {sent.PendingRecipients} room member(s)."
                            : $"Sent {name} to the room.";
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
                TryDeleteManagedTransferPackage(path, "after a failed transfer download");
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
        preparedCatalogFingerprint = null;
        preparedCommand = null;
        preparedPose = null;
        preparedDirectPlayback = null;
        preparedCarrierPlayback = null;
        RunSync(sync.CancelReadyAsync(), "Group-play readiness cancelled.");
    }

    public void ForceSyncStart() =>
        RunSync(sync.ForceStartAsync(), "Started every prepared room member's selected animation role.");

    public void RemoveSyncMember(RoomMemberDto member) =>
        RunSync(sync.RemoveMemberAsync(member.ConnectionId), $"Removed {member.DisplayName} from the room.");

    private void CancelGroupReadinessForSolo()
    {
        preparedModKey = null;
        preparedCatalogFingerprint = null;
        preparedCommand = null;
        preparedPose = null;
        preparedDirectPlayback = null;
        preparedCarrierPlayback = null;
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
            Name = "Invite to Synastry",
            PrefixChar = 'S',
            OnClicked = _ => SendRoomInvite(targetName, worldName, roomCode)
        });
    }

    private void SendRoomInvite(string targetName, string worldName, string roomCode)
    {
        var cleanName = Regex.Replace(targetName, @"[^\p{L}'\- ]", "").Trim();
        var cleanWorld = Regex.Replace(worldName, @"[^\p{L}\d\-]", "");
        if (cleanName.Length == 0) return;
        var recipient = cleanWorld.Length == 0 ? cleanName : $"{cleanName}@{cleanWorld}";
        ExecuteCommand($"/tell {recipient} Synastry room invitation: {roomCode}");
        Status = $"Invited {cleanName} to room {roomCode}.";
        mainWindow.NotifyRoomInviteSent(cleanName);
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        if (chatMessage.LogKind != XivChatType.TellIncoming) return;
        var match = Regex.Match(
            chatMessage.Message.TextValue,
            @"^(?:Synastry room invitation|EmoteLink room code):\s*([A-Za-z0-9]{4,8})\s*$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return;
        var code = match.Groups[1].Value.ToUpperInvariant();
        var senderName = chatMessage.Sender.TextValue.Trim();
        if (string.IsNullOrWhiteSpace(senderName)) senderName = "A player";
        roomInvites.Enqueue(new RoomInvite(senderName, code));
        Status = $"{senderName} invited you to room {code}.";
        miniPlayerWindow.IsOpen = false;
        mainWindow.IsOpen = true;
        chatMessage.Message = $"Synastry invitation: {senderName} invited you to room {code}.";
    }

    private void RunSync(Task operation, string success) => RunSync(operation, () => success);

    private void RunSync(Task operation, Func<string> success)
    {
        _ = operation.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) Status = success();
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

    public void EnsureDetectedEmotes(string directory, string name)
    {
        if (modEmotes.ContainsKey(directory)) return;
        IndexDetectedEmotes(directory, name);
        animationIndexCache.MarkEmotesIndexed(directory, modEmotes.GetValueOrDefault(directory, []));
        _ = animationIndexCache.SaveInBackgroundAsync();
    }

    public void ActivateDetectedPose(string directory, string name, PoseTarget pose)
    {
        var trigger = $"pose:{pose.Kind}:{pose.Index}";
        PublishDetectedTriggerSelection(directory, trigger);
        if (ActivateInternal(directory, name, pose)) OfferFreeUsePrompts(directory, name, trigger);
    }

    public void ActivateDetectedPoseSolo(string directory, string name, PoseTarget pose)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, pose, false);
    }

    public void ActivateDetectedEmote(string directory, string name, EmoteTarget emote)
    {
        var trigger = $"emote:{emote.Id}";
        PublishDetectedTriggerSelection(directory, trigger);
        if (ActivateInternal(directory, name, null, requestedCommand: emote.Command))
            OfferFreeUsePrompts(directory, name, trigger);
    }

    public void ActivateDetectedEmoteSolo(string directory, string name, EmoteTarget emote)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, null, false, emote.Command);
    }

    public IReadOnlyList<CustomAnimationCommand> CustomAnimationCommands => configuration.CustomAnimationCommands
        .OrderBy(assignment => assignment.Command, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public List<AnimationCommandTarget> GetAvailableAnimationCommandTargets()
    {
        var targets = new List<AnimationCommandTarget>();
        foreach (var mod in Mods)
        {
            EnsureDetectedEmotes(mod.Directory, mod.Name);
            foreach (var pose in GetDetectedPoses(mod.Directory))
                targets.Add(new AnimationCommandTarget(
                    mod.Directory,
                    mod.Name,
                    CustomAnimationTriggerKind.Pose,
                    $"{pose.Kind}:{pose.Index}",
                    PoseDisplayName(pose)));
            foreach (var emote in GetDetectedEmotes(mod.Directory))
                targets.Add(new AnimationCommandTarget(
                    mod.Directory,
                    mod.Name,
                    CustomAnimationTriggerKind.Emote,
                    emote.Id.ToString(),
                    $"{emote.Name} (ID {emote.Id})"));
        }
        return targets
            .OrderBy(target => target.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.AnimationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.ModDirectory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool AssignCustomAnimationCommand(string rawCommand, AnimationCommandTarget target)
    {
        if (!TryNormalizeCustomCommand(rawCommand, out var command, out var error))
        {
            Status = error;
            return false;
        }
        if (command.Equals(PrimaryCommand, StringComparison.OrdinalIgnoreCase) ||
            command.Equals(FallbackCommand, StringComparison.OrdinalIgnoreCase))
        {
            Status = $"{command} is reserved by Synastry. Choose another command.";
            return false;
        }
        if (!modsByDirectory.ContainsKey(target.ModDirectory))
        {
            Status = $"{target.ModName} is no longer in the animation library.";
            return false;
        }

        var previous = configuration.CustomAnimationCommands.FirstOrDefault(assignment =>
            assignment.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
        var wasRegistered = registeredCustomCommands.Remove(command);
        if (wasRegistered) Commands.RemoveHandler(command);
        if (!TryRegisterCustomAnimationCommand(command, out error))
        {
            if (wasRegistered) TryRegisterCustomAnimationCommand(command, out _);
            Status = error;
            return false;
        }

        configuration.CustomAnimationCommands.RemoveAll(assignment =>
            assignment.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
        configuration.CustomAnimationCommands.Add(new CustomAnimationCommand
        {
            Command = command,
            ModDirectory = target.ModDirectory,
            ModName = target.ModName,
            TriggerKind = target.TriggerKind,
            TriggerValue = target.TriggerValue,
            AnimationName = target.AnimationName
        });
        configuration.Save(PluginInterface);
        Status = previous is null
            ? $"Assigned {command} to {target.AnimationName}."
            : $"Reassigned {command} to {target.AnimationName}.";
        return true;
    }

    public void RemoveCustomAnimationCommand(string rawCommand)
    {
        if (!TryNormalizeCustomCommand(rawCommand, out var command, out _)) return;
        var removed = configuration.CustomAnimationCommands.RemoveAll(assignment =>
            assignment.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (registeredCustomCommands.Remove(command)) Commands.RemoveHandler(command);
        if (removed == 0) return;
        configuration.Save(PluginInterface);
        Status = $"Removed custom animation command {command}.";
    }

    public bool RunCustomAnimationCommand(string rawCommand)
    {
        if (!TryNormalizeCustomCommand(rawCommand, out var command, out var error))
        {
            Status = error;
            return false;
        }
        return ExecuteCustomAnimationCommand(command);
    }

    private void RegisterConfiguredAnimationCommands()
    {
        var cleaned = new List<CustomAnimationCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var assignment in configuration.CustomAnimationCommands)
        {
            if (!TryNormalizeCustomCommand(assignment.Command, out var command, out _) ||
                command.Equals(PrimaryCommand, StringComparison.OrdinalIgnoreCase) ||
                command.Equals(FallbackCommand, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(command))
            {
                changed = true;
                continue;
            }
            if (!assignment.Command.Equals(command, StringComparison.Ordinal))
            {
                assignment.Command = command;
                changed = true;
            }
            cleaned.Add(assignment);
            if (!TryRegisterCustomAnimationCommand(command, out var error))
                Log.Warning("Could not register custom animation command {Command}: {Error}", command, error);
        }
        if (!changed) return;
        configuration.CustomAnimationCommands = cleaned;
        configuration.Save(PluginInterface);
    }

    private bool TryRegisterCustomAnimationCommand(string command, out string error)
    {
        if (registeredCustomCommands.Contains(command))
        {
            error = "";
            return true;
        }
        if (!Commands.AddHandler(command, new CommandInfo((_, _) => ExecuteCustomAnimationCommand(command))
            {
                HelpMessage = "Run a custom Synastry animation assignment.",
                ShowInHelp = false
            }))
        {
            error = $"{command} is already registered by another plugin. Choose another command.";
            return false;
        }
        registeredCustomCommands.Add(command);
        error = "";
        return true;
    }

    private bool ExecuteCustomAnimationCommand(string command)
    {
        var assignment = configuration.CustomAnimationCommands.FirstOrDefault(candidate =>
            candidate.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (assignment is null)
        {
            Status = $"No Synastry animation is assigned to {command}.";
            return false;
        }
        if (!modsByDirectory.TryGetValue(assignment.ModDirectory, out var mod))
        {
            Status = $"{command} cannot run because {assignment.ModName} is not in the current animation library.";
            return false;
        }

        if (assignment.TriggerKind == CustomAnimationTriggerKind.Pose)
        {
            var parts = assignment.TriggerValue.Split(':', 2);
            if (parts.Length == 2 && Enum.TryParse<PoseKind>(parts[0], true, out var kind) &&
                byte.TryParse(parts[1], out var index))
            {
                var pose = GetDetectedPoses(mod.Directory).FirstOrDefault(candidate =>
                    candidate.Kind == kind && candidate.Index == index);
                if (pose is not null)
                {
                    ActivateDetectedPose(mod.Directory, mod.Name, pose);
                    return true;
                }
            }
        }
        else if (uint.TryParse(assignment.TriggerValue, out var emoteId))
        {
            EnsureDetectedEmotes(mod.Directory, mod.Name);
            var emote = GetDetectedEmotes(mod.Directory).FirstOrDefault(candidate => candidate.Id == emoteId);
            if (emote is not null)
            {
                ActivateDetectedEmote(mod.Directory, mod.Name, emote);
                return true;
            }
        }

        Status = $"{command} cannot run because {assignment.AnimationName} is no longer detected in {mod.Name}.";
        return false;
    }

    private static bool TryNormalizeCustomCommand(string rawCommand, out string command, out string error)
    {
        var clean = rawCommand.Trim().TrimStart('/');
        if (!Regex.IsMatch(clean, @"^[A-Za-z][A-Za-z0-9_-]{0,31}$"))
        {
            command = "";
            error = "Commands must start with a letter and contain only letters, numbers, underscores, or hyphens (32 characters maximum).";
            return false;
        }
        command = "/" + clean.ToLowerInvariant();
        error = "";
        return true;
    }

    private static string PoseDisplayName(PoseTarget pose) => pose.Kind switch
    {
        PoseKind.Sit => $"Chair Sit {pose.Index}",
        PoseKind.GroundSit => $"Ground Sit {pose.Index}",
        PoseKind.Doze => $"Doze {pose.Index}",
        _ => $"Idle {pose.Index}"
    };

    public bool IsFreeUseEnabled => sync.IsFreeUse;

    public void SetFreeUse(bool enabled)
    {
        if (!sync.IsInRoom)
        {
            Status = "Join a room before turning on FREE USE.";
            return;
        }
        Status = enabled ? "Turning on FREE USE..." : "Turning off FREE USE...";
        RunSync(sync.SetFreeUseAsync(enabled), enabled
            ? "FREE USE is on. Room members choose your role and mod options, and you are readied automatically."
            : "FREE USE is off. You choose your own roles again.");
    }

    public bool IsFreeUseMemberAvailable(string connectionId) =>
        sync.Room?.Members.Any(member => member.ConnectionId == connectionId && member.FreeUse) == true;

    /// <summary>A copy of the chooser's own options, edited in the prompt before it is sent.</summary>
    public Dictionary<string, List<string>> GetFreeUseOptionTemplate(string directory) =>
        GetActivationSelections(directory).ToDictionary(
            pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);

    public void DirectFreeUse(FreeUsePrompt prompt, string trigger, IReadOnlyDictionary<string, List<string>> options)
    {
        if (IsModPrivate(prompt.Directory) ||
            !modCatalogKeys.TryGetValue(prompt.Directory, out var fingerprint) ||
            !modSyncKeys.TryGetValue(prompt.Directory, out var modKey))
        {
            Status = $"{prompt.ModName} cannot be shared with {prompt.MemberName}.";
            return;
        }
        var request = new FreeUseDirectionRequest(
            fingerprint,
            modKey,
            prompt.ModName,
            trigger,
            options.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()));
        Status = $"Sending your choice to {prompt.MemberName}...";
        _ = sync.DirectFreeUseAsync(prompt.MemberConnectionId, request).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                Status = $"{prompt.MemberName} is preparing the role you chose.";
                return;
            }
            // The prompt has already closed, so a refusal is repeated in chat where it will be seen.
            var error = task.Exception?.GetBaseException();
            Status = $"Could not choose for {prompt.MemberName}: {error?.Message ?? "the relay did not respond."}";
            if (error is not null) Log.Warning(error, "FREE USE choice for {Member} failed.", prompt.MemberName);
            var message = Status;
            _ = Framework.RunOnFrameworkThread(() => Chat.PrintError($"[Synastry] {message}"));
        }, TaskScheduler.Default);
    }

    private void OfferFreeUsePrompts(string directory, string name, string ownTrigger)
    {
        if (sync.Room is not { } room || IsModPrivate(directory) || !modCatalogKeys.ContainsKey(directory)) return;
        FollowWithTestPartner(directory, ownTrigger);
        var partnerId = partnerSync?.ConnectionId;
        foreach (var member in room.Members.Where(member =>
                     member.FreeUse && !sync.IsCurrentMember(member.ConnectionId) && member.ConnectionId != partnerId))
        {
            mainWindow.ShowFreeUsePrompt(new FreeUsePrompt(
                directory, name, ownTrigger, member.ConnectionId, member.DisplayName));
            SurfacePromptFromMiniPlayer();
        }
    }

    private void ProcessFreeUseDirectives()
    {
        while (freeUseDirectives.TryDequeue(out var directive))
        {
            // The relay checks the flag too; this guards a choice that crossed a FREE USE toggle-off.
            if (!sync.IsFreeUse)
            {
                Log.Information("Ignored a FREE USE choice from {Sender} because FREE USE is off.", directive.DirectedBy);
                continue;
            }

            var directory = modCatalogKeys.FirstOrDefault(pair =>
                pair.Value.Equals(directive.Fingerprint, StringComparison.OrdinalIgnoreCase)).Key;
            if (string.IsNullOrWhiteSpace(directory) || IsModPrivate(directory) ||
                !modsByDirectory.TryGetValue(directory, out var mod))
            {
                ReportPlaybackFailure(directive.ModName,
                    $"{directive.DirectedBy} chose it for you, but it is not in your shared animation library.");
                continue;
            }
            if (!TryResolveFreeUseTrigger(directory, directive.Trigger, out var pose, out var command, out var animationName))
            {
                ReportPlaybackFailure(mod.Name, $"{directive.DirectedBy} chose a role this mod does not have.");
                continue;
            }

            // The chooser's options win for this activation only; nothing is saved to this configuration.
            var selections = GetActivationSelections(directory).ToDictionary(
                pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var (group, options) in directive.Options) selections[group] = options.ToList();

            Chat.Print($"[Synastry] FREE USE: {directive.DirectedBy} chose {animationName} in {mod.Name} for you.");
            Log.Information("FREE USE: {Sender} chose {Trigger} in {ModName}.", directive.DirectedBy, directive.Trigger, mod.Name);
            PublishDetectedTriggerSelection(directory, directive.Trigger);
            ActivateInternal(directory, mod.Name, pose, requestedCommand: command, selectionOverride: selections);
        }
    }

    private bool TryResolveFreeUseTrigger(
        string directory,
        string trigger,
        out PoseTarget? pose,
        out string? command,
        out string animationName)
    {
        pose = null;
        command = null;
        animationName = "";
        if (trigger.StartsWith("pose:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trigger["pose:".Length..].Split(':', 2);
            if (parts.Length != 2 || !Enum.TryParse<PoseKind>(parts[0], true, out var kind) ||
                !byte.TryParse(parts[1], out var index) || index > PoseService.MaxPoseIndex) return false;
            pose = new PoseTarget(kind, index);
            animationName = PoseDisplayName(pose);
            return true;
        }
        if (!trigger.StartsWith("emote:", StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(trigger["emote:".Length..], out var emoteId) ||
            !emotePlaybackById.TryGetValue(emoteId, out var info)) return false;
        command = info.Command;
        EnsureDetectedEmotes(directory, modsByDirectory[directory].Name);
        animationName = GetDetectedEmotes(directory).FirstOrDefault(emote => emote.Id == emoteId)?.Name ?? info.Command;
        return true;
    }

    private void PublishDetectedTriggerSelection(string directory, string trigger)
    {
        if (sync.IsInRoom && modSyncKeys.TryGetValue(directory, out var modKey))
            RunSync(sync.SetOptionSelectionAsync(modKey, "$detected-trigger", trigger),
                "Shared your selected animation role with the room.");
    }

    private void NormalizeSelections(string directory, IReadOnlyList<ModOptionGroup> groups)
    {
        if (!configuration.ModOptionSelections.TryGetValue(directory, out var selections)) return;
        foreach (var group in groups.Where(group => !group.IsMultiSelect))
            if (selections.TryGetValue(group.Name, out var selected) && selected.Count > 1)
                selected.RemoveRange(1, selected.Count - 1);
    }

    public IReadOnlyList<(string Directory, string Name)> GetOrganizedMods(string? categoryId)
    {
        var revision = Volatile.Read(ref libraryOrderRevision);
        if (cachedLibraryOrderRevision != revision)
        {
            organizedModsCache.Clear();
            cachedLibraryOrderRevision = revision;
        }
        var cacheKey = categoryId ?? UncategorizedCacheKey;
        if (organizedModsCache.TryGetValue(cacheKey, out var cached)) return cached;
        var order = categoryId is null
            ? configuration.UncategorizedOrder
            : configuration.Categories.FirstOrDefault(folder => folder.Id == categoryId)?.ModDirectories ?? [];
        var organized = OrderModsForLibrary(order.Where(modsByDirectory.ContainsKey)
            .Select(directory => modsByDirectory[directory]));
        organizedModsCache[cacheKey] = organized;
        return organized;
    }

    public List<(string Directory, string Name)> OrderModsForLibrary(
        IEnumerable<(string Directory, string Name)> mods) => mods
            .OrderBy(mod => GetMatchSortTier(mod.Directory))
            .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ModCategory> GetChildCategories(string? parentId)
    {
        var normalizedParent = string.IsNullOrWhiteSpace(parentId) ? null : parentId;
        return configuration.Categories.Where(category =>
                normalizedParent is null
                    ? string.IsNullOrWhiteSpace(category.ParentId)
                    : category.ParentId?.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
    }

    public int GetCategoryModCount(string categoryId)
    {
        var categoryIds = GetCategoryTreeIds(categoryId);
        return configuration.Categories
            .Where(category => categoryIds.Contains(category.Id))
            .SelectMany(category => category.ModDirectories)
            .Where(modsByDirectory.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    public string GetCategoryPath(string categoryId)
    {
        var byId = configuration.Categories
            .GroupBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentId = categoryId;
        while (seen.Add(currentId) && byId.TryGetValue(currentId, out var category))
        {
            parts.Add(category.Name);
            if (string.IsNullOrWhiteSpace(category.ParentId)) break;
            currentId = category.ParentId;
        }
        parts.Reverse();
        return string.Join(" / ", parts);
    }

    private int GetMatchSortTier(string directory)
    {
        if (sync.IsInRoom && IsModPrivate(directory)) return 5;   // Private mods always stay at the bottom in rooms.
        if (GetRemoteModSelector(directory) is not null) return 0; // Purple: suggested.
        if (IsModPrivate(directory)) return 3;                    // Cyan: private.
        var (matches, members) = GetModMatch(directory);
        if (members > 1 && matches >= members) return 1; // Green: everyone has it.
        if (members > 1 && matches > 1) return 2;        // Orange: some members have it.
        return 4;                                        // White: no shared match.
    }

    public void CreateCategory(string name, string? parentCategoryId = null)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var hasRequestedParent = !string.IsNullOrWhiteSpace(parentCategoryId);
        var parent = !hasRequestedParent
            ? null
            : configuration.Categories.FirstOrDefault(category =>
                category.Id.Equals(parentCategoryId, StringComparison.OrdinalIgnoreCase));
        if (hasRequestedParent && parent is null) return;
        configuration.Categories.Add(new ModCategory { Name = name, ParentId = parent?.Id });
        SaveOrganization();
        Status = parent is null
            ? $"Created folder {name}."
            : $"Created subfolder {name} inside {parent.Name}.";
    }

    public void RenameCategory(string categoryId, string name)
    {
        var category = configuration.Categories.FirstOrDefault(item => item.Id == categoryId);
        var clean = name.Trim();
        if (category is null || clean.Length == 0 || category.Name.Equals(clean, StringComparison.Ordinal)) return;
        category.Name = clean;
        SaveOrganization();
        Status = $"Renamed folder to {clean}.";
    }

    public void DeleteCategory(string categoryId)
    {
        var category = configuration.Categories.FirstOrDefault(item =>
            item.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
        if (category is null) return;
        var parentId = category.ParentId;
        foreach (var child in configuration.Categories.Where(item =>
                     item.ParentId?.Equals(category.Id, StringComparison.OrdinalIgnoreCase) == true))
            child.ParentId = parentId;
        configuration.UncategorizedOrder.AddRange(category.ModDirectories);
        configuration.Categories.Remove(category);
        NormalizeOrganization();
        Status = "Deleted the folder. Its mods moved to Uncategorized and its subfolders moved up one level.";
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

    public void MoveMods(
        IReadOnlyCollection<string> directories,
        string? targetCategoryId,
        string? beforeDirectory = null)
    {
        var requested = directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = configuration.Categories.SelectMany(category => category.ModDirectories)
            .Concat(configuration.UncategorizedOrder)
            .Where(requested.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count == 0) return;

        var target = targetCategoryId is null
            ? configuration.UncategorizedOrder
            : configuration.Categories.FirstOrDefault(item => item.Id == targetCategoryId)?.ModDirectories;
        if (target is null) return;

        configuration.UncategorizedOrder.RemoveAll(requested.Contains);
        foreach (var category in configuration.Categories)
            category.ModDirectories.RemoveAll(requested.Contains);

        var index = beforeDirectory is null
            ? -1
            : target.FindIndex(item => item.Equals(beforeDirectory, StringComparison.OrdinalIgnoreCase));
        if (index < 0) target.AddRange(ordered); else target.InsertRange(index, ordered);
        SaveOrganization();
        Status = $"Moved {ordered.Count} selected mod(s).";
    }

    public void MoveCategory(string sourceId, string? targetParentId)
    {
        var source = configuration.Categories.FirstOrDefault(item =>
            item.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
        var hasTargetParent = !string.IsNullOrWhiteSpace(targetParentId);
        var target = !hasTargetParent
            ? null
            : configuration.Categories.FirstOrDefault(item =>
                item.Id.Equals(targetParentId, StringComparison.OrdinalIgnoreCase));
        if (source is null || hasTargetParent && target is null) return;
        if (target is not null && (target.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase) ||
                                   IsCategoryInside(target.Id, source.Id)))
        {
            Status = "A folder cannot be moved inside itself or one of its subfolders.";
            return;
        }

        var modCount = GetCategoryModCount(source.Id);
        source.ParentId = target?.Id;
        configuration.Categories.Remove(source);
        configuration.Categories.Add(source);
        SaveOrganization();
        Status = target is null
            ? $"Moved {source.Name} to the top level with {modCount} animation mod(s)."
            : $"Moved {source.Name} inside {target.Name} with {modCount} animation mod(s).";
    }

    public void MoveCategoryBefore(string sourceId, string beforeId)
    {
        if (sourceId.Equals(beforeId, StringComparison.OrdinalIgnoreCase)) return;
        var source = configuration.Categories.FirstOrDefault(item =>
            item.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
        var before = configuration.Categories.FirstOrDefault(item =>
            item.Id.Equals(beforeId, StringComparison.OrdinalIgnoreCase));
        if (source is null || before is null) return;

        var targetParentId = before.ParentId;
        if (!string.IsNullOrWhiteSpace(targetParentId) &&
            (targetParentId.Equals(source.Id, StringComparison.OrdinalIgnoreCase) ||
             IsCategoryInside(targetParentId, source.Id)))
        {
            Status = "A folder cannot be moved inside itself or one of its subfolders.";
            return;
        }

        var modCount = GetCategoryModCount(source.Id);
        source.ParentId = targetParentId;
        configuration.Categories.Remove(source);
        var beforeIndex = configuration.Categories.FindIndex(item =>
            item.Id.Equals(before.Id, StringComparison.OrdinalIgnoreCase));
        if (beforeIndex < 0)
        {
            configuration.Categories.Add(source);
            SaveOrganization();
            return;
        }
        configuration.Categories.Insert(beforeIndex, source);
        SaveOrganization();
        Status = $"Moved {source.Name} before {before.Name} with {modCount} animation mod(s).";
    }

    private void NormalizeOrganization()
    {
        var categoriesById = configuration.Categories
            .GroupBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var category in configuration.Categories)
        {
            if (string.IsNullOrWhiteSpace(category.ParentId))
            {
                category.ParentId = null;
                continue;
            }
            if (category.ParentId.Equals(category.Id, StringComparison.OrdinalIgnoreCase) ||
                !categoriesById.ContainsKey(category.ParentId))
                category.ParentId = null;
        }
        // Break any malformed parent cycle so every folder remains reachable from the root.
        foreach (var category in configuration.Categories)
        {
            var seenParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { category.Id };
            var current = category;
            while (!string.IsNullOrWhiteSpace(current.ParentId) &&
                   categoriesById.TryGetValue(current.ParentId, out var parent))
            {
                if (!seenParents.Add(parent.Id))
                {
                    category.ParentId = null;
                    break;
                }
                current = parent;
            }
        }

        var available = Mods.Select(mod => mod.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in configuration.Categories)
            category.ModDirectories.RemoveAll(directory => !available.Contains(directory) || !seen.Add(directory));
        configuration.UncategorizedOrder.RemoveAll(directory => !available.Contains(directory) || !seen.Add(directory));
        configuration.UncategorizedOrder.AddRange(Mods.Select(mod => mod.Directory).Where(seen.Add));
        SaveOrganization();
    }

    private HashSet<string> GetCategoryTreeIds(string rootId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(rootId);
        while (pending.TryPop(out var categoryId))
        {
            if (!result.Add(categoryId)) continue;
            foreach (var child in configuration.Categories.Where(category =>
                         category.ParentId?.Equals(categoryId, StringComparison.OrdinalIgnoreCase) == true))
                pending.Push(child.Id);
        }
        return result;
    }

    private bool IsCategoryInside(string categoryId, string possibleAncestorId)
    {
        var current = configuration.Categories.FirstOrDefault(category =>
            category.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && seen.Add(current.Id))
        {
            if (current.Id.Equals(possibleAncestorId, StringComparison.OrdinalIgnoreCase)) return true;
            current = string.IsNullOrWhiteSpace(current.ParentId)
                ? null
                : configuration.Categories.FirstOrDefault(category =>
                    category.Id.Equals(current.ParentId, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private void SaveOrganization()
    {
        InvalidateLibraryOrder();
        configuration.Save(PluginInterface);
    }

    private void InvalidateLibraryOrder() => Interlocked.Increment(ref libraryOrderRevision);

    public void Activate(string directory, string name) => ActivateInternal(directory, name, null);

    public void ActivateSolo(string directory, string name)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, null, false);
    }

    /// <returns>True when the animation was scheduled or prepared for the room.</returns>
    private bool ActivateInternal(
        string directory,
        string name,
        PoseTarget? requestedPose,
        bool allowGroupPlay = true,
        string? requestedCommand = null,
        IReadOnlyDictionary<string, List<string>>? selectionOverride = null)
    {
        if (requestedPose is null && requestedCommand is null &&
            modPoses.TryGetValue(directory, out var detected) && detected.Count == 1)
            requestedPose = detected[0];
        ClearTemporaryAssignmentsInternal(false, false);
        var collection = penumbra.GetPlayerCollection();
        if (collection is null)
        {
            ReportPlaybackFailure(name, "Penumbra has no collection assigned to your character.");
            return false;
        }
        ClearRemotePlaybacksInCollection(collection.Value.Id);
        var selections = selectionOverride ?? GetActivationSelections(directory);
        var activation = penumbra.Activate(collection.Value.Id, directory, name, selections);
        if (!activation.Success)
        {
            ReportPlaybackFailure(name, activation.Error);
            return false;
        }

        configuration.ActiveAssignments.Add(new TemporaryAssignment(collection.Value.Id, directory, name));
        configuration.Save(PluginInterface);
        NowPlaying = new NowPlayingInfo(directory, name, NowPlayingLabel(directory, requestedPose, requestedCommand));
        playingSince = Environment.TickCount64;
        playingSignature = null;
        playingStarted = false;
        waitingForAnimation = true;
        activationTime = Environment.TickCount64;
        movementTrackingStart = activationTime + 1200;
        hasMovementSample = false;
        movementFrames = 0;

        if (requestedPose is not null)
        {
            if (allowGroupPlay && PrepareForGroupPlay(directory, name, null, requestedPose, null)) return true;
            SchedulePose(name, requestedPose, 300);
            return true;
        }

        var command = requestedCommand ?? DetectEmoteCommand(directory, name);
        if (command is null)
        {
            ReportPlaybackFailure(
                name,
                "No emote command could be detected. Select the option containing the animation, refresh the library, and try again.");
            return false;
        }

        if (!TryCreatePlayback(command, out var playback))
        {
            ReportPlaybackFailure(
                name,
                $"{command} has no PAP-backed action timeline in the current game data.");
            return false;
        }
        RememberTypedEmoteDefault(playback.EmoteId, directory);

        // Preserve the game's normal networked emote path whenever the character owns it.
        // Direct timeline playback is only the fallback that bypasses a locked emote.
        if (IsEmoteUnlocked(playback.EmoteId))
        {
            if (allowGroupPlay && PrepareForGroupPlay(directory, name, command, null, null)) return true;
            ScheduleCommand(name, command, 300);
            return true;
        }

        var conversion = TryConvertLockedEmote(directory, name, command);
        if (conversion.Success)
        {
            convertedMods.Add(directory);
            if (conversion.ChangedFiles)
            {
                var reload = penumbra.Reload(directory, name);
                if (!reload.Success)
                {
                    ReportPlaybackFailure(
                        name,
                        $"The files were converted, but Penumbra could not reload them: {reload.Error}. " +
                        "Use Rediscover Mods in Penumbra, then try again.");
                    return false;
                }
                var reactivation = penumbra.Activate(collection.Value.Id, directory, name, selections);
                if (!reactivation.Success)
                {
                    ReportPlaybackFailure(
                        name,
                        $"Penumbra reloaded the conversion but would not reactivate it: {reactivation.Error}");
                    return false;
                }
            }
            var carrier = new CarrierPlayback(
                conversion.CarrierEmoteId,
                conversion.CarrierCommand,
                name);
            if (allowGroupPlay &&
                PrepareForGroupPlay(directory, name, null, null, playback, carrier))
                return true;
            ScheduleCarrierPlayback(carrier, conversion.ChangedFiles ? 650 : 300);
            if (sync.IsConnected && modCatalogKeys.TryGetValue(directory, out var convertedFingerprint) &&
                convertedFingerprint.Length == 64)
                pendingNearbyBroadcast = (convertedFingerprint, playback);
            return true;
        }

        if (conversion.ChangedFiles)
        {
            convertedMods.Remove(directory);
            var reload = penumbra.Reload(directory, name);
            if (!reload.Success)
            {
                ReportPlaybackFailure(
                    name,
                    $"The obsolete carrier was restored, but Penumbra could not reload the mod: {reload.Error}. " +
                    "Use Rediscover Mods in Penumbra, then try again.");
                return false;
            }
            var reactivation = penumbra.Activate(collection.Value.Id, directory, name, selections);
            if (!reactivation.Success)
            {
                ReportPlaybackFailure(
                    name,
                    $"Penumbra reloaded the restored mod but would not reactivate it: {reactivation.Error}");
                return false;
            }
        }

        ReportPlaybackNotice(
            name,
            $"Permanent carrier conversion was unavailable: {conversion.Message} " +
            "Synastry is using the legacy direct-play fallback for this attempt.");
        if (allowGroupPlay && PrepareForGroupPlay(directory, name, null, null, playback)) return true;
        ScheduleDirectPlayback(name, Objects.LocalPlayer?.Address ?? 0, playback, 300);
        if (sync.IsConnected && modCatalogKeys.TryGetValue(directory, out var fingerprint) && fingerprint.Length == 64)
            pendingNearbyBroadcast = (fingerprint, playback);
        return true;
    }

    private bool PrepareForGroupPlay(
        string directory,
        string modName,
        string? command,
        PoseTarget? pose,
        EmotePlayback? directPlayback,
        CarrierPlayback? carrierPlayback = null)
    {
        if (!sync.IsInRoom) return false;
        preparedModKey = modSyncKeys.TryGetValue(directory, out var key) ? key : NormalizeModKey(modName);
        preparedCatalogFingerprint = modCatalogKeys.GetValueOrDefault(directory);
        preparedCommand = command;
        preparedPose = pose;
        preparedDirectPlayback = directPlayback;
        preparedCarrierPlayback = carrierPlayback;
        Status = $"Prepared {modName}; waiting for everyone in room {sync.Room!.RoomCode}.";
        var assetPath = WarmUpPreparedAnimation(command, directPlayback, carrierPlayback);
        RunSync(sync.ReadyWithAssetAsync(preparedModKey, assetPath), $"Ready with {modName}; waiting for the group.");
        return true;
    }

    /// <summary>
    /// Loads the prepared emote's animation now, so a sync plugin sends it to the room before the
    /// start. Returns its game path when a mod replaces it. Poses and local-only direct playback
    /// aren't preloaded.
    /// </summary>
    private string WarmUpPreparedAnimation(string? command, EmotePlayback? directPlayback, CarrierPlayback? carrierPlayback)
    {
        if (directPlayback is not null && carrierPlayback is null) return "";
        EmotePlaybackInfo? info = null;
        if (carrierPlayback is not null) emotePlaybackById.TryGetValue(carrierPlayback.EmoteId, out info);
        else if (command is not null) emotePlaybackByCommand.TryGetValue(command, out info);
        if (info is null || !TryCreatePlayback(info, out var playback)) return "";
        var main = info.Timelines.FirstOrDefault(timeline => timeline.RowId == playback.MainTimeline);
        return main is null ? "" : preloader.WarmUp(playback.MainTimeline, main.Key);
    }

    /// <summary>Room members whose animation files are still on their way to this client.</summary>
    public IReadOnlyList<string> WaitingForAnimationFiles => preloader.WaitingFor;

    private void SchedulePose(string modName, PoseTarget pose, int delayMs)
    {
        Status = $"Activated {modName}; switching to {PoseLabel(pose)}.";
        NewStartScheduled();
        pendingPose = pose;
        pendingCommandTime = Environment.TickCount64 + delayMs;
    }

    private void ScheduleCommand(string modName, string command, int delayMs)
    {
        Status = $"Activated {modName}; starting {command}.";
        NewStartScheduled();
        pendingCommand = command;
        pendingCommandTime = Environment.TickCount64 + delayMs;
    }

    private void ScheduleDirectPlayback(string modName, nint actorAddress, EmotePlayback playback, long delayMs)
    {
        Status = $"Activated {modName}; starting its native timeline.";
        NewStartScheduled();
        pendingDirectPlayback = new PendingDirectPlayback(actorAddress, playback, modName);
        pendingCommandTime = Environment.TickCount64 + delayMs;
    }

    private void ScheduleCarrierPlayback(CarrierPlayback playback, long delayMs)
    {
        var startAt = Environment.TickCount64 + delayMs;
        NewStartScheduled();
        pendingCarrierPlayback = new PendingCarrierPlayback(playback, startAt, startAt + 600);
        pendingCommandTime = startAt;
        Status = $"Activated {playback.ModName}; starting its permanent carrier {playback.Command}.";
    }

    private void ActivateVanillaEmoteRedirect(EmotePlaybackInfo source)
    {
        var collection = penumbra.GetPlayerCollection();
        if (collection is null)
        {
            ActivateLockedVanillaDirectFallback(
                source,
                "Penumbra has no collection assigned to your character.");
            return;
        }

        ClearTemporaryAssignmentsInternal(false, false);
        ClearRemotePlaybacksInCollection(collection.Value.Id);
        var sourceCandidate = new EmoteConversionCandidate(
            source.EmoteId,
            source.Command,
            source.Timelines.Select(timeline => new EmoteConversionTimeline(
                timeline.Slot,
                timeline.Key,
                timeline.IsPersistentLoop)).ToList());
        var carriers = BuildCarrierCandidates(source, true);
        var build = vanillaEmoteRedirect.Build(penumbra.GetModRoot(), sourceCandidate, carriers);
        if (!build.Success)
        {
            ActivateLockedVanillaDirectFallback(source, build.Message);
            return;
        }

        var registeredBeforeBuild = penumbra.GetMods().Any(mod =>
            mod.Directory.Equals(VanillaEmoteRedirectService.ModDirectory, StringComparison.OrdinalIgnoreCase));
        if (!registeredBeforeBuild)
        {
            var added = penumbra.AddMod(VanillaEmoteRedirectService.ModDirectory);
            if (!added.Success)
            {
                ActivateLockedVanillaDirectFallback(
                    source,
                    $"Penumbra could not register Synastry Redirect: {added.Error}");
                return;
            }
        }

        var registeredMod = penumbra.GetMods().FirstOrDefault(mod =>
            mod.Directory.Equals(VanillaEmoteRedirectService.ModDirectory, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(registeredMod.Directory))
        {
            ActivateLockedVanillaDirectFallback(
                source,
                "Penumbra accepted the Synastry Redirect folder but did not register it as a valid mod.");
            return;
        }

        var reload = penumbra.Reload(
            registeredMod.Directory,
            registeredMod.Name);
        if (!reload.Success)
        {
            ActivateLockedVanillaDirectFallback(
                source,
                $"Penumbra could not reload Synastry Redirect: {reload.Error}");
            return;
        }

        var selections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var redirectActivation = penumbra.Activate(
            collection.Value.Id,
            registeredMod.Directory,
            registeredMod.Name,
            selections,
            10000);
        if (!redirectActivation.Success)
        {
            ActivateLockedVanillaDirectFallback(
                source,
                $"Penumbra would not temporarily enable Synastry Redirect: {redirectActivation.Error}");
            return;
        }

        configuration.ActiveAssignments.Add(new TemporaryAssignment(
            collection.Value.Id,
            registeredMod.Directory,
            registeredMod.Name));
        configuration.Save(PluginInterface);
        waitingForAnimation = true;
        activationTime = Environment.TickCount64;
        movementTrackingStart = activationTime + 1200;
        hasMovementSample = false;
        movementFrames = 0;
        ReportPlaybackNotice(
            source.Command,
            $"Synastry Redirect rebuilt the locked vanilla emote through {build.CarrierCommand}.");
        ScheduleCarrierPlayback(
            new CarrierPlayback(
                build.CarrierEmoteId,
                build.CarrierCommand,
                $"locked {source.Command}"),
            registeredBeforeBuild ? 650 : 900);
    }

    private void ActivateLockedVanillaDirectFallback(EmotePlaybackInfo source, string redirectFailure)
    {
        if (!TryCreatePlayback(source, out var playback) || Objects.LocalPlayer is not { } player)
        {
            ReportPlaybackFailure(
                source.Command,
                $"Synastry Redirect was unavailable: {redirectFailure} The native timeline could not be started.");
            return;
        }

        ClearTemporaryAssignmentsInternal(false, false);
        waitingForAnimation = true;
        activationTime = Environment.TickCount64;
        movementTrackingStart = activationTime + 1200;
        hasMovementSample = false;
        movementFrames = 0;
        ReportPlaybackNotice(
            source.Command,
            $"Synastry Redirect was unavailable: {redirectFailure} " +
            "Using local-only direct playback; other players will not see this fallback.");
        ScheduleDirectPlayback($"locked {source.Command}", player.Address, playback, 50);
    }

    private InPlaceConversionResult TryConvertLockedEmote(
        string directory,
        string modName,
        string command)
    {
        if (IsRefreshingMods)
            return new InPlaceConversionResult(
                false,
                "The animation library is refreshing. Wait for it to finish and try again.");
        if (!emotePlaybackByCommand.TryGetValue(command, out var source))
            return new InPlaceConversionResult(false, $"Timeline information for {command} is unavailable.");
        if (!modSyncKeys.TryGetValue(directory, out var originalSyncKey) ||
            !modCatalogKeys.TryGetValue(directory, out var originalFingerprint))
            return new InPlaceConversionResult(
                false,
                "The mod's original identity is not indexed yet. Refresh the library and try again.");

        var sourceLoop = source.Timelines.Any(timeline => timeline.IsPersistentLoop);
        var family = ClassifyCarrierFamily(command, source, sourceLoop);
        var candidates = BuildCarrierCandidates(source, false);

        if (candidates.Count == 0)
        {
            var reason = CarrierCatalog.MissingMessage(family, sourceLoop);
            if (!inPlaceEmoteConverter.IsConverted(penumbra.GetModRoot(), directory))
                return new InPlaceConversionResult(false, reason);
        }

        return inPlaceEmoteConverter.Convert(
            penumbra.GetModRoot(),
            directory,
            modName,
            originalSyncKey,
            originalFingerprint,
            source.EmoteId,
            source.Command,
            source.Timelines.Select(timeline => new EmoteConversionTimeline(
                timeline.Slot,
                timeline.Key,
                timeline.IsPersistentLoop)).ToList(),
            candidates);
    }

    private List<EmoteConversionCandidate> BuildCarrierCandidates(
        EmotePlaybackInfo source,
        bool standaloneVanillaRedirect)
    {
        var sourceLoop = source.Timelines.Any(timeline => timeline.IsPersistentLoop);
        var family = ClassifyCarrierFamily(source.Command, source, sourceLoop);
        // Emotes outside the curated families would otherwise have no carrier at all and fall
        // back to local-only direct play. They use the same generic carriers as a locked vanilla emote.
        var useGenericCarriers = standaloneVanillaRedirect || family == CarrierFamily.None;
        var familyCount = CarrierCatalog.For(family, sourceLoop, false).Count;
        var rankByCommand = CarrierCatalog.For(family, sourceLoop, useGenericCarriers)
            .Select((carrier, rank) => (carrier.Command, rank))
            .ToDictionary(item => item.Command, item => item.rank, StringComparer.OrdinalIgnoreCase);
        var sourceSlots = source.Timelines.Select(timeline => timeline.Slot).ToHashSet();
        var sourceHasIntro = HasIntroAnimation(source);
        return emotePlaybackById.Values
            .Where(candidate =>
                candidate.EmoteId != source.EmoteId &&
                rankByCommand.ContainsKey(candidate.Command) &&
                IsEmoteUnlocked(candidate.EmoteId) &&
                IsSafeCarrierEmote(candidate.EmoteId) &&
                candidate.Timelines.Any(timeline => timeline.IsPersistentLoop) == sourceLoop &&
                candidate.Timelines.Any(timeline => sourceSlots.Contains(timeline.Slot)))
            // The source's own family first, then carriers without an intro of their own (it would
            // play before a mod that has none), then the most commonly owned.
            .OrderBy(candidate => rankByCommand[candidate.Command] >= familyCount)
            .ThenBy(candidate => !sourceHasIntro && HasIntroAnimation(candidate))
            .ThenBy(candidate => rankByCommand[candidate.Command])
            .ThenByDescending(candidate =>
                sourceSlots.SetEquals(candidate.Timelines.Select(timeline => timeline.Slot)))
            .ThenBy(candidate => Math.Abs(candidate.Timelines.Count - source.Timelines.Count))
            .ThenBy(candidate => candidate.EmoteId)
            .Select(candidate => new EmoteConversionCandidate(
                candidate.EmoteId,
                candidate.Command,
                candidate.Timelines.Select(timeline => new EmoteConversionTimeline(
                    timeline.Slot,
                    timeline.Key,
                    timeline.IsPersistentLoop)).ToList()))
            .ToList();
    }

    private readonly Dictionary<string, bool> introAnimationByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the emote's intro timeline (slot 1) has a vanilla body animation.</summary>
    private bool HasIntroAnimation(EmotePlaybackInfo info)
    {
        var intro = info.Timelines.FirstOrDefault(timeline => timeline.Slot == 1);
        if (intro is null) return false;
        if (!introAnimationByKey.TryGetValue(intro.Key, out var exists))
        {
            try { exists = DataManager.FileExists($"chara/human/c0101/animation/a0001/bt_common/{intro.Key}.pap"); }
            catch { exists = false; }
            introAnimationByKey[intro.Key] = exists;
        }
        return exists;
    }

    private static CarrierFamily ClassifyCarrierFamily(
        string sourceCommand,
        EmotePlaybackInfo source,
        bool sourceLoop)
    {
        if (sourceCommand.Equals("/dote", StringComparison.OrdinalIgnoreCase))
            return CarrierFamily.Dote;
        if (!sourceLoop)
            return CarrierFamily.OneShot;
        if (source.Timelines.Any(timeline =>
                timeline.Key.StartsWith("emote/dance", StringComparison.OrdinalIgnoreCase)) ||
            sourceCommand.Equals("/songbird", StringComparison.OrdinalIgnoreCase))
            return CarrierFamily.LoopingDance;
        if (PropLoopCommands.Contains(sourceCommand))
            return CarrierFamily.PropLoop;
        if (!GroundLoopCommands.Contains(sourceCommand) &&
            source.Timelines.Any(timeline =>
                timeline.Key.StartsWith("emote/loop_emot", StringComparison.OrdinalIgnoreCase)))
            return CarrierFamily.StandingLoop;
        return CarrierFamily.None;
    }

    private static bool IsSafeCarrierEmote(uint emoteId)
    {
        var row = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRow(emoteId);
        if (row is not { } emote ||
            emote.EmoteCategory.RowId is not (1 or 2) ||
            emote.EmoteMode.RowId is 1 or 2 ||
            emote.DrawsWeapon ||
            emote.RowId is 90 or 218 or 219 or 243 or 244 or 253)
            return false;
        foreach (var timelineReference in emote.ActionTimeline)
        {
            if (timelineReference.RowId == 0 || timelineReference.ValueNullable is not { } timeline) continue;
            if (timeline.LoadType == 1 || timeline.ActionTimelineIDMode == 2) return false;
        }
        return true;
    }

    private IReadOnlyDictionary<string, List<string>> GetActivationSelections(string directory)
    {
        EnsureDefaultOptionSelections(directory);
        return configuration.ModOptionSelections.TryGetValue(directory, out var saved)
            ? saved
            : new Dictionary<string, List<string>>();
    }

    /// <summary>
    /// Fills every option group the user has not chosen yet with the mod author's Penumbra
    /// defaults, so Synastry starts from the same setup Penumbra shows for a fresh install.
    /// Explicit choices are never replaced.
    /// </summary>
    public void EnsureDefaultOptionSelections(string directory)
    {
        if (optionDefaultsChecked.Contains(directory) ||
            !optionGroups.TryGetValue(directory, out var groups)) return;
        configuration.ModOptionSelections.TryGetValue(directory, out var saved);
        if (groups.All(group => saved?.ContainsKey(group.Name) == true))
        {
            optionDefaultsChecked.Add(directory);
            return;
        }

        // Checked once per indexed state; this runs every frame while a mod's options are open.
        optionDefaultsChecked.Add(directory);
        var root = penumbra.GetModRoot();
        if (string.IsNullOrWhiteSpace(root)) return;
        var defaults = AnimationManifestScanner.ReadOptionDefaults(Path.Combine(root, directory));
        if (defaults.Count == 0) return;

        saved ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seeded = 0;
        foreach (var group in groups)
        {
            if (saved.ContainsKey(group.Name)) continue;
            var groupDefaults = defaults.GetValueOrDefault(group.Name) ??
                                defaults.FirstOrDefault(pair => pair.Key.Trim()
                                    .Equals(group.Name.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
            if (groupDefaults is null) continue;
            // Keep the index's spelling so the option UI recognizes the choice; activation maps
            // it back to Penumbra's exact option names.
            var options = groupDefaults
                .Select(option => group.Options.FirstOrDefault(candidate =>
                    candidate.Trim().Equals(option.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(group.IsMultiSelect ? int.MaxValue : 1)
                .ToList();
            if (!group.IsMultiSelect && options.Count == 0) continue;
            saved[group.Name] = options;
            seeded++;
        }

        if (seeded == 0) return;
        configuration.ModOptionSelections[directory] = saved;
        SaveOrganization();
        Log.Debug("Applied Penumbra default options to {Count} unset group(s) in {ModDirectory}.", seeded, directory);
    }

    private void ReportPlaybackFailure(string modName, string reason)
    {
        Status = $"{modName} did not play: {reason}";
        Chat.PrintError($"[Synastry] {Status}");
        Log.Warning("Animation playback did not start for {ModName}: {Reason}", modName, reason);
    }

    private void ReportPlaybackNotice(string modName, string reason)
    {
        Status = $"{modName}: {reason}";
        Chat.Print($"[Synastry] {Status}");
        Log.Information("Animation playback notice for {ModName}: {Reason}", modName, reason);
    }

    private Task BroadcastLocalPlaybackAsync(string fingerprint, EmotePlayback playback, long startAt)
    {
        return EnsureLocalPresenceAsync().ContinueWith(task =>
        {
            task.GetAwaiter().GetResult();
            var remaining = (int)Math.Clamp(startAt - Environment.TickCount64, 100, 3000);
            return sync.BroadcastLocalAnimationAsync(fingerprint, playback.EmoteId, remaining);
        }, TaskScheduler.Default).Unwrap().ContinueWith(task =>
        {
            if (task.Exception is not null)
                Log.Debug(task.Exception.GetBaseException(),
                    "Nearby relay playback was unavailable; local direct playback will continue.");
        }, TaskScheduler.Default);
    }

    private static string NormalizeModKey(string modName) =>
        string.Join(' ', modName.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsReporterId(string value) =>
        value.Length == 32 && value.All(Uri.IsHexDigit);

    private string EffectiveRelayUrl() =>
        IsAllowedLocalRelay(configuration.LocalRelayUrl) ? configuration.LocalRelayUrl.Trim().TrimEnd('/') : PublicRelayUrl;

    private void SetLocalRelayOverride(string value)
    {
        var clean = value.Trim().TrimEnd('/');
        if (clean.Length > 0 && !IsAllowedLocalRelay(clean))
        {
            Status = "A development relay override must use http:// or https:// on localhost or a loopback IP.";
            return;
        }
        configuration.LocalRelayUrl = clean;
        configuration.Save(PluginInterface);
        RunSync(sync.DisconnectAsync(), clean.Length == 0
            ? "Development relay cleared; the next connection will use the public relay."
            : $"Development relay set to {clean}. Press Connect to use it.");
    }

    private static bool IsAllowedLocalRelay(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(uri.Host, out var address) &&
               System.Net.IPAddress.IsLoopback(address);
    }

    private IReadOnlyList<string> GetCatalogFingerprints() => modCatalogKeys
        .Where(pair => !IsModPrivate(pair.Key))
        .Select(pair => pair.Value)
        .Distinct()
        .ToList();

    private static string CatalogFingerprint(string modSyncKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modSyncKey)));

    private static string BuildModSyncKey(string modName, IEnumerable<string> gamePaths)
    {
        var identity = NormalizeModKey(modName) + "\n" + string.Join('\n', gamePaths.Select(path => path.ToLowerInvariant()));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        return $"{NormalizeModKey(modName)}:{hash}";
    }

    private static string OptionPoseKey(string directory, string group, string option) =>
        $"{directory}\u001f{group}\u001f{option}";

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
            var officialCommands = new[]
                {
                    textCommand.Value.Command.ToString(),
                    textCommand.Value.ShortCommand.ToString(),
                    textCommand.Value.Alias.ToString(),
                    textCommand.Value.ShortAlias.ToString()
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => value.StartsWith('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var command = officialCommands.FirstOrDefault();
            var name = row.Name.ExtractText();
            if (command is null || name.Length == 0) continue;
            AddEmoteName(row.RowId, name, command);
            var timelines = row.ActionTimeline
                .Select((timeline, slot) => (timeline, slot))
                .Where(item => item.timeline is { RowId: > 0, IsValid: true })
                .Select(item => new EmoteTimelineInfo(
                    item.slot,
                    item.timeline.RowId,
                    NormalizeTimelineKey(item.timeline.Value.Key.ExtractText()),
                    item.timeline.Value.Pause))
                .Where(timeline => timeline.Key.Length > 0)
                .DistinctBy(timeline =>
                    (timeline.RowId, timeline.Key.ToUpperInvariant(), timeline.IsPersistentLoop))
                .ToList();
            if (timelines.Count > 0)
            {
                var playback = new EmotePlaybackInfo(row.RowId, command, timelines);
                foreach (var officialCommand in officialCommands)
                    emotePlaybackByCommand.TryAdd(officialCommand, playback);
                emotePlaybackById.TryAdd(row.RowId, playback);
            }
        }
        Log.Information("Loaded {Count} emote names for automatic playback.", emoteCommandsByName.Count);
    }

    private bool TryCreatePlayback(string command, out EmotePlayback playback)
    {
        playback = null!;
        if (!emotePlaybackByCommand.TryGetValue(command, out var info)) return false;
        return TryCreatePlayback(info, out playback);
    }

    private bool IsEmoteUnlocked(uint emoteId)
    {
        var row = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRow(emoteId);
        return row is { } emote && UnlockState.IsEmoteUnlocked(emote);
    }

    private static bool TryCreatePlayback(EmotePlaybackInfo info, out EmotePlayback playback)
    {
        playback = null!;
        var main = info.Timelines.FirstOrDefault(timeline => timeline.Slot == 0) ??
                   info.Timelines.FirstOrDefault(timeline => timeline.IsPersistentLoop) ??
                   info.Timelines.FirstOrDefault();
        if (main is null || main.RowId > ushort.MaxValue) return false;
        var isLoop = main.IsPersistentLoop || info.Timelines.Any(timeline => timeline.IsPersistentLoop);
        var intro = isLoop ? info.Timelines.FirstOrDefault(timeline => timeline.Slot == 1) : null;
        playback = new EmotePlayback(
            info.EmoteId,
            (ushort)main.RowId,
            intro is { RowId: <= ushort.MaxValue } ? (ushort)intro.RowId : (ushort)0,
            isLoop);
        return true;
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

    private static string NormalizeTimelineKey(string value)
    {
        var normalized = value.Replace('\\', '/').Trim().Trim('/').ToLowerInvariant();
        if (normalized.EndsWith(".pap", StringComparison.Ordinal)) normalized = normalized[..^4];
        const string actionPrefix = "chara/action/";
        if (normalized.StartsWith(actionPrefix, StringComparison.Ordinal))
            normalized = normalized[actionPrefix.Length..];
        const string commonMarker = "bt_common/";
        var commonIndex = normalized.IndexOf(commonMarker, StringComparison.Ordinal);
        return commonIndex >= 0 ? normalized[(commonIndex + commonMarker.Length)..] : normalized;
    }

    private static void ExecuteCommand(string command)
    {
        var ui = UIModule.Instance();
        if (ui is null) return;
        using var text = new Utf8String(command);
        ui->ProcessChatBoxEntry(&text);
    }

    public void SyncLobbyEmotes()
    {
        if (!sync.IsInRoom)
        {
            Status = "Join a Synastry room before using lobby EmoteSync.";
            return;
        }

        var refreshed = RefreshLobbyEmotes();
        Status = refreshed == 0
            ? "No visible lobby animations were available to sync."
            : $"EmoteSync reset {refreshed} visible lobby animation(s).";
    }

    public void OpenSimpleHeelsTempOffset()
    {
        if (!SimpleHeelsAvailable)
        {
            Status = "Simple Heels is not installed or loaded.";
            return;
        }

        ExecuteCommand("/heels temp");
        Status = "Opened Simple Heels temporary offset.";
    }

    public void OpenSimpleHeelsLivePose()
    {
        if (!SimpleHeelsAvailable)
        {
            Status = "Simple Heels is not installed or loaded.";
            return;
        }

        ExecuteCommand("/heels livepose");
        Status = "Opened Simple Heels LivePose.";
    }

    public bool IsAnimationSpeedMatching => animationSpeedMatchTargetAddress != 0;
    public bool AnimationSpeedAvailable => animationSpeedController is not null;

    public int AnimationSpeedPercent
    {
        get
        {
            if (TryGetAnimationSpeedMatchTarget(out var target))
                return ReadAnimationSpeedPercent(target.Address);
            return (int)MathF.Round((animationSpeedOverride ?? 1f) * 100f);
        }
    }

    public string AnimationSpeedMatchButtonLabel
    {
        get
        {
            if (TryGetAnimationSpeedMatchTarget(out var matchedTarget))
                return $"Match {matchedTarget.Name.TextValue} ({ReadAnimationSpeedPercent(matchedTarget.Address)}%)";

            var target = Targets.SoftTarget ?? Targets.Target;
            return target is IPlayerCharacter player
                ? $"Match {player.Name.TextValue} ({ReadAnimationSpeedPercent(player.Address)}%)"
                : "Match target";
        }
    }

    public bool CanMatchAnimationSpeed => IsAnimationSpeedMatching ||
                                          (Targets.SoftTarget ?? Targets.Target) is IPlayerCharacter;

    public void SetAnimationSpeedPercent(int percent)
    {
        animationSpeedMatchTargetAddress = 0;
        animationSpeedMatchTargetName = "";
        percent = Math.Clamp(percent, -200, 200);
        if (percent == 100)
        {
            ResetAnimationSpeed();
            return;
        }

        var player = Objects.LocalPlayer;
        if (player is null) return;
        animationSpeedOverride = percent / 100f;
        animationSpeedPosition = player.Position;
        ApplyAnimationSpeed(player.Address, animationSpeedOverride.Value);
        Status = $"Animation speed set to {percent}%.";
    }

    public void ResetAnimationSpeed()
    {
        ClearAnimationSpeedState();
        if (Objects.LocalPlayer is { } player) ApplyAnimationSpeed(player.Address, 1f);
        Status = "Animation speed reset to 100%.";
    }

    private void ClearAnimationSpeedState()
    {
        animationSpeedOverride = null;
        animationSpeedPosition = null;
        animationSpeedMatchTargetAddress = 0;
        animationSpeedMatchTargetName = "";
    }

    public void ToggleAnimationSpeedMatch()
    {
        if (IsAnimationSpeedMatching)
        {
            var previousName = animationSpeedMatchTargetName;
            ResetAnimationSpeed();
            Status = string.IsNullOrWhiteSpace(previousName)
                ? "Animation speed target matching stopped."
                : $"Stopped matching {previousName}'s animation speed.";
            return;
        }

        var target = Targets.SoftTarget ?? Targets.Target;
        if (target is not IPlayerCharacter player)
        {
            Status = "Target another player to match their animation speed.";
            return;
        }

        animationSpeedOverride = null;
        animationSpeedPosition = null;
        animationSpeedMatchTargetAddress = player.Address;
        animationSpeedMatchTargetName = player.Name.TextValue;
        ApplyMatchedAnimationSpeed(player);
        Status = $"Matching {animationSpeedMatchTargetName}'s animation speed.";
    }

    private bool TryGetAnimationSpeedMatchTarget(out IPlayerCharacter target)
    {
        target = null!;
        if (animationSpeedMatchTargetAddress == 0) return false;
        target = Objects.OfType<IPlayerCharacter>()
            .FirstOrDefault(candidate => candidate.Address == animationSpeedMatchTargetAddress)!;
        return target is not null;
    }

    private static int ReadAnimationSpeedPercent(nint address)
    {
        var character = (Character*)address;
        return character is null
            ? 100
            : (int)MathF.Round(Math.Clamp(character->Timeline.OverallSpeed, -2f, 2f) * 100f);
    }

    private static void ApplyAnimationSpeed(nint address, float speed)
    {
        var character = (Character*)address;
        if (character is null) return;
        character->Timeline.OverallSpeed = Math.Clamp(speed, -2f, 2f);
    }

    private float? GetAnimationSpeedHookOverride()
    {
        if (animationSpeedMatchTargetAddress != 0)
        {
            if (!TryGetAnimationSpeedMatchTarget(out var target)) return null;
            var targetCharacter = (Character*)target.Address;
            return targetCharacter is null
                ? null
                : Math.Clamp(targetCharacter->Timeline.OverallSpeed, -2f, 2f);
        }

        return animationSpeedOverride;
    }

    private void ApplyMatchedAnimationSpeed(IPlayerCharacter target)
    {
        if (Objects.LocalPlayer is not { } player) return;
        var targetCharacter = (Character*)target.Address;
        if (targetCharacter is null) return;
        ApplyAnimationSpeed(player.Address, targetCharacter->Timeline.OverallSpeed);
    }

    private void UpdateAnimationSpeed()
    {
        var player = Objects.LocalPlayer;
        if (player is null)
        {
            animationSpeedOverride = null;
            animationSpeedPosition = null;
            animationSpeedMatchTargetAddress = 0;
            animationSpeedMatchTargetName = "";
            return;
        }

        if (animationSpeedMatchTargetAddress != 0)
        {
            if (TryGetAnimationSpeedMatchTarget(out var target))
            {
                ApplyMatchedAnimationSpeed(target);
                return;
            }

            var lostName = animationSpeedMatchTargetName;
            animationSpeedMatchTargetAddress = 0;
            animationSpeedMatchTargetName = "";
            ApplyAnimationSpeed(player.Address, 1f);
            Status = string.IsNullOrWhiteSpace(lostName)
                ? "Animation speed matching stopped because the target was lost."
                : $"Animation speed matching stopped because {lostName} was lost.";
            return;
        }

        if (!animationSpeedOverride.HasValue) return;
        if (!animationSpeedPosition.HasValue ||
            System.Numerics.Vector3.DistanceSquared(animationSpeedPosition.Value, player.Position) > 0.01f)
        {
            ResetAnimationSpeed();
            Status = "Animation speed reset after movement.";
            return;
        }

        ApplyAnimationSpeed(player.Address, animationSpeedOverride.Value);
    }

    private int RefreshLobbyEmotes()
    {
        var room = sync.Room;
        var localPlayer = Objects.LocalPlayer;
        if (room is null || localPlayer is null) return 0;

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
        return refreshed;
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

    public void ClearTemporaryAssignments() => ClearTemporaryAssignmentsInternal(true);

    private string NowPlayingLabel(string directory, PoseTarget? pose, string? command)
    {
        if (pose is not null) return PoseDisplayName(pose);
        if (command is null) return "";
        var emote = modEmotes.GetValueOrDefault(directory)?.FirstOrDefault(candidate =>
            candidate.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
        return emote?.Name ?? command;
    }

    private void ClearTemporaryAssignmentsInternal(bool cancelGroupReady, bool clearRemote = true)
    {
        NowPlaying = null;
        if (activeDirectPlayback is { } direct)
        {
            ActionTimelinePlayback.Stop(direct.ActorAddress, direct.OriginalBaseOverride);
            activeDirectPlayback = null;
        }
        if (clearRemote)
        {
            foreach (var remote in activeRemotePlaybacks)
                if (remote.IsLoop && Objects.Any(candidate => candidate.Address == remote.ActorAddress))
                    ActionTimelinePlayback.Stop(remote.ActorAddress, remote.OriginalBaseOverride);
            activeRemotePlaybacks.Clear();
            pendingRemotePlaybacks.Clear();
        }
        foreach (var assignment in configuration.ActiveAssignments.ToList())
        {
            if (!clearRemote && remoteAssignments.Contains(assignment)) continue;
            if (penumbra.Remove(assignment)) configuration.ActiveAssignments.Remove(assignment);
        }
        if (clearRemote) remoteAssignments.Clear();

        configuration.Save(PluginInterface);
        waitingForAnimation = false;
        pendingCommand = null;
        pendingDirectPlayback = null;
        pendingCarrierPlayback = null;
        pendingPose = null;
        pendingNearbyBroadcast = null;
        pendingSelectionModKey = null;
        lobbyEmoteRefreshTime = 0;
        cyclingPose = null;
        hasMovementSample = false;
        movementFrames = 0;
        if (cancelGroupReady && sync.IsInRoom)
        {
            preparedModKey = null;
            preparedCatalogFingerprint = null;
            preparedCommand = null;
            preparedPose = null;
            preparedDirectPlayback = null;
            preparedCarrierPlayback = null;
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
        var localPlayer = Objects.LocalPlayer;
        var player = (Character*)(localPlayer?.Address ?? 0);
        if (target is null || localPlayer is null || player is null)
        {
            Status = "Select a nearby target before aligning.";
            return;
        }

        var distance = System.Numerics.Vector3.Distance(localPlayer.Position, target.Position);
        var isLoopingEmote = player->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop;
        if (isLoopingEmote)
        {
            if (distance > MaxMidEmoteAlignDistance)
            {
                Status = $"Mid-emote alignment is limited to {MaxMidEmoteAlignDistance:F1} yalms.";
                return;
            }

            alignmentTargetAddress = target.Address;
            alignmentFramesRemaining = 12;
            alignmentStableFrames = 0;
            SuppressMovementCleanupForAlignment();
            ApplyAlignment(target.Position, target.Rotation);
            Status = "Aligning to the nearby target without interrupting the emote...";
            return;
        }

        if (player->Mode != CharacterModes.Normal)
        {
            Status = "Alignment is available while standing normally or performing a looping emote.";
            return;
        }
        if (distance > MaxAlignDistance)
        {
            Status = $"Move within {MaxAlignDistance:F0} yalms of the target before aligning.";
            return;
        }

        var position = target.Position;
        var rotation = target.Rotation;
        var targetAddress = target.Address;
        Status = "Aligning position and facing direction...";
        movement.WalkTo(position, () =>
        {
            alignmentTargetAddress = targetAddress;
            alignmentFramesRemaining = 12;
            alignmentStableFrames = 0;
            SuppressMovementCleanupForAlignment();
            ApplyAlignment(position, rotation);
        });
    }

    private void SuppressMovementCleanupForAlignment()
    {
        hasMovementSample = false;
        movementFrames = 0;
        movementTrackingStart = Math.Max(movementTrackingStart, Environment.TickCount64 + 250);
    }

    private void OnUpdate(IFramework _)
    {
        ProcessTypedEmoteRequest();
        ProcessAnimationSuggestionNotifications();
        UpdateLocalPresence();
        ProcessModRefresh();
        ProcessReceivedRoleLabels();
        ProcessReceivedCommunityRoleLabels();
        ProcessTransferOffers();
        if (roleSyncPending) StartRoleLabelSync();
        if (communityRoleSyncPending) StartCommunityRoleLabelSync();
        UpdateAlignment();
        contactAlign.Tick(configuration.AutomaticLineUp, IsSimpleHeelsLoadedCached(), IsSynastryAnimationPlaying());
        if (bodySyncPending) StartBodyProfileSync();
        testPartner.Tick();
        MeasureTestPartner();
        UpdateTestPartnerRoom();
        UpdateContactIk();
        preloader.Tick();
        UpdateAnimationSpeed();
        ProcessCompletedDownloads();
        ProcessAddedMod();
        ProcessFreeUseDirectives();
        ProcessSyncPlaySignals();
        ProcessLocalAnimationSignals();
        UpdateRemotePlaybacks();
        var animationStarted = false;
        var standingUp = StandUpFirst();
        if (!standingUp && pendingNearbyBroadcast is { } nearby)
        {
            pendingNearbyBroadcast = null;
            // Only announce a start that is still going to happen.
            if (pendingDirectPlayback is not null || pendingCarrierPlayback is not null)
                AnnounceToNearby(nearby.Fingerprint, nearby.Playback, pendingCarrierPlayback?.NextAttempt ?? pendingCommandTime);
        }
        if (!standingUp)
        {
        if (pendingPose is not null && Environment.TickCount64 >= pendingCommandTime)
        {
            var pose = pendingPose;
            pendingPose = null;
            ExecutePose(pose);
            animationStarted = true;
        }
        if (pendingCommand is not null && Environment.TickCount64 >= pendingCommandTime)
        {
            var command = pendingCommand;
            pendingCommand = null;
            ExecuteCommand(command);
            animationStarted = true;
        }
        if (pendingCarrierPlayback is not null &&
            Environment.TickCount64 >= pendingCarrierPlayback.NextAttempt)
        {
            var pending = pendingCarrierPlayback;
            if (TryExecuteCarrierEmote(pending.Playback.EmoteId))
            {
                pendingCarrierPlayback = null;
                animationStarted = true;
                Status =
                    $"Started {pending.Playback.ModName} through permanent carrier {pending.Playback.Command}.";
            }
            else if (Environment.TickCount64 < pending.Deadline)
            {
                pendingCarrierPlayback = pending with
                {
                    NextAttempt = Environment.TickCount64 + 50
                };
            }
            else
            {
                pendingCarrierPlayback = null;
                pendingSelectionModKey = null;
                ReportPlaybackFailure(
                    pending.Playback.ModName,
                    $"The game refused carrier {pending.Playback.Command}. " +
                    "Stop your current action, leave restricted character states, and try again.");
            }
        }
        if (pendingDirectPlayback is not null && Environment.TickCount64 >= pendingCommandTime)
        {
            var pending = pendingDirectPlayback;
            pendingDirectPlayback = null;
            if (ActionTimelinePlayback.Start(
                    pending.ActorAddress,
                    pending.Playback,
                    out var originalBaseOverride))
            {
                if (pending.Playback.IsLoop)
                    activeDirectPlayback = new ActiveDirectPlayback(pending.ActorAddress, originalBaseOverride);
                animationStarted = true;
                Status = $"Started {pending.ModName} with its native action timeline.";
            }
            else
            {
                Status = $"Could not start {pending.ModName}'s action timeline.";
            }
        }
        }
        if (animationStarted)
        {
            // Group starts can arrive long after activation, and chair sits walk into the seat.
            // Measure movement from the real start so neither can cancel the override.
            movementTrackingStart = Environment.TickCount64 + 2000;
            hasMovementSample = false;
            movementFrames = 0;
            // The same goes for line-up and bone bending: learn the loop from the real start.
            playingSince = Environment.TickCount64;
            playingSignature = null;
            playingStarted = true;
        }
        if (animationStarted && pendingSelectionModKey is not null)
        {
            mainWindow.NotifyAnimationStarted();
            ClearRemoteSelections(pendingSelectionModKey);
            pendingSelectionModKey = null;
            if (configuration.AutomaticEmoteSync)
            {
                lobbyEmoteRefreshTime = Environment.TickCount64 + LobbyEmoteRefreshDelayMs;
                Status = "Animation started; lobby EmoteSync will run in 6 seconds.";
            }
            else
            {
                lobbyEmoteRefreshTime = 0;
                Status = "Animation started; automatic lobby EmoteSync is disabled.";
            }
        }
        if (lobbyEmoteRefreshTime > 0 && Environment.TickCount64 >= lobbyEmoteRefreshTime)
        {
            lobbyEmoteRefreshTime = 0;
            var refreshed = RefreshLobbyEmotes();
            Status = refreshed == 0
                ? "Lobby EmoteSync ran; no visible lobby animations were available to reset."
                : $"Lobby EmoteSync reset {refreshed} visible lobby animation(s).";
            Log.Information("Ran lobby EmoteSync after {DelayMilliseconds} ms for {ActorCount} visible actors.",
                LobbyEmoteRefreshDelayMs, refreshed);
        }
        UpdatePoseCycling();
        if (!waitingForAnimation) return;
        UpdateMovementCleanup();
    }

    private static bool TryExecuteCarrierEmote(uint emoteId)
    {
        if (emoteId > ushort.MaxValue) return false;
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteManager.Instance();
        if (manager is null) return false;
        var targetId = Objects.LocalPlayer is { } player
            ? NoireLib.Helpers.GameObjectHelper.GetTargetId(player)
            : NoireLib.Helpers.EmoteHelper.NoEmoteTargetId;
        var option = NoireLib.Helpers.EmoteHelper.EmoteOptionFor(targetId);
        return manager->ExecuteEmote((ushort)emoteId, &option);
    }

    private void ProcessTransferOffers()
    {
        while (incomingTransferOffers.TryDequeue(out var offer))
        {
            var alreadyInstalled = offer.CatalogFingerprint.Length == 64 &&
                modCatalogKeys.Values.Contains(offer.CatalogFingerprint, StringComparer.OrdinalIgnoreCase);
            if (!alreadyInstalled)
            {
                transferOffers.Enqueue(offer);
                continue;
            }

            RunSync(sync.CompleteModTransferAsync(offer.TransferId),
                $"Already have {offer.ModName}; marked the transfer as received.");
        }
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
            var pending = new PendingPenumbraInstall(
                result.Offer, result.Path, ReceivedModFolder);
            pendingPenumbraInstalls.Add(pending);
            var installation = penumbra.InstallMod(result.Path);
            if (!installation.Success)
            {
                pendingPenumbraInstalls.Remove(pending);
                TryDeleteManagedTransferPackage(result.Path, "after Penumbra rejected the install request");
                Status = $"Downloaded {result.Offer.ModName}, but installation failed: {installation.Error}.";
                continue;
            }
            RunSync(sync.CompleteModTransferAsync(result.Offer.TransferId),
                $"Downloaded {result.Offer.ModName}; Penumbra is installing it.");
        }
    }

    private void CompletePendingPenumbraInstall(string modName, string catalogFingerprint)
    {
        var matches = pendingPenumbraInstalls
            .Where(pending =>
                pending.Offer.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase) &&
                pending.Offer.CatalogFingerprint.Equals(catalogFingerprint, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return;
        if (matches.Count > 1)
        {
            // ModAdded does not include the source package path. When duplicate installs of
            // the same animation are pending, deleting either file could race the other import.
            // Leave both for the conservative startup sweep instead of guessing.
            Log.Warning(
                "Could not safely correlate Penumbra's completed install for {ModName}; {MatchCount} identical transfer packages are pending.",
                modName, matches.Count);
            return;
        }

        var completed = matches[0];
        pendingPenumbraInstalls.Remove(completed);
        TryDeleteManagedTransferPackage(completed.Path, "after Penumbra confirmed the transferred mod was added");
    }

    private static void SweepStaleTransferPackages()
    {
        try
        {
            var cutoff = DateTime.UtcNow - StaleTransferPackageAge;
            foreach (var path in Directory.EnumerateFiles(
                         Path.GetTempPath(), "EmoteLink-*.pmp", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (!IsManagedTransferPackage(path) || File.GetLastWriteTimeUtc(path) > cutoff) continue;
                    TryDeleteManagedTransferPackage(path, "during startup stale-package cleanup");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not inspect stale transfer package {PackagePath}.", path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not sweep stale Synastry transfer packages.");
        }
    }

    private static bool IsManagedTransferPackage(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(Path.GetExtension(fullPath), ".pmp", StringComparison.OrdinalIgnoreCase))
                return false;

            const string prefix = "EmoteLink-";
            var stem = Path.GetFileNameWithoutExtension(fullPath);
            return stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   Guid.TryParseExact(stem[prefix.Length..], "N", out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteManagedTransferPackage(string path, string reason)
    {
        if (!IsManagedTransferPackage(path))
        {
            Log.Warning("Refused to delete an unrecognized transfer package path {PackagePath}.", path);
            return false;
        }

        try
        {
            File.Delete(path);
            Log.Debug("Deleted transfer package {PackagePath} {Reason}.", path, reason);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete transfer package {PackagePath} {Reason}.", path, reason);
            return false;
        }
    }

    private void RememberOptionSelection(OptionSelectionDto selection)
    {
        remoteOptionSelections[selection.MemberName + "\n" + selection.ModKey + "\n" + selection.Group] = selection;
        InvalidateLibraryOrder();
        var activatedTrigger = selection.Group.Equals("$detected-trigger", StringComparison.OrdinalIgnoreCase)
            ? selection.Option
            : "";
        QueueAnimationSuggestion(selection.MemberName, selection.ModKey, activatedTrigger,
            notify: activatedTrigger.Length > 0);
    }

    private void QueueAnimationSuggestion(
        string memberName,
        string modKey,
        string activatedTrigger = "",
        bool notify = false)
    {
        var suggestionKey = SuggestionKey(memberName, modKey);
        if (notify) ignoredAnimationSuggestions.TryRemove(suggestionKey, out _);
        else if (ignoredAnimationSuggestions.ContainsKey(suggestionKey)) return;
        var mod = Mods.FirstOrDefault(candidate => modSyncKeys.TryGetValue(candidate.Directory, out var key) &&
            key.Equals(modKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(mod.Directory)) return;
        if (activatedTrigger.Length == 0 &&
            activeAnimationSuggestions.TryGetValue(suggestionKey, out var existing))
            activatedTrigger = existing.ActivatedTrigger;
        var suggestion = new AnimationSuggestion(memberName, modKey, mod.Directory, mod.Name, activatedTrigger);
        activeAnimationSuggestions[suggestionKey] = suggestion;
        if (notify && !sync.IsFreeUse && !modKey.Equals(preparedModKey, StringComparison.OrdinalIgnoreCase))
            incomingAnimationSuggestions.Enqueue(suggestion);
        Log.Information("Marked animation suggestion from {MemberName}: {ModName}.", memberName, mod.Name);
    }

    private void ProcessAnimationSuggestionNotifications()
    {
        while (incomingAnimationSuggestions.TryDequeue(out var suggestion))
        {
            mainWindow.ShowAnimationSuggestion(suggestion);
            SurfacePromptFromMiniPlayer();
        }
    }

    public bool IsAnimationSuggestionActive(AnimationSuggestion suggestion) =>
        sync.IsInRoom && activeAnimationSuggestions.ContainsKey(
            SuggestionKey(suggestion.SuggestedBy, suggestion.ModKey));

    public void IgnoreAnimationSuggestion(AnimationSuggestion suggestion)
    {
        ignoredAnimationSuggestions[SuggestionKey(suggestion.SuggestedBy, suggestion.ModKey)] = 0;
        ClearRemoteSelections(suggestion.SuggestedBy, suggestion.ModKey);
        if (!sync.IsInRoom) return;
        RunSync(sync.DeclineAnimationSuggestionAsync(suggestion.ModKey, suggestion.SuggestedBy),
            $"Ignored {suggestion.SuggestedBy}'s animation suggestion.");
    }

    private void OnAnimationSuggestionDeclined(AnimationSuggestionDeclinedDto decline)
    {
        if (decline.SuggestedBy.Equals(CurrentCharacterName(), StringComparison.OrdinalIgnoreCase))
            Status = $"{decline.DeclinedBy} declined your animation suggestion.";
    }

    private static string SuggestionKey(string memberName, string modKey) => memberName + "\n" + modKey;

    private void OnSyncStateChanged()
    {
        InvalidateLibraryOrder();
        if (!sync.IsConnected)
        {
            communityRelayConnected = false;
            communityRoleSyncPending = false;
        }
        else if (!communityRelayConnected)
        {
            communityRelayConnected = true;
            communityRoleSyncPending = true;
        }
        var roomCode = sync.Room?.RoomCode;
        if (roomCode is null)
        {
            remoteSelectionRoom = null;
            remoteOptionSelections.Clear();
            activeAnimationSuggestions.Clear();
            ignoredAnimationSuggestions.Clear();
            while (incomingAnimationSuggestions.TryDequeue(out _)) { }
            remoteReadyModKeys.Clear();
            roleSyncPending = false;
            while (receivedRoleLabels.TryDequeue(out _)) { }
            while (freeUseDirectives.TryDequeue(out _)) { }
            return;
        }
        var roomChanged = !roomCode.Equals(remoteSelectionRoom, StringComparison.OrdinalIgnoreCase);
        if (roomChanged)
        {
            remoteSelectionRoom = roomCode;
            remoteOptionSelections.Clear();
            activeAnimationSuggestions.Clear();
            ignoredAnimationSuggestions.Clear();
            while (incomingAnimationSuggestions.TryDequeue(out _)) { }
            remoteReadyModKeys.Clear();
            roleSyncPending = true;
            bodySyncPending = true;
            sharedBodies.Clear();
            contactMaps.Forget();
        }
        var room = sync.Room!;
        var currentMembers = room.Members.Select(member => member.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in remoteOptionSelections.Where(pair => !currentMembers.Contains(pair.Value.MemberName)))
            remoteOptionSelections.TryRemove(pair.Key, out _);
        foreach (var pair in activeAnimationSuggestions.Where(pair => !currentMembers.Contains(pair.Value.SuggestedBy)))
            activeAnimationSuggestions.TryRemove(pair.Key, out _);

        var currentReadyModKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in room.Members.Where(member =>
                     !sync.IsCurrentMember(member.ConnectionId) && member.Ready &&
                     !string.IsNullOrWhiteSpace(member.ModKey)))
            currentReadyModKeys[member.DisplayName] = member.ModKey;
        foreach (var previous in remoteReadyModKeys)
        {
            if (currentReadyModKeys.TryGetValue(previous.Key, out var currentModKey) &&
                currentModKey.Equals(previous.Value, StringComparison.OrdinalIgnoreCase)) continue;
            ClearRemoteSelections(previous.Key, previous.Value);
            remoteReadyModKeys.TryRemove(previous.Key, out _);
        }
        foreach (var member in room.Members.Where(member =>
                     !sync.IsCurrentMember(member.ConnectionId) && member.Ready && !string.IsNullOrWhiteSpace(member.ModKey)))
        {
            remoteReadyModKeys[member.DisplayName] = member.ModKey;
            QueueAnimationSuggestion(member.DisplayName, member.ModKey);
        }
        if (!roomChanged) return;
        _ = sync.GetOptionSelectionsAsync().ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            var activeRoom = sync.Room;
            if (activeRoom is null || !activeRoom.RoomCode.Equals(roomCode, StringComparison.OrdinalIgnoreCase)) return;
            var activeSuggestions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in activeRoom.Members.Where(member =>
                         !sync.IsCurrentMember(member.ConnectionId) && member.Ready &&
                         !string.IsNullOrWhiteSpace(member.ModKey)))
                activeSuggestions[member.DisplayName] = member.ModKey;
            foreach (var selection in task.Result)
                if (activeSuggestions.TryGetValue(selection.MemberName, out var activeModKey) &&
                    activeModKey.Equals(selection.ModKey, StringComparison.OrdinalIgnoreCase))
                    RememberOptionSelection(selection);
        }, TaskScheduler.Default);
    }

    private void StartRoleLabelSync()
    {
        roleSyncPending = false;
        if (!sync.IsInRoom) return;
        foreach (var (key, label) in configuration.OptionNotes.ToList())
        {
            var parts = key.Split('\n', 3);
            if (parts.Length != 3 || !IsSynchronizedRoleGroup(parts[1]) || IsModPrivate(parts[0]) ||
                !modSyncKeys.TryGetValue(parts[0], out var modKey)) continue;
            _ = sync.SetRoleLabelAsync(modKey, parts[1], parts[2], label);
        }
        _ = sync.GetRoleLabelsAsync().ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            foreach (var label in task.Result) receivedRoleLabels.Enqueue(label);
        }, TaskScheduler.Default);
    }

    private void ProcessReceivedRoleLabels()
    {
        var changed = false;
        while (receivedRoleLabels.TryDequeue(out var shared))
        {
            if (!sync.IsInRoom || string.IsNullOrWhiteSpace(shared.Label) ||
                !IsSynchronizedRoleGroup(shared.Group)) continue;
            var mod = Mods.FirstOrDefault(candidate => modSyncKeys.TryGetValue(candidate.Directory, out var modKey) &&
                modKey.Equals(shared.ModKey, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(mod.Directory)) continue;
            var key = OptionNoteKey(mod.Directory, shared.Group, shared.Option);
            if (configuration.OptionNotes.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing))
                continue;
            configuration.OptionNotes[key] = shared.Label.Trim()[..Math.Min(20, shared.Label.Trim().Length)];
            // Someone else's tag: community tags may replace it, and it is never sent to the
            // relay as this player's vote. Editing it makes it theirs.
            configuration.CommunityRoleKeys.Add(key);
            changed = true;
            Log.Information("Received role label for {ModName} from {MemberName}.", mod.Name, shared.MemberName);
        }
        if (changed) configuration.Save(PluginInterface);
    }

    private void StartCommunityRoleLabelSync()
    {
        communityRoleSyncPending = false;
        if (!sync.IsConnected) return;
        foreach (var (key, label) in configuration.OptionNotes.ToList())
        {
            var parts = key.Split('\n', 3);
            if (parts.Length != 3 || !IsSynchronizedRoleGroup(parts[1]) || IsModPrivate(parts[0]) ||
                !modCatalogKeys.TryGetValue(parts[0], out var fingerprint)) continue;
            var metadata = GetCommunityRoleMetadata(parts[0], parts[1], parts[2]);
            _ = sync.RegisterCommunityRoleMetadataAsync(
                fingerprint, parts[1], parts[2], metadata.ModName, metadata.AnimationName);
            if (configuration.CommunityRoleKeys.Contains(key)) continue;
            _ = sync.SubmitCommunityRoleLabelAsync(
                fingerprint, parts[1], parts[2], label, configuration.CommunityReporterId,
                metadata.ModName, metadata.AnimationName);
        }
        // The relay answers at most 1,000 fingerprints per request, matching DownloadCommunityTags.
        foreach (var batch in modCatalogKeys
                     .Where(pair => !IsModPrivate(pair.Key))
                     .Select(pair => pair.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Chunk(1000))
            _ = sync.GetCommunityRoleLabelsAsync(batch).ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) return;
                foreach (var label in task.Result) receivedCommunityRoleLabels.Enqueue(label);
            }, TaskScheduler.Default);
    }

    private void ProcessReceivedCommunityRoleLabels()
    {
        var changed = false;
        while (receivedCommunityRoleLabels.TryDequeue(out var shared))
        {
            if (!sync.IsConnected || !IsSynchronizedRoleGroup(shared.Group)) continue;
            var mod = Mods.FirstOrDefault(candidate => modCatalogKeys.TryGetValue(candidate.Directory, out var fingerprint) &&
                fingerprint.Equals(shared.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(mod.Directory) || IsModPrivate(mod.Directory)) continue;
            var key = OptionNoteKey(mod.Directory, shared.Group, shared.Option);
            var label = shared.Label.Trim()[..Math.Min(20, shared.Label.Trim().Length)];

            // A moderator's decision overrides everyone once, the player's own tag included.
            // Afterwards the player may change it again; only a newer moderation re-applies.
            var newModeration = shared.Revision > 0 &&
                shared.Revision > configuration.AppliedTagModerations.GetValueOrDefault(key);
            if (newModeration)
            {
                configuration.AppliedTagModerations[key] = shared.Revision;
                if (label.Length == 0)
                {
                    configuration.OptionNotes.Remove(key);
                    configuration.CommunityRoleKeys.Remove(key);
                }
                else
                {
                    configuration.OptionNotes[key] = label;
                    configuration.CommunityRoleKeys.Add(key);
                }
                changed = true;
                Log.Information("Applied a moderated tag for {ModName}.", mod.Name);
                continue;
            }

            if (label.Length == 0) continue;
            var isCommunityManaged = configuration.CommunityRoleKeys.Contains(key);
            if (!isCommunityManaged && configuration.OptionNotes.TryGetValue(key, out var existing) &&
                !string.IsNullOrWhiteSpace(existing)) continue;
            configuration.OptionNotes[key] = label;
            configuration.CommunityRoleKeys.Add(key);
            var metadata = GetCommunityRoleMetadata(mod.Directory, shared.Group, shared.Option);
            _ = sync.RegisterCommunityRoleMetadataAsync(
                shared.Fingerprint, shared.Group, shared.Option, metadata.ModName, metadata.AnimationName);
            changed = true;
        }
        if (changed) configuration.Save(PluginInterface);
    }

    private static bool IsSynchronizedRoleGroup(string group) =>
        group.Equals("$detected-pose", StringComparison.OrdinalIgnoreCase) ||
        group.Equals("$detected-emote", StringComparison.OrdinalIgnoreCase);

    private (string ModName, string AnimationName) GetCommunityRoleMetadata(
        string directory, string group, string option)
    {
        var modName = modsByDirectory.TryGetValue(directory, out var mod) ? mod.Name : directory;
        if (group.Equals("$detected-pose", StringComparison.OrdinalIgnoreCase))
        {
            var pose = GetDetectedPoses(directory).FirstOrDefault(candidate =>
                $"{candidate.Kind}:{candidate.Index}".Equals(option, StringComparison.OrdinalIgnoreCase));
            if (pose is not null)
            {
                var animationName = pose.Kind switch
                {
                    PoseKind.Sit => $"Chair Sit {pose.Index}",
                    PoseKind.GroundSit => $"Ground Sit {pose.Index}",
                    PoseKind.Doze => $"Doze {pose.Index}",
                    _ => $"Idle {pose.Index}"
                };
                return (modName, animationName);
            }
        }
        else if (group.Equals("$detected-emote", StringComparison.OrdinalIgnoreCase) &&
                 uint.TryParse(option, out var emoteId))
        {
            var emote = GetDetectedEmotes(directory).FirstOrDefault(candidate => candidate.Id == emoteId);
            if (emote is not null) return (modName, $"{emote.Name} (ID {emote.Id})");
        }
        return (modName, option);
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

    private void UpdateLocalPresence()
    {
        if (!sync.IsConnected)
        {
            localPresenceScope = "";
            localPresenceUpdatePending = false;
            nextLocalPresenceAttempt = 0;
            return;
        }
        var player = Objects.LocalPlayer;
        if (player is null) return;
        var scope = $"{player.CurrentWorld.RowId}:{ClientState.TerritoryType}";
        if (scope.Equals(localPresenceScope, StringComparison.Ordinal) || localPresenceUpdatePending ||
            Environment.TickCount64 < nextLocalPresenceAttempt) return;
        localPresenceUpdatePending = true;
        nextLocalPresenceAttempt = Environment.TickCount64 + 30_000;
        _ = EnsureLocalPresenceAsync().ContinueWith(task =>
        {
            localPresenceUpdatePending = false;
            if (task.Exception is not null)
                Log.Debug(task.Exception.GetBaseException(), "Could not update nearby relay presence.");
        }, TaskScheduler.Default);
    }

    private Task EnsureLocalPresenceAsync()
    {
        var player = Objects.LocalPlayer;
        if (!sync.IsConnected || player is null) return Task.CompletedTask;
        var scope = $"{player.CurrentWorld.RowId}:{ClientState.TerritoryType}";
        return sync.SetLocalPresenceAsync(scope, player.Name.TextValue, player.HomeWorld.RowId)
            .ContinueWith(task =>
            {
                task.GetAwaiter().GetResult();
                localPresenceScope = scope;
                nextLocalPresenceAttempt = 0;
            }, TaskScheduler.Default);
    }

    private void ProcessLocalAnimationSignals()
    {
        while (localAnimationSignals.TryDequeue(out var signal))
        {
            var actor = Objects.OfType<IPlayerCharacter>().FirstOrDefault(player =>
                player.Name.TextValue.Equals(signal.SenderName, StringComparison.OrdinalIgnoreCase) &&
                (signal.SenderHomeWorldId == 0 || player.HomeWorld.RowId == signal.SenderHomeWorldId));
            if (actor is null)
            {
                Log.Debug("Skipped nearby animation {SequenceId}; sender is not a visible actor.", signal.SequenceId);
                continue;
            }
            var indexedMod = modCatalogKeys.FirstOrDefault(pair =>
                pair.Value.Equals(signal.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(indexedMod.Key) ||
                !modsByDirectory.TryGetValue(indexedMod.Key, out var mod) ||
                !emotePlaybackById.TryGetValue(signal.EmoteId, out var playbackInfo) ||
                !TryCreatePlayback(playbackInfo, out var playback))
            {
                Log.Debug("Skipped nearby animation {SequenceId}; its local mod or timeline was unavailable.",
                    signal.SequenceId);
                continue;
            }

            var objectIndex = FindObjectIndex(actor.Address);
            var collection = objectIndex < 0 ? null : penumbra.GetCollectionForObject(objectIndex);
            ClearRemotePlaybackForActor(actor.Address);
            if (collection is null)
            {
                Log.Debug("Skipped nearby animation {SequenceId}; {Sender} has no Penumbra collection.",
                    signal.SequenceId, signal.SenderName);
                continue;
            }
            // Temporary settings apply to a whole collection. When the sender resolves to the
            // collection the local player is animating with, mirroring them would either replace
            // or compete with the local override, so the local animation wins.
            if (HasLocalAssignmentInCollection(collection.Value.Id))
            {
                Log.Debug(
                    "Skipped nearby animation {SequenceId}; {Sender} shares the collection of your active animation.",
                    signal.SequenceId, signal.SenderName);
                continue;
            }
            var activation = penumbra.Activate(
                collection.Value.Id, mod.Directory, mod.Name, GetActivationSelections(mod.Directory));
            if (!activation.Success)
            {
                Log.Debug("Skipped nearby animation {SequenceId}; Penumbra could not activate {ModName} for {Sender}: {Error}",
                    signal.SequenceId, mod.Name, signal.SenderName, activation.Error);
                continue;
            }

            var assignment = new TemporaryAssignment(collection.Value.Id, mod.Directory, mod.Name);
            configuration.ActiveAssignments.Add(assignment);
            remoteAssignments.Add(assignment);
            configuration.Save(PluginInterface);
            var delay = signal.DelayMilliseconds > 0
                ? signal.DelayMilliseconds
                : Math.Max(0, signal.StartUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            pendingRemotePlaybacks.Add(new PendingRemotePlayback(
                actor.Address, playback, assignment, mod.Name, Environment.TickCount64 + delay));
            Log.Information("Scheduled nearby animation {SequenceId} from {Sender} in {DelayMilliseconds} ms.",
                signal.SequenceId, signal.SenderName, delay);
        }
    }

    private static int FindObjectIndex(nint address)
    {
        for (var index = 0; index < Objects.Length; index++)
            if (Objects[index]?.Address == address) return index;
        return -1;
    }

    private void UpdateRemotePlaybacks()
    {
        var now = Environment.TickCount64;
        for (var index = pendingRemotePlaybacks.Count - 1; index >= 0; index--)
        {
            var pending = pendingRemotePlaybacks[index];
            if (now < pending.StartAt) continue;
            pendingRemotePlaybacks.RemoveAt(index);
            var actor = Objects.FirstOrDefault(candidate => candidate.Address == pending.ActorAddress);
            if (actor is null || !ActionTimelinePlayback.Start(
                    pending.ActorAddress, pending.Playback, out var originalBaseOverride))
            {
                RemoveRemoteAssignment(pending.Assignment);
                continue;
            }
            activeRemotePlaybacks.Add(new ActiveRemotePlayback(
                pending.ActorAddress,
                originalBaseOverride,
                pending.Assignment,
                actor.Position,
                pending.Playback.IsLoop,
                now + (pending.Playback.IsLoop ? 21_600_000 : 15_000)));
        }

        for (var index = activeRemotePlaybacks.Count - 1; index >= 0; index--)
        {
            var active = activeRemotePlaybacks[index];
            var actor = Objects.FirstOrDefault(candidate => candidate.Address == active.ActorAddress);
            var moved = actor is not null &&
                System.Numerics.Vector3.DistanceSquared(actor.Position, active.StartPosition) > 0.01f;
            if (actor is not null && now < active.CleanupAt && (!active.IsLoop || !moved)) continue;
            if (actor is not null && active.IsLoop)
                ActionTimelinePlayback.Stop(active.ActorAddress, active.OriginalBaseOverride);
            RemoveRemoteAssignment(active.Assignment);
            activeRemotePlaybacks.RemoveAt(index);
        }
    }

    private void ClearRemotePlaybackForActor(nint actorAddress)
    {
        for (var index = pendingRemotePlaybacks.Count - 1; index >= 0; index--)
        {
            if (pendingRemotePlaybacks[index].ActorAddress != actorAddress) continue;
            RemoveRemoteAssignment(pendingRemotePlaybacks[index].Assignment);
            pendingRemotePlaybacks.RemoveAt(index);
        }
        for (var index = activeRemotePlaybacks.Count - 1; index >= 0; index--)
        {
            var active = activeRemotePlaybacks[index];
            if (active.ActorAddress != actorAddress) continue;
            if (active.IsLoop) ActionTimelinePlayback.Stop(active.ActorAddress, active.OriginalBaseOverride);
            RemoveRemoteAssignment(active.Assignment);
            activeRemotePlaybacks.RemoveAt(index);
        }
    }

    private bool HasLocalAssignmentInCollection(Guid collectionId) =>
        configuration.ActiveAssignments.Any(assignment =>
            assignment.CollectionId == collectionId && !remoteAssignments.Contains(assignment));

    private void ClearRemotePlaybacksInCollection(Guid collectionId)
    {
        var actors = pendingRemotePlaybacks.Where(pending => pending.Assignment.CollectionId == collectionId)
            .Select(pending => pending.ActorAddress)
            .Concat(activeRemotePlaybacks.Where(active => active.Assignment.CollectionId == collectionId)
                .Select(active => active.ActorAddress))
            .Distinct()
            .ToList();
        foreach (var actor in actors) ClearRemotePlaybackForActor(actor);
    }

    private void RemoveRemoteAssignment(TemporaryAssignment assignment)
    {
        penumbra.Remove(assignment);
        configuration.ActiveAssignments.Remove(assignment);
        remoteAssignments.Remove(assignment);
        configuration.Save(PluginInterface);
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
            pendingCarrierPlayback = preparedCarrierPlayback is null
                ? null
                : new PendingCarrierPlayback(
                    preparedCarrierPlayback,
                    pendingCommandTime,
                    pendingCommandTime + 600);
            pendingDirectPlayback = preparedCarrierPlayback is not null || preparedDirectPlayback is null
                ? null
                : new PendingDirectPlayback(
                    Objects.LocalPlayer?.Address ?? 0,
                    preparedDirectPlayback,
                    "group animation");
            NewStartScheduled();
            if (preparedDirectPlayback is not null &&
                preparedCatalogFingerprint is { Length: 64 } fingerprint)
                pendingNearbyBroadcast = (fingerprint, preparedDirectPlayback);
            pendingSelectionModKey = signal.ModKey;
            lobbyEmoteRefreshTime = 0;
            Status = $"Group ready. Starting in {delay / 1000f:F1}s.";
            mainWindow.NotifyGroupPlaybackScheduled();
            Log.Information(
                "Group play {SequenceId} received; scheduling {ModKey} in {DelayMilliseconds} ms ({TimingMode}).",
                signal.SequenceId,
                signal.ModKey,
                delay,
                signal.DelayMilliseconds > 0 ? "relay countdown" : "legacy UTC");
            preparedModKey = null;
            preparedCatalogFingerprint = null;
            preparedCommand = null;
            preparedPose = null;
            preparedDirectPlayback = null;
            preparedCarrierPlayback = null;
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
        InvalidateLibraryOrder();
    }

    private void ClearRemoteSelections(string memberName, string modKey)
    {
        foreach (var pair in remoteOptionSelections.Where(pair =>
                     pair.Value.MemberName.Equals(memberName, StringComparison.OrdinalIgnoreCase) &&
                     pair.Value.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase)))
            remoteOptionSelections.TryRemove(pair.Key, out _);
        activeAnimationSuggestions.TryRemove(SuggestionKey(memberName, modKey), out _);
        InvalidateLibraryOrder();
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
            case PoseKind.Sit:
                if (configuration.SitDozeAnywhere && !configuration.DozeAnywhereOnly && anywherePoses is not null)
                    anywherePoses.EnterChairPose();
                else
                    ExecuteCommand("/sit");
                break;
            case PoseKind.Doze:
                if (configuration.SitDozeAnywhere && anywherePoses is not null)
                    anywherePoses.EnterDozePose();
                else
                    ExecuteCommand("/doze");
                break;
            case PoseKind.Idle: BeginPoseCycling(pose, 150); break;
        }
        if (pose.Kind != PoseKind.Idle) BeginPoseCycling(pose, 500);
    }

    // ---- Standing up before a standing animation ------------------------------------------

    private const int StandUpTimeoutMs = 6000;
    private const int StandUpRetryMs = 2500;
    private const int StandUpSettleMs = 250;
    private long standUpStartedAt;
    private long standUpCommandAt;
    private long standUpSettledAt;
    private PoseKind? standUpFrom;
    /// <summary>Set when getting up timed out, so the same start doesn't keep trying.</summary>
    private bool standUpGaveUp;
    private (string Fingerprint, EmotePlayback Playback)? pendingNearbyBroadcast;

    private void AnnounceToNearby(string fingerprint, EmotePlayback playback, long startAt) =>
        _ = BroadcastLocalPlaybackAsync(fingerprint, playback, startAt);

    /// <summary>A new start was scheduled: it gets its own try at standing up, and nearby players are
    /// told about it, not a start it replaced.</summary>
    private void NewStartScheduled()
    {
        standUpGaveUp = false;
        pendingNearbyBroadcast = null;
        needsToStandFor = null;
    }

    /// <summary>The answer for the current start in one seat, so waiting seated doesn't ask Penumbra every frame.</summary>
    private (PoseKind Seat, bool Needs)? needsToStandFor;

    /// <summary>
    /// An emote or standing pose won't start from a seat, and a seat's own command (/sit, /groundsit,
    /// /doze) stands you up from it. So when something standing is about to play while you're seated,
    /// this stands you up first and holds the start until you're on your feet. A room start does the
    /// same during its countdown. True while it is holding the start back.
    /// </summary>
    private bool StandUpFirst()
    {
        var now = Environment.TickCount64;
        if (pendingCommand is null && pendingCarrierPlayback is null && pendingDirectPlayback is null && pendingPose is null)
        {
            standUpStartedAt = 0;
            standUpGaveUp = false;
            needsToStandFor = null;
            return false;
        }
        var seat = poses.CurrentKind();
        var seated = seat is PoseKind.Sit or PoseKind.GroundSit or PoseKind.Doze;
        if (standUpStartedAt == 0)
        {
            if (standUpGaveUp || !seated) return false;
            if (needsToStandFor is not { } known || known.Seat != seat)
                needsToStandFor = known = (seat!.Value, NeedsToStand(seat!.Value));
            if (!known.Needs) return false;
            standUpStartedAt = now;
            StandUpFrom(seat!.Value, now);
            return true;
        }

        // Getting up moves you off the seat; that isn't walking away from the animation.
        movementTrackingStart = Math.Max(movementTrackingStart, now + 1500);
        hasMovementSample = false;
        movementFrames = 0;
        if (now - standUpStartedAt > StandUpTimeoutMs)
        {
            Status = "Couldn't stand up first; starting anyway.";
            ReleaseStandUp(now);
            standUpGaveUp = true;
            return false;
        }
        if (seated)
        {
            // A chair doze gets up into the chair: stand up from that too. Same seat for a while
            // means the command didn't take (it landed mid-transition), so try once more.
            if (seat != standUpFrom || now - standUpCommandAt > StandUpRetryMs)
            {
                // A new pick can replace what started this (activation clears and reschedules in
                // one call): only stand up again if what's pending now still needs it.
                if (!NeedsToStand(seat!.Value))
                {
                    ReleaseStandUp(now);
                    return false;
                }
                StandUpFrom(seat!.Value, now);
            }
            standUpSettledAt = 0;
            return true;
        }
        var player = (Character*)(Objects.LocalPlayer?.Address ?? 0);
        if (player is null || player->Mode != CharacterModes.Normal)
        {
            standUpSettledAt = 0;
            return true;
        }
        if (standUpSettledAt == 0) standUpSettledAt = now;
        if (now - standUpSettledAt < StandUpSettleMs) return true;
        ReleaseStandUp(now);
        return false;
    }

    private void StandUpFrom(PoseKind seat, long now)
    {
        ExecuteCommand(seat switch
        {
            PoseKind.GroundSit => "/groundsit",
            PoseKind.Doze => "/doze",
            _ => "/sit"
        });
        standUpFrom = seat;
        standUpCommandAt = now;
        standUpSettledAt = 0;
        Status = "Standing up first...";
    }

    /// <summary>On your feet: a carrier's retry window restarts so the wait doesn't use it up.</summary>
    private void ReleaseStandUp(long now)
    {
        standUpStartedAt = 0;
        standUpFrom = null;
        standUpSettledAt = 0;
        if (pendingCarrierPlayback is { } carrier)
            pendingCarrierPlayback = carrier with
            {
                NextAttempt = Math.Max(carrier.NextAttempt, now),
                Deadline = Math.Max(carrier.Deadline, Math.Max(carrier.NextAttempt, now) + 600)
            };
    }

    /// <summary>
    /// Whether what's about to play needs you standing when you're in <paramref name="seat"/>. A
    /// standing pose does; another seat doesn't. An emote does, unless it is a seat itself or the
    /// animation replaces the version the game plays in this seat (slot 2 on the ground, slot 3 in a
    /// chair): then it was made to be played seated.
    /// </summary>
    private bool NeedsToStand(PoseKind seat)
    {
        if (pendingPose is { } pose) return pose.Kind == PoseKind.Idle;
        uint emoteId;
        if (pendingCarrierPlayback is { } carrier) emoteId = carrier.Playback.EmoteId;
        else if (pendingDirectPlayback is { } direct) emoteId = direct.Playback.EmoteId;
        else if (pendingCommand is { } command && emotePlaybackByCommand.TryGetValue(command, out var info)) emoteId = info.EmoteId;
        else return false;   // a command that isn't an emote: leave it to the game
        if (emoteId == 0) return true;   // a bare standing timeline
        if (DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRowOrDefault(emoteId) is not { } emote) return false;
        if (emote.EmoteMode.RowId is 1 or 2 or 3) return false;
        var slot = seat switch { PoseKind.GroundSit => 2, PoseKind.Sit => 3, PoseKind.Doze => 5, _ => -1 };
        if (slot < 0 || slot >= emote.ActionTimeline.Count) return true;
        var seatedVersion = emote.ActionTimeline[slot];
        if (seatedVersion.RowId == 0 || seatedVersion.ValueNullable is not { } timeline) return true;
        // The game plays the same timeline in this seat as standing (every facial expression does).
        if (seatedVersion.RowId == emote.ActionTimeline[0].RowId) return false;
        // Direct playback plays its own main timeline whatever the seat, never the seated version.
        if (pendingCarrierPlayback is null && pendingDirectPlayback is { } directPlay &&
            directPlay.Playback.MainTimeline != seatedVersion.RowId)
            return true;
        return !IsReplacedForYou(timeline.Key.ExtractText());
    }

    /// <summary>
    /// Whether the animation Synastry is playing for you replaces this timeline. Another mod's seated
    /// version doesn't count: that isn't the animation you picked.
    /// </summary>
    private bool IsReplacedForYou(string timelineKey)
    {
        if (string.IsNullOrWhiteSpace(timelineKey)) return false;
        var root = penumbra.GetModRoot();
        var collection = penumbra.GetPlayerCollection();
        if (string.IsNullOrWhiteSpace(root) || collection is null) return false;
        var owners = configuration.ActiveAssignments
            .Where(assignment => assignment.CollectionId == collection.Value.Id && !remoteAssignments.Contains(assignment))
            .Select(assignment => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, assignment.ModDirectory))) +
                                  Path.DirectorySeparatorChar)
            .ToList();
        if (owners.Count == 0) return false;
        var character = (Character*)(Objects.LocalPlayer?.Address ?? 0);
        ushort race = 101;
        if (character is not null && character->DrawObject is not null &&
            character->DrawObject->GetObjectType() == ObjectType.CharacterBase &&
            ((CharacterBase*)character->DrawObject)->GetModelType() == CharacterBase.ModelType.Human)
            race = ((Human*)character->DrawObject)->RaceSexId;
        // The game falls back to the base race's file when a race has none of its own.
        var races = new List<ushort> { race };
        if (race / 100 % 2 == 0 && race != 201) races.Add(201);
        if (race != 101) races.Add(101);
        foreach (var candidate in races)
        {
            var gamePath = $"chara/human/c{candidate:D4}/animation/a0001/bt_common/{timelineKey}.pap";
            bool exists;
            try { exists = DataManager.FileExists(gamePath); }
            catch { exists = false; }
            if (!exists) continue;   // the game skips a race with no file of its own; a redirect there never loads
            var file = penumbra.ResolvePlayerPath(gamePath);
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file)) return false;
            var full = Path.GetFullPath(file);
            return owners.Any(owner => full.StartsWith(owner, StringComparison.OrdinalIgnoreCase));
        }
        return false;
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

    private void ToggleWindow()
    {
        if (mainWindow.IsOpen || miniPlayerWindow.IsOpen)
        {
            mainWindow.IsOpen = false;
            miniPlayerWindow.IsOpen = false;
            return;
        }
        if (configuration.UseMiniPlayer) miniPlayerWindow.IsOpen = true;
        else mainWindow.IsOpen = true;
    }

    /// <summary>Prompts only show in the full window; bring it up for one while the mini player is
    /// showing, without changing which one Synastry opens as.</summary>
    private void SurfacePromptFromMiniPlayer()
    {
        if (!miniPlayerWindow.IsOpen) return;
        miniPlayerWindow.IsOpen = false;
        mainWindow.IsOpen = true;
    }

    public void ShowMiniPlayer()
    {
        mainWindow.IsOpen = false;
        miniPlayerWindow.IsOpen = true;
        configuration.UseMiniPlayer = true;
        configuration.Save(PluginInterface);
    }

    public void ShowFullWindow()
    {
        miniPlayerWindow.IsOpen = false;
        mainWindow.IsOpen = true;
        configuration.UseMiniPlayer = false;
        configuration.Save(PluginInterface);
    }

    public void OpenSettings()
    {
        try
        {
            settingsWindow.Open();
        }
        catch (Exception ex)
        {
            // Never let a failed folder lookup keep the window shut.
            Log.Warning(ex, "Settings opened without Penumbra's folder list.");
            settingsWindow.IsOpen = true;
        }
    }

    public void OpenCustomCommands()
    {
        customCommandsWindow.Open();
    }

    public void OpenHowTo()
    {
        settingsWindow.IsOpen = false;
        mainWindow.StartTutorial();
    }

    public void Dispose()
    {
        LeaveTestPartnerRoom();
        testPartner.Dispose();
        contactIk?.Dispose();
        agentExecuteEmoteHook?.Dispose();
        Theme.Dispose();
        var refreshCancellation = modRefreshCancellation;
        modRefreshCancellation = null;
        refreshCancellation?.Cancel();
        try { modRefreshWorker?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is TaskCanceledException)) { }
        refreshCancellation?.Dispose();
        modRefreshWorker = null;
        modScanFramePermit.Dispose();
        // Keep the mods validated so far; otherwise an unload mid-refresh rescans them next time.
        animationIndexCache.Save();
        ClearAnimationSpeedState();
        animationSpeedController?.Dispose();
        movement.Dispose();
        anywherePoses?.Dispose();
        try
        {
            ClearTemporaryAssignments();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Temporary assignments could not be cleared while Synastry was unloading.");
        }
        Framework.Update -= OnUpdate;
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        Chat.ChatMessage -= OnChatMessage;
        Chat.RemoveChatLinkHandler();
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        foreach (var command in registeredCustomCommands.ToList()) Commands.RemoveHandler(command);
        registeredCustomCommands.Clear();
        Commands.RemoveHandler(PrimaryCommand);
        Commands.RemoveHandler(FallbackCommand);
        penumbra.ModAdded -= OnPenumbraModAdded;
        penumbra.Dispose();
        sync.DisposeAsync().AsTask().GetAwaiter().GetResult();
        windows.RemoveAllWindows();
        NoireLibMain.Dispose();
    }
}

public sealed record AnimationSuggestion(
    string SuggestedBy,
    string ModKey,
    string Directory,
    string ModName,
    string ActivatedTrigger = "");
public sealed record EmoteTarget(uint Id, string Name, string Command);
public sealed record NowPlayingInfo(string Directory, string ModName, string Animation);
public sealed record TypedEmoteCandidate(string Directory, string ModName, EmoteTarget Emote);
public sealed record AnimationCommandTarget(
    string ModDirectory,
    string ModName,
    CustomAnimationTriggerKind TriggerKind,
    string TriggerValue,
    string AnimationName);
public sealed record RoomInvite(string SenderName, string RoomCode);
public sealed record FreeUsePrompt(
    string Directory,
    string ModName,
    string OwnTrigger,
    string MemberConnectionId,
    string MemberName);

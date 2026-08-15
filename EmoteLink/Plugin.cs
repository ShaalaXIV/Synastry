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
using System.Diagnostics;

namespace EmoteLink;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string PrimaryCommand = "/syn";
    private const string FallbackCommand = "/synastry";
    private const string LoopCarrierCommand = "/beesknees";
    private const string OneShotCarrierCommand = "/cheer";
    private const string CarrierTag = "Synastry Carrier";
    private const int CarrierPriority = 10_000;
    private const float MaxAlignDistance = 2f;
    private const float MaxMidEmoteAlignDistance = 0.5f;
    private const int LobbyEmoteRefreshDelayMs = 6000;
    private const double RefreshFrameBudgetMilliseconds = 4;
    private static readonly TimeSpan StaleTransferPackageAge = TimeSpan.FromHours(24);
    private const string PublicRelayUrl = "https://emotelink.aethercast.org";

    private sealed record PendingPenumbraInstall(ModTransferOfferDto Offer, string Path, string ReceiveFolder);
    private sealed record EmoteTimelineInfo(uint RowId, string Key, bool IsLoop);
    private sealed record EmotePlaybackInfo(IReadOnlyList<EmoteTimelineInfo> Timelines);
    private sealed record ActiveCarrier(
        Guid CollectionId,
        string Tag,
        int Priority,
        string Command,
        IReadOnlySet<uint> TimelineIds,
        long CreatedAt)
    {
        public bool AnimationStarted { get; set; }
        public long LastSeenAt { get; set; }
    }

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
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;

    private readonly Configuration configuration;
    private readonly AnimationIndexCache animationIndexCache;
    private readonly PenumbraService penumbra;
    private readonly MovementService movement;
    private readonly PoseService poses;
    private readonly AnywherePoseService? anywherePoses;
    private readonly AnimationSpeedService? animationSpeedController;
    private readonly AnimationSyncService sync;
    private readonly WindowSystem windows = new("Synastry");
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly HowToWindow howToWindow;
    private bool waitingForAnimation;
    private long activationTime;
    private readonly Dictionary<string, string> emoteCommandsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmoteTarget> emoteTargetsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmotePlaybackInfo> emotePlaybackByCommand =
        new(StringComparer.OrdinalIgnoreCase);
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
    private string? preparedModKey;
    private string? preparedCommand;
    private PoseTarget? preparedPose;
    private ActiveCarrier? activeCarrier;
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
    private readonly ConcurrentDictionary<string, string> remoteReadyModKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? remoteSelectionRoom;
    private bool roleSyncPending;
    private bool communityRoleSyncPending;
    private bool communityRelayConnected;
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
    private int refreshRelayMods;
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
    public bool IsRefreshingMods => modRefreshCancellation is not null;
    public string ReceivedModFolder => (configuration.ReceivedModFolder ?? "").Replace('\\', '/').Trim('/');
    public string Status { get; private set; } = "Ready.";
    public AnimationSyncService Sync => sync;
    public string SyncDisplayName => CurrentCharacterName() ?? "Unavailable";

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var upgradedConfiguration = configuration.Version < 8;
        if (upgradedConfiguration) configuration.Version = 8;
        animationIndexCache = AnimationIndexCache.Load(
            Path.Combine(PluginInterface.ConfigDirectory.FullName, "animation-index.json"), Log);
        _ = Task.Run(SweepStaleTransferPackages);
        var generatedReporterIdentity = false;
        if (!IsReporterId(configuration.CommunityReporterId))
        {
            configuration.CommunityReporterId = Guid.NewGuid().ToString("N");
            generatedReporterIdentity = true;
        }
        if (!IsReporterId(configuration.CatalogReporterId))
        {
            configuration.CatalogReporterId = Guid.NewGuid().ToString("N");
            generatedReporterIdentity = true;
        }
        if (generatedReporterIdentity || upgradedConfiguration) configuration.Save(PluginInterface);
        penumbra = new PenumbraService(PluginInterface, Log);
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
        sync.PlayReceived += signal => syncPlaySignals.Enqueue(signal);
        sync.ModTransferOffered += offer => incomingTransferOffers.Enqueue(offer);
        sync.OptionSelectionChanged += RememberOptionSelection;
        sync.RoleLabelChanged += label => receivedRoleLabels.Enqueue(label);
        sync.CommunityRoleLabelChanged += label => receivedCommunityRoleLabels.Enqueue(label);
        sync.AnimationSuggestionDeclined += OnAnimationSuggestionDeclined;
        sync.StateChanged += OnSyncStateChanged;
        sync.Diagnostic += (message, exception) =>
        {
            if (exception is null) Log.Information("{Message}", message);
            else Log.Warning(exception, "{Message}", message);
        };
        mainWindow = new MainWindow(this);
        settingsWindow = new SettingsWindow(this);
        howToWindow = new HowToWindow(TextureProvider);
        BuildEmoteLookup();
        windows.AddWindow(mainWindow);
        windows.AddWindow(settingsWindow);
        windows.AddWindow(howToWindow);

        if (!configuration.HasSeenHowTo)
        {
            configuration.HasSeenHowTo = true;
            configuration.Save(PluginInterface);
            howToWindow.IsOpen = true;
        }

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        Framework.Update += OnUpdate;
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Chat.ChatMessage += OnChatMessage;
        Commands.AddHandler(PrimaryCommand, new CommandInfo(HandleCommand)
        {
            HelpMessage = "Open Synastry, join with /syn join ROOMCODE, or select a localhost dev relay with /syn relay URL."
        });
        Commands.AddHandler(FallbackCommand, new CommandInfo(HandleCommand)
        {
            HelpMessage = "Fallback command for Synastry. The shorter /syn command is also available."
        });

        // Recover from an unload/crash that left our tracked overrides behind.
        ClearTemporaryAssignments();
        RefreshMods();
    }

    private void HandleCommand(string _, string arguments)
    {
        var match = Regex.Match(arguments, @"^\s*join\s+([A-Za-z0-9]{4,8})\s*$", RegexOptions.IgnoreCase);
        if (match.Success) JoinSyncRoom(match.Groups[1].Value);
        else if (Regex.IsMatch(arguments, @"^\s*relay\s+(?:default|reset)\s*$", RegexOptions.IgnoreCase))
            SetLocalRelayOverride("");
        else if (Regex.Match(arguments, @"^\s*relay\s+(\S+)\s*$", RegexOptions.IgnoreCase) is
                 { Success: true } relayMatch)
            SetLocalRelayOverride(relayMatch.Groups[1].Value);
        else ToggleWindow();
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

        var allMods = penumbra.GetMods();
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
        refreshRelayMods = 0;
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
            sync,
            configuration.CatalogReporterId,
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
            if (result.Kind == ModRefreshResultKind.CatalogReported)
            {
                animationIndexCache.MarkCatalogReported(
                    result.Mod.Directory, result.Signature, result.Mod.Name, DateTimeOffset.UtcNow);
                if (Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds >= RefreshFrameBudgetMilliseconds)
                    break;
                continue;
            }

            ApplyModRefreshResult(result);
            refreshProcessedMods++;
            if (result.CacheHit) refreshCachedMods++;
            else if (result.RelayHit) refreshRelayMods++;
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
                 $"{refreshRelayMods} relay-assisted, {refreshScannedMods} validated in the background.";
        refreshCachedMods = 0;
        refreshScannedMods = 0;
        refreshRelayMods = 0;
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
        modSyncKeys[mod.Directory] = BuildModSyncKey(mod.Name, payload.PapGamePaths);
        modCatalogKeys[mod.Directory] = CatalogFingerprint(modSyncKeys[mod.Directory]);
        foreach (var optionPose in payload.OptionPoses)
            optionPoses[OptionPoseKey(mod.Directory, optionPose.Group, optionPose.Option)] =
                new PoseTarget(optionPose.Kind, optionPose.Index);
        foreach (var (group, multi) in payload.MultiSelectGroups)
            optionGroupMulti[OptionGroupKey(mod.Directory, group)] = multi;
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

        modSyncKeys[mod.Directory] = cached.SyncKey;
        modCatalogKeys[mod.Directory] = CatalogFingerprint(cached.SyncKey);
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
            var allMods = penumbra.GetMods();
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
            refreshRelayMods = 0;
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
                sync,
                configuration.CatalogReporterId,
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
                optionGroupMulti.Remove(OptionGroupKey(directory, group.Name));
                foreach (var option in group.Options)
                    optionPoses.Remove(OptionPoseKey(directory, group.Name, option));
            }
        }
        optionGroups.Remove(directory);
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
            ? "Sit/doze anywhere enabled. Chair-sit and doze animations will skip furniture checks."
            : "Sit/doze anywhere disabled. Chair-sit and doze will use normal game placement.";
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
        preparedCommand = null;
        preparedPose = null;
        RunSync(sync.CancelReadyAsync(), "Group-play readiness cancelled.");
    }

    public void ForceSyncStart() =>
        RunSync(sync.ForceStartAsync(), "Started every prepared room member's selected animation role.");

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
        PublishDetectedTriggerSelection(directory, $"pose:{pose.Kind}:{pose.Index}");
        ActivateInternal(directory, name, pose);
    }

    public void ActivateDetectedPoseSolo(string directory, string name, PoseTarget pose)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, pose, false);
    }

    public void ActivateDetectedEmote(string directory, string name, EmoteTarget emote)
    {
        PublishDetectedTriggerSelection(directory, $"emote:{emote.Id}");
        ActivateInternal(directory, name, null, requestedCommand: emote.Command);
    }

    public void ActivateDetectedEmoteSolo(string directory, string name, EmoteTarget emote)
    {
        CancelGroupReadinessForSolo();
        ActivateInternal(directory, name, null, false, emote.Command);
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
        var organized = order.Where(modsByDirectory.ContainsKey)
            .Select(directory => modsByDirectory[directory])
            .OrderBy(mod => GetMatchSortTier(mod.Directory))
            .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        organizedModsCache[cacheKey] = organized;
        return organized;
    }

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
            Chat.PrintError($"[Synastry] {name} was activated, but its emote could not be detected.");
            return;
        }

        var playbackCommand = TryActivateEmoteCarrier(collection.Value.Id, directory, command, out var carrierCommand)
            ? carrierCommand
            : command;
        if (allowGroupPlay && PrepareForGroupPlay(directory, name, playbackCommand, null)) return;
        ScheduleCommand(name, playbackCommand, 300);
    }

    private bool TryActivateEmoteCarrier(
        Guid collectionId,
        string directory,
        string sourceCommand,
        out string carrierCommand)
    {
        carrierCommand = sourceCommand;
        if (!emotePlaybackByCommand.TryGetValue(sourceCommand, out var sourceInfo) ||
            sourceInfo.Timelines.Count == 0) return false;

        carrierCommand = sourceInfo.Timelines.Any(timeline => timeline.IsLoop)
            ? LoopCarrierCommand
            : OneShotCarrierCommand;
        if (sourceCommand.Equals(carrierCommand, StringComparison.OrdinalIgnoreCase)) return false;
        if (!emotePlaybackByCommand.TryGetValue(carrierCommand, out var carrierInfo) ||
            carrierInfo.Timelines.Count == 0)
        {
            Log.Warning("Carrier command {CarrierCommand} was not found in the current Emote sheet.", carrierCommand);
            carrierCommand = sourceCommand;
            return false;
        }

        var root = penumbra.GetModRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            carrierCommand = sourceCommand;
            return false;
        }

        var modPath = Path.Combine(root, directory);
        if (!Directory.Exists(modPath))
        {
            carrierCommand = sourceCommand;
            return false;
        }

        var candidates = new List<(string SourceGamePath, string CarrierGamePath)>();
        foreach (var sourceGamePath in ReadPapGamePaths(modPath))
        {
            var normalized = sourceGamePath.Replace('\\', '/').ToLowerInvariant();
            const string commonMarker = "/bt_common/";
            var commonIndex = normalized.IndexOf(commonMarker, StringComparison.Ordinal);
            if (commonIndex < 0 || !normalized.EndsWith(".pap", StringComparison.Ordinal)) continue;
            var relativeSourcePath = normalized[(commonIndex + commonMarker.Length)..^4];
            if (!relativeSourcePath.StartsWith("emote", StringComparison.Ordinal)) continue;
            var sourceLeaf = TimelineLeaf(relativeSourcePath);
            var sourceTimeline = sourceInfo.Timelines.FirstOrDefault(timeline =>
                relativeSourcePath.Equals(timeline.Key, StringComparison.OrdinalIgnoreCase) ||
                sourceLeaf.Equals(TimelineLeaf(timeline.Key), StringComparison.OrdinalIgnoreCase));
            if (sourceTimeline is null) continue;
            var targetTimeline = carrierInfo.Timelines.FirstOrDefault(timeline =>
                                     timeline.IsLoop == sourceTimeline.IsLoop) ??
                                 carrierInfo.Timelines.First();
            var carrierTimelinePath = targetTimeline.Key.Contains('/')
                ? targetTimeline.Key
                : "emote/" + targetTimeline.Key;
            candidates.Add((normalized,
                normalized[..(commonIndex + commonMarker.Length)] + carrierTimelinePath + ".pap"));
        }

        if (candidates.Count == 0)
        {
            carrierCommand = sourceCommand;
            return false;
        }

        var resolvedPaths = penumbra.ResolvePlayerPaths(candidates.Select(candidate => candidate.SourceGamePath).ToList());
        if (resolvedPaths.Count != candidates.Count)
        {
            carrierCommand = sourceCommand;
            return false;
        }

        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < candidates.Count; index++)
        {
            var resolvedPath = resolvedPaths[index];
            if (!Path.IsPathFullyQualified(resolvedPath) || !File.Exists(resolvedPath)) continue;
            mappings[candidates[index].CarrierGamePath] = resolvedPath;
        }

        if (mappings.Count == 0)
        {
            carrierCommand = sourceCommand;
            return false;
        }

        if (!penumbra.AddCarrierMod(CarrierTag, collectionId, mappings, CarrierPriority))
        {
            carrierCommand = sourceCommand;
            return false;
        }

        activeCarrier = new ActiveCarrier(
            collectionId,
            CarrierTag,
            CarrierPriority,
            carrierCommand,
            carrierInfo.Timelines.Select(timeline => timeline.RowId).ToHashSet(),
            Environment.TickCount64);
        Log.Information(
            "Mapped {MappingCount} selected PAP path(s) from {SourceCommand} to carrier {CarrierCommand}.",
            mappings.Count,
            sourceCommand,
            carrierCommand);
        return true;
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

    private static SortedSet<string> ReadPapGamePaths(string modPath)
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
        return gamePaths;
    }

    private static string BuildModSyncKey(string modName, IEnumerable<string> gamePaths)
    {
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
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                CollectPapGamePaths(root, modPapPaths);
                if (file.EndsWith("default_mod.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("Groups", out var groupsElement) &&
                        groupsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var groupElement in groupsElement.EnumerateArray())
                            IndexPoseOptionGroup(groupElement, directory);
                    }
                    continue;
                }

                IndexPoseOptionGroup(root, directory);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not inspect pose options in {File}.", file);
            }
        }
        modPoses[directory] = DetectPoseTargets(modPapPaths);
    }

    private void IndexPoseOptionGroup(JsonElement groupElement, string directory)
    {
        if (groupElement.ValueKind != JsonValueKind.Object ||
            !groupElement.TryGetProperty("Name", out var groupNameElement) ||
            !groupElement.TryGetProperty("Options", out var optionsElement)) return;
        var groupName = groupNameElement.GetString();
        if (string.IsNullOrWhiteSpace(groupName) || optionsElement.ValueKind != JsonValueKind.Array) return;
        var isMulti = groupElement.TryGetProperty("Type", out var typeElement) &&
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
            var timelines = row.ActionTimeline
                .Where(timeline => timeline is { RowId: > 0, IsValid: true })
                .Select(timeline => new EmoteTimelineInfo(
                    timeline.RowId,
                    NormalizeTimelineKey(timeline.Value.Key.ExtractText()),
                    timeline.Value.IsLoop))
                .Where(timeline => timeline.Key.Length > 0)
                .DistinctBy(timeline => (timeline.RowId, timeline.Key.ToUpperInvariant(), timeline.IsLoop))
                .ToList();
            if (timelines.Count > 0)
                emotePlaybackByCommand.TryAdd(command, new EmotePlaybackInfo(timelines));
        }
        foreach (var carrierCommand in new[] { LoopCarrierCommand, OneShotCarrierCommand })
        {
            if (emotePlaybackByCommand.TryGetValue(carrierCommand, out var carrier))
                Log.Information("Carrier {CarrierCommand} timelines: {Timelines}.", carrierCommand,
                    string.Join(", ", carrier.Timelines.Select(timeline =>
                        $"{timeline.RowId}:{timeline.Key}{(timeline.IsLoop ? " [loop]" : "")}")));
            else
                Log.Warning("Carrier {CarrierCommand} was not found in the current Emote sheet.", carrierCommand);
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

    private static string TimelineLeaf(string timelinePath)
    {
        var slash = timelinePath.LastIndexOf('/');
        return slash >= 0 ? timelinePath[(slash + 1)..] : timelinePath;
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

    private void ClearTemporaryAssignmentsInternal(bool cancelGroupReady)
    {
        if (activeCarrier is { } carrier)
        {
            penumbra.RemoveCarrierMod(carrier.Tag, carrier.CollectionId, carrier.Priority);
            activeCarrier = null;
        }
        foreach (var assignment in configuration.ActiveAssignments.ToList())
            if (penumbra.Remove(assignment)) configuration.ActiveAssignments.Remove(assignment);

        configuration.Save(PluginInterface);
        waitingForAnimation = false;
        pendingCommand = null;
        pendingPose = null;
        pendingSelectionModKey = null;
        lobbyEmoteRefreshTime = 0;
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
        ProcessModRefresh();
        ProcessReceivedRoleLabels();
        ProcessReceivedCommunityRoleLabels();
        ProcessTransferOffers();
        if (roleSyncPending) StartRoleLabelSync();
        if (communityRoleSyncPending) StartCommunityRoleLabelSync();
        UpdateAlignment();
        UpdateAnimationSpeed();
        ProcessCompletedDownloads();
        ProcessAddedMod();
        ProcessSyncPlaySignals();
        var animationStarted = false;
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
        if (animationStarted && pendingSelectionModKey is not null)
        {
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
        UpdateCarrierLifecycle();
        UpdatePoseCycling();
        if (!waitingForAnimation) return;
        UpdateMovementCleanup();
    }

    private void UpdateCarrierLifecycle()
    {
        var carrier = activeCarrier;
        if (carrier is null) return;
        if (string.Equals(pendingCommand, carrier.Command, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preparedCommand, carrier.Command, StringComparison.OrdinalIgnoreCase)) return;

        var player = (Character*)(Objects.LocalPlayer?.Address ?? 0);
        var timelineId = player is null ? 0u : player->Timeline.TimelineSequencer.TimelineIds[0];
        if (carrier.TimelineIds.Contains(timelineId))
        {
            carrier.AnimationStarted = true;
            carrier.LastSeenAt = Environment.TickCount64;
            return;
        }

        if (!carrier.AnimationStarted && Environment.TickCount64 - carrier.CreatedAt < 5000) return;
        if (carrier.AnimationStarted && Environment.TickCount64 - carrier.LastSeenAt < 500) return;
        var status = carrier.AnimationStarted
            ? "Carrier animation finished; removed Synastry's temporary emote mapping."
            : "Carrier animation did not start; removed Synastry's temporary emote mapping.";
        ClearTemporaryAssignmentsInternal(false);
        Status = status;
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
            remoteReadyModKeys.Clear();
            roleSyncPending = false;
            while (receivedRoleLabels.TryDequeue(out _)) { }
            return;
        }
        var roomChanged = !roomCode.Equals(remoteSelectionRoom, StringComparison.OrdinalIgnoreCase);
        if (roomChanged)
        {
            remoteSelectionRoom = roomCode;
            remoteOptionSelections.Clear();
            activeAnimationSuggestions.Clear();
            remoteReadyModKeys.Clear();
            roleSyncPending = true;
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
        var fingerprints = modCatalogKeys
            .Where(pair => !IsModPrivate(pair.Key))
            .Select(pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _ = sync.GetCommunityRoleLabelsAsync(fingerprints).ContinueWith(task =>
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
            if (!sync.IsConnected || string.IsNullOrWhiteSpace(shared.Label) ||
                !IsSynchronizedRoleGroup(shared.Group)) continue;
            var mod = Mods.FirstOrDefault(candidate => modCatalogKeys.TryGetValue(candidate.Directory, out var fingerprint) &&
                fingerprint.Equals(shared.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(mod.Directory) || IsModPrivate(mod.Directory)) continue;
            var key = OptionNoteKey(mod.Directory, shared.Group, shared.Option);
            var isCommunityManaged = configuration.CommunityRoleKeys.Contains(key);
            if (!isCommunityManaged && configuration.OptionNotes.TryGetValue(key, out var existing) &&
                !string.IsNullOrWhiteSpace(existing)) continue;
            configuration.OptionNotes[key] = shared.Label.Trim()[..Math.Min(20, shared.Label.Trim().Length)];
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
            lobbyEmoteRefreshTime = 0;
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
                if (configuration.SitDozeAnywhere && anywherePoses is not null)
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

    public void OpenSettings()
    {
        settingsWindow.Open();
    }

    public void OpenHowTo() => howToWindow.IsOpen = true;

    public void Dispose()
    {
        var refreshCancellation = modRefreshCancellation;
        modRefreshCancellation = null;
        refreshCancellation?.Cancel();
        try { modRefreshWorker?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is TaskCanceledException)) { }
        refreshCancellation?.Dispose();
        modRefreshWorker = null;
        modScanFramePermit.Dispose();
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
        Commands.RemoveHandler(PrimaryCommand);
        Commands.RemoveHandler(FallbackCommand);
        penumbra.ModAdded -= OnPenumbraModAdded;
        penumbra.Dispose();
        sync.DisposeAsync().AsTask().GetAwaiter().GetResult();
        windows.RemoveAllWindows();
    }
}

public sealed record AnimationSuggestion(string SuggestedBy, string ModKey, string Directory, string ModName);
public sealed record EmoteTarget(uint Id, string Name, string Command);
public sealed record RoomInvite(string SenderName, string RoomCode);

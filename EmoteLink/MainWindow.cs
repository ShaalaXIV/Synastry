using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace EmoteLink;

public sealed class MainWindow : Window
{
    private static readonly Vector4 AccentColor = new(0.91f, 0.66f, 0.29f, 1f);
    private static readonly Vector4 AccentHoveredColor = new(0.98f, 0.74f, 0.38f, 1f);
    private static readonly Vector4 AccentActiveColor = new(0.78f, 0.49f, 0.16f, 1f);
    private static readonly Vector4 CoralColor = new(0.93f, 0.39f, 0.38f, 1f);
    private static readonly Vector4 WindowColor = new(0.027f, 0.039f, 0.052f, 1f);
    private static readonly Vector4 PanelColor = new(0.043f, 0.059f, 0.078f, 1f);
    private static readonly Vector4 NestedPanelColor = new(0.032f, 0.045f, 0.059f, 1f);
    private static readonly Vector4 FrameColor = new(0.075f, 0.1f, 0.13f, 1f);
    private static readonly Vector4 FrameHoveredColor = new(0.11f, 0.145f, 0.18f, 1f);
    private static readonly Vector4 BorderColor = new(0.18f, 0.23f, 0.28f, 1f);
    private static readonly Vector4 TextColor = new(0.94f, 0.94f, 0.92f, 1f);
    private static readonly Vector4 MutedColor = new(0.56f, 0.61f, 0.67f, 1f);
    private static readonly Vector4 EveryoneColor = new(0.42f, 0.78f, 0.28f, 1f);
    private static readonly Vector4 SomeColor = new(1f, 0.56f, 0.16f, 1f);
    private static readonly Vector4 ClaimedColor = new(0.67f, 0.42f, 0.88f, 1f);
    private static readonly Vector4 PrivateColor = new(0.25f, 0.75f, 0.82f, 1f);
    private readonly Plugin plugin;
    private string search = "";
    private string newFolderName = "";
    private string roomCode = "";
    private readonly List<ModTransferOfferDto> pendingTransferOffers = [];
    private bool transferInboxOpen;
    private RoomInvite? activeRoomInvite;
    private readonly Queue<AnimationSuggestion> queuedAnimationSuggestions = [];
    private AnimationSuggestion? activeAnimationSuggestion;
    private bool openAnimationSuggestionPopup;
    private readonly List<FreeUsePrompt> queuedFreeUsePrompts = [];
    private FreeUsePrompt? activeFreeUsePrompt;
    private bool openFreeUsePopup;
    private Dictionary<string, List<string>> freeUseOptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> noteBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> correctionBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> folderRenameBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> folderChildBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> folderOpenStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedMods = new(StringComparer.OrdinalIgnoreCase);
    private LibraryScope libraryScope = LibraryScope.All;
    private string? activeCategoryId;
    private string? selectionAnchor;
    private string? selectionAnchorGroup;
    private const string ModPayload = "EMOTELINK_MOD";
    private const string FolderPayload = "EMOTELINK_FOLDER";
    private const string DiscordInviteUrl = "https://discord.com/invite/jhPaQcvWW";
    private bool tutorialActive;
    private TutorialStep tutorialStep;
    private string tutorialFeedback = "";
    private string tutorialAnimationName = "";

    private enum TutorialStep
    {
        Connect,
        Room,
        ConfigureAnimation,
        QueueTogether,
        TriggerPlayback,
        Complete
    }

    private enum LibraryScope
    {
        All,
        Category,
        Uncategorized,
        Private
    }

    public MainWindow(Plugin plugin) : base("Synastry###EmoteLink")
    {
        this.plugin = plugin;
        Size = new Vector2(1180, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    public override void Draw()
    {
        CollectTransferOffers();
        ObserveTutorialState();
        PushUiStyle();
        try
        {
            DrawDeckHeader();
            ImGui.Spacing();
            if (tutorialActive)
            {
                DrawTutorialCard();
                ImGui.Spacing();
            }
            DrawDeckBody();
            DrawAnimationSuggestionPopup();
            DrawFreeUsePopup();
            DrawRoomInvitePopup();
            DrawTransferInboxWindow();
        }
        finally
        {
            PopUiStyle();
        }
    }

    public void StartTutorial()
    {
        tutorialActive = true;
        tutorialStep = TutorialStep.Connect;
        tutorialFeedback = "";
        tutorialAnimationName = "";
        IsOpen = true;
    }

    public void NotifyGroupPlaybackScheduled()
    {
        if (!tutorialActive || tutorialStep < TutorialStep.QueueTogether ||
            tutorialStep > TutorialStep.TriggerPlayback) return;
        tutorialStep = TutorialStep.TriggerPlayback;
        tutorialFeedback = "Everyone is ready. Synastry has received the start signal.";
    }

    public void NotifyRoomInviteSent(string playerName)
    {
        if (!tutorialActive || tutorialStep != TutorialStep.Room) return;
        tutorialFeedback = $"Invitation sent to {playerName}. Stay on this step until they join the room.";
    }

    public void NotifyAnimationStarted()
    {
        if (!tutorialActive || tutorialStep < TutorialStep.QueueTogether) return;
        tutorialStep = TutorialStep.Complete;
        tutorialFeedback = "";
    }

    public void ShowAnimationSuggestion(AnimationSuggestion suggestion)
    {
        if (activeAnimationSuggestion is null)
        {
            activeAnimationSuggestion = suggestion;
            openAnimationSuggestionPopup = true;
        }
        else if (SameSuggestion(activeAnimationSuggestion, suggestion))
        {
            activeAnimationSuggestion = suggestion;
        }
        else if (!queuedAnimationSuggestions.Any(queued => SameSuggestion(queued, suggestion)))
        {
            queuedAnimationSuggestions.Enqueue(suggestion);
        }
        IsOpen = true;
    }

    public void ShowFreeUsePrompt(FreeUsePrompt prompt)
    {
        // A newer choice for the same member replaces the older one.
        queuedFreeUsePrompts.RemoveAll(queued => queued.MemberConnectionId == prompt.MemberConnectionId);
        if (activeFreeUsePrompt is null || activeFreeUsePrompt.MemberConnectionId == prompt.MemberConnectionId)
            BeginFreeUsePrompt(prompt);
        else
            queuedFreeUsePrompts.Add(prompt);
        IsOpen = true;
    }

    private void BeginFreeUsePrompt(FreeUsePrompt prompt)
    {
        activeFreeUsePrompt = prompt;
        freeUseOptions = plugin.GetFreeUseOptionTemplate(prompt.Directory);
        openFreeUsePopup = true;
    }

    private void ObserveTutorialState()
    {
        if (!tutorialActive) return;
        if (tutorialStep == TutorialStep.Connect && plugin.Sync.IsConnected)
        {
            tutorialStep = TutorialStep.Room;
            tutorialFeedback = "Connected. Now make a room for the people who will animate together.";
        }
        if (tutorialStep == TutorialStep.Room && plugin.Sync.Room is { Members.Count: >= 2 })
        {
            tutorialStep = TutorialStep.ConfigureAnimation;
            tutorialFeedback = "Your partner is in the room. Open an animation mod in the center panel.";
        }
        else if (tutorialStep == TutorialStep.Room && plugin.Sync.Room is { Members.Count: 1 } &&
                 !tutorialFeedback.StartsWith("Invitation sent", StringComparison.Ordinal) &&
                 !tutorialFeedback.StartsWith("Room created", StringComparison.Ordinal))
        {
            tutorialFeedback = "Room created. Right-click your partner in-game and choose Invite to Synastry.";
        }

        if (tutorialStep == TutorialStep.QueueTogether && plugin.Sync.Room is { } room)
        {
            var currentReady = room.Members.Any(member =>
                plugin.Sync.IsCurrentMember(member.ConnectionId) && member.Ready);
            var everyoneReady = room.Members.Count >= 2 && room.Members.All(member => member.Ready);
            if (currentReady && !tutorialFeedback.StartsWith("You are queued", StringComparison.Ordinal))
                tutorialFeedback = "You are queued. Your partner must choose their role and become Ready too.";
            if (everyoneReady)
            {
                tutorialStep = TutorialStep.TriggerPlayback;
                tutorialFeedback = "Everyone is ready. The relay will send the synchronized start signal.";
            }
        }
    }

    private void DrawTutorialCard()
    {
        var height = tutorialStep == TutorialStep.ConfigureAnimation ? 130f : 110f;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.075f, 0.07f, 0.052f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, AccentColor);
        ImGui.BeginChild("guided-tutorial", new Vector2(0, height), true);

        var stepNumber = Math.Min((int)tutorialStep + 1, 5);
        ImGui.TextColored(AccentColor, $"GUIDED TUTORIAL   STEP {stepNumber} OF 5");
        ImGui.SameLine();
        DrawTutorialProgress(stepNumber);
        var closeLabel = tutorialStep == TutorialStep.Complete ? "Finish" : "End tutorial";
        var closeWidth = ButtonWidth(closeLabel);
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - closeWidth));
        if (ImGui.SmallButton(closeLabel)) tutorialActive = false;

        var (title, instructions) = TutorialCopy();
        ImGui.TextUnformatted(title);
        ImGui.TextWrapped(instructions);
        if (!string.IsNullOrWhiteSpace(tutorialFeedback))
            ImGui.TextColored(tutorialStep == TutorialStep.ConfigureAnimation ? SomeColor : EveryoneColor,
                tutorialFeedback);

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private void DrawTutorialProgress(int currentStep)
    {
        for (var index = 1; index <= 5; index++)
        {
            ImGui.TextColored(index <= currentStep ? AccentColor : MutedColor, index <= currentStep ? "\u25CF" : "\u25CB");
            if (index < 5) ImGui.SameLine(0, 4f);
        }
    }

    private (string Title, string Instructions) TutorialCopy() => tutorialStep switch
    {
        TutorialStep.Connect => (
            "1. Connect to the animation relay",
            "Press Connect in the highlighted Current Link panel. This only connects Synastry; it does not put you in a room yet."),
        TutorialStep.Room => (
            "2. Create or join a room, then invite your partner",
            "Create a new room or enter a room code to join one. Then right-click your partner in-game and choose Invite to Synastry. You can also copy and share the room code. This step advances when a second person joins."),
        TutorialStep.ConfigureAnimation => (
            "3. Configure and select an animation",
            "Open a mod in the highlighted Animations panel. IMPORTANT: choose the mod option that contains the animation first. If its options are not set, Penumbra has no PAP selected and pressing the animation can appear to do nothing. Then press the animation/role button (not Solo)."),
        TutorialStep.QueueTogether => (
            "4. Queue the animation with another person",
            $"{(tutorialAnimationName.Length > 0 ? tutorialAnimationName + " is selected. " : "")}You should appear Ready in Current Link. Your partner opens the same animation, chooses their role/options, and presses Ready. This step requires at least two room members."),
        TutorialStep.TriggerPlayback => (
            "5. Trigger the synchronized animation",
            "When everyone is Ready, the relay starts the animation automatically. The room leader can use Force start when appropriate. Keep this open until Synastry confirms playback began."),
        _ => (
            "Tutorial complete",
            "The animation started successfully. You connected, formed a room, configured the mod, queued with a partner, and triggered synchronized playback. You can restart this guide at any time from Settings > How To.")
    };

    private bool TryTutorialAnimationAction(string directory, string animationName)
    {
        if (!tutorialActive || tutorialStep != TutorialStep.ConfigureAnimation) return true;
        var groups = plugin.GetOptionGroups(directory);
        var hasSelectedOption = groups.Count == 0 || groups.Any(group =>
            group.Options.Any(option => plugin.IsOptionSelected(directory, group.Name, option)));
        if (!hasSelectedOption)
        {
            tutorialFeedback = "No mod option is selected yet. Pick the option that contains the animation, then press this button again.";
            return false;
        }

        tutorialAnimationName = animationName;
        tutorialStep = TutorialStep.QueueTogether;
        tutorialFeedback = "Animation selected. Waiting for Synastry to mark you Ready.";
        return true;
    }

    private bool TutorialHighlightsLinkPanel => tutorialActive && tutorialStep is
        TutorialStep.Connect or TutorialStep.Room or TutorialStep.QueueTogether or TutorialStep.TriggerPlayback;

    private void DrawTutorialHighlightAroundLastItem()
    {
        var pulse = 0.72f + 0.28f * (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) * 0.5f;
        var color = ImGui.GetColorU32(new Vector4(AccentColor.X, AccentColor.Y, AccentColor.Z, pulse));
        var padding = new Vector2(3f, 3f);
        ImGui.GetWindowDrawList().AddRect(
            ImGui.GetItemRectMin() - padding,
            ImGui.GetItemRectMax() + padding,
            color,
            8f,
            ImDrawFlags.None,
            2.5f);
    }

    private void DrawDeckHeader()
    {
        var contentRight = ImGui.GetWindowContentRegionMax().X;
        ImGui.TextColored(AccentColor, "S Y N A S T R Y");

        var settingsWidth = ButtonWidth("Settings");
        var commandsWidth = ButtonWidth("Emote Commands");
        var headerActionsWidth = commandsWidth + settingsWidth + ImGui.GetStyle().ItemSpacing.X;
        var relayStatus = plugin.Sync.RelayConnectionStatus;
        var relayWidth = ImGui.CalcTextSize(relayStatus).X + 20f;
        var relayX = MathF.Max(ImGui.GetCursorPosX() + 220f, (contentRight - relayWidth) * 0.5f);
        if (relayX + relayWidth < contentRight - headerActionsWidth - 18f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(relayX);
            ImGui.TextColored(plugin.Sync.IsConnected ? CoralColor : MutedColor, "\u25CF");
            ImGui.SameLine();
            ImGui.TextUnformatted(relayStatus);
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(contentRight - headerActionsWidth);
        if (ImGui.Button("Emote Commands", new Vector2(commandsWidth, 0))) plugin.OpenCustomCommands();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Assign custom slash commands to Synastry animations.");
        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(settingsWidth, 0))) plugin.OpenSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open playback, received-animation, community-label, and tutorial settings.");

        ImGui.TextColored(plugin.PenumbraAvailable ? EveryoneColor : SomeColor, "\u25CF");
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.PenumbraAvailable ? "PENUMBRA" : "PENUMBRA UNAVAILABLE");
        ImGui.SameLine(0, 18f);
        ImGui.TextColored(plugin.SimpleHeelsAvailable ? EveryoneColor : MutedColor, "\u25CF");
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.SimpleHeelsAvailable ? "SIMPLE HEELS" : "SIMPLE HEELS NOT FOUND");

        var refreshLabel = plugin.IsRefreshingMods ? "Refreshing..." : "Refresh library";
        var refreshWidth = ButtonWidth(refreshLabel);
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), contentRight - refreshWidth));
        var refreshDisabled = plugin.IsRefreshingMods;
        if (refreshDisabled) ImGui.BeginDisabled();
        if (ImGui.SmallButton(refreshLabel)) plugin.RefreshMods();
        if (refreshDisabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reuse the local animation index and scan only new or changed Penumbra mods.");

        if (!string.IsNullOrWhiteSpace(plugin.Status))
        {
            ImGui.TextColored(plugin.PenumbraAvailable ? EveryoneColor : SomeColor,
                plugin.PenumbraAvailable ? "\u25C6" : "!");
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.Status);
        }
        ImGui.Separator();
    }

    private void DrawDeckBody()
    {
        var legendHeight = ImGui.GetFrameHeightWithSpacing() + 8f;
        var panelsHeight = MathF.Max(120f,
            ImGui.GetContentRegionAvail().Y - legendHeight - ImGui.GetStyle().ItemSpacing.Y);
        ImGui.BeginChild("constellation-panels-row", new Vector2(0, panelsHeight), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("constellation-deck", 3, flags, Vector2.Zero))
        {
            ImGui.TableSetupColumn("Library", ImGuiTableColumnFlags.WidthStretch, 0.24f);
            ImGui.TableSetupColumn("Animations", ImGuiTableColumnFlags.WidthStretch, 0.53f);
            ImGui.TableSetupColumn("Current link", ImGuiTableColumnFlags.WidthStretch, 0.23f);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawLibraryRail();
            ImGui.TableSetColumnIndex(1);
            DrawDeckLibrary();
            ImGui.TableSetColumnIndex(2);
            DrawLinkPanel();
            ImGui.EndTable();
        }
        ImGui.EndChild();

        ImGui.Separator();
        DrawLegend();
    }

    private void DrawLibraryRail()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelColor);
        ImGui.BeginChild("library-rail", Vector2.Zero, true);
        DrawSectionHeading("ANIMATION LIBRARY");
        ImGui.SameLine();
        ImGui.TextDisabled($"{plugin.Mods.Count:N0}");
        ImGui.Spacing();

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = MathF.Max(80f, (ImGui.GetContentRegionAvail().X - spacing) * 0.5f);
        if (ImGui.Button("New folder", new Vector2(buttonWidth, 0))) ImGui.OpenPopup("Create folder");
        ImGui.SameLine();
        if (ImGui.Button("Mark all private", new Vector2(buttonWidth, 0)))
            ImGui.OpenPopup("Mark every animation private?");
        var hasPendingTransfers = pendingTransferOffers.Count > 0;
        if (!hasPendingTransfers) ImGui.BeginDisabled();
        if (hasPendingTransfers) ImGui.PushStyleColor(ImGuiCol.Text, RainbowTextColor());
        if (ImGui.Button(hasPendingTransfers
                ? $"Inbox ({pendingTransferOffers.Count})"
                : "Inbox", new Vector2(-1, 0))) transferInboxOpen = true;
        if (hasPendingTransfers) ImGui.PopStyleColor();
        if (!hasPendingTransfers) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasPendingTransfers
                ? "Open animation transfers waiting for retrieval."
                : "No animation transfers are waiting for retrieval.");
        DrawCreateFolderPopup();
        DrawMarkAllPrivatePopup();

        ImGui.Separator();
        var toolsHeight = ImGui.GetFrameHeightWithSpacing() * 8f + 24f;
        ImGui.BeginChild("folder-constellation", new Vector2(0, -(toolsHeight + ImGui.GetFrameHeightWithSpacing())),
            false);
        DrawScopeItem("all", "All animations", plugin.Mods.Count,
            libraryScope == LibraryScope.All, LibraryScope.All);
        foreach (var category in plugin.GetChildCategories(null).ToList())
            DrawCategoryNavigation(category, 0);
        DrawScopeItem("uncategorized", "Uncategorized", plugin.GetOrganizedMods(null).Count,
            libraryScope == LibraryScope.Uncategorized, LibraryScope.Uncategorized, acceptMods: true);
        DrawScopeItem("private", "Private", plugin.Mods.Count(mod => plugin.IsModPrivate(mod.Directory)),
            libraryScope == LibraryScope.Private, LibraryScope.Private);
        ImGui.EndChild();

        DrawCharacterTools();

        ImGui.PushStyleColor(ImGuiCol.Text, AccentColor);
        if (ImGui.Selectable("Need help?  Open Discord", false)) Util.OpenLink(DiscordInviteUrl);
        ImGui.PopStyleColor();
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawCharacterTools()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NestedPanelColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.BeginChild("character-tools", new Vector2(0, ImGui.GetFrameHeightWithSpacing() * 8f + 16f), true);
        ImGui.TextDisabled("CHARACTER TOOLS");

        ImGui.TextColored(AccentColor, "ANIMATION SPEED");
        var resetWidth = ButtonWidth("Reset");
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - resetWidth));
        if (ImGui.SmallButton("Reset")) plugin.ResetAnimationSpeed();

        var speedPercent = plugin.AnimationSpeedPercent;
        if (!plugin.AnimationSpeedAvailable || plugin.IsAnimationSpeedMatching) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##animation-speed", ref speedPercent, -200, 200, "%d%%"))
            plugin.SetAnimationSpeedPercent(speedPercent);
        if (!plugin.AnimationSpeedAvailable || plugin.IsAnimationSpeedMatching) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!plugin.AnimationSpeedAvailable
                ? "Synastry's animation-speed hook is unavailable for this game version."
                : plugin.IsAnimationSpeedMatching
                ? "Stop target matching before setting a manual animation speed."
                : "Synastry animation speed: -200% reverse, 0% freeze, 100% normal, 200% double speed.");

        var wasMatchingSpeed = plugin.IsAnimationSpeedMatching;
        var matchSpeedDisabled = !plugin.AnimationSpeedAvailable || !plugin.CanMatchAnimationSpeed;
        if (matchSpeedDisabled) ImGui.BeginDisabled();
        if (wasMatchingSpeed)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(AccentColor.X, AccentColor.Y, AccentColor.Z, 0.35f));
        if (ImGui.Button(plugin.AnimationSpeedMatchButtonLabel, new Vector2(-1, 0)))
            plugin.ToggleAnimationSpeedMatch();
        if (wasMatchingSpeed) ImGui.PopStyleColor();
        if (matchSpeedDisabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!plugin.AnimationSpeedAvailable
                ? "Synastry's animation-speed hook is unavailable for this game version."
                : plugin.CanMatchAnimationSpeed
                ? "Continuously match the targeted player's current animation speed. Click again to stop."
                : "Target another player to match their animation speed.");

        ImGui.Separator();

        if (ImGui.Button(plugin.IsAligning ? "Cancel alignment" : "Align to Target", new Vector2(-1, 0)))
            plugin.ToggleAlignment();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Match the target's position and facing. During a looping emote, the target must be within 0.5 yalms.");

        if (!plugin.Sync.IsInRoom) ImGui.BeginDisabled();
        if (ImGui.Button("Emote Sync", new Vector2(-1, 0))) plugin.SyncLobbyEmotes();
        if (!plugin.Sync.IsInRoom) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.Sync.IsInRoom
                ? "Reset animation time for visible members of this Synastry room."
                : "Join a Synastry room to use Emote Sync.");

        if (!plugin.SimpleHeelsAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Livepose", new Vector2(-1, 0))) plugin.OpenSimpleHeelsLivePose();
        if (!plugin.SimpleHeelsAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.SimpleHeelsAvailable
                ? "Open /heels livepose."
                : "Simple Heels is not installed or loaded.");

        if (!plugin.SimpleHeelsAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Temp Pose", new Vector2(-1, 0))) plugin.OpenSimpleHeelsTempOffset();
        if (!plugin.SimpleHeelsAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.SimpleHeelsAvailable
                ? "Open /heels temp."
                : "Simple Heels is not installed or loaded.");

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private void DrawScopeItem(string id, string label, int count, bool selected, LibraryScope scope,
        bool acceptMods = false)
    {
        ImGui.PushID(id);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(AccentColor.X, AccentColor.Y, AccentColor.Z, 0.16f));
        if (ImGui.Selectable($"     {label}  {count:N0}", selected, ImGuiSelectableFlags.None,
                new Vector2(0, 30f)))
        {
            libraryScope = scope;
            activeCategoryId = null;
            ClearModSelection();
        }
        ImGui.PopStyleColor();
        DrawConstellationMarker(selected, 0);
        if (scope == LibraryScope.All) AcceptFolderRootDrop();
        if (acceptMods) AcceptModDrop(null);
        ImGui.PopID();
    }

    private void DrawCategoryNavigation(ModCategory category, int depth)
    {
        ImGui.PushID(category.Id);
        var children = plugin.GetChildCategories(category.Id).ToList();
        var selected = libraryScope == LibraryScope.Category &&
                       activeCategoryId?.Equals(category.Id, StringComparison.OrdinalIgnoreCase) == true;
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth |
                    ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.DefaultOpen |
                    (selected ? ImGuiTreeNodeFlags.Selected : ImGuiTreeNodeFlags.None) |
                    (children.Count == 0 ? ImGuiTreeNodeFlags.Leaf : ImGuiTreeNodeFlags.None);
        var open = ImGui.TreeNodeEx($"{category.Name}  {plugin.GetCategoryModCount(category.Id):N0}##folder", flags);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            libraryScope = LibraryScope.Category;
            activeCategoryId = category.Id;
            ClearModSelection();
        }
        DrawConstellationMarker(selected, depth + 1);
        DrawFolderContextMenu(category);
        DrawFolderDragSource(category);
        AcceptFolderDrop(category);
        AcceptModDrop(category.Id);
        if (open)
        {
            foreach (var child in children) DrawCategoryNavigation(child, depth + 1);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private static void DrawConstellationMarker(bool selected, int depth)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = new Vector2(min.X + 12f + depth * 2f, (min.Y + max.Y) * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        var lineColor = ImGui.GetColorU32(new Vector4(AccentColor.X, AccentColor.Y, AccentColor.Z, 0.3f));
        var nodeColor = ImGui.GetColorU32(selected ? AccentColor : new Vector4(0.72f, 0.75f, 0.78f, 0.9f));
        drawList.AddLine(new Vector2(center.X, min.Y - 3f), new Vector2(center.X, max.Y + 3f), lineColor, 1f);
        drawList.AddCircleFilled(center, selected ? 4f : 2.5f, nodeColor);
        if (selected) drawList.AddCircle(center, 7f, lineColor, 16, 1f);
    }

    private void DrawFolderContextMenu(ModCategory category)
    {
        if (!ImGui.BeginPopupContextItem("folderMenu")) return;
        ImGui.TextDisabled("Folder options");
        if (!folderRenameBuffers.TryGetValue(category.Id, out var rename)) rename = category.Name;
        ImGui.SetNextItemWidth(220f);
        var submitRename = ImGui.InputText("##folderRename", ref rename, 80,
            ImGuiInputTextFlags.EnterReturnsTrue);
        folderRenameBuffers[category.Id] = rename;
        ImGui.SameLine();
        if ((ImGui.SmallButton("Rename") || submitRename) && !string.IsNullOrWhiteSpace(rename))
        {
            plugin.RenameCategory(category.Id, rename);
            folderRenameBuffers[category.Id] = rename.Trim();
            ImGui.CloseCurrentPopup();
        }
        ImGui.Separator();
        ImGui.TextDisabled("Create subfolder");
        if (!folderChildBuffers.TryGetValue(category.Id, out var childName)) childName = "";
        ImGui.SetNextItemWidth(220f);
        var submitChild = ImGui.InputText("##childFolderName", ref childName, 80,
            ImGuiInputTextFlags.EnterReturnsTrue);
        folderChildBuffers[category.Id] = childName;
        ImGui.SameLine();
        if ((ImGui.SmallButton("Create") || submitChild) && !string.IsNullOrWhiteSpace(childName))
        {
            plugin.CreateCategory(childName, category.Id);
            folderChildBuffers[category.Id] = "";
            ImGui.CloseCurrentPopup();
        }
        if (!string.IsNullOrWhiteSpace(category.ParentId) && ImGui.MenuItem("Move folder to top level"))
            plugin.MoveCategory(category.Id, null);
        if (selectedMods.Count > 0 && ImGui.MenuItem($"Move {selectedMods.Count} selected here"))
        {
            plugin.MoveMods(selectedMods, category.Id);
            ClearModSelection();
        }
        ImGui.Separator();
        if (ImGui.MenuItem("Delete folder"))
        {
            if (activeCategoryId?.Equals(category.Id, StringComparison.OrdinalIgnoreCase) == true)
            {
                libraryScope = LibraryScope.All;
                activeCategoryId = null;
            }
            plugin.DeleteCategory(category.Id);
        }
        ImGui.EndPopup();
    }

    private void DrawDeckLibrary()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NestedPanelColor);
        ImGui.BeginChild("animation-deck", Vector2.Zero, true);
        var mods = VisibleDeckMods();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var searchWidth = MathF.Min(320f, MathF.Max(150f, availableWidth * 0.46f));
        var titleWidth = MathF.Max(90f, availableWidth - searchWidth - 105f);
        DrawSectionHeading(TruncateText(CurrentLibraryTitle(), titleWidth));
        ImGui.SameLine();
        ImGui.TextDisabled($"{mods.Count:N0} MODS");

        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - searchWidth));
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##deck-search", "Search animations...", ref search, 128);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Search the entire animation library, regardless of the selected folder.");

        selectedMods.RemoveWhere(directory => !plugin.Mods.Any(mod =>
            mod.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)));
        if (selectedMods.Count > 0)
        {
            ImGui.TextColored(AccentColor, $"{selectedMods.Count} SELECTED");
            ImGui.SameLine();
            ImGui.TextDisabled("Shift-click selects a range");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) ClearModSelection();
        }
        else
        {
            ImGui.TextDisabled(search.Length > 0
                ? "Searching every folder"
                : "Open a row to choose an animation, pose, or option");
        }
        ImGui.Separator();

        ImGui.BeginChild("animation-rows", Vector2.Zero, false);
        if (mods.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(search.Length > 0
                ? "No animations match this search."
                : "This view does not contain any animation mods yet.");
        }
        foreach (var mod in mods)
        {
            var categoryId = FindCategoryForMod(mod.Directory);
            DrawModRow(mod, categoryId, mods);
            ImGui.Spacing();
        }
        ImGui.EndChild();
        ImGui.EndChild();
        if (tutorialActive && tutorialStep == TutorialStep.ConfigureAnimation)
            DrawTutorialHighlightAroundLastItem();
        ImGui.PopStyleColor();
    }

    private List<(string Directory, string Name)> VisibleDeckMods()
    {
        IEnumerable<(string Directory, string Name)> source;
        if (search.Length > 0)
        {
            source = plugin.Mods.Where(MatchesSearch);
        }
        else
        {
            source = libraryScope switch
            {
                LibraryScope.Category when activeCategoryId is not null =>
                    plugin.GetOrganizedMods(activeCategoryId),
                LibraryScope.Uncategorized => plugin.GetOrganizedMods(null),
                LibraryScope.Private => plugin.Mods.Where(mod => plugin.IsModPrivate(mod.Directory)),
                _ => plugin.Mods
            };
        }
        return plugin.OrderModsForLibrary(
            source.DistinctBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase));
    }

    private string CurrentLibraryTitle()
    {
        if (search.Length > 0) return "SEARCH RESULTS";
        return libraryScope switch
        {
            LibraryScope.Category when activeCategoryId is not null =>
                plugin.GetCategoryPath(activeCategoryId).ToUpperInvariant(),
            LibraryScope.Uncategorized => "UNCATEGORIZED",
            LibraryScope.Private => "PRIVATE",
            _ => "ALL ANIMATIONS"
        };
    }

    private string? FindCategoryForMod(string directory) => plugin.Categories.FirstOrDefault(category =>
        category.ModDirectories.Any(item => item.Equals(directory, StringComparison.OrdinalIgnoreCase)))?.Id;

    private void DrawLinkPanel()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelColor);
        ImGui.BeginChild("current-link", Vector2.Zero, true);
        DrawSectionHeading("CURRENT LINK");
        ImGui.Separator();
        ImGui.Spacing();

        if (!plugin.Sync.IsConnected)
        {
            ImGui.TextColored(MutedColor, "NO ACTIVE LINK");
            ImGui.TextWrapped("Connect to create or join a synchronized animation room.");
            ImGui.Spacing();
            if (DrawPrimaryButton("Connect", -1f)) plugin.ConnectSync();
            if (tutorialActive && tutorialStep == TutorialStep.Connect)
                DrawTutorialHighlightAroundLastItem();
            ImGui.TextDisabled($"Character: {plugin.SyncDisplayName}");
            ImGui.EndChild();
            if (TutorialHighlightsLinkPanel) DrawTutorialHighlightAroundLastItem();
            ImGui.PopStyleColor();
            return;
        }

        ImGui.TextColored(EveryoneColor, "\u25CF  CONNECTED");
        ImGui.TextDisabled(plugin.SyncDisplayName);
        ImGui.TextWrapped(plugin.Sync.RelayConnectionStatus);
        ImGui.Spacing();

        var room = plugin.Sync.Room;
        if (!plugin.Sync.IsInRoom || room is null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("ROOM CODE");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##deck-room", "Enter a room code", ref roomCode, 8);
            if (DrawPrimaryButton("Join room", -1f)) plugin.JoinSyncRoom(roomCode);
            if (ImGui.Button("Create a new room", new Vector2(-1, 0))) plugin.CreateSyncRoom();
            if (ImGui.Button("Disconnect", new Vector2(-1, 0))) plugin.DisconnectSync();
            ImGui.EndChild();
            if (TutorialHighlightsLinkPanel) DrawTutorialHighlightAroundLastItem();
            ImGui.PopStyleColor();
            return;
        }

        ImGui.Separator();
        ImGui.TextDisabled("ROOM CODE");
        ImGui.TextColored(AccentColor, room.RoomCode);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy"))
        {
            ImGui.SetClipboardText(room.RoomCode);
            plugin.NotifyRoomCodeCopied(room.RoomCode);
        }

        DrawMemberConstellation(room);
        if (plugin.Sync.IsRoomLeader)
        {
            foreach (var member in room.Members.Where(member =>
                         !plugin.Sync.IsCurrentMember(member.ConnectionId)))
            {
                ImGui.PushID(member.ConnectionId);
                if (ImGui.SmallButton($"Remove {member.DisplayName}")) plugin.RemoveSyncMember(member);
                ImGui.PopID();
            }
        }
        var wasFreeUse = plugin.IsFreeUseEnabled;
        var freeUse = wasFreeUse;
        if (wasFreeUse) ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
        if (ImGui.Checkbox("FREE USE mode", ref freeUse)) plugin.SetFreeUse(freeUse);
        if (wasFreeUse) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Let room members choose your role and mod options.\n" +
                             "Their choice is applied temporarily and you are readied automatically.\n" +
                             "Private mods are never used. Turn it off at any time.");
        ImGui.Separator();
        var readyMembers = room.Members.Count(member => member.Ready);
        var allReady = room.Members.Count > 0 && readyMembers == room.Members.Count;
        ImGui.TextColored(allReady ? EveryoneColor : SomeColor,
            allReady ? "\u25CF  READY TO PLAY" : $"\u25CF  {readyMembers}/{room.Members.Count} READY");
        ImGui.TextWrapped("Choose an animation in the center deck. Playback begins when each assigned member is ready.");

        if (ImGui.Button("Cancel ready", new Vector2(-1, 0))) plugin.CancelSyncReady();
        if (plugin.Sync.IsRoomLeader && DrawPrimaryButton("Force start", -1f)) plugin.ForceSyncStart();
        if (ImGui.Button("Leave room", new Vector2(-1, 0))) plugin.LeaveSyncRoom();
        ImGui.EndChild();
        if (TutorialHighlightsLinkPanel) DrawTutorialHighlightAroundLastItem();
        ImGui.PopStyleColor();
    }

    private void DrawMemberConstellation(RoomStateDto room)
    {
        var members = room.Members.Take(8).ToList();
        var height = Math.Clamp(70f + members.Count * 35f, 120f, 300f);
        var size = new Vector2(ImGui.GetContentRegionAvail().X, height);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##member-constellation", size);
        var drawList = ImGui.GetWindowDrawList();
        var x = origin.X + size.X * 0.5f;
        var top = origin.Y + 24f;
        var step = members.Count <= 1 ? 0f : (height - 48f) / (members.Count - 1);
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            var center = new Vector2(x, top + step * index);
            var isCurrentMember = plugin.Sync.IsCurrentMember(member.ConnectionId);
            var color = member.Ready ? EveryoneColor : isCurrentMember ? AccentColor : CoralColor;
            var packed = ImGui.GetColorU32(color);
            var faint = ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.28f));
            if (index > 0)
            {
                var previous = new Vector2(x, top + step * (index - 1));
                drawList.AddLine(previous, center, faint, 1f);
            }
            drawList.AddCircle(center, 13f, faint, 24, 1f);
            drawList.AddCircleFilled(center, member.Ready ? 4f : 3f, packed);
            drawList.AddLine(new Vector2(center.X - 7f, center.Y), new Vector2(center.X + 7f, center.Y), packed, 1f);
            drawList.AddLine(new Vector2(center.X, center.Y - 7f), new Vector2(center.X, center.Y + 7f), packed, 1f);
            var suffix = member.IsLeader ? "  HOST" : member.Ready ? "  READY" : isCurrentMember ? "  YOU" : "  WAITING";
            var label = member.DisplayName + suffix + (member.FreeUse ? "  FREE USE" : "");
            var labelSize = ImGui.CalcTextSize(label);
            drawList.AddText(new Vector2(x - labelSize.X * 0.5f, center.Y + 17f),
                ImGui.GetColorU32(member.Ready ? TextColor : MutedColor), label);
        }
        if (room.Members.Count > members.Count)
            ImGui.TextDisabled($"+{room.Members.Count - members.Count} more room member(s)");
    }

    private void DrawCloudInboxButton()
    {
        ImGui.Spacing();
        var hasPendingTransfers = pendingTransferOffers.Count > 0;
        if (!hasPendingTransfers) ImGui.BeginDisabled();
        if (hasPendingTransfers) ImGui.PushStyleColor(ImGuiCol.Text, RainbowTextColor());
        if (ImGui.Button(hasPendingTransfers
                ? $"Animations in cloud ({pendingTransferOffers.Count})"
                : "No animations in cloud", new Vector2(-1, 0))) transferInboxOpen = true;
        if (hasPendingTransfers) ImGui.PopStyleColor();
        if (!hasPendingTransfers) ImGui.EndDisabled();
    }

    private void DrawDeckFooter()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(90f, (ImGui.GetContentRegionAvail().X - spacing * 3f) / 4f);
        if (!plugin.Sync.IsInRoom) ImGui.BeginDisabled();
        if (ImGui.Button("EmoteSync", new Vector2(width, 34f))) plugin.SyncLobbyEmotes();
        if (!plugin.Sync.IsInRoom) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(plugin.IsAligning ? "Cancel alignment" : "Align to target", new Vector2(width, 34f)))
            plugin.ToggleAlignment();

        ImGui.SameLine();
        if (!plugin.SimpleHeelsAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Temp Offset", new Vector2(width, 34f))) plugin.OpenSimpleHeelsTempOffset();
        if (!plugin.SimpleHeelsAvailable) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!plugin.SimpleHeelsAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Livepose", new Vector2(width, 34f))) plugin.OpenSimpleHeelsLivePose();
        if (!plugin.SimpleHeelsAvailable) ImGui.EndDisabled();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(plugin.PenumbraAvailable ? EveryoneColor : SomeColor, "\u25CF");
        ImGui.SameLine();
        ImGui.TextUnformatted(plugin.PenumbraAvailable ? "PENUMBRA CONNECTED" : "PENUMBRA UNAVAILABLE");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.PenumbraAvailable
                ? "Penumbra is available and animation mods can be activated."
                : "Penumbra is not currently available.");

        ImGui.SameLine(0, 18f);
        ImGui.TextColored(plugin.SimpleHeelsAvailable ? EveryoneColor : MutedColor, "\u25CF");
        ImGui.SameLine();
        ImGui.TextUnformatted(plugin.SimpleHeelsAvailable ? "SIMPLE HEELS CONNECTED" : "SIMPLE HEELS NOT FOUND");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.SimpleHeelsAvailable
                ? "Simple Heels is loaded; Temp Offset and Livepose are available below."
                : "Install and load Simple Heels to use Temp Offset and Livepose.");

        var refreshLabel = plugin.IsRefreshingMods ? "Refreshing..." : "Refresh";
        var refreshWidth = ButtonWidth(refreshLabel);
        var settingsWidth = ButtonWidth("Settings");
        var totalWidth = refreshWidth + settingsWidth + ImGui.GetStyle().ItemSpacing.X;
        var actionStart = ImGui.GetWindowContentRegionMax().X - totalWidth;
        if (actionStart > ImGui.GetCursorPosX() + 24f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(actionStart);
        }
        else
        {
            ImGui.Spacing();
        }

        var refreshDisabled = plugin.IsRefreshingMods;
        if (refreshDisabled) ImGui.BeginDisabled();
        if (ImGui.Button(refreshLabel, new Vector2(refreshWidth, 0)))
            plugin.RefreshMods();
        if (refreshDisabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reuse the local animation index and scan only new or changed Penumbra mods.");
        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(settingsWidth, 0))) plugin.OpenSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open playback, received-animation, community-label, and tutorial settings.");

        ImGui.SetCursorPosX(actionStart);
        ImGui.PushStyleColor(ImGuiCol.Text, AccentColor);
        if (ImGui.Selectable("Need Help?  discord.com/invite/jhPaQcvWW", false,
                ImGuiSelectableFlags.None, new Vector2(totalWidth, 0)))
            Util.OpenLink(DiscordInviteUrl);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the Synastry Discord invite in your browser.");
    }

    private void DrawStatusLine()
    {
        ImGui.TextColored(plugin.PenumbraAvailable ? EveryoneColor : SomeColor, plugin.PenumbraAvailable ? "✓" : "!");
        ImGui.SameLine();
        ImGui.TextWrapped(plugin.Status);
    }

    private void DrawGroupPlay()
    {
        var room = plugin.Sync.Room;
        var height = !plugin.Sync.IsConnected
            ? 198f
            : !plugin.Sync.IsInRoom || room is null
                ? 226f
                : MathF.Min(370f, 228f + room.Members.Count * 31f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.BeginChild("group-play-card", new Vector2(0, height), true);

        DrawSectionHeading("GROUP PLAY");

        if (!plugin.Sync.IsConnected)
        {
            ImGui.TextDisabled($"Not connected  —  {plugin.Sync.Status}");
            ImGui.TextDisabled($"Character: {plugin.SyncDisplayName}");
            ImGui.Spacing();
            if (DrawPrimaryButton("Connect", 116f)) plugin.ConnectSync();
            ImGui.SameLine();
            ImGui.TextDisabled("Connect to create or join a synchronized room.");
            DrawCharacterUtilities();
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            return;
        }

        ImGui.TextDisabled("Connected as");
        ImGui.SameLine();
        ImGui.TextUnformatted(plugin.SyncDisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled($"—  {plugin.Sync.RelayConnectionStatus}");

        if (!plugin.Sync.IsInRoom || room is null)
        {
            ImGui.Spacing();
            if (DrawPrimaryButton("Create room", 126f)) plugin.CreateSyncRoom();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(126f);
            ImGui.InputTextWithHint("##room", "Room code", ref roomCode, 8);
            ImGui.SameLine();
            if (ImGui.Button("Join room")) plugin.JoinSyncRoom(roomCode);
            ImGui.SameLine();
            if (ImGui.Button("Disconnect")) plugin.DisconnectSync();
            ImGui.Spacing();
            ImGui.TextDisabled("Create a room, or enter a code shared by another player.");
            DrawCharacterUtilities();
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            return;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Room code");
        ImGui.SameLine();
        if (ImGui.Button($"{room.RoomCode}  Copy##copyRoomCode"))
        {
            ImGui.SetClipboardText(room.RoomCode);
            plugin.NotifyRoomCodeCopied(room.RoomCode);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy room code");

        var leaveWidth = ButtonWidth("Leave room");
        var cancelWidth = ButtonWidth("Cancel ready");
        var forceWidth = plugin.Sync.IsRoomLeader ? ButtonWidth("Force start") : 0f;
        var controlsWidth = leaveWidth + cancelWidth + forceWidth +
                            ImGui.GetStyle().ItemSpacing.X * (plugin.Sync.IsRoomLeader ? 2 : 1);
        var controlsStart = ImGui.GetWindowContentRegionMax().X - controlsWidth;
        if (controlsStart > ImGui.GetCursorPosX() + 16f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(controlsStart);
        }
        if (ImGui.Button("Cancel ready", new Vector2(cancelWidth, 0))) plugin.CancelSyncReady();
        ImGui.SameLine();
        if (ImGui.Button("Leave room", new Vector2(leaveWidth, 0))) plugin.LeaveSyncRoom();
        if (plugin.Sync.IsRoomLeader)
        {
            ImGui.SameLine();
            if (DrawPrimaryButton("Force start", forceWidth)) plugin.ForceSyncStart();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Start every room member who currently has a role or animation prepared.");
        }

        ImGui.Spacing();
        foreach (var member in room.Members)
        {
            ImGui.PushID(member.ConnectionId);
            ImGui.Separator();
            ImGui.TextColored(member.Ready ? EveryoneColor : SomeColor, "\u25CF");
            ImGui.SameLine();
            ImGui.TextUnformatted(member.DisplayName);
            if (member.IsLeader)
            {
                ImGui.SameLine();
                ImGui.TextColored(AccentColor, "HOST");
            }

            var removeWidth = plugin.Sync.IsRoomLeader && !plugin.Sync.IsCurrentMember(member.ConnectionId)
                ? ButtonWidth("Remove")
                : 0f;
            var state = member.Ready ? "READY" : "WAITING";
            var stateWidth = ImGui.CalcTextSize(state).X;
            var stateX = ImGui.GetWindowContentRegionMax().X - stateWidth -
                         (removeWidth > 0 ? removeWidth + ImGui.GetStyle().ItemSpacing.X : 0);
            ImGui.SameLine();
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), stateX));
            ImGui.TextColored(member.Ready ? EveryoneColor : SomeColor, state);
            if (plugin.Sync.IsRoomLeader && !plugin.Sync.IsCurrentMember(member.ConnectionId))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) plugin.RemoveSyncMember(member);
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Choose an animation below. Playback starts when everyone has prepared their role.");
        DrawCharacterUtilities();
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private void DrawCharacterUtilities()
    {
        ImGui.Spacing();
        if (ImGui.Button(plugin.IsAligning ? "Cancel alignment" : "Align / teleport to target"))
            plugin.ToggleAlignment();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Match your character's position and facing direction to the current target.");

        ImGui.Spacing();
        var hasPendingTransfers = pendingTransferOffers.Count > 0;
        var transferLabel = hasPendingTransfers
            ? "You have Animations pending retrieval"
            : "No Animations in the Cloud";
        if (!hasPendingTransfers) ImGui.BeginDisabled();
        if (hasPendingTransfers) ImGui.PushStyleColor(ImGuiCol.Text, RainbowTextColor());
        var openInbox = ImGui.Button($"{transferLabel}##animationCloud", new Vector2(ImGui.GetContentRegionAvail().X, 0));
        if (hasPendingTransfers) ImGui.PopStyleColor();
        if (!hasPendingTransfers) ImGui.EndDisabled();
        if (openInbox) transferInboxOpen = true;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasPendingTransfers
                ? $"Open the retrieval queue for {pendingTransferOffers.Count} pending animation(s)."
                : "No animation transfers are waiting for you.");
    }

    private void DrawLibrary()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.BeginChild("animation-library-card", new Vector2(0, -44f), true);

        DrawSectionHeading("ANIMATION LIBRARY");
        ImGui.SameLine();
        ImGui.TextDisabled($"{plugin.Mods.Count} MODS");

        var newFolderWidth = ButtonWidth("New folder");
        var privateWidth = ButtonWidth("Mark all private");
        var searchWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - newFolderWidth - privateWidth -
                                          ImGui.GetStyle().ItemSpacing.X * 2);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##search", "Search animation mods...", ref search, 128);
        ImGui.SameLine();
        if (ImGui.Button("New folder", new Vector2(newFolderWidth, 0))) ImGui.OpenPopup("Create folder");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Create a top-level folder. Right-click any folder to create a subfolder inside it.");
        ImGui.SameLine();
        if (ImGui.Button("Mark all private", new Vector2(privateWidth, 0)))
            ImGui.OpenPopup("Mark every animation private?");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide every animation mod from group matching and transfers.");
        DrawCreateFolderPopup();
        DrawMarkAllPrivatePopup();

        selectedMods.RemoveWhere(directory => !plugin.Mods.Any(mod =>
            mod.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)));
        ImGui.Spacing();
        var selectionFooterHeight = selectedMods.Count > 0
            ? ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y
            : 0f;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NestedPanelColor);
        ImGui.BeginChild("mods", new Vector2(0, -(38f + selectionFooterHeight)), true);
        foreach (var category in plugin.GetChildCategories(null).ToList()) DrawCategory(category);
        DrawModGroup(null, "Uncategorized", true);
        ImGui.EndChild();
        ImGui.PopStyleColor();

        if (selectedMods.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(AccentColor, $"{selectedMods.Count} MOD{(selectedMods.Count == 1 ? "" : "S")} SELECTED");
            ImGui.SameLine();
            ImGui.TextDisabled("Shift-click another mod to select a range.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear selection")) ClearModSelection();
        }

        DrawLegend();
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private void DrawFooterActions()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(80f, (ImGui.GetContentRegionAvail().X - spacing * 2f) / 3f);

        if (!plugin.Sync.IsInRoom) ImGui.BeginDisabled();
        if (ImGui.Button("EmoteSync", new Vector2(width, 34f))) plugin.SyncLobbyEmotes();
        if (!plugin.Sync.IsInRoom) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.Sync.IsInRoom
                ? "Reset animation time only for visible members of this Synastry room."
                : "Join a Synastry room to use lobby-only EmoteSync.");

        ImGui.SameLine();
        if (!plugin.SimpleHeelsAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Temp Offset", new Vector2(width, 34f))) plugin.OpenSimpleHeelsTempOffset();
        if (!plugin.SimpleHeelsAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.SimpleHeelsAvailable
                ? "Open /heels temp."
                : "Simple Heels is not installed or loaded.");

        ImGui.SameLine();
        if (!plugin.SimpleHeelsAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Livepose", new Vector2(width, 34f))) plugin.OpenSimpleHeelsLivePose();
        if (!plugin.SimpleHeelsAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.SimpleHeelsAvailable
                ? "Open /heels livepose."
                : "Simple Heels is not installed or loaded.");
    }

    private static void DrawLegend()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("What do the colors mean?");
        ImGui.SameLine(0, 18f);
        DrawLegendItem(EveryoneColor, "Everyone");
        ImGui.SameLine(0, 24f);
        DrawLegendItem(SomeColor, "Some members");
        ImGui.SameLine(0, 24f);
        DrawLegendItem(ClaimedColor, "Suggested");
        ImGui.SameLine(0, 24f);
        DrawLegendItem(PrivateColor, "Private");
        ImGui.SameLine(0, 24f);
        DrawLegendItem(MutedColor, "No match");
    }

    private static void DrawLegendItem(Vector4 color, string label)
    {
        ImGui.TextColored(color, "\u25CF");
        ImGui.SameLine();
        ImGui.TextDisabled(label);
    }

    private void DrawAnimationSuggestionPopup()
    {
        if (activeAnimationSuggestion is not null &&
            !plugin.IsAnimationSuggestionActive(activeAnimationSuggestion))
            activeAnimationSuggestion = null;
        if (activeAnimationSuggestion is null && queuedAnimationSuggestions.TryDequeue(out var queued))
        {
            activeAnimationSuggestion = queued;
            openAnimationSuggestionPopup = true;
        }
        if (activeAnimationSuggestion is null) return;

        const string popupTitle = "Incoming animation###SynastryAnimationSuggestion";
        if (openAnimationSuggestionPopup)
        {
            ImGui.OpenPopup(popupTitle);
            openAnimationSuggestionPopup = false;
        }

        ImGui.SetNextWindowSize(new Vector2(760f, 580f), ImGuiCond.Appearing);
        var popupOpen = true;
        if (ImGui.BeginPopupModal(popupTitle, ref popupOpen, ImGuiWindowFlags.NoCollapse))
        {
            var suggestion = activeAnimationSuggestion;
            if (suggestion is null)
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                return;
            }

            ImGui.TextColored(ClaimedColor, $"{suggestion.SuggestedBy} activated an animation");
            ImGui.TextWrapped("Their selected animation is highlighted in purple. Choose the role or animation you want to prepare, or ignore this request.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextColored(AccentColor, suggestion.ModName);

            var localMod = plugin.Mods.FirstOrDefault(mod =>
                mod.Directory.Equals(suggestion.Directory, StringComparison.OrdinalIgnoreCase));
            var animationActivated = false;
            if (string.IsNullOrWhiteSpace(localMod.Directory))
            {
                ImGui.TextDisabled("This animation mod is no longer available in your local Synastry library.");
            }
            else
            {
                plugin.EnsureDetectedEmotes(localMod.Directory, localMod.Name);
                var groups = plugin.GetOptionGroups(localMod.Directory);
                var detectedPoses = plugin.GetDetectedPoses(localMod.Directory);
                var detectedEmotes = plugin.GetDetectedEmotes(localMod.Directory);
                var selectedAnimation = SuggestionAnimationName(
                    suggestion.ActivatedTrigger, detectedPoses, detectedEmotes);
                if (selectedAnimation.Length > 0)
                    ImGui.TextColored(ClaimedColor, $"Selected: {selectedAnimation}");
                var detailParts = new List<string>();
                if (groups.Count > 0)
                    detailParts.Add($"{groups.Count} option group{(groups.Count == 1 ? "" : "s")}");
                if (detectedPoses.Count > 0)
                    detailParts.Add($"{detectedPoses.Count} pose{(detectedPoses.Count == 1 ? "" : "s")}");
                if (detectedEmotes.Count > 0)
                    detailParts.Add($"{detectedEmotes.Count} emote{(detectedEmotes.Count == 1 ? "" : "s")}");
                if (detailParts.Count > 0) ImGui.TextDisabled(string.Join("  \u00B7  ", detailParts));

                ImGui.Spacing();
                ImGui.BeginChild("suggested-animation-mod", new Vector2(0, -42f), true);
                animationActivated = DrawOptions(
                    localMod, groups, detectedPoses, detectedEmotes, suggestionMode: true);
                ImGui.EndChild();
            }

            if (animationActivated)
            {
                activeAnimationSuggestion = null;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                if (ImGui.Button("Ignore", new Vector2(120f, 0)))
                {
                    plugin.IgnoreAnimationSuggestion(suggestion);
                    activeAnimationSuggestion = null;
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Close this request without preparing an animation.");
            }

            ImGui.EndPopup();
        }

        if (!popupOpen && activeAnimationSuggestion is { } dismissed)
        {
            plugin.IgnoreAnimationSuggestion(dismissed);
            activeAnimationSuggestion = null;
        }
    }

    private void DrawFreeUsePopup()
    {
        if (activeFreeUsePrompt is not null && !plugin.IsFreeUseMemberAvailable(activeFreeUsePrompt.MemberConnectionId))
            activeFreeUsePrompt = null;
        while (activeFreeUsePrompt is null && queuedFreeUsePrompts.Count > 0)
        {
            var next = queuedFreeUsePrompts[0];
            queuedFreeUsePrompts.RemoveAt(0);
            if (plugin.IsFreeUseMemberAvailable(next.MemberConnectionId)) BeginFreeUsePrompt(next);
        }

        const string popupTitle = "FREE USE###SynastryFreeUse";
        if (activeFreeUsePrompt is not { } prompt)
        {
            // The member left or turned FREE USE off while the prompt was showing; a modal that is
            // never submitted again would keep blocking the window, so close it explicitly.
            if (ImGui.IsPopupOpen(popupTitle) && ImGui.BeginPopupModal(popupTitle))
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
            return;
        }

        // Keep asking until the modal is showing; a role picked inside another modal opens this one next frame.
        if (openFreeUsePopup && !ImGui.IsPopupOpen(popupTitle)) ImGui.OpenPopup(popupTitle);

        ImGui.SetNextWindowSize(new Vector2(640f, 540f), ImGuiCond.Appearing);
        var popupOpen = true;
        if (ImGui.BeginPopupModal(popupTitle, ref popupOpen, ImGuiWindowFlags.NoCollapse))
        {
            openFreeUsePopup = false;
            ImGui.TextColored(ClaimedColor, $"{prompt.MemberName} is in FREE USE mode");
            ImGui.TextWrapped("Choose the role they will play. Their Synastry applies the options below temporarily and readies them automatically.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextColored(AccentColor, prompt.ModName);
            var poses = plugin.GetDetectedPoses(prompt.Directory);
            var emotes = plugin.GetDetectedEmotes(prompt.Directory);
            var ownRole = SuggestionAnimationName(prompt.OwnTrigger, poses, emotes);
            if (ownRole.Length > 0) ImGui.TextColored(ClaimedColor, $"Your role: {ownRole}");

            string? chosenTrigger = null;
            ImGui.BeginChild("free-use-choice", new Vector2(0, -42f), true);
            ImGui.TextColored(AccentColor, $"ROLE FOR {prompt.MemberName.ToUpperInvariant()}");
            foreach (var pose in poses)
                if (DrawFreeUseRoleButton(prompt, "$detected-pose", $"{pose.Kind}:{pose.Index}",
                        PoseDisplayName(pose), $"pose:{pose.Kind}:{pose.Index}"))
                    chosenTrigger = $"pose:{pose.Kind}:{pose.Index}";
            foreach (var emote in emotes)
                if (DrawFreeUseRoleButton(prompt, "$detected-emote", emote.Id.ToString(),
                        $"{emote.Name} (ID {emote.Id})", $"emote:{emote.Id}"))
                    chosenTrigger = $"emote:{emote.Id}";
            if (poses.Count == 0 && emotes.Count == 0)
                ImGui.TextDisabled("This mod has no detected roles to choose.");
            DrawFreeUseOptions(prompt.Directory);
            ImGui.EndChild();

            if (chosenTrigger is not null)
            {
                plugin.DirectFreeUse(prompt, chosenTrigger, freeUseOptions);
                activeFreeUsePrompt = null;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                if (ImGui.Button("Skip", new Vector2(120f, 0)))
                {
                    activeFreeUsePrompt = null;
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Do not choose a role for {prompt.MemberName} this time.");
            }
            ImGui.EndPopup();
        }

        if (!popupOpen) activeFreeUsePrompt = null;
    }

    private bool DrawFreeUseRoleButton(
        FreeUsePrompt prompt,
        string group,
        string option,
        string animationName,
        string trigger)
    {
        var note = plugin.GetOptionNote(prompt.Directory, group, option);
        var label = string.IsNullOrWhiteSpace(note) ? animationName : $"{note}  ({animationName})";
        var isOwnRole = trigger.Equals(prompt.OwnTrigger, StringComparison.OrdinalIgnoreCase);
        if (isOwnRole)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ClaimedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.58f, 1f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.3f, 0.88f, 1f));
        }
        var clicked = ImGui.Button($"{label}##{trigger}", new Vector2(-1, 0));
        if (isOwnRole) ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isOwnRole
                ? $"Your role. {prompt.MemberName} can play it too."
                : $"{prompt.MemberName} plays {animationName}.");
        return clicked;
    }

    private void DrawFreeUseOptions(string directory)
    {
        var groups = plugin.GetOptionGroups(directory);
        if (groups.Count == 0) return;
        ImGui.Spacing();
        ImGui.TextColored(AccentColor, "THEIR MOD OPTIONS");
        ImGui.TextDisabled("Copied from your setup. Changes here apply to them only.");
        foreach (var group in groups)
        {
            ImGui.PushID("free-use-" + group.Name);
            var selected = freeUseOptions.GetValueOrDefault(group.Name) ?? [];
            if (!group.IsMultiSelect)
            {
                var current = group.Options.FirstOrDefault(option =>
                    selected.Contains(option, StringComparer.OrdinalIgnoreCase));
                ImGui.SetNextItemWidth(MathF.Min(260f, MathF.Max(120f, ImGui.GetContentRegionAvail().X * 0.52f)));
                if (ImGui.BeginCombo(group.Name, current ?? "Mod default"))
                {
                    foreach (var option in group.Options)
                        if (ImGui.Selectable(option, option.Equals(current, StringComparison.OrdinalIgnoreCase)))
                            freeUseOptions[group.Name] = [option];
                    ImGui.EndCombo();
                }
            }
            else
            {
                ImGui.TextUnformatted(group.Name);
                ImGui.Indent();
                foreach (var option in group.Options)
                {
                    var enabled = selected.Contains(option, StringComparer.OrdinalIgnoreCase);
                    if (!ImGui.Checkbox(option, ref enabled)) continue;
                    selected = selected.Where(item => !item.Equals(option, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (enabled) selected.Add(option);
                    freeUseOptions[group.Name] = selected;
                }
                ImGui.Unindent();
            }
            ImGui.PopID();
        }
    }

    private static bool SameSuggestion(AnimationSuggestion left, AnimationSuggestion right) =>
        left.SuggestedBy.Equals(right.SuggestedBy, StringComparison.OrdinalIgnoreCase) &&
        left.ModKey.Equals(right.ModKey, StringComparison.OrdinalIgnoreCase);

    private static string SuggestionAnimationName(
        string trigger,
        IReadOnlyList<PoseTarget> detectedPoses,
        IReadOnlyList<EmoteTarget> detectedEmotes)
    {
        if (trigger.StartsWith("emote:", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(trigger["emote:".Length..], out var emoteId))
        {
            var emote = detectedEmotes.FirstOrDefault(candidate => candidate.Id == emoteId);
            if (emote is not null) return $"{emote.Name} (ID {emote.Id})";
        }
        if (trigger.StartsWith("pose:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trigger["pose:".Length..].Split(':', 2);
            if (parts.Length == 2 && Enum.TryParse<PoseKind>(parts[0], true, out var kind) &&
                byte.TryParse(parts[1], out var index))
            {
                var pose = detectedPoses.FirstOrDefault(candidate =>
                    candidate.Kind == kind && candidate.Index == index);
                if (pose is not null) return PoseDisplayName(pose);
            }
        }
        return "";
    }

    private void DrawRoomInvitePopup()
    {
        if (activeRoomInvite is null && plugin.TryTakeRoomInvite(out var invite))
        {
            activeRoomInvite = invite;
            ImGui.OpenPopup("Synastry room invitation");
        }
        if (!ImGui.BeginPopupModal("Synastry room invitation", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var active = activeRoomInvite;
        if (active is null) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        ImGui.TextWrapped($"{active.SenderName} invited you to join a Synastry room.");
        ImGui.TextDisabled($"Room code: {active.RoomCode}");
        ImGui.Spacing();
        if (ImGui.Button("Accept", new Vector2(110, 0)))
        {
            plugin.AcceptRoomInvite(active);
            activeRoomInvite = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Decline", new Vector2(110, 0)))
        {
            plugin.DeclineRoomInvite(active);
            activeRoomInvite = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void CollectTransferOffers()
    {
        pendingTransferOffers.RemoveAll(offer => offer.ExpiresAt <= DateTimeOffset.UtcNow);
        var received = false;
        while (plugin.TryTakeTransferOffer(out var offer))
        {
            if (pendingTransferOffers.Any(existing => existing.TransferId == offer.TransferId)) continue;
            pendingTransferOffers.Add(offer);
            received = true;
        }
        if (received) transferInboxOpen = true;
    }

    private void DrawTransferInboxWindow()
    {
        if (!transferInboxOpen) return;
        ImGui.SetNextWindowSize(new Vector2(520, 320), ImGuiCond.Appearing);
        if (!ImGui.Begin("Animations pending retrieval###SynastryTransferInbox", ref transferInboxOpen))
        {
            ImGui.End();
            return;
        }

        if (pendingTransferOffers.Count == 0)
        {
            ImGui.TextDisabled("No Animations in the Cloud");
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("Choose any animation you want to retrieve. Unhandled animations stay in this queue until they expire.");
        ImGui.Spacing();
        for (var index = 0; index < pendingTransferOffers.Count; index++)
        {
            var offer = pendingTransferOffers[index];
            ImGui.PushID(offer.TransferId);
            ImGui.TextColored(AccentColor, offer.ModName);
            ImGui.TextDisabled($"From {offer.SenderName}  •  {offer.Size / 1024f / 1024f:F1} MB");
            var remaining = offer.ExpiresAt - DateTimeOffset.UtcNow;
            ImGui.TextDisabled($"Available for {Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes))} more minute(s)");
            if (ImGui.Button("Retrieve", new Vector2(110, 0)))
            {
                plugin.AcceptModTransfer(offer);
                pendingTransferOffers.RemoveAt(index--);
            }
            else
            {
                ImGui.SameLine();
                if (ImGui.Button("Decline", new Vector2(110, 0)))
                {
                    plugin.DeclineModTransfer(offer);
                    pendingTransferOffers.RemoveAt(index--);
                }
            }
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.End();
    }

    private static Vector4 RainbowTextColor()
    {
        var hue = (float)(ImGui.GetTime() * 0.2 % 1.0);
        var scaled = hue * 6f;
        var fraction = scaled - MathF.Floor(scaled);
        return ((int)scaled % 6) switch
        {
            0 => new Vector4(1f, fraction, 0f, 1f),
            1 => new Vector4(1f - fraction, 1f, 0f, 1f),
            2 => new Vector4(0f, 1f, fraction, 1f),
            3 => new Vector4(0f, 1f - fraction, 1f, 1f),
            4 => new Vector4(fraction, 0f, 1f, 1f),
            _ => new Vector4(1f, 0f, 1f - fraction, 1f)
        };
    }

    private void DrawCreateFolderPopup()
    {
        if (!ImGui.BeginPopup("Create folder")) return;
        ImGui.TextUnformatted("Folder name");
        ImGui.SetNextItemWidth(260);
        var submit = ImGui.InputText("##folderName", ref newFolderName, 80,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.Button("Create") || submit)
        {
            plugin.CreateCategory(newFolderName);
            newFolderName = "";
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawMarkAllPrivatePopup()
    {
        if (!ImGui.BeginPopupModal("Mark every animation private?", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("Every animation mod currently in the library will be private.");
        ImGui.TextDisabled("You can unhide individual mods from their right-click menu.");
        ImGui.Spacing();
        if (ImGui.Button("Mark all private", new Vector2(130f, 0)))
        {
            plugin.MarkAllModsPrivate();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(90f, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawCategory(ModCategory category)
    {
        ImGui.PushID(category.Id);
        var children = plugin.GetChildCategories(category.Id).ToList();
        var visible = search.Length == 0 || CategoryMatchesSearch(category);
        if (visible)
        {
            var hadOpenState = folderOpenStates.TryGetValue(category.Id, out var previousOpen);
            if (search.Length > 0) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            else if (hadOpenState) ImGui.SetNextItemOpen(previousOpen, ImGuiCond.Always);
            var totalMods = plugin.GetCategoryModCount(category.Id);
            var open = ImGui.CollapsingHeader($"{category.Name}  {totalMods}##folderHeader",
                ImGuiTreeNodeFlags.DefaultOpen);
            DrawFolderDragSource(category);
            var dragHovered = AcceptFolderDrop(category) | AcceptModDrop(category.Id);
            // Dear ImGui automatically opens tree headers when a drag payload is held
            // over them. Keep the user's prior state so moving a mod does not expand
            // its destination folder as a side effect.
            folderOpenStates[category.Id] = dragHovered && hadOpenState ? previousOpen : open;
            if (ImGui.BeginPopupContextItem("folderMenu"))
            {
                ImGui.TextDisabled("Folder options");
                if (!folderRenameBuffers.TryGetValue(category.Id, out var rename)) rename = category.Name;
                ImGui.SetNextItemWidth(220f);
                var submitRename = ImGui.InputText("##folderRename", ref rename, 80,
                    ImGuiInputTextFlags.EnterReturnsTrue);
                folderRenameBuffers[category.Id] = rename;
                ImGui.SameLine();
                if ((ImGui.SmallButton("Rename") || submitRename) && !string.IsNullOrWhiteSpace(rename))
                {
                    plugin.RenameCategory(category.Id, rename);
                    folderRenameBuffers[category.Id] = rename.Trim();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.Separator();
                ImGui.TextDisabled("Create subfolder");
                if (!folderChildBuffers.TryGetValue(category.Id, out var childName)) childName = "";
                ImGui.SetNextItemWidth(220f);
                var submitChild = ImGui.InputText("##childFolderName", ref childName, 80,
                    ImGuiInputTextFlags.EnterReturnsTrue);
                folderChildBuffers[category.Id] = childName;
                ImGui.SameLine();
                if ((ImGui.SmallButton("Create") || submitChild) && !string.IsNullOrWhiteSpace(childName))
                {
                    plugin.CreateCategory(childName, category.Id);
                    folderChildBuffers[category.Id] = "";
                    ImGui.CloseCurrentPopup();
                }
                if (!string.IsNullOrWhiteSpace(category.ParentId) && ImGui.MenuItem("Move folder to top level"))
                    plugin.MoveCategory(category.Id, null);
                if (selectedMods.Count > 0 && ImGui.MenuItem($"Move {selectedMods.Count} selected here"))
                {
                    plugin.MoveMods(selectedMods, category.Id);
                    ClearModSelection();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Delete folder")) plugin.DeleteCategory(category.Id);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Mods directly in this folder move to Uncategorized. Subfolders move up one level.");
                ImGui.EndPopup();
            }
            if (open)
            {
                ImGui.Indent(18f);
                foreach (var child in children) DrawCategory(child);
                DrawModGroup(category.Id, null, false);
                ImGui.Unindent(18f);
            }
        }
        ImGui.PopID();
    }

    private bool CategoryMatchesSearch(ModCategory category, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(category.Id)) return false;
        if (plugin.GetOrganizedMods(category.Id).Any(MatchesSearch)) return true;
        return plugin.GetChildCategories(category.Id).Any(child => CategoryMatchesSearch(child, visited));
    }

    private void DrawModGroup(string? categoryId, string? heading, bool drawDropTarget)
    {
        var mods = plugin.GetOrganizedMods(categoryId);
        if (heading is not null)
        {
            var open = ImGui.CollapsingHeader($"{heading}  {mods.Count}##modGroup",
                ImGuiTreeNodeFlags.DefaultOpen);
            if (categoryId is null) AcceptFolderRootDrop();
            if (drawDropTarget) AcceptModDrop(categoryId);
            if (!open) return;
        }

        var drewAny = false;
        var visibleMods = mods.Where(MatchesSearch).ToList();
        foreach (var mod in visibleMods)
        {
            drewAny = true;
            DrawModRow(mod, categoryId, visibleMods);
        }
        if (!drewAny)
            ImGui.TextDisabled(search.Length == 0 ? "  Drop mods here" : "  No matching animation mods");
    }

    private void DrawModRow(
        (string Directory, string Name) mod,
        string? categoryId,
        IReadOnlyList<(string Directory, string Name)> groupOrder)
    {
        ImGui.PushID(mod.Directory);
        var groups = plugin.GetOptionGroups(mod.Directory);
        var detectedPoses = plugin.GetDetectedPoses(mod.Directory);
        var detectedEmotes = plugin.GetDetectedEmotes(mod.Directory);
        var match = plugin.GetModMatch(mod.Directory);
        var selectedBy = plugin.GetRemoteModSelector(mod.Directory);
        var isPrivate = plugin.IsModPrivate(mod.Directory);
        var hasMatchColor = match.Members > 1 && match.Matches > 1;
        var statusLabel = selectedBy is not null
            ? $"Suggested by {selectedBy}"
            : isPrivate
                ? "Private"
                : hasMatchColor
                    ? match.Matches >= match.Members ? "Everyone" : "Some members"
                    : "";
        var statusColor = selectedBy is not null
            ? ClaimedColor
            : isPrivate
                ? PrivateColor
                : match.Matches >= match.Members ? EveryoneColor : SomeColor;
        if (selectedBy is not null)
            ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
        else if (isPrivate)
            ImGui.PushStyleColor(ImGuiCol.Text, PrivateColor);
        else if (hasMatchColor)
            ImGui.PushStyleColor(ImGuiCol.Text,
                match.Matches >= match.Members ? EveryoneColor : SomeColor);
        // Every indexed animation can be expanded. Emote enrichment is intentionally lazy:
        // Penumbra's changed-item IPC runs only when the user opens this row, never hundreds
        // of times during a bulk library refresh.
        const bool hasDetails = true;
        var sendWidth = plugin.Sync.IsInRoom && !isPrivate
            ? ButtonWidth("Send")
            : 0;
        var statusWidth = string.IsNullOrEmpty(statusLabel) ? 0f : ImGui.CalcTextSize(statusLabel).X;
        var controlsWidth = sendWidth + statusWidth;
        if (sendWidth > 0 && statusWidth > 0) controlsWidth += ImGui.GetStyle().ItemSpacing.X;
        var labelWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - controlsWidth - 42f);
        var displayName = TruncateText(mod.Name, labelWidth);
        var selected = selectedMods.Contains(mod.Directory);
        // Keep the tree hitbox confined to the animation label. A framed/span row owns the
        // trailing pixels too, which prevents the Send button from receiving mouse input.
        var treeFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick |
                        (selected ? ImGuiTreeNodeFlags.Selected : ImGuiTreeNodeFlags.None);
        ImGui.PushStyleColor(ImGuiCol.Header,
            selected
                ? new Vector4(AccentColor.X, AccentColor.Y, AccentColor.Z, 0.22f)
                : new Vector4(FrameColor.X, FrameColor.Y, FrameColor.Z, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,
            new Vector4(AccentColor.X, AccentColor.Y, AccentColor.Z, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,
            new Vector4(AccentColor.X, AccentColor.Y, AccentColor.Z, 0.2f));
        var open = hasDetails
            ? ImGui.TreeNodeEx(displayName, treeFlags)
            : ImGui.Selectable(displayName, selected, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(labelWidth, 0));
        ImGui.PopStyleColor(3);
        if (selectedBy is not null || isPrivate || hasMatchColor) ImGui.PopStyleColor();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            HandleModSelection(mod.Directory, categoryId, groupOrder);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !selectedMods.Contains(mod.Directory))
            SelectOnly(mod.Directory, categoryId);
        if (ImGui.IsItemHovered())
        {
            if (selectedBy is not null) ImGui.SetTooltip($"{selectedBy} selected an option in this mod.");
            else if (!displayName.Equals(mod.Name, StringComparison.Ordinal)) ImGui.SetTooltip(mod.Name);
        }

        if (ImGui.BeginPopupContextItem("modMenu"))
        {
            var targets = selectedMods.Contains(mod.Directory) ? selectedMods.ToList() : [mod.Directory];
            if (targets.Count > 1) ImGui.TextDisabled($"{targets.Count} selected mods");
            var anyPublic = targets.Any(directory => !plugin.IsModPrivate(directory));
            var anyPrivate = targets.Any(plugin.IsModPrivate);
            if (anyPublic && ImGui.MenuItem(targets.Count > 1 ? "Mark selected private" : "Mark private"))
                plugin.SetModsPrivate(targets, true);
            if (anyPrivate && ImGui.MenuItem(targets.Count > 1 ? "Unmark selected private" : "Unmark private"))
                plugin.SetModsPrivate(targets, false);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Private mods are not advertised or transferable in group play.");
            if (targets.Count == 1 && plugin.IsModConverted(mod.Directory))
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Restore original animation files"))
                    plugin.RestoreConvertedMod(mod.Directory, mod.Name);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Restore Synastry's backup and remove the permanent carrier conversion.");
            }
            if (ImGui.BeginMenu(targets.Count > 1 ? "Move selected to folder" : "Move to folder"))
            {
                if (ImGui.MenuItem("Uncategorized"))
                {
                    plugin.MoveMods(targets, null);
                    ClearModSelection();
                }
                foreach (var category in plugin.Categories)
                {
                    if (!ImGui.MenuItem(plugin.GetCategoryPath(category.Id))) continue;
                    plugin.MoveMods(targets, category.Id);
                    ClearModSelection();
                }
                ImGui.EndMenu();
            }
            if (targets.Count > 1 && ImGui.MenuItem("Clear selection")) ClearModSelection();
            ImGui.EndPopup();
        }

        if (ImGui.BeginDragDropSource())
        {
            ImGui.SetDragDropPayload(ModPayload, Encoding.UTF8.GetBytes(mod.Directory));
            ImGui.TextUnformatted(selectedMods.Contains(mod.Directory) && selectedMods.Count > 1
                ? $"Move {selectedMods.Count} selected mods"
                : mod.Name);
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(ModPayload);
            var source = ReadPayload(payload);
            if (source is not null && source != mod.Directory)
                MoveDroppedMods(source, categoryId, mod.Directory);
            ImGui.EndDragDropTarget();
        }

        if (controlsWidth > 0)
        {
            ImGui.SameLine();
            // The full-width framed tree row leaves its same-line cursor at the far edge.
            // Anchor the controls to the content edge directly so Send remains visible.
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - controlsWidth);
            if (statusWidth > 0) ImGui.TextColored(statusColor, statusLabel);
            if (sendWidth > 0)
            {
                if (statusWidth > 0) ImGui.SameLine();
                if (ImGui.SmallButton("Send")) plugin.SendMod(mod.Directory, mod.Name);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Offer this public mod to the room (75 MB maximum).");
            }
        }
        var detailParts = new List<string>();
        if (plugin.IsModConverted(mod.Directory)) detailParts.Add("permanent carrier");
        if (groups.Count > 0) detailParts.Add($"{groups.Count} option group{(groups.Count == 1 ? "" : "s")}");
        if (detectedPoses.Count > 0) detailParts.Add($"{detectedPoses.Count} pose{(detectedPoses.Count == 1 ? "" : "s")}");
        if (detectedEmotes.Count > 0) detailParts.Add($"{detectedEmotes.Count} emote{(detectedEmotes.Count == 1 ? "" : "s")}");
        if (detailParts.Count == 0) detailParts.Add("Open to inspect animations and options");
        if (!open) ImGui.Indent(25f);
        ImGui.TextDisabled(string.Join("  \u00B7  ", detailParts));
        if (!open) ImGui.Unindent(25f);

        if (hasDetails && open)
        {
            plugin.EnsureDetectedEmotes(mod.Directory, mod.Name);
            detectedEmotes = plugin.GetDetectedEmotes(mod.Directory);
            DrawOptions(mod, groups, detectedPoses, detectedEmotes);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private bool DrawOptions(
        (string Directory, string Name) mod,
        IReadOnlyList<ModOptionGroup> groups,
        IReadOnlyList<PoseTarget> detectedPoses,
        IReadOnlyList<EmoteTarget> detectedEmotes,
        bool suggestionMode = false)
    {
        var animationActivated = false;
        if (detectedPoses.Count > 0 || detectedEmotes.Count > 0)
            animationActivated = DrawAnimationButtons(mod, detectedPoses, detectedEmotes, suggestionMode);
        if (groups.Count > 0)
        {
            plugin.EnsureDefaultOptionSelections(mod.Directory);
            ImGui.Spacing();
            ImGui.TextColored(AccentColor, "MOD OPTIONS");
        }
        foreach (var group in groups)
        {
            ImGui.PushID(group.Name);
            var groupSelectedBy = plugin.GetRemoteGroupSelector(mod.Directory, group.Name);
            if (!group.IsMultiSelect)
            {
                var selectedOption = group.Options.FirstOrDefault(option =>
                    plugin.IsOptionSelected(mod.Directory, group.Name, option));
                var preview = selectedOption ?? "Mod default";
                if (groupSelectedBy is not null) ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
                ImGui.SetNextItemWidth(MathF.Min(260f, MathF.Max(120f, ImGui.GetContentRegionAvail().X * 0.52f)));
                var comboOpen = ImGui.BeginCombo(group.Name, preview);
                var groupHovered = ImGui.IsItemHovered();
                if (groupSelectedBy is not null) ImGui.PopStyleColor();
                if (groupHovered && groupSelectedBy is not null)
                    ImGui.SetTooltip($"{groupSelectedBy} selected an option in this group.");
                if (comboOpen)
                {
                    foreach (var option in group.Options)
                    {
                        ImGui.PushID(option);
                        var selected = option.Equals(selectedOption, StringComparison.OrdinalIgnoreCase);
                        var selectedBy = plugin.GetRemoteOptionSelector(mod.Directory, group.Name, option);
                        var pose = plugin.GetOptionPose(mod.Directory, group.Name, option);
                        var label = pose is null ? option : $"{option}  ({PoseDisplayName(pose)})";
                        if (selectedBy is not null) ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
                        if (ImGui.Selectable(label, selected))
                            plugin.SetOptionSelected(mod.Directory, group.Name, option, true,
                                multiSelect: false, broadcastSelection: false);
                        if (selected) ImGui.SetItemDefaultFocus();
                        if (selectedBy is not null)
                        {
                            ImGui.PopStyleColor();
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{selectedBy} selected this option.");
                        }
                        ImGui.PopID();
                    }
                    ImGui.EndCombo();
                }
                ImGui.PopID();
                continue;
            }
            if (groupSelectedBy is not null) ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
            var groupFlags = suggestionMode ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            var groupOpen = ImGui.TreeNodeEx(group.Name, groupFlags);
            if (groupSelectedBy is not null)
            {
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{groupSelectedBy} selected an option in this group.");
            }
            if (groupOpen)
            {
                foreach (var option in group.Options)
                {
                    ImGui.PushID(option);
                    var selected = plugin.IsOptionSelected(mod.Directory, group.Name, option);
                    var selectedBy = plugin.GetRemoteOptionSelector(mod.Directory, group.Name, option);
                    if (selectedBy is not null)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
                        ImGui.PushStyleColor(ImGuiCol.CheckMark, ClaimedColor);
                    }
                    if (ImGui.Checkbox(option, ref selected))
                    {
                        plugin.SetOptionSelected(mod.Directory, group.Name, option, selected,
                            group.IsMultiSelect, broadcastSelection: false);
                    }
                    if (selectedBy is not null)
                    {
                        ImGui.PopStyleColor(2);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{selectedBy} selected this option.");
                    }
                    var pose = plugin.GetOptionPose(mod.Directory, group.Name, option);
                    if (pose is not null)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(PoseDisplayName(pose));
                    }
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        return animationActivated;
    }

    private bool DrawAnimationButtons(
        (string Directory, string Name) mod,
        IReadOnlyList<PoseTarget> detectedPoses,
        IReadOnlyList<EmoteTarget> detectedEmotes,
        bool suggestionMode)
    {
        var animationActivated = false;
        var actionCount = detectedPoses.Count + detectedEmotes.Count;
        var available = ImGui.GetContentRegionAvail().X;
        var columns = Math.Clamp((int)(available / 230f), 1, 3);
        var rows = (actionCount + columns - 1) / columns;
        var height = ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y * (rows + 2) +
                     ImGui.GetFrameHeight() * rows + ImGui.GetStyle().WindowPadding.Y * 2;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, NestedPanelColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.BeginChild("animation-actions", new Vector2(0, height), true);
        ImGui.TextColored(AccentColor, "EMOTES & POSES");

        available = ImGui.GetContentRegionAvail().X;
        var cellWidth = (available - ImGui.GetStyle().ItemSpacing.X * (columns - 1)) / columns;
        var soloWidth = plugin.Sync.IsInRoom && !suggestionMode ? ButtonWidth("Solo") : 0f;
        var actionWidth = cellWidth - soloWidth - (soloWidth > 0 ? ImGui.GetStyle().ItemSpacing.X : 0f);
        var cellIndex = 0;

        foreach (var pose in detectedPoses)
        {
            if (cellIndex % columns != 0) ImGui.SameLine();
            ImGui.PushID($"detected-{pose.Kind}-{pose.Index}");
            if (DrawRoleActionButton(mod.Directory, "$detected-pose", $"{pose.Kind}:{pose.Index}",
                    PoseDisplayName(pose), showAssignmentWhenUnlabeled: true, width: actionWidth))
            {
                var animationName = PoseDisplayName(pose);
                if (suggestionMode || TryTutorialAnimationAction(mod.Directory, animationName))
                {
                    plugin.ActivateDetectedPose(mod.Directory, mod.Name, pose);
                    animationActivated = true;
                }
            }
            if (plugin.Sync.IsInRoom && !suggestionMode)
            {
                ImGui.SameLine();
                if (ImGui.Button("Solo", new Vector2(soloWidth, 0)))
                    plugin.ActivateDetectedPoseSolo(mod.Directory, mod.Name, pose);
            }
            ImGui.PopID();
            cellIndex++;
        }

        foreach (var emote in detectedEmotes)
        {
            if (cellIndex % columns != 0) ImGui.SameLine();
            ImGui.PushID($"emote-{emote.Id}");
            var animationName = $"{emote.Name} (ID {emote.Id})";
            if (DrawRoleActionButton(mod.Directory, "$detected-emote", emote.Id.ToString(),
                    animationName, showAssignmentWhenUnlabeled: true, width: actionWidth))
            {
                if (suggestionMode || TryTutorialAnimationAction(mod.Directory, animationName))
                {
                    plugin.ActivateDetectedEmote(mod.Directory, mod.Name, emote);
                    animationActivated = true;
                }
            }
            if (plugin.Sync.IsInRoom && !suggestionMode)
            {
                ImGui.SameLine();
                if (ImGui.Button("Solo", new Vector2(soloWidth, 0)))
                    plugin.ActivateDetectedEmoteSolo(mod.Directory, mod.Name, emote);
            }
            ImGui.PopID();
            cellIndex++;
        }

        ImGui.EndChild();
        if (tutorialActive && tutorialStep == TutorialStep.ConfigureAnimation)
            DrawTutorialHighlightAroundLastItem();
        ImGui.PopStyleColor(2);
        return animationActivated;
    }

    private bool DrawRoleActionButton(
        string directory,
        string group,
        string option,
        string animationName,
        bool showAssignmentWhenUnlabeled = false,
        float width = 0f)
    {
        var key = NoteKey(directory, group, option);
        var savedNote = plugin.GetOptionNote(directory, group, option);
        if (!noteBuffers.TryGetValue(key, out var note) ||
            (string.IsNullOrWhiteSpace(note) && !string.IsNullOrWhiteSpace(savedNote))) note = savedNote;
        noteBuffers[key] = note;
        var hasRole = !string.IsNullOrWhiteSpace(note);
        var label = hasRole
            ? plugin.Sync.IsInRoom ? $"{note} - Ready" : note
            : showAssignmentWhenUnlabeled
                ? plugin.Sync.IsInRoom ? $"{animationName} - Ready" : animationName
                : plugin.Sync.IsInRoom ? "Ready" : "Activate";
        var selectedBy = plugin.GetRemoteDetectedTriggerSelector(directory, group, option);
        if (selectedBy is not null)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ClaimedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.58f, 1f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.3f, 0.88f, 1f));
        }
        var clicked = ImGui.Button(label, new Vector2(width, 0));
        if (selectedBy is not null) ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(selectedBy is null
                ? $"{animationName}\nRight-click to edit the role label."
                : $"{animationName}\nSelected by {selectedBy}.\nRight-click to edit the role label.");

        if (ImGui.BeginPopupContextItem("editRole"))
        {
            ImGui.TextUnformatted(animationName);
            ImGui.SetNextItemWidth(220);
            var submit = ImGui.InputTextWithHint("##role", "Actor role...", ref note, 21,
                ImGuiInputTextFlags.EnterReturnsTrue);
            noteBuffers[key] = note;
            if (ImGui.Button("Save") || submit)
            {
                plugin.SaveOptionNote(directory, group, option, note);
                noteBuffers[key] = plugin.GetOptionNote(directory, group, option);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                plugin.SaveOptionNote(directory, group, option, "");
                noteBuffers[key] = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                noteBuffers[key] = plugin.GetOptionNote(directory, group, option);
                ImGui.CloseCurrentPopup();
            }
            if (hasRole)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Report a bad shared label");
                if (!correctionBuffers.TryGetValue(key, out var correction)) correction = "";
                ImGui.SetNextItemWidth(220);
                ImGui.InputTextWithHint("##correction", "Suggested correction...", ref correction, 21);
                correctionBuffers[key] = correction;
                if (ImGui.Button("Report correction") && !string.IsNullOrWhiteSpace(correction))
                {
                    plugin.ReportBadRoleLabel(directory, group, option, correction);
                    noteBuffers[key] = plugin.GetOptionNote(directory, group, option);
                    correctionBuffers[key] = "";
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Applies immediately for you. Five matching reports update the community label.");
            }
            ImGui.EndPopup();
        }
        return clicked;
    }

    private static void PushUiStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));

        ImGui.PushStyleColor(ImGuiCol.Text, TextColor);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, MutedColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, FrameColor);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, FrameHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.Button, FrameColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, FrameHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.205f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, FrameColor);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, FrameHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.18f, 0.205f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Separator, BorderColor);
    }

    private static void PopUiStyle()
    {
        ImGui.PopStyleColor(13);
        ImGui.PopStyleVar(5);
    }

    private static void DrawSectionHeading(string label) => ImGui.TextColored(AccentColor, label);

    private static bool DrawPrimaryButton(string label, float width)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActiveColor);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.08f, 0.07f, 0.04f, 1f));
        var clicked = ImGui.Button(label, new Vector2(width, 0));
        ImGui.PopStyleColor(4);
        return clicked;
    }

    private static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    private static string TruncateText(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        const string suffix = "\u2026";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + suffix).X <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return text[..low].TrimEnd() + suffix;
    }

    private static string NoteKey(string directory, string group, string option) =>
        directory + "\n" + group + "\n" + option;

    private static string PoseDisplayName(PoseTarget pose) => pose.Kind switch
    {
        PoseKind.Sit => $"Chair Sit {pose.Index}",
        PoseKind.GroundSit => $"Ground Sit {pose.Index}",
        PoseKind.Doze => $"Doze {pose.Index}",
        _ => $"Idle {pose.Index}"
    };

    private void DrawFolderDragSource(ModCategory category)
    {
        if (!ImGui.BeginDragDropSource()) return;
        ImGui.SetDragDropPayload(FolderPayload, Encoding.UTF8.GetBytes(category.Id));
        ImGui.TextUnformatted($"{category.Name}  ({plugin.GetCategoryModCount(category.Id)} mods)");
        if (plugin.GetChildCategories(category.Id).Count > 0)
            ImGui.TextDisabled("Includes all nested folders");
        ImGui.EndDragDropSource();
    }

    private bool AcceptFolderDrop(ModCategory target)
    {
        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        if (!ImGui.BeginDragDropTarget()) return false;
        var payload = ImGui.AcceptDragDropPayload(FolderPayload);
        var source = ReadPayload(payload);
        var hovered = source is not null;
        if (source is not null)
        {
            var topEdge = ImGui.GetIO().MousePos.Y <=
                          itemMin.Y + MathF.Min(7f, (itemMax.Y - itemMin.Y) * 0.35f);
            ImGui.SetTooltip(topEdge ? $"Place before {target.Name}" : $"Move inside {target.Name}");
            if (payload.IsDelivery())
            {
                if (topEdge) plugin.MoveCategoryBefore(source, target.Id);
                else plugin.MoveCategory(source, target.Id);
            }
        }
        ImGui.EndDragDropTarget();
        return hovered;
    }

    private void AcceptFolderRootDrop()
    {
        if (!ImGui.BeginDragDropTarget()) return;
        var payload = ImGui.AcceptDragDropPayload(FolderPayload);
        var source = ReadPayload(payload);
        if (source is not null)
        {
            ImGui.SetTooltip("Move folder to the top level");
            if (payload.IsDelivery()) plugin.MoveCategory(source, null);
        }
        ImGui.EndDragDropTarget();
    }

    private bool AcceptModDrop(string? categoryId)
    {
        if (!ImGui.BeginDragDropTarget()) return false;
        var payload = ImGui.AcceptDragDropPayload(ModPayload);
        var source = ReadPayload(payload);
        var hovered = source is not null;
        if (source is not null && payload.IsDelivery()) MoveDroppedMods(source, categoryId);
        ImGui.EndDragDropTarget();
        return hovered;
    }

    private void HandleModSelection(
        string directory,
        string? categoryId,
        IReadOnlyList<(string Directory, string Name)> groupOrder)
    {
        var groupKey = categoryId ?? "\0uncategorized";
        var io = ImGui.GetIO();
        if (io.KeyShift && selectionAnchor is not null && selectionAnchorGroup == groupKey)
        {
            var anchorIndex = FindModIndex(groupOrder, selectionAnchor);
            var clickedIndex = FindModIndex(groupOrder, directory);
            if (anchorIndex >= 0 && clickedIndex >= 0)
            {
                if (!io.KeyCtrl) selectedMods.Clear();
                var start = Math.Min(anchorIndex, clickedIndex);
                var end = Math.Max(anchorIndex, clickedIndex);
                for (var index = start; index <= end; index++) selectedMods.Add(groupOrder[index].Directory);
                return;
            }
        }

        if (io.KeyCtrl)
        {
            if (!selectedMods.Add(directory)) selectedMods.Remove(directory);
        }
        else
        {
            selectedMods.Clear();
            selectedMods.Add(directory);
        }
        selectionAnchor = directory;
        selectionAnchorGroup = groupKey;
    }

    private static int FindModIndex(
        IReadOnlyList<(string Directory, string Name)> mods,
        string directory)
    {
        for (var index = 0; index < mods.Count; index++)
            if (mods[index].Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private void SelectOnly(string directory, string? categoryId)
    {
        selectedMods.Clear();
        selectedMods.Add(directory);
        selectionAnchor = directory;
        selectionAnchorGroup = categoryId ?? "\0uncategorized";
    }

    private void ClearModSelection()
    {
        selectedMods.Clear();
        selectionAnchor = null;
        selectionAnchorGroup = null;
    }

    private void MoveDroppedMods(string source, string? categoryId, string? beforeDirectory = null)
    {
        if (selectedMods.Contains(source) && selectedMods.Count > 1)
        {
            if (beforeDirectory is not null && selectedMods.Contains(beforeDirectory)) return;
            plugin.MoveMods(selectedMods, categoryId, beforeDirectory);
            ClearModSelection();
            return;
        }
        plugin.MoveMod(source, categoryId, beforeDirectory);
    }

    private bool MatchesSearch((string Directory, string Name) mod) =>
        search.Length == 0 || mod.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        mod.Directory.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static unsafe string? ReadPayload(ImGuiPayloadPtr payload)
    {
        if (payload.Handle == null || payload.DataSize <= 0) return null;
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(payload.Data, payload.DataSize));
    }
}

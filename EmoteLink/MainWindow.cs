using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace EmoteLink;

/// <summary>
/// The Synastry window: folders on the left, the selected folder's animations in the middle, the
/// room on the right and the character tools along the bottom. See Theme for the look.
/// </summary>
public sealed class MainWindow : Window
{
    private const float HeaderHeight = 54f;
    private const float FooterHeight = 46f;
    private const float FoldersWidth = 200f;
    private const float RoomWidth = 290f;
    private const float RowHeight = 38f;
    private const string ModPayload = "EMOTELINK_MOD";
    private const string FolderPayload = "EMOTELINK_FOLDER";
    private const string DiscordInviteUrl = "https://discord.com/invite/jhPaQcvWW";

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
    private readonly HashSet<string> collapsedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedMods = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> openMods = new(StringComparer.OrdinalIgnoreCase);
    private LibraryScope libraryScope = LibraryScope.All;
    private string? activeCategoryId;
    private string? selectionAnchor;
    private string? selectionAnchorGroup;
    private bool openCreateFolder;
    private bool openMarkAllPrivate;
    // The role this player last readied, shown as theirs while the room still has them ready.
    private string? readyRoleKey;
    private string lastLineUpStatus = "";
    private long lineUpStatusAt;
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
        Size = new Vector2(1100, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse;
    }

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.Pop();
    }

    public override void Draw()
    {
        CollectTransferOffers();
        ObserveTutorialState();
        var width = ImGui.GetContentRegionAvail().X;

        DrawHeader(width);
        if (!plugin.PenumbraAvailable) DrawBanner(width, "Penumbra isn't running, so animations can't be switched on.");
        if (tutorialActive) DrawTutorialCard(width);
        var bodyHeight = ImGui.GetContentRegionAvail().Y - FooterHeight * Theme.Scale;
        DrawBody(width, bodyHeight);
        DrawFooter(width);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * Theme.Scale);
        DrawMenuPopup();
        ImGui.PopStyleVar();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 20f) * Theme.Scale);
        DrawCreateFolderPopup();
        DrawMarkAllPrivatePopup();
        DrawAnimationSuggestionPopup();
        DrawFreeUsePopup();
        DrawRoomInvitePopup();
        ImGui.PopStyleVar();
        DrawTransferInboxWindow();
    }

    // ---- Notifications from the plugin ----------------------------------------------------------

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
        tutorialFeedback = "Everyone is ready. Synastry has the start signal.";
    }

    public void NotifyRoomInviteSent(string playerName)
    {
        if (!tutorialActive || tutorialStep != TutorialStep.Room) return;
        tutorialFeedback = $"Invite sent to {playerName}. This step finishes when they join.";
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

    // ---- Header, banner, footer -----------------------------------------------------------------

    private void DrawHeader(float width)
    {
        var s = Theme.Scale;
        var height = HeaderHeight * s;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        using (Theme.TitleFont())
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + 22f * s, start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f));
            ImGui.TextUnformatted("Synastry");
        }
        ImGui.SameLine(0, 12f * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X,
            start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f + 2f * s));
        Theme.Quiet(plugin.IsRefreshingMods
            ? "Updating your library…"
            : $"{plugin.Mods.Count:N0} animation{(plugin.Mods.Count == 1 ? "" : "s")}");

        var button = 30f * s;
        var right = start.X + width - 12f * s;
        var searchWidth = MathF.Min(270f * s, width * 0.28f);
        ImGui.SetCursorScreenPos(new Vector2(right - button * 2 - 10f * s - searchWidth,
            start.Y + (height - ImGui.GetFrameHeight()) * 0.5f));
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##search", "Search every folder", ref search, 128);

        ImGui.SetCursorScreenPos(new Vector2(right - button * 2 - 4f * s, start.Y + (height - button) * 0.5f));
        if (IconButton("##menu", button, DrawMenuIcon)) ImGui.OpenPopup("synastry-menu");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Menu");
        ImGui.SetCursorScreenPos(new Vector2(right - button, start.Y + (height - button) * 0.5f));
        if (IconButton("##close", button, DrawCloseIcon)) IsOpen = false;

        Theme.HorizontalLine(draw, new Vector2(start.X, start.Y + height), width);
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height + 1f));
        ImGui.Dummy(Vector2.Zero);
    }

    private static void DrawBanner(float width, string message)
    {
        var s = Theme.Scale;
        var start = ImGui.GetCursorScreenPos();
        var height = 30f * s;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(Theme.WithAlpha(Theme.Rose, 0.12f)));
        draw.AddText(start + new Vector2(22f * s, (height - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(Theme.RoseHover), message);
        Theme.HorizontalLine(draw, new Vector2(start.X, start.Y + height), width);
        ImGui.Dummy(new Vector2(width, height + 1f));
    }

    private void DrawFooter(float width)
    {
        var s = Theme.Scale;
        var height = FooterHeight * s;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        Theme.HorizontalLine(draw, start, width);
        var y = start.Y + (height - ImGui.GetFrameHeight()) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(start.X + 12f * s, y));

        if (Theme.Text(plugin.IsAligning ? "Cancel alignment" : "Align to target", Theme.Soft))
            plugin.ToggleAlignment();
        Tooltip("Walk onto your target's spot and facing. During a looping emote they must be within half a yalm.");

        ImGui.SameLine(0, 2f * s);
        Disabled(!plugin.Sync.IsInRoom, () =>
        {
            if (Theme.Text("Emote sync")) plugin.SyncLobbyEmotes();
        });
        Tooltip(plugin.Sync.IsInRoom
            ? "Restart everyone in the room's animation at the same moment."
            : "Join a room to sync emotes with it.");

        ImGui.SameLine(0, 2f * s);
        var heels = plugin.SimpleHeelsAvailable;
        Disabled(!heels, () =>
        {
            if (Theme.Text(plugin.IsLiningUp ? "Lining up…" : "Line up")) plugin.LineUpNow();
        });
        Tooltip(heels
            ? "Line up a couple animation now: penis with mouth, vagina or anus.\n" +
              (plugin.AutomaticLineUpEnabled ? "This also happens by itself when a couple animation starts." : "Automatic line-up is off in Settings.")
            : "Line up needs Simple Heels.");

        ImGui.SameLine(0, 2f * s);
        Disabled(!heels, () =>
        {
            if (Theme.Text("Temp offset")) plugin.OpenSimpleHeelsTempOffset();
        });
        Tooltip(heels ? "Open Simple Heels' temporary offset." : "Simple Heels isn't installed or loaded.");

        ImGui.SameLine(0, 2f * s);
        Disabled(!heels, () =>
        {
            if (Theme.Text("Livepose")) plugin.OpenSimpleHeelsLivePose();
        });
        Tooltip(heels ? "Open Simple Heels' Livepose." : "Simple Heels isn't installed or loaded.");

        DrawSpeedControls(start, width, y);
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawSpeedControls(Vector2 footerStart, float width, float y)
    {
        var s = Theme.Scale;
        var matchLabel = plugin.IsAnimationSpeedMatching ? "Stop matching" : "Match target";
        var matchWidth = ImGui.CalcTextSize(matchLabel).X + 12f * s;
        var sliderWidth = 130f * s;
        var percentWidth = 46f * s;
        var labelWidth = ImGui.CalcTextSize("Speed").X;
        var x = footerStart.X + width - 12f * s - matchWidth - 8f * s - percentWidth - 6f * s - sliderWidth - 10f * s - labelWidth;
        ImGui.SetCursorScreenPos(new Vector2(x, y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f));
        Theme.Label("Speed");
        ImGui.SameLine(0, 10f * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, y));

        var speed = plugin.AnimationSpeedPercent;
        var locked = !plugin.AnimationSpeedAvailable || plugin.IsAnimationSpeedMatching;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Raised);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 4f) * s);
        Disabled(locked, () =>
        {
            ImGui.SetNextItemWidth(sliderWidth);
            if (ImGui.SliderInt("##speed", ref speed, -200, 200, "")) plugin.SetAnimationSpeedPercent(speed);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) plugin.ResetAnimationSpeed();
        });
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        Tooltip(!plugin.AnimationSpeedAvailable
            ? "Animation speed isn't available for this game version."
            : plugin.IsAnimationSpeedMatching
                ? "Following your target's speed. Stop matching to set it yourself."
                : "Drag to change your animation speed: negative plays backwards, 0 freezes. Right-click resets it.");

        ImGui.SameLine(0, 6f * s);
        var text = $"{speed}%";
        using (Theme.NumberFont())
        {
            var textWidth = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X + percentWidth - textWidth,
                y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f));
            ImGui.TextColored(speed == 100 ? Theme.Soft : Theme.Bone, text);
        }

        ImGui.SameLine(0, 8f * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, y));
        Disabled(!plugin.AnimationSpeedAvailable || !plugin.CanMatchAnimationSpeed && !plugin.IsAnimationSpeedMatching, () =>
        {
            if (Theme.Text(matchLabel, plugin.IsAnimationSpeedMatching ? Theme.AzureText : Theme.Soft))
                plugin.ToggleAnimationSpeedMatch();
        });
        Tooltip(plugin.CanMatchAnimationSpeed || plugin.IsAnimationSpeedMatching
            ? "Keep your animation speed matched to your target's."
            : "Target another player to match their speed.");
    }

    // ---- Body -----------------------------------------------------------------------------------

    private void DrawBody(float width, float height)
    {
        var s = Theme.Scale;
        var start = ImGui.GetCursorScreenPos();
        var folders = FoldersWidth * s;
        var room = RoomWidth * s;
        var middle = MathF.Max(200f * s, width - folders - room);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 16f) * s);
        ImGui.BeginChild("folders", new Vector2(folders, height), false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        DrawFolders();
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild("animations", new Vector2(middle, height), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        DrawAnimations();
        ImGui.EndChild();
        if (tutorialActive && tutorialStep == TutorialStep.ConfigureAnimation) HighlightLastItem();

        ImGui.SameLine(0, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 16f) * s);
        ImGui.BeginChild("room", new Vector2(room, height), false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        DrawRoom();
        ImGui.EndChild();
        if (TutorialHighlightsRoom) HighlightLastItem();

        var draw = ImGui.GetWindowDrawList();
        Theme.VerticalLine(draw, new Vector2(start.X + folders, start.Y), height);
        Theme.VerticalLine(draw, new Vector2(start.X + folders + middle, start.Y), height);
    }

    // ---- Folders --------------------------------------------------------------------------------

    private void DrawFolders()
    {
        var s = Theme.Scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10f * s);
        Theme.Label("Folders");
        ImGui.Dummy(new Vector2(0, 2f * s));

        var listHeight = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - 4f * s;
        ImGui.BeginChild("folder-list", new Vector2(0, listHeight), false, ImGuiWindowFlags.None);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2f) * s);

        if (FolderRow("all", "All animations", plugin.Mods.Count, libraryScope == LibraryScope.All && search.Length == 0, 0, null, out _))
            SelectScope(LibraryScope.All, null);
        AcceptFolderRootDrop();

        foreach (var category in plugin.GetChildCategories(null).ToList())
            DrawFolderTree(category, 0);

        if (FolderRow("unsorted", "Unsorted", plugin.GetOrganizedMods(null).Count,
                libraryScope == LibraryScope.Uncategorized && search.Length == 0, 0, null, out _))
            SelectScope(LibraryScope.Uncategorized, null);
        AcceptModDrop(null);

        var lineStart = ImGui.GetCursorScreenPos();
        Theme.HorizontalLine(ImGui.GetWindowDrawList(), lineStart + new Vector2(10f * s, 6f * s),
            ImGui.GetContentRegionAvail().X - 20f * s);
        ImGui.Dummy(new Vector2(0, 12f * s));

        if (FolderRow("private", "Private", plugin.Mods.Count(mod => plugin.IsModPrivate(mod.Directory)),
                libraryScope == LibraryScope.Private && search.Length == 0, 0, null, out _))
            SelectScope(LibraryScope.Private, null);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Never offered to the room or sent to anyone.");

        ImGui.PopStyleVar();
        ImGui.EndChild();

        ImGui.Dummy(new Vector2(0, 2f * s));
        if (IconTextButton("##new-folder", "New folder", DrawPlusIcon)) openCreateFolder = true;
        Tooltip("Make a folder. Right-click a folder for a subfolder, renaming or deleting.");
    }

    private void SelectScope(LibraryScope scope, string? categoryId)
    {
        libraryScope = scope;
        activeCategoryId = categoryId;
        search = "";
        ClearModSelection();
    }

    private void DrawFolderTree(ModCategory category, int depth)
    {
        ImGui.PushID(category.Id);
        var children = plugin.GetChildCategories(category.Id).ToList();
        var open = !collapsedFolders.Contains(category.Id);
        var selected = libraryScope == LibraryScope.Category && search.Length == 0 &&
                       activeCategoryId?.Equals(category.Id, StringComparison.OrdinalIgnoreCase) == true;
        if (FolderRow("folder", category.Name, plugin.GetCategoryModCount(category.Id), selected, depth,
                children.Count > 0 ? open : null, out var toggled))
            SelectScope(LibraryScope.Category, category.Id);
        if (toggled && !collapsedFolders.Remove(category.Id)) collapsedFolders.Add(category.Id);
        DrawFolderContextMenu(category);
        DrawFolderDragSource(category);
        AcceptFolderDrop(category);
        AcceptModDrop(category.Id);
        if (open)
            foreach (var child in children) DrawFolderTree(child, depth + 1);
        ImGui.PopID();
    }

    /// <summary>One folder line. A folder with subfolders has an arrow; clicking the arrow opens or
    /// closes it, clicking the name selects it.</summary>
    private static bool FolderRow(string id, string label, int count, bool selected, int depth, bool? open, out bool toggled)
    {
        var s = Theme.Scale;
        var width = ImGui.GetContentRegionAvail().X;
        var height = 30f * s;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Selectable($"##{id}", selected, ImGuiSelectableFlags.None, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var indent = (10f + depth * 14f) * s;
        toggled = false;
        if (open is { } isOpen)
        {
            DrawChevron(draw, new Vector2(pos.X + indent + 3f * s, pos.Y + height * 0.5f), isOpen,
                ImGui.GetColorU32(hovered ? Theme.Soft : Theme.Faint));
            if (clicked && ImGui.GetIO().MousePos.X < pos.X + indent + 12f * s)
            {
                toggled = true;
                clicked = false;
            }
            indent += 14f * s;
        }

        var textColor = selected || hovered ? Theme.Bone : Theme.Soft;
        var countText = count.ToString("N0");
        float countWidth;
        using (Theme.NumberFont())
        {
            countWidth = ImGui.CalcTextSize(countText).X;
            draw.AddText(new Vector2(pos.X + width - 10f * s - countWidth, pos.Y + (height - ImGui.GetTextLineHeight()) * 0.5f),
                ImGui.GetColorU32(selected ? Theme.Ash : Theme.Faint), countText);
        }
        draw.AddText(new Vector2(pos.X + indent, pos.Y + (height - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(textColor), Theme.Truncate(label, width - indent - countWidth - 22f * s));
        return clicked;
    }

    private void DrawFolderContextMenu(ModCategory category)
    {
        if (!ImGui.BeginPopupContextItem("folderMenu")) return;
        Theme.Label("Rename");
        if (!folderRenameBuffers.TryGetValue(category.Id, out var rename)) rename = category.Name;
        ImGui.SetNextItemWidth(220f * Theme.Scale);
        var submitRename = ImGui.InputText("##folderRename", ref rename, 80, ImGuiInputTextFlags.EnterReturnsTrue);
        folderRenameBuffers[category.Id] = rename;
        ImGui.SameLine();
        if ((Theme.Outline("Save##rename") || submitRename) && !string.IsNullOrWhiteSpace(rename))
        {
            plugin.RenameCategory(category.Id, rename);
            folderRenameBuffers[category.Id] = rename.Trim();
            ImGui.CloseCurrentPopup();
        }

        Theme.Label("New subfolder");
        if (!folderChildBuffers.TryGetValue(category.Id, out var childName)) childName = "";
        ImGui.SetNextItemWidth(220f * Theme.Scale);
        var submitChild = ImGui.InputTextWithHint("##childFolderName", "Name", ref childName, 80,
            ImGuiInputTextFlags.EnterReturnsTrue);
        folderChildBuffers[category.Id] = childName;
        ImGui.SameLine();
        if ((Theme.Outline("Make##child") || submitChild) && !string.IsNullOrWhiteSpace(childName))
        {
            plugin.CreateCategory(childName, category.Id);
            folderChildBuffers[category.Id] = "";
            ImGui.CloseCurrentPopup();
        }

        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(category.ParentId) && ImGui.MenuItem("Move to the top level"))
            plugin.MoveCategory(category.Id, null);
        if (selectedMods.Count > 0 && ImGui.MenuItem($"Move the {selectedMods.Count} selected here"))
        {
            plugin.MoveMods(selectedMods, category.Id);
            ClearModSelection();
        }
        if (ImGui.MenuItem("Delete folder"))
        {
            if (activeCategoryId?.Equals(category.Id, StringComparison.OrdinalIgnoreCase) == true)
                SelectScope(LibraryScope.All, null);
            plugin.DeleteCategory(category.Id);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Its animations move to Unsorted and its subfolders move up a level.");
        ImGui.EndPopup();
    }

    // ---- Animations -----------------------------------------------------------------------------

    private void DrawAnimations()
    {
        var s = Theme.Scale;
        var mods = VisibleMods();
        selectedMods.RemoveWhere(directory => !plugin.Mods.Any(mod =>
            mod.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)));

        var headerStart = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorScreenPos(headerStart + new Vector2(24f * s, 14f * s));
        using (Theme.HeadingFont())
            ImGui.TextUnformatted(Theme.Truncate(CurrentLibraryTitle(), width * 0.55f));
        ImGui.SameLine(0, 10f * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, headerStart.Y + 18f * s));
        Theme.Number(mods.Count.ToString("N0"));

        if (selectedMods.Count > 0)
        {
            var clear = "Clear";
            var info = $"{selectedMods.Count} selected";
            var clearWidth = ImGui.CalcTextSize(clear).X + 12f * s;
            ImGui.SetCursorScreenPos(new Vector2(headerStart.X + width - 18f * s - clearWidth - ImGui.CalcTextSize(info).X - 6f * s,
                headerStart.Y + 16f * s));
            ImGui.TextColored(Theme.RoseHover, info);
            ImGui.SameLine(0, 6f * s);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, headerStart.Y + 12f * s));
            if (Theme.Text(clear)) ClearModSelection();
        }
        else
        {
            var hint = plugin.Sync.IsInRoom ? "Everyone has it first" : "Ctrl or Shift-click to select";
            ImGui.SetCursorScreenPos(new Vector2(headerStart.X + width - 22f * s - ImGui.CalcTextSize(hint).X,
                headerStart.Y + 18f * s));
            Theme.Quiet(hint);
        }

        ImGui.SetCursorScreenPos(new Vector2(headerStart.X, headerStart.Y + 50f * s));
        ImGui.Dummy(Vector2.Zero);

        var status = CurrentStatus();
        var statusHeight = status.Length > 0 ? 30f * s : 0f;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 0f) * s);
        ImGui.BeginChild("rows", new Vector2(0, ImGui.GetContentRegionAvail().Y - statusHeight),
            false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.None);
        ImGui.PopStyleVar();
        if (mods.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 8f * s));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12f * s);
            Theme.Quiet(search.Length > 0
                ? "No animations match this search."
                : libraryScope == LibraryScope.Category
                    ? "Drag animations here to fill this folder."
                    : "No animation mods here yet.");
        }
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 2f) * s);
        foreach (var mod in mods)
            DrawModRow(mod, FindCategoryForMod(mod.Directory), mods);
        ImGui.PopStyleVar();
        ImGui.Dummy(new Vector2(0, 12f * s));
        ImGui.EndChild();

        if (status.Length > 0)
        {
            var statusStart = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            Theme.HorizontalLine(draw, statusStart, width);
            draw.AddText(statusStart + new Vector2(24f * s, (statusHeight - ImGui.GetTextLineHeight()) * 0.5f),
                ImGui.GetColorU32(Theme.Ash), Theme.Truncate(status, width - 48f * s));
            if (ImGui.IsMouseHoveringRect(statusStart, statusStart + new Vector2(width, statusHeight)))
                ImGui.SetTooltip(status);
            ImGui.Dummy(new Vector2(width, statusHeight));
        }
    }

    /// <summary>The line under the list: a fresh line-up result, else the plugin's last message.</summary>
    private string CurrentStatus()
    {
        var lineUp = plugin.LineUpStatus;
        if (lineUp != lastLineUpStatus)
        {
            lastLineUpStatus = lineUp;
            lineUpStatusAt = Environment.TickCount64;
        }
        if (lineUp.Length > 0 && Environment.TickCount64 - lineUpStatusAt < 8000) return lineUp;
        var status = plugin.Status;
        return status is "Ready." or "" ? "" : status;
    }

    private void DrawModRow(
        (string Directory, string Name) mod,
        string? categoryId,
        IReadOnlyList<(string Directory, string Name)> order)
    {
        var s = Theme.Scale;
        ImGui.PushID(mod.Directory);
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = RowHeight * s;
        var open = openMods.Contains(mod.Directory);
        var selected = selectedMods.Contains(mod.Directory);
        var isPrivate = plugin.IsModPrivate(mod.Directory);
        var pickedBy = plugin.GetRemoteModSelector(mod.Directory);
        var inRoom = plugin.Sync.IsInRoom;

        if (open)
        {
            draw.ChannelsSplit(2);
            draw.ChannelsSetCurrent(1);
        }

        ImGui.Selectable("##row", selected, ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowItemOverlap,
            new Vector2(width, height));
        var rowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) &&
                         ImGui.IsMouseHoveringRect(start, start + new Vector2(width, height));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var io = ImGui.GetIO();
            if (io.KeyCtrl || io.KeyShift) HandleModSelection(mod.Directory, categoryId, order);
            else if (!openMods.Remove(mod.Directory)) openMods.Add(mod.Directory);
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !selectedMods.Contains(mod.Directory))
            SelectOnly(mod.Directory, categoryId);
        if (ImGui.IsItemHovered() && pickedBy is not null)
            ImGui.SetTooltip($"{pickedBy} picked this animation.");
        DrawModContextMenu(mod);
        DrawModDragAndDrop(mod, categoryId);

        Theme.AvailabilityMark(draw, new Vector2(start.X + 16f * s, start.Y + height * 0.5f), AvailabilityOf(mod.Directory, isPrivate));
        var textY = start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;
        var nameColor = pickedBy is not null ? Theme.AzureText : isPrivate ? Theme.Ash : Theme.Bone;

        // What sits at the right: Send to room on hover, otherwise who picked it or what it holds.
        var trailing = pickedBy is not null ? $"{pickedBy} picked this" : isPrivate ? "private" : Summary(mod.Directory);
        var trailingColor = pickedBy is not null ? Theme.AzureText : Theme.Faint;
        var canSend = inRoom && !isPrivate;
        var sendLabel = "Send to room";
        var trailingWidth = rowHovered && canSend
            ? ImGui.CalcTextSize(sendLabel).X + 24f * s
            : ImGui.CalcTextSize(trailing).X;
        var nameWidth = width - 36f * s - trailingWidth - 28f * s;
        draw.AddText(new Vector2(start.X + 36f * s, textY), ImGui.GetColorU32(nameColor), Theme.Truncate(mod.Name, nameWidth));

        if (rowHovered && canSend)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 3f) * s);
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - trailingWidth - 6f * s,
                start.Y + (height - ImGui.GetFrameHeight()) * 0.5f));
            if (Theme.Outline(sendLabel)) plugin.SendMod(mod.Directory, mod.Name);
            ImGui.PopStyleVar();
            Tooltip("Offer this animation to everyone in the room who doesn't have it (75 MB at most).");
        }
        else if (trailing.Length > 0)
        {
            draw.AddText(new Vector2(start.X + width - trailingWidth - 12f * s, textY), ImGui.GetColorU32(trailingColor), trailing);
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(Vector2.Zero);

        if (open)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + 36f * s, start.Y + height + 2f * s));
            ImGui.BeginGroup();
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f) * s);
            plugin.EnsureDetectedEmotes(mod.Directory, mod.Name);
            DrawModDetails(mod, false);
            ImGui.PopStyleVar();
            ImGui.EndGroup();
            var bottom = MathF.Max(ImGui.GetItemRectMax().Y, start.Y + height) + 16f * s;
            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(start, new Vector2(start.X + width, bottom), ImGui.GetColorU32(Theme.Raised), 3f * s);
            draw.ChannelsMerge();
            ImGui.SetCursorScreenPos(new Vector2(start.X, bottom + 2f * s));
            ImGui.Dummy(Vector2.Zero);
        }
        ImGui.PopID();
    }

    private Availability AvailabilityOf(string directory, bool isPrivate)
    {
        if (isPrivate) return Availability.Private;
        if (!plugin.Sync.IsInRoom) return Availability.Solo;
        var (matches, members) = plugin.GetModMatch(directory);
        if (members <= 1) return Availability.Solo;
        if (matches >= members) return Availability.Everyone;
        return matches > 1 ? Availability.Some : Availability.OnlyYou;
    }

    private string Summary(string directory)
    {
        if (plugin.IsModConverted(directory)) return "carrier";
        var emotes = plugin.GetDetectedEmotes(directory).Count;
        var poses = plugin.GetDetectedPoses(directory).Count;
        if (emotes > 0) return $"{emotes} emote{(emotes == 1 ? "" : "s")}";
        if (poses > 0) return $"{poses} pose{(poses == 1 ? "" : "s")}";
        var groups = plugin.GetOptionGroups(directory).Count;
        return groups > 0 ? $"{groups} option{(groups == 1 ? "" : "s")}" : "";
    }

    private void DrawModContextMenu((string Directory, string Name) mod)
    {
        if (!ImGui.BeginPopupContextItem("modMenu")) return;
        var targets = selectedMods.Contains(mod.Directory) ? selectedMods.ToList() : [mod.Directory];
        if (targets.Count > 1) Theme.Label($"{targets.Count} selected");
        var anyPublic = targets.Any(directory => !plugin.IsModPrivate(directory));
        var anyPrivate = targets.Any(plugin.IsModPrivate);
        if (anyPublic && ImGui.MenuItem("Make private"))
            plugin.SetModsPrivate(targets, true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Private animations are never offered to a room or sent to anyone.");
        if (anyPrivate && ImGui.MenuItem("Make public"))
            plugin.SetModsPrivate(targets, false);
        if (targets.Count == 1 && plugin.IsModConverted(mod.Directory))
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Restore the original animation files"))
                plugin.RestoreConvertedMod(mod.Directory, mod.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Undo Synastry's carrier conversion from its backup.");
        }
        if (ImGui.BeginMenu("Move to folder"))
        {
            if (ImGui.MenuItem("Unsorted"))
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

    private void DrawModDragAndDrop((string Directory, string Name) mod, string? categoryId)
    {
        if (ImGui.BeginDragDropSource())
        {
            ImGui.SetDragDropPayload(ModPayload, Encoding.UTF8.GetBytes(mod.Directory));
            ImGui.TextUnformatted(selectedMods.Contains(mod.Directory) && selectedMods.Count > 1
                ? $"Move {selectedMods.Count} animations"
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
    }

    /// <summary>An opened animation: its roles as chips, then its mod options. Returns true when a
    /// role was chosen (the suggestion prompt closes then).</summary>
    private bool DrawModDetails((string Directory, string Name) mod, bool suggestionMode)
    {
        var poses = plugin.GetDetectedPoses(mod.Directory);
        var emotes = plugin.GetDetectedEmotes(mod.Directory);
        var groups = plugin.GetOptionGroups(mod.Directory);
        var activated = false;

        if (poses.Count + emotes.Count > 0)
        {
            Theme.Label(plugin.Sync.IsInRoom && !suggestionMode ? "Roles" : "Animations");
            ImGui.BeginGroup();
            activated = DrawRoleChips(mod, poses, emotes, suggestionMode);
            ImGui.EndGroup();
            if (tutorialActive && tutorialStep == TutorialStep.ConfigureAnimation) HighlightLastItem();
        }
        else
        {
            Theme.Quiet("No emote or pose found in this mod's current options.");
        }

        if (groups.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4f * Theme.Scale));
            DrawOptionGroups(mod.Directory, groups);
        }
        return activated;
    }

    private bool DrawRoleChips(
        (string Directory, string Name) mod,
        IReadOnlyList<PoseTarget> poses,
        IReadOnlyList<EmoteTarget> emotes,
        bool suggestionMode)
    {
        var activated = false;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var first = true;

        void Place(string label)
        {
            var chipWidth = ImGui.CalcTextSize(label).X + 30f * Theme.Scale;
            if (!first)
            {
                ImGui.SameLine(0, 8f * Theme.Scale);
                if (ImGui.GetCursorScreenPos().X + chipWidth > right) ImGui.NewLine();
            }
            first = false;
        }

        foreach (var pose in poses)
        {
            ImGui.PushID($"pose-{pose.Kind}-{pose.Index}");
            var name = PoseDisplayName(pose);
            var option = $"{pose.Kind}:{pose.Index}";
            var choice = RoleChip(mod.Directory, "$detected-pose", option, name, suggestionMode, Place, out var solo);
            if (choice && (suggestionMode || TryTutorialAnimationAction(mod.Directory, name)))
            {
                plugin.ActivateDetectedPose(mod.Directory, mod.Name, pose);
                RememberReady(mod.Directory, "$detected-pose", option);
                activated = true;
            }
            if (solo) plugin.ActivateDetectedPoseSolo(mod.Directory, mod.Name, pose);
            ImGui.PopID();
        }

        foreach (var emote in emotes)
        {
            ImGui.PushID($"emote-{emote.Id}");
            var name = emote.Name;
            var option = emote.Id.ToString();
            var choice = RoleChip(mod.Directory, "$detected-emote", option, name, suggestionMode, Place, out var solo);
            if (choice && (suggestionMode || TryTutorialAnimationAction(mod.Directory, name)))
            {
                plugin.ActivateDetectedEmote(mod.Directory, mod.Name, emote);
                RememberReady(mod.Directory, "$detected-emote", option);
                activated = true;
            }
            if (solo) plugin.ActivateDetectedEmoteSolo(mod.Directory, mod.Name, emote);
            ImGui.PopID();
        }
        return activated;
    }

    private void RememberReady(string directory, string group, string option)
    {
        if (plugin.Sync.IsInRoom) readyRoleKey = NoteKey(directory, group, option);
    }

    private bool IsLocallyReady()
    {
        var room = plugin.Sync.Room;
        return room is not null && room.Members.Any(member => plugin.Sync.IsCurrentMember(member.ConnectionId) && member.Ready);
    }

    /// <summary>A role chip. Its right-click menu renames the role, reports a bad shared name and,
    /// in a room, plays it just for this player.</summary>
    private bool RoleChip(
        string directory,
        string group,
        string option,
        string animationName,
        bool suggestionMode,
        Action<string> place,
        out bool solo)
    {
        solo = false;
        var key = NoteKey(directory, group, option);
        var savedNote = plugin.GetOptionNote(directory, group, option);
        if (!noteBuffers.TryGetValue(key, out var note) ||
            (string.IsNullOrWhiteSpace(note) && !string.IsNullOrWhiteSpace(savedNote))) note = savedNote;
        noteBuffers[key] = note;
        var role = string.IsNullOrWhiteSpace(savedNote) ? animationName : savedNote;
        var pickedBy = plugin.GetRemoteDetectedTriggerSelector(directory, group, option);
        var inRoom = plugin.Sync.IsInRoom;
        var mine = inRoom && !suggestionMode && readyRoleKey == key && IsLocallyReady();

        var (label, state) = pickedBy is not null
            ? ($"{role}, {pickedBy}", suggestionMode ? ChipState.Taken : ChipState.Theirs)
            : mine
                ? ($"{role}, you're ready", ChipState.Mine)
                : (role, ChipState.Open);
        place(label);
        var clicked = Theme.Chip($"{label}##chip", state);
        if (ImGui.IsItemHovered())
        {
            var action = suggestionMode ? $"Ready as {role}" : inRoom ? $"Get ready as {role}" : $"Play {animationName}";
            var who = pickedBy is not null ? $"\n{pickedBy} picked this role." : "";
            var name = !role.Equals(animationName, StringComparison.Ordinal) ? $"\n{animationName}" : "";
            ImGui.SetTooltip($"{action}{name}{who}\nRight-click to rename{(inRoom && !suggestionMode ? " or play solo" : "")}.");
        }

        if (ImGui.BeginPopupContextItem("roleMenu"))
        {
            ImGui.TextUnformatted(animationName);
            if (inRoom && !suggestionMode && ImGui.MenuItem("Play just for me"))
                solo = true;
            ImGui.Separator();
            Theme.Label("Role name");
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
            if (!string.IsNullOrWhiteSpace(savedNote))
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
                Tooltip("Changes it for you now. Five matching suggestions change it for everyone.");
            }
            ImGui.EndPopup();
        }
        return clicked;
    }

    private void DrawOptionGroups(string directory, IReadOnlyList<ModOptionGroup> groups)
    {
        var s = Theme.Scale;
        plugin.EnsureDefaultOptionSelections(directory);
        var available = ImGui.GetContentRegionAvail().X - 16f * s;
        var columns = available > 440f * s ? 2 : 1;
        var columnWidth = MathF.Min(280f * s, (available - (columns - 1) * 18f * s) / columns);

        var singles = groups.Where(group => !group.IsMultiSelect).ToList();
        for (var index = 0; index < singles.Count; index++)
        {
            var group = singles[index];
            if (index % columns != 0) ImGui.SameLine(0, 18f * s);
            ImGui.PushID(group.Name);
            ImGui.BeginGroup();
            var groupPickedBy = plugin.GetRemoteGroupSelector(directory, group.Name);
            ImGui.TextColored(groupPickedBy is not null ? Theme.AzureText : Theme.Ash, Theme.Truncate(group.Name, columnWidth));
            var current = group.Options.FirstOrDefault(option => plugin.IsOptionSelected(directory, group.Name, option));
            ImGui.SetNextItemWidth(columnWidth);
            if (groupPickedBy is not null) ImGui.PushStyleColor(ImGuiCol.Text, Theme.AzureText);
            var comboOpen = ImGui.BeginCombo("##options", current ?? "Mod default");
            if (groupPickedBy is not null) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered() && groupPickedBy is not null)
                ImGui.SetTooltip($"{groupPickedBy} picked an option here.");
            if (comboOpen)
            {
                foreach (var option in group.Options)
                {
                    ImGui.PushID(option);
                    var selected = option.Equals(current, StringComparison.OrdinalIgnoreCase);
                    var pickedBy = plugin.GetRemoteOptionSelector(directory, group.Name, option);
                    var pose = plugin.GetOptionPose(directory, group.Name, option);
                    var label = pose is null ? option : $"{option}  ({PoseDisplayName(pose)})";
                    if (pickedBy is not null) label += $"  ({pickedBy}'s pick)";
                    if (pickedBy is not null) ImGui.PushStyleColor(ImGuiCol.Text, Theme.AzureText);
                    if (ImGui.Selectable(label, selected))
                        plugin.SetOptionSelected(directory, group.Name, option, true,
                            multiSelect: false, broadcastSelection: false);
                    if (pickedBy is not null) ImGui.PopStyleColor();
                    if (selected) ImGui.SetItemDefaultFocus();
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
            ImGui.EndGroup();
            ImGui.PopID();
        }

        foreach (var group in groups.Where(group => group.IsMultiSelect))
        {
            ImGui.PushID(group.Name);
            ImGui.Dummy(new Vector2(0, 2f * s));
            var groupPickedBy = plugin.GetRemoteGroupSelector(directory, group.Name);
            ImGui.TextColored(groupPickedBy is not null ? Theme.AzureText : Theme.Ash, group.Name);
            var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            var first = true;
            foreach (var option in group.Options)
            {
                ImGui.PushID(option);
                var pose = plugin.GetOptionPose(directory, group.Name, option);
                var label = pose is null ? option : $"{option} ({PoseDisplayName(pose)})";
                var width = ImGui.GetFrameHeight() + ImGui.CalcTextSize(label).X + 24f * s;
                if (!first)
                {
                    ImGui.SameLine(0, 18f * s);
                    if (ImGui.GetCursorScreenPos().X + width > right) ImGui.NewLine();
                }
                first = false;
                var selected = plugin.IsOptionSelected(directory, group.Name, option);
                var pickedBy = plugin.GetRemoteOptionSelector(directory, group.Name, option);
                if (pickedBy is not null) ImGui.PushStyleColor(ImGuiCol.Text, Theme.AzureText);
                if (ImGui.Checkbox(label, ref selected))
                    plugin.SetOptionSelected(directory, group.Name, option, selected, true, broadcastSelection: false);
                if (pickedBy is not null)
                {
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{pickedBy} picked this.");
                }
                ImGui.PopID();
            }
            ImGui.PopID();
        }
    }

    private List<(string Directory, string Name)> VisibleMods()
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
                LibraryScope.Category when activeCategoryId is not null => plugin.GetOrganizedMods(activeCategoryId),
                LibraryScope.Uncategorized => plugin.GetOrganizedMods(null),
                LibraryScope.Private => plugin.Mods.Where(mod => plugin.IsModPrivate(mod.Directory)),
                _ => plugin.Mods
            };
        }
        return plugin.OrderModsForLibrary(source.DistinctBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase));
    }

    private string CurrentLibraryTitle()
    {
        if (search.Length > 0) return $"Results for “{search}”";
        return libraryScope switch
        {
            LibraryScope.Category when activeCategoryId is not null => plugin.GetCategoryPath(activeCategoryId),
            LibraryScope.Uncategorized => "Unsorted",
            LibraryScope.Private => "Private",
            _ => "All animations"
        };
    }

    private string? FindCategoryForMod(string directory) => plugin.Categories.FirstOrDefault(category =>
        category.ModDirectories.Any(item => item.Equals(directory, StringComparison.OrdinalIgnoreCase)))?.Id;

    // ---- Room -----------------------------------------------------------------------------------

    private void DrawRoom()
    {
        ImGui.BeginGroup();
        var s = Theme.Scale;
        var width = ImGui.GetContentRegionAvail().X;
        var top = ImGui.GetCursorScreenPos();
        Theme.Heading("Room");
        var (linkText, linkColor) = plugin.Sync.IsConnected
            ? ("Linked", Theme.AzureText)
            : ("Not linked", Theme.Faint);
        var linkWidth = ImGui.CalcTextSize(linkText).X;
        ImGui.SameLine();
        ImGui.SetCursorScreenPos(new Vector2(top.X + width - linkWidth, top.Y + 6f * s));
        ImGui.TextColored(linkColor, linkText);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(plugin.Sync.RelayConnectionStatus);
        ImGui.Dummy(new Vector2(0, 8f * s));

        if (!plugin.Sync.IsConnected) DrawNotLinked(width);
        else if (!plugin.Sync.IsInRoom || plugin.Sync.Room is null) DrawLinkedNoRoom(width);
        else DrawInRoom(plugin.Sync.Room, width);
        ImGui.EndGroup();
    }

    private void DrawNotLinked(float width)
    {
        var s = Theme.Scale;
        Theme.Wrapped("Link up to play with other people. Your library, roles and tools work without it.", Theme.Soft);
        ImGui.Dummy(new Vector2(0, 8f * s));
        if (Theme.Primary("Link up", width, 36f * s)) plugin.ConnectSync();
        if (tutorialActive && tutorialStep == TutorialStep.Connect) HighlightLastItem();
        Theme.Quiet($"as {plugin.SyncDisplayName}");
        BottomAligned(() =>
        {
            if (Theme.Text("Help on Discord", Theme.Ash)) Util.OpenLink(DiscordInviteUrl);
        });
    }

    private void DrawLinkedNoRoom(float width)
    {
        var s = Theme.Scale;
        Theme.Label("Room code");
        using (Theme.CodeFont())
        {
            ImGui.SetNextItemWidth(width);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 4f) * s);
            ImGui.InputTextWithHint("##room-code", "ABC123", ref roomCode, 8, ImGuiInputTextFlags.CharsUppercase);
            ImGui.PopStyleVar();
        }
        ImGui.Dummy(new Vector2(0, 4f * s));
        if (Theme.Primary("Join", width, 36f * s)) plugin.JoinSyncRoom(roomCode);
        if (Theme.Outline("Start a new room", width, 36f * s)) plugin.CreateSyncRoom();
        if (tutorialActive && tutorialStep == TutorialStep.Room) HighlightLastItem();
        ImGui.Dummy(new Vector2(0, 4f * s));
        Theme.Wrapped("Or right-click someone in game and choose Invite to Synastry.", Theme.Ash);
        DrawOffersLine(width);
        BottomAligned(() =>
        {
            if (Theme.Text("Unlink", Theme.Ash)) plugin.DisconnectSync();
        });
    }

    private void DrawInRoom(RoomStateDto room, float width)
    {
        var s = Theme.Scale;
        using (Theme.CodeFont())
        {
            ImGui.TextUnformatted(room.RoomCode);
        }
        var codeRect = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy the room code.");
        if (ImGui.IsItemClicked()) CopyRoomCode(room);
        ImGui.SameLine(0, 10f * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, codeRect.Item2.Y - ImGui.GetFrameHeight() - 2f * s));
        if (Theme.Text("Copy", Theme.Ash)) CopyRoomCode(room);

        ImGui.Dummy(new Vector2(0, 6f * s));
        Theme.Label("Cast");
        var draw = ImGui.GetWindowDrawList();
        foreach (var member in room.Members)
        {
            ImGui.PushID(member.ConnectionId);
            var isMe = plugin.Sync.IsCurrentMember(member.ConnectionId);
            var start = ImGui.GetCursorScreenPos();
            var rowHeight = 34f * s;
            ImGui.InvisibleButton("##member", new Vector2(width, rowHeight));
            if (plugin.Sync.IsRoomLeader && !isMe && ImGui.BeginPopupContextItem("memberMenu"))
            {
                if (ImGui.MenuItem($"Remove {member.DisplayName}")) plugin.RemoveSyncMember(member);
                ImGui.EndPopup();
            }
            if (ImGui.IsItemHovered() && plugin.Sync.IsRoomLeader && !isMe)
                ImGui.SetTooltip("Right-click to remove them from the room.");

            var color = isMe ? Theme.Rose : Theme.Azure;
            var midY = start.Y + rowHeight * 0.5f;
            Theme.Dot(draw, new Vector2(start.X + 4f * s, midY), color, member.Ready || isMe);
            var nameX = start.X + 16f * s;
            var textY = midY - ImGui.GetTextLineHeight() * 0.5f;
            var stateText = member.Ready ? "ready" : "waiting";
            var stateWidth = ImGui.CalcTextSize(stateText).X + (member.Ready ? 16f * s : 0f);
            var name = Theme.Truncate(member.DisplayName, width - 16f * s - stateWidth - 60f * s);
            draw.AddText(new Vector2(nameX, textY), ImGui.GetColorU32(Theme.Bone), name);
            var tagX = nameX + ImGui.CalcTextSize(name).X + 6f * s;
            if (member.IsLeader)
            {
                draw.AddText(new Vector2(tagX, textY), ImGui.GetColorU32(Theme.Faint), "host");
                tagX += ImGui.CalcTextSize("host").X + 6f * s;
            }
            if (member.FreeUse) draw.AddText(new Vector2(tagX, textY), ImGui.GetColorU32(Theme.AzureText), "Free Use");
            var stateX = start.X + width - stateWidth;
            if (member.Ready)
            {
                Theme.Check(draw, new Vector2(stateX, midY - 4.5f * s), color);
                draw.AddText(new Vector2(stateX + 16f * s, textY), ImGui.GetColorU32(Theme.Bone), stateText);
            }
            else
            {
                draw.AddText(new Vector2(stateX, textY), ImGui.GetColorU32(Theme.Faint), stateText);
            }
            if (member != room.Members[^1])
                draw.AddLine(new Vector2(start.X, start.Y + rowHeight), new Vector2(start.X + width, start.Y + rowHeight),
                    ImGui.GetColorU32(Theme.Selected), 1f);
            ImGui.PopID();
        }

        ImGui.Dummy(new Vector2(0, 8f * s));
        var freeUse = plugin.IsFreeUseEnabled;
        if (Theme.Toggle("##free-use", "Free Use Mode", ref freeUse)) plugin.SetFreeUse(freeUse);
        Tooltip("Others in the room choose your role and options. They apply to that animation only and you're readied automatically. Private animations are never used.");

        DrawOffersLine(width);
        BottomAligned(() =>
        {
            var ready = room.Members.Count(member => member.Ready);
            Theme.Number($"{ready} of {room.Members.Count}", Theme.Bone);
            ImGui.SameLine(0, 8f * s);
            Theme.Label(ready == room.Members.Count ? "ready. Starting together." : "ready. Plays when everyone is.");
            if (plugin.Sync.IsRoomLeader)
            {
                if (Theme.Primary("Start now", width, 36f * s)) plugin.ForceSyncStart();
                Tooltip("Start everyone who has picked a role, without waiting for the rest.");
            }
            if (Theme.Text("Unready", Theme.Soft)) plugin.CancelSyncReady();
            ImGui.SameLine();
            var leave = "Leave room";
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, width - ImGui.CalcTextSize("Unready").X - ImGui.CalcTextSize(leave).X - 36f * s));
            if (Theme.Text(leave, Theme.Ash)) plugin.LeaveSyncRoom();
        }, plugin.Sync.IsRoomLeader ? 3 : 2);
    }

    private void CopyRoomCode(RoomStateDto room)
    {
        ImGui.SetClipboardText(room.RoomCode);
        plugin.NotifyRoomCodeCopied(room.RoomCode);
    }

    /// <summary>The line that appears when someone has offered this player an animation.</summary>
    private void DrawOffersLine(float width)
    {
        if (pendingTransferOffers.Count == 0) return;
        var s = Theme.Scale;
        ImGui.Dummy(new Vector2(0, 8f * s));
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        Theme.HorizontalLine(draw, start, width);
        ImGui.Dummy(new Vector2(0, 6f * s));
        var count = pendingTransferOffers.Count;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.AzureText, $"{count} animation{(count == 1 ? "" : "s")} offered to you");
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Review").X - 12f * s));
        if (Theme.Text("Review", Theme.Bone)) transferInboxOpen = true;
        var end = ImGui.GetCursorScreenPos();
        Theme.HorizontalLine(draw, new Vector2(start.X, end.Y + 2f * s), width);
        ImGui.Dummy(new Vector2(0, 4f * s));
    }

    /// <summary>Draws a few lines pinned to the bottom of the column.</summary>
    private static void BottomAligned(Action draw, int lines = 1)
    {
        var s = Theme.Scale;
        var needed = lines * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y) + (lines > 1 ? 40f * s : 0f);
        var available = ImGui.GetContentRegionAvail().Y;
        if (available > needed) ImGui.Dummy(new Vector2(0, available - needed));
        draw();
    }

    // ---- Popups ---------------------------------------------------------------------------------

    private void DrawMenuPopup()
    {
        if (!ImGui.BeginPopup("synastry-menu")) return;
        Disabled(plugin.IsRefreshingMods, () =>
        {
            if (ImGui.MenuItem(plugin.IsRefreshingMods ? "Updating your library…" : "Refresh library"))
                plugin.RefreshMods();
        });
        Tooltip("Look for new or changed animation mods in Penumbra.");
        if (ImGui.MenuItem("Emote commands")) plugin.OpenCustomCommands();
        Tooltip("Give any animation its own slash command, like /wicked.");
        if (ImGui.MenuItem("Settings")) plugin.OpenSettings();
        ImGui.Separator();
        if (ImGui.MenuItem("New folder")) openCreateFolder = true;
        if (ImGui.MenuItem("Make everything private…")) openMarkAllPrivate = true;
        ImGui.Separator();
        if (ImGui.MenuItem("Show me how it works")) StartTutorial();
        if (ImGui.MenuItem("Help on Discord")) Util.OpenLink(DiscordInviteUrl);
        ImGui.EndPopup();
    }

    private void DrawCreateFolderPopup()
    {
        if (openCreateFolder)
        {
            ImGui.OpenPopup("New folder###SynastryNewFolder");
            openCreateFolder = false;
        }
        if (!ImGui.BeginPopupModal("New folder###SynastryNewFolder", ImGuiWindowFlags.AlwaysAutoResize)) return;
        Theme.Heading("New folder");
        ImGui.SetNextItemWidth(280f * Theme.Scale);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        var submit = ImGui.InputTextWithHint("##folderName", "Dances, Couples…", ref newFolderName, 80,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.Dummy(new Vector2(0, 4f * Theme.Scale));
        if ((Theme.Primary("Make folder") || submit) && !string.IsNullOrWhiteSpace(newFolderName))
        {
            plugin.CreateCategory(newFolderName);
            newFolderName = "";
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Theme.Text("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawMarkAllPrivatePopup()
    {
        if (openMarkAllPrivate)
        {
            ImGui.OpenPopup("Make everything private###SynastryAllPrivate");
            openMarkAllPrivate = false;
        }
        if (!ImGui.BeginPopupModal("Make everything private###SynastryAllPrivate", ImGuiWindowFlags.AlwaysAutoResize)) return;
        Theme.Heading("Make everything private?");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360f * Theme.Scale);
        Theme.Wrapped("Every animation in your library stops being offered to rooms or sent to anyone. " +
                      "You can make single animations public again from their right-click menu.", Theme.Soft);
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, 4f * Theme.Scale));
        if (Theme.Primary("Make everything private"))
        {
            plugin.MarkAllModsPrivate();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Theme.Text("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawAnimationSuggestionPopup()
    {
        if (activeAnimationSuggestion is not null && !plugin.IsAnimationSuggestionActive(activeAnimationSuggestion))
            activeAnimationSuggestion = null;
        if (activeAnimationSuggestion is null && queuedAnimationSuggestions.TryDequeue(out var queued))
        {
            activeAnimationSuggestion = queued;
            openAnimationSuggestionPopup = true;
        }
        if (activeAnimationSuggestion is null) return;

        const string popupTitle = "Picked for the room###SynastryAnimationSuggestion";
        if (openAnimationSuggestionPopup)
        {
            ImGui.OpenPopup(popupTitle);
            openAnimationSuggestionPopup = false;
        }

        ImGui.SetNextWindowSize(new Vector2(560f, 460f) * Theme.Scale, ImGuiCond.Appearing);
        var popupOpen = true;
        if (ImGui.BeginPopupModal(popupTitle, ref popupOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            var suggestion = activeAnimationSuggestion;
            if (suggestion is null)
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                return;
            }

            ImGui.TextColored(Theme.AzureText, $"{suggestion.SuggestedBy} picked");
            Theme.Title(suggestion.ModName);
            var localMod = plugin.Mods.FirstOrDefault(mod =>
                mod.Directory.Equals(suggestion.Directory, StringComparison.OrdinalIgnoreCase));
            var animationActivated = false;
            ImGui.Dummy(new Vector2(0, 6f * Theme.Scale));
            ImGui.BeginChild("suggested-animation", new Vector2(0, -(ImGui.GetFrameHeightWithSpacing() + 18f * Theme.Scale)),
                false, ImGuiWindowFlags.None);
            if (string.IsNullOrWhiteSpace(localMod.Directory))
            {
                Theme.Wrapped("This animation isn't in your library anymore.", Theme.Ash);
            }
            else
            {
                plugin.EnsureDetectedEmotes(localMod.Directory, localMod.Name);
                var detectedPoses = plugin.GetDetectedPoses(localMod.Directory);
                var detectedEmotes = plugin.GetDetectedEmotes(localMod.Directory);
                var theirs = SuggestionAnimationName(suggestion.ActivatedTrigger, detectedPoses, detectedEmotes);
                Theme.Wrapped(theirs.Length > 0
                    ? $"{suggestion.SuggestedBy} is playing {theirs}. Pick your role to get ready."
                    : "Pick your role to get ready.", Theme.Soft);
                ImGui.Dummy(new Vector2(0, 4f * Theme.Scale));
                animationActivated = DrawModDetails(localMod, true);
                Theme.Quiet("Option changes here only affect your copy.");
            }
            ImGui.EndChild();

            var footer = ImGui.GetCursorScreenPos();
            Theme.HorizontalLine(ImGui.GetWindowDrawList(), footer, ImGui.GetContentRegionAvail().X);
            ImGui.Dummy(new Vector2(0, 10f * Theme.Scale));
            if (animationActivated)
            {
                activeAnimationSuggestion = null;
                ImGui.CloseCurrentPopup();
            }
            else if (Theme.Text("Not now"))
            {
                plugin.IgnoreAnimationSuggestion(suggestion);
                activeAnimationSuggestion = null;
                ImGui.CloseCurrentPopup();
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

        const string popupTitle = "Free Use Mode###SynastryFreeUse";
        if (activeFreeUsePrompt is not { } prompt)
        {
            // The member left or switched free use off while the prompt was showing; a modal that is
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

        ImGui.SetNextWindowSize(new Vector2(560f, 480f) * Theme.Scale, ImGuiCond.Appearing);
        var popupOpen = true;
        if (ImGui.BeginPopupModal(popupTitle, ref popupOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            openFreeUsePopup = false;
            ImGui.TextColored(Theme.AzureText, $"{prompt.MemberName} is in Free Use Mode");
            Theme.Title(prompt.ModName);
            var poses = plugin.GetDetectedPoses(prompt.Directory);
            var emotes = plugin.GetDetectedEmotes(prompt.Directory);
            var ownRole = SuggestionAnimationName(prompt.OwnTrigger, poses, emotes);
            if (ownRole.Length > 0) Theme.Label($"You're playing {ownRole}.");
            ImGui.Dummy(new Vector2(0, 6f * Theme.Scale));

            string? chosenTrigger = null;
            ImGui.BeginChild("free-use-choice", new Vector2(0, -(ImGui.GetFrameHeightWithSpacing() + 18f * Theme.Scale)),
                false, ImGuiWindowFlags.None);
            Theme.Label($"Role for {prompt.MemberName}");
            var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            var first = true;
            void Place(string label)
            {
                var chipWidth = ImGui.CalcTextSize(label).X + 30f * Theme.Scale;
                if (!first)
                {
                    ImGui.SameLine(0, 8f * Theme.Scale);
                    if (ImGui.GetCursorScreenPos().X + chipWidth > right) ImGui.NewLine();
                }
                first = false;
            }
            foreach (var pose in poses)
                if (FreeUseRoleChip(prompt, "$detected-pose", $"{pose.Kind}:{pose.Index}", PoseDisplayName(pose),
                        $"pose:{pose.Kind}:{pose.Index}", Place))
                    chosenTrigger = $"pose:{pose.Kind}:{pose.Index}";
            foreach (var emote in emotes)
                if (FreeUseRoleChip(prompt, "$detected-emote", emote.Id.ToString(), emote.Name, $"emote:{emote.Id}", Place))
                    chosenTrigger = $"emote:{emote.Id}";
            if (poses.Count == 0 && emotes.Count == 0)
                Theme.Quiet("This animation has no roles to pick.");
            DrawFreeUseOptions(prompt.Directory);
            ImGui.EndChild();

            Theme.HorizontalLine(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), ImGui.GetContentRegionAvail().X);
            ImGui.Dummy(new Vector2(0, 10f * Theme.Scale));
            if (chosenTrigger is not null)
            {
                plugin.DirectFreeUse(prompt, chosenTrigger, freeUseOptions);
                activeFreeUsePrompt = null;
                ImGui.CloseCurrentPopup();
            }
            else if (Theme.Text("Skip"))
            {
                activeFreeUsePrompt = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (!popupOpen) activeFreeUsePrompt = null;
    }

    private bool FreeUseRoleChip(
        FreeUsePrompt prompt,
        string group,
        string option,
        string animationName,
        string trigger,
        Action<string> place)
    {
        var note = plugin.GetOptionNote(prompt.Directory, group, option);
        var role = string.IsNullOrWhiteSpace(note) ? animationName : note;
        var isOwnRole = trigger.Equals(prompt.OwnTrigger, StringComparison.OrdinalIgnoreCase);
        var label = isOwnRole ? $"{role}, yours" : role;
        place(label);
        var clicked = Theme.Chip($"{label}##{trigger}", isOwnRole ? ChipState.Mine : ChipState.Open);
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
        var s = Theme.Scale;
        ImGui.Dummy(new Vector2(0, 6f * s));
        Theme.Label("Their options, copied from yours. Changes here are for them only.");
        foreach (var group in groups)
        {
            ImGui.PushID("free-use-" + group.Name);
            var selected = freeUseOptions.GetValueOrDefault(group.Name) ?? [];
            if (!group.IsMultiSelect)
            {
                var current = group.Options.FirstOrDefault(option => selected.Contains(option, StringComparer.OrdinalIgnoreCase));
                ImGui.SetNextItemWidth(MathF.Min(260f * s, ImGui.GetContentRegionAvail().X * 0.6f));
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
                ImGui.TextColored(Theme.Ash, group.Name);
                foreach (var option in group.Options)
                {
                    var enabled = selected.Contains(option, StringComparer.OrdinalIgnoreCase);
                    if (!ImGui.Checkbox(option, ref enabled)) continue;
                    selected = selected.Where(item => !item.Equals(option, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (enabled) selected.Add(option);
                    freeUseOptions[group.Name] = selected;
                }
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
            if (emote is not null) return emote.Name;
        }
        if (trigger.StartsWith("pose:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trigger["pose:".Length..].Split(':', 2);
            if (parts.Length == 2 && Enum.TryParse<PoseKind>(parts[0], true, out var kind) &&
                byte.TryParse(parts[1], out var index))
            {
                var pose = detectedPoses.FirstOrDefault(candidate => candidate.Kind == kind && candidate.Index == index);
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
            ImGui.OpenPopup("Room invite###SynastryRoomInvite");
        }
        if (!ImGui.BeginPopupModal("Room invite###SynastryRoomInvite", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;
        var active = activeRoomInvite;
        if (active is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.TextColored(Theme.AzureText, $"{active.SenderName} invited you to their room");
        using (Theme.CodeFont()) ImGui.TextUnformatted(active.RoomCode);
        ImGui.Dummy(new Vector2(0, 6f * Theme.Scale));
        if (Theme.Primary("Join", 140f * Theme.Scale))
        {
            plugin.AcceptRoomInvite(active);
            activeRoomInvite = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Theme.Text("Decline"))
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
        var s = Theme.Scale;
        ImGui.SetNextWindowSize(new Vector2(440f, 360f) * s, ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 18f) * s);
        var visible = ImGui.Begin("Offered to you###SynastryTransferInbox", ref transferInboxOpen, ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleVar();
        if (!visible)
        {
            ImGui.End();
            return;
        }

        if (pendingTransferOffers.Count == 0)
        {
            Theme.Quiet("Nothing waiting. Offers show up here when someone in your room sends you an animation.");
            ImGui.End();
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < pendingTransferOffers.Count; index++)
        {
            var offer = pendingTransferOffers[index];
            ImGui.PushID(offer.TransferId);
            ImGui.TextUnformatted(offer.ModName);
            Theme.Label($"from {offer.SenderName}");
            ImGui.SameLine(0, 6f * s);
            Theme.Number($"{offer.Size / 1024f / 1024f:0.0} MB");
            var remaining = Math.Max(0, (int)Math.Ceiling((offer.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes));
            Theme.Quiet($"Expires in {remaining} minute{(remaining == 1 ? "" : "s")}");
            if (Theme.Primary("Install", 110f * s))
            {
                plugin.AcceptModTransfer(offer);
                pendingTransferOffers.RemoveAt(index--);
            }
            else
            {
                ImGui.SameLine();
                if (Theme.Text("Decline"))
                {
                    plugin.DeclineModTransfer(offer);
                    pendingTransferOffers.RemoveAt(index--);
                }
            }
            ImGui.Dummy(new Vector2(0, 4f * s));
            Theme.HorizontalLine(draw, ImGui.GetCursorScreenPos(), ImGui.GetContentRegionAvail().X);
            ImGui.Dummy(new Vector2(0, 8f * s));
            ImGui.PopID();
        }
        ImGui.End();
    }

    // ---- Guide ----------------------------------------------------------------------------------

    private void ObserveTutorialState()
    {
        if (!tutorialActive) return;
        if (tutorialStep == TutorialStep.Connect && plugin.Sync.IsConnected)
        {
            tutorialStep = TutorialStep.Room;
            tutorialFeedback = "Linked. Now start a room for the people you're playing with.";
        }
        if (tutorialStep == TutorialStep.Room && plugin.Sync.Room is { Members.Count: >= 2 })
        {
            tutorialStep = TutorialStep.ConfigureAnimation;
            tutorialFeedback = "Your partner is in the room. Open an animation in the middle.";
        }
        else if (tutorialStep == TutorialStep.Room && plugin.Sync.Room is { Members.Count: 1 } &&
                 !tutorialFeedback.StartsWith("Invite sent", StringComparison.Ordinal) &&
                 !tutorialFeedback.StartsWith("Room started", StringComparison.Ordinal))
        {
            tutorialFeedback = "Room started. Right-click your partner in game and choose Invite to Synastry.";
        }

        if (tutorialStep == TutorialStep.QueueTogether && plugin.Sync.Room is { } room)
        {
            var currentReady = room.Members.Any(member => plugin.Sync.IsCurrentMember(member.ConnectionId) && member.Ready);
            var everyoneReady = room.Members.Count >= 2 && room.Members.All(member => member.Ready);
            if (currentReady && !tutorialFeedback.StartsWith("You're ready", StringComparison.Ordinal))
                tutorialFeedback = "You're ready. Your partner picks their role next.";
            if (everyoneReady)
            {
                tutorialStep = TutorialStep.TriggerPlayback;
                tutorialFeedback = "Everyone is ready. It starts in a moment.";
            }
        }
    }

    private void DrawTutorialCard(float width)
    {
        var s = Theme.Scale;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + new Vector2(22f * s, 12f * s));
        ImGui.BeginGroup();
        var step = Math.Min((int)tutorialStep + 1, 5);
        Theme.Label(tutorialStep == TutorialStep.Complete ? "Guide" : $"Guide, step {step} of 5");
        var (title, instructions) = TutorialCopy();
        Theme.Heading(title);
        ImGui.PushTextWrapPos(start.X - ImGui.GetWindowPos().X + width - 160f * s);
        Theme.Wrapped(instructions, Theme.Soft);
        if (!string.IsNullOrWhiteSpace(tutorialFeedback)) Theme.Wrapped(tutorialFeedback, Theme.AzureText);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var bottom = ImGui.GetItemRectMax().Y + 12f * s;

        var close = tutorialStep == TutorialStep.Complete ? "Done" : "End guide";
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - ImGui.CalcTextSize(close).X - 34f * s, start.Y + 10f * s));
        if (Theme.Text(close)) tutorialActive = false;

        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, new Vector2(start.X + width, bottom), ImGui.GetColorU32(Theme.Raised));
        draw.ChannelsMerge();
        Theme.HorizontalLine(draw, new Vector2(start.X, bottom), width);
        ImGui.SetCursorScreenPos(new Vector2(start.X, bottom + 1f));
        ImGui.Dummy(Vector2.Zero);
    }

    private (string Title, string Instructions) TutorialCopy() => tutorialStep switch
    {
        TutorialStep.Connect => (
            "Link up",
            "Press Link up on the right. It connects you to Synastry; it doesn't put you in a room yet."),
        TutorialStep.Room => (
            "Start a room and bring your partner in",
            "Start a new room or type a code to join one. Then right-click your partner in game and choose Invite to Synastry, or send them the code."),
        TutorialStep.ConfigureAnimation => (
            "Pick an animation",
            "Open an animation in the middle. Set its options first if it has any: with none chosen, Penumbra has nothing to play. Then click your role."),
        TutorialStep.QueueTogether => (
            "Get ready together",
            $"{(tutorialAnimationName.Length > 0 ? tutorialAnimationName + " is picked. " : "")}You show as ready on the right. Your partner opens the same animation and picks their role."),
        TutorialStep.TriggerPlayback => (
            "Play",
            "When everyone is ready it starts by itself. The host can press Start now instead of waiting."),
        _ => (
            "That's it",
            "You linked up, started a room, picked an animation and played it together. Find this guide again in the menu.")
    };

    private bool TryTutorialAnimationAction(string directory, string animationName)
    {
        if (!tutorialActive || tutorialStep != TutorialStep.ConfigureAnimation) return true;
        var groups = plugin.GetOptionGroups(directory);
        var hasSelectedOption = groups.Count == 0 || groups.Any(group =>
            group.Options.Any(option => plugin.IsOptionSelected(directory, group.Name, option)));
        if (!hasSelectedOption)
        {
            tutorialFeedback = "No option is chosen yet. Pick the option that holds the animation, then click the role again.";
            return false;
        }

        tutorialAnimationName = animationName;
        tutorialStep = TutorialStep.QueueTogether;
        tutorialFeedback = "Picked. Waiting for Synastry to mark you ready.";
        return true;
    }

    private bool TutorialHighlightsRoom => tutorialActive && tutorialStep is
        TutorialStep.Connect or TutorialStep.Room or TutorialStep.QueueTogether or TutorialStep.TriggerPlayback;

    private static void HighlightLastItem()
    {
        var pulse = 0.55f + 0.45f * (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) * 0.5f;
        var padding = new Vector2(3f, 3f) * Theme.Scale;
        ImGui.GetWindowDrawList().AddRect(
            ImGui.GetItemRectMin() - padding,
            ImGui.GetItemRectMax() + padding,
            ImGui.GetColorU32(Theme.WithAlpha(Theme.Rose, pulse)),
            4f * Theme.Scale,
            ImDrawFlags.None,
            2f * Theme.Scale);
    }

    // ---- Small helpers --------------------------------------------------------------------------

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }

    private static void Disabled(bool disabled, Action draw)
    {
        if (disabled) ImGui.BeginDisabled();
        draw();
        if (disabled) ImGui.EndDisabled();
    }

    private static bool IconButton(string id, float size, Action<ImDrawListPtr, Vector2, uint> icon)
    {
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered) draw.AddRectFilled(start, start + new Vector2(size, size), ImGui.GetColorU32(Theme.RowHover), 3f * Theme.Scale);
        icon(draw, start + new Vector2(size, size) * 0.5f, ImGui.GetColorU32(hovered ? Theme.Bone : Theme.Soft));
        return clicked;
    }

    private static bool IconTextButton(string id, string label, Action<ImDrawListPtr, Vector2, uint> icon)
    {
        var s = Theme.Scale;
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.CalcTextSize(label).X + 34f * s, ImGui.GetFrameHeight());
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered) draw.AddRectFilled(start, start + size, ImGui.GetColorU32(Theme.RowHover), 3f * s);
        var color = ImGui.GetColorU32(hovered ? Theme.Bone : Theme.Ash);
        icon(draw, start + new Vector2(16f * s, size.Y * 0.5f), color);
        draw.AddText(start + new Vector2(28f * s, (size.Y - ImGui.GetTextLineHeight()) * 0.5f), color, label);
        return clicked;
    }

    private static void DrawMenuIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var s = Theme.Scale;
        draw.AddLine(center + new Vector2(-7f, -4.5f) * s, center + new Vector2(7f, -4.5f) * s, color, 1.4f * s);
        draw.AddLine(center + new Vector2(-7f, 0f) * s, center + new Vector2(7f, 0f) * s, color, 1.4f * s);
        draw.AddLine(center + new Vector2(-7f, 4.5f) * s, center + new Vector2(2f, 4.5f) * s, color, 1.4f * s);
    }

    private static void DrawCloseIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var s = Theme.Scale;
        draw.AddLine(center + new Vector2(-4.5f, -4.5f) * s, center + new Vector2(4.5f, 4.5f) * s, color, 1.4f * s);
        draw.AddLine(center + new Vector2(4.5f, -4.5f) * s, center + new Vector2(-4.5f, 4.5f) * s, color, 1.4f * s);
    }

    private static void DrawPlusIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var s = Theme.Scale;
        draw.AddLine(center + new Vector2(-4.5f, 0) * s, center + new Vector2(4.5f, 0) * s, color, 1.4f * s);
        draw.AddLine(center + new Vector2(0, -4.5f) * s, center + new Vector2(0, 4.5f) * s, color, 1.4f * s);
    }

    private static void DrawChevron(ImDrawListPtr draw, Vector2 center, bool open, uint color)
    {
        var s = Theme.Scale;
        if (open)
            draw.AddTriangleFilled(center + new Vector2(-3.5f, -2f) * s, center + new Vector2(3.5f, -2f) * s,
                center + new Vector2(0, 2.5f) * s, color);
        else
            draw.AddTriangleFilled(center + new Vector2(-2f, -3.5f) * s, center + new Vector2(-2f, 3.5f) * s,
                center + new Vector2(2.5f, 0) * s, color);
    }

    private static string NoteKey(string directory, string group, string option) =>
        directory + "\n" + group + "\n" + option;

    private static string PoseDisplayName(PoseTarget pose) => pose.Kind switch
    {
        PoseKind.Sit => $"Chair sit {pose.Index}",
        PoseKind.GroundSit => $"Ground sit {pose.Index}",
        PoseKind.Doze => $"Doze {pose.Index}",
        _ => $"Idle {pose.Index}"
    };

    // ---- Selection and drag and drop ------------------------------------------------------------

    private void DrawFolderDragSource(ModCategory category)
    {
        if (!ImGui.BeginDragDropSource()) return;
        ImGui.SetDragDropPayload(FolderPayload, Encoding.UTF8.GetBytes(category.Id));
        ImGui.TextUnformatted($"{category.Name} ({plugin.GetCategoryModCount(category.Id)})");
        if (plugin.GetChildCategories(category.Id).Count > 0)
            Theme.Label("Its subfolders come along");
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
            var topEdge = ImGui.GetIO().MousePos.Y <= itemMin.Y + MathF.Min(7f, (itemMax.Y - itemMin.Y) * 0.35f);
            ImGui.SetTooltip(topEdge ? $"Put it before {target.Name}" : $"Put it inside {target.Name}");
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
            ImGui.SetTooltip("Move it to the top level");
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
                var first = Math.Min(anchorIndex, clickedIndex);
                var last = Math.Max(anchorIndex, clickedIndex);
                for (var index = first; index <= last; index++) selectedMods.Add(groupOrder[index].Directory);
                return;
            }
        }

        if (!selectedMods.Add(directory)) selectedMods.Remove(directory);
        selectionAnchor = directory;
        selectionAnchorGroup = groupKey;
    }

    private static int FindModIndex(IReadOnlyList<(string Directory, string Name)> mods, string directory)
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

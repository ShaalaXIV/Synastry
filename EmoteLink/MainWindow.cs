using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class MainWindow : Window
{
    private static readonly Vector4 AccentColor = new(0.88f, 0.62f, 0.18f, 1f);
    private static readonly Vector4 AccentHoveredColor = new(0.98f, 0.72f, 0.28f, 1f);
    private static readonly Vector4 AccentActiveColor = new(0.76f, 0.49f, 0.1f, 1f);
    private static readonly Vector4 PanelColor = new(0.075f, 0.09f, 0.105f, 1f);
    private static readonly Vector4 NestedPanelColor = new(0.055f, 0.068f, 0.08f, 1f);
    private static readonly Vector4 FrameColor = new(0.11f, 0.13f, 0.15f, 1f);
    private static readonly Vector4 FrameHoveredColor = new(0.15f, 0.175f, 0.2f, 1f);
    private static readonly Vector4 BorderColor = new(0.22f, 0.25f, 0.28f, 1f);
    private static readonly Vector4 TextColor = new(0.92f, 0.93f, 0.94f, 1f);
    private static readonly Vector4 MutedColor = new(0.62f, 0.65f, 0.69f, 1f);
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
    private readonly Dictionary<string, string> noteBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> correctionBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> folderRenameBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> folderOpenStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedMods = new(StringComparer.OrdinalIgnoreCase);
    private string? selectionAnchor;
    private string? selectionAnchorGroup;
    private const string ModPayload = "EMOTELINK_MOD";
    private const string FolderPayload = "EMOTELINK_FOLDER";

    public MainWindow(Plugin plugin) : base("Synastry###EmoteLink")
    {
        this.plugin = plugin;
        Size = new Vector2(780, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 540),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        CollectTransferOffers();
        PushUiStyle();
        try
        {
            DrawHeader();
            ImGui.Spacing();
            DrawStatusLine();
            ImGui.Spacing();
            DrawGroupPlay();
            ImGui.Spacing();
            DrawLibrary();
            ImGui.Spacing();
            DrawFooterActions();
            DrawRoomInvitePopup();
            DrawTransferInboxWindow();
        }
        finally
        {
            PopUiStyle();
        }
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
        var communityWidth = ButtonWidth("Community labels");
        var howToWidth = ButtonWidth("How to");
        var totalWidth = refreshWidth + communityWidth + howToWidth + ImGui.GetStyle().ItemSpacing.X * 2;
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

        if (plugin.IsRefreshingMods) ImGui.BeginDisabled();
        if (ImGui.Button(refreshLabel, new Vector2(refreshWidth, 0)))
            plugin.RefreshMods();
        if (plugin.IsRefreshingMods) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Community labels", new Vector2(communityWidth, 0))) plugin.DownloadCommunityTags();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Download accepted community role labels now. Your manually entered labels are preserved.");
        ImGui.SameLine();
        if (ImGui.Button("How to", new Vector2(howToWidth, 0))) plugin.OpenHowTo();
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
        ImGui.TextDisabled($"—  {plugin.Sync.Status}");

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
        ImGui.SameLine();
        var automaticSync = plugin.AutomaticEmoteSyncEnabled;
        if (ImGui.Checkbox("Auto EmoteSync", ref automaticSync)) plugin.SetAutomaticEmoteSync(automaticSync);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically run lobby-only EmoteSync six seconds after synchronized room playback starts.");
        ImGui.SameLine();
        var anywhere = plugin.SitDozeAnywhereEnabled;
        if (!plugin.SitDozeAnywhereAvailable) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Sit/doze anywhere", ref anywhere)) plugin.SetSitDozeAnywhere(anywhere);
        if (!plugin.SitDozeAnywhereAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.SitDozeAnywhereAvailable
                ? "When enabled, chair-sit and doze animations can start without nearby furniture."
                : "Unavailable because the required game hooks could not be initialized.");

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
        foreach (var category in plugin.Categories.ToList()) DrawCategory(category);
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
        var mods = plugin.GetOrganizedMods(category.Id);
        var visible = search.Length == 0 || mods.Any(MatchesSearch);
        if (visible)
        {
            var hadOpenState = folderOpenStates.TryGetValue(category.Id, out var previousOpen);
            if (hadOpenState) ImGui.SetNextItemOpen(previousOpen, ImGuiCond.Always);
            var open = ImGui.CollapsingHeader($"{category.Name}  {mods.Count}##folderHeader",
                ImGuiTreeNodeFlags.DefaultOpen);
            DrawFolderDragSource(category);
            var dragHovered = AcceptFolderReorder(category) | AcceptModDrop(category.Id);
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
                if (selectedMods.Count > 0 && ImGui.MenuItem($"Move {selectedMods.Count} selected here"))
                {
                    plugin.MoveMods(selectedMods, category.Id);
                    ClearModSelection();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Delete folder")) plugin.DeleteCategory(category.Id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mods will move to Uncategorized.");
                ImGui.EndPopup();
            }
            if (open) DrawModGroup(category.Id, null, false);
        }
        ImGui.PopID();
    }

    private void DrawModGroup(string? categoryId, string? heading, bool drawDropTarget)
    {
        var mods = plugin.GetOrganizedMods(categoryId);
        if (heading is not null)
        {
            var open = ImGui.CollapsingHeader($"{heading}  {mods.Count}##modGroup",
                ImGuiTreeNodeFlags.DefaultOpen);
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
        var treeFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick |
                        (selected ? ImGuiTreeNodeFlags.Selected : ImGuiTreeNodeFlags.None);
        var open = hasDetails
            ? ImGui.TreeNodeEx(displayName, treeFlags)
            : ImGui.Selectable(displayName, selected, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(labelWidth, 0));
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
            if (ImGui.BeginMenu(targets.Count > 1 ? "Move selected to folder" : "Move to folder"))
            {
                if (ImGui.MenuItem("Uncategorized"))
                {
                    plugin.MoveMods(targets, null);
                    ClearModSelection();
                }
                foreach (var category in plugin.Categories)
                {
                    if (!ImGui.MenuItem(category.Name)) continue;
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
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(),
                ImGui.GetWindowContentRegionMax().X - controlsWidth));
            if (statusWidth > 0) ImGui.TextColored(statusColor, statusLabel);
            if (sendWidth > 0)
            {
                if (statusWidth > 0) ImGui.SameLine();
                if (ImGui.SmallButton("Send")) plugin.SendMod(mod.Directory, mod.Name);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Offer this public mod to the room (75 MB maximum).");
            }
        }
        if (hasDetails && open)
        {
            plugin.EnsureDetectedEmotes(mod.Directory, mod.Name);
            detectedEmotes = plugin.GetDetectedEmotes(mod.Directory);
            DrawOptions(mod, groups, detectedPoses, detectedEmotes);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private void DrawOptions((string Directory, string Name) mod, IReadOnlyList<ModOptionGroup> groups,
        IReadOnlyList<PoseTarget> detectedPoses, IReadOnlyList<EmoteTarget> detectedEmotes)
    {
        if (detectedPoses.Count > 0 || detectedEmotes.Count > 0)
            DrawAnimationButtons(mod, detectedPoses, detectedEmotes);
        if (groups.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(AccentColor, "MOD OPTIONS");
        }
        foreach (var group in groups)
        {
            ImGui.PushID(group.Name);
            var groupSelectedBy = plugin.GetRemoteGroupSelector(mod.Directory, group.Name);
            if (groupSelectedBy is not null) ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
            var groupOpen = ImGui.TreeNodeEx(group.Name, ImGuiTreeNodeFlags.None);
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
    }

    private void DrawAnimationButtons((string Directory, string Name) mod,
        IReadOnlyList<PoseTarget> detectedPoses, IReadOnlyList<EmoteTarget> detectedEmotes)
    {
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
        var soloWidth = plugin.Sync.IsInRoom ? ButtonWidth("Solo") : 0f;
        var actionWidth = cellWidth - soloWidth - (soloWidth > 0 ? ImGui.GetStyle().ItemSpacing.X : 0f);
        var cellIndex = 0;

        foreach (var pose in detectedPoses)
        {
            if (cellIndex % columns != 0) ImGui.SameLine();
            ImGui.PushID($"detected-{pose.Kind}-{pose.Index}");
            if (DrawRoleActionButton(mod.Directory, "$detected-pose", $"{pose.Kind}:{pose.Index}",
                    PoseDisplayName(pose), showAssignmentWhenUnlabeled: true, width: actionWidth))
                plugin.ActivateDetectedPose(mod.Directory, mod.Name, pose);
            if (plugin.Sync.IsInRoom)
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
                plugin.ActivateDetectedEmote(mod.Directory, mod.Name, emote);
            if (plugin.Sync.IsInRoom)
            {
                ImGui.SameLine();
                if (ImGui.Button("Solo", new Vector2(soloWidth, 0)))
                    plugin.ActivateDetectedEmoteSolo(mod.Directory, mod.Name, emote);
            }
            ImGui.PopID();
            cellIndex++;
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
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

    private static void DrawFolderDragSource(ModCategory category)
    {
        if (!ImGui.BeginDragDropSource()) return;
        ImGui.SetDragDropPayload(FolderPayload, Encoding.UTF8.GetBytes(category.Id));
        ImGui.TextUnformatted(category.Name);
        ImGui.EndDragDropSource();
    }

    private bool AcceptFolderReorder(ModCategory category)
    {
        if (!ImGui.BeginDragDropTarget()) return false;
        var payload = ImGui.AcceptDragDropPayload(FolderPayload);
        var source = ReadPayload(payload);
        var hovered = source is not null;
        if (source is not null && payload.IsDelivery()) plugin.MoveCategory(source, category.Id);
        ImGui.EndDragDropTarget();
        return hovered;
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

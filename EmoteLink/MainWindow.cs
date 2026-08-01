using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class MainWindow : Window
{
    private static readonly Vector4 EveryoneColor = new(0.35f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 SomeColor = new(1f, 0.62f, 0.2f, 1f);
    private readonly Plugin plugin;
    private string search = "";
    private string newFolderName = "";
    private string syncDisplayName;
    private string roomCode = "";
    private const string ModPayload = "EMOTELINK_MOD";
    private const string FolderPayload = "EMOTELINK_FOLDER";

    public MainWindow(Plugin plugin) : base("EmoteLink###EmoteLink")
    {
        this.plugin = plugin;
        syncDisplayName = plugin.SyncDisplayName;
        Size = new Vector2(520, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.Text(plugin.PenumbraAvailable ? "Penumbra: connected" : "Penumbra: unavailable");
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) plugin.RefreshMods();

        if (ImGui.Button(plugin.IsAligning ? "Cancel alignment" : "Align / teleport to target"))
            plugin.ToggleAlignment();
        ImGui.SameLine();
        if (ImGui.Button("Clear temporary animations")) plugin.ClearTemporaryAssignments();

        ImGui.Separator();
        if (ImGui.Button("New folder")) ImGui.OpenPopup("Create folder");
        DrawCreateFolderPopup();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search Penumbra mods...", ref search, 128);
        ImGui.TextDisabled("Activating a mod clears the previous temporary animation first.");
        ImGui.TextWrapped(plugin.Status);
        DrawGroupPlay();

        if (ImGui.BeginChild("mods", new Vector2(0, 0), true))
        {
            foreach (var category in plugin.Categories.ToList()) DrawCategory(category);
            DrawModGroup(null, "Uncategorized", true);
            ImGui.EndChild();
        }
    }

    private void DrawGroupPlay()
    {
        if (!ImGui.CollapsingHeader("Group Play")) return;
        ImGui.TextDisabled(plugin.Sync.IsConnected ? "Connected" : "Not connected");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##syncName", "Display name", ref syncDisplayName, 40);

        if (!plugin.Sync.IsConnected)
        {
            if (ImGui.Button("Connect")) plugin.ConnectSync(syncDisplayName);
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.Sync.Status);
            return;
        }

        if (!plugin.Sync.IsInRoom)
        {
            if (ImGui.Button("Create room")) plugin.CreateSyncRoom(syncDisplayName);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputTextWithHint("##room", "Room code", ref roomCode, 8);
            ImGui.SameLine();
            if (ImGui.Button("Join")) plugin.JoinSyncRoom(roomCode, syncDisplayName);
            ImGui.SameLine();
            if (ImGui.Button("Disconnect")) plugin.DisconnectSync();
            return;
        }

        var room = plugin.Sync.Room;
        if (room is null) return;
        ImGui.TextUnformatted("Room:");
        ImGui.SameLine();
        if (ImGui.SmallButton($"{room.RoomCode}##copyRoomCode"))
        {
            ImGui.SetClipboardText(room.RoomCode);
            plugin.NotifyRoomCodeCopied(room.RoomCode);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy room code");
        ImGui.SameLine();
        if (ImGui.SmallButton("Leave")) plugin.LeaveSyncRoom();
        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel ready")) plugin.CancelSyncReady();
        foreach (var member in room.Members)
        {
            var marker = member.Ready ? "Ready" : "Waiting";
            ImGui.BulletText($"{member.DisplayName}{(member.IsLeader ? " (host)" : "")} — {marker}");
        }
        ImGui.TextColored(EveryoneColor, "● Everyone has it");
        ImGui.SameLine();
        ImGui.TextColored(SomeColor, "● Some members");
        ImGui.SameLine();
        ImGui.TextDisabled("○ No match");
        ImGui.TextDisabled("Choose an animation below to ready up. Playback begins when everyone is ready on the same mod.");
        ImGui.Separator();
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

    private void DrawCategory(ModCategory category)
    {
        ImGui.PushID(category.Id);
        var visible = search.Length == 0 || plugin.GetOrganizedMods(category.Id).Any(MatchesSearch);
        if (visible)
        {
            var open = ImGui.CollapsingHeader(category.Name, ImGuiTreeNodeFlags.DefaultOpen);
            DrawFolderDragSource(category);
            AcceptFolderReorder(category);
            AcceptModDrop(category.Id);
            if (ImGui.BeginPopupContextItem("folderMenu"))
            {
                ImGui.TextDisabled("Folder options");
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
        if (heading is not null)
        {
            ImGui.Separator();
            ImGui.TextDisabled(heading);
            if (drawDropTarget) AcceptModDrop(categoryId);
        }

        var mods = plugin.GetOrganizedMods(categoryId);
        var drewAny = false;
        foreach (var mod in mods)
        {
            if (!MatchesSearch(mod)) continue;
            drewAny = true;
            DrawModRow(mod, categoryId);
        }
        if (!drewAny && search.Length == 0) ImGui.TextDisabled("  Drop mods here");
    }

    private void DrawModRow((string Directory, string Name) mod, string? categoryId)
    {
        ImGui.PushID(mod.Directory);
        if (ImGui.Button(plugin.Sync.IsInRoom ? "Ready" : "Activate"))
            plugin.Activate(mod.Directory, mod.Name);
        if (plugin.Sync.IsInRoom)
        {
            ImGui.SameLine();
            if (ImGui.Button("Solo")) plugin.ActivateSolo(mod.Directory, mod.Name);
        }
        ImGui.SameLine();
        var groups = plugin.GetOptionGroups(mod.Directory);
        var match = plugin.GetModMatch(mod.Directory);
        var hasMatchColor = match.Members > 1 && match.Matches > 1;
        if (hasMatchColor)
            ImGui.PushStyleColor(ImGuiCol.Text,
                match.Matches >= match.Members ? EveryoneColor : SomeColor);
        var open = groups.Count > 0
            ? ImGui.TreeNodeEx(mod.Name, ImGuiTreeNodeFlags.SpanAvailWidth)
            : ImGui.Selectable(mod.Name, false, ImGuiSelectableFlags.AllowDoubleClick);
        if (hasMatchColor) ImGui.PopStyleColor();

        if (ImGui.BeginDragDropSource())
        {
            ImGui.SetDragDropPayload(ModPayload, Encoding.UTF8.GetBytes(mod.Directory));
            ImGui.TextUnformatted(mod.Name);
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(ModPayload);
            var source = ReadPayload(payload);
            if (source is not null && source != mod.Directory)
                plugin.MoveMod(source, categoryId, mod.Directory);
            ImGui.EndDragDropTarget();
        }
        if (groups.Count > 0 && open)
        {
            DrawOptions(mod, groups);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private void DrawOptions((string Directory, string Name) mod, IReadOnlyList<ModOptionGroup> groups)
    {
        foreach (var group in groups)
        {
            ImGui.PushID(group.Name);
            ImGui.TextDisabled(group.Name);
            foreach (var option in group.Options)
            {
                ImGui.PushID(option);
                var selected = plugin.IsOptionSelected(mod.Directory, group.Name, option);
                if (ImGui.Checkbox(option, ref selected))
                {
                    plugin.SetOptionSelected(mod.Directory, group.Name, option, selected, group.IsMultiSelect);
                    var appliedSelection = plugin.IsOptionSelected(mod.Directory, group.Name, option);
                    plugin.ApplyOption(mod.Directory, mod.Name, group.Name, option, appliedSelection);
                }
                var pose = plugin.GetOptionPose(mod.Directory, group.Name, option);
                ImGui.SameLine();
                var poseLabel = pose is null ? "Set pose..." : $"{pose.Kind} {pose.Index}";
                if (ImGui.SmallButton($"{poseLabel}##pose")) ImGui.OpenPopup("Pose assignment");
                DrawPoseAssignmentPopup(mod.Directory, group.Name, option, pose);
                ImGui.SameLine();
                if (ImGui.SmallButton($"{(plugin.Sync.IsInRoom ? "Ready" : "Activate")}##option"))
                    plugin.ActivateOption(mod.Directory, mod.Name, group.Name, option, group.IsMultiSelect);
                if (plugin.Sync.IsInRoom)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Solo##option"))
                        plugin.ActivateOptionSolo(mod.Directory, mod.Name, group.Name, option, group.IsMultiSelect);
                }
                ImGui.PopID();
            }
            ImGui.PopID();
        }
    }

    private void DrawPoseAssignmentPopup(string directory, string group, string option, PoseTarget? current)
    {
        if (!ImGui.BeginPopup("Pose assignment")) return;
        ImGui.TextUnformatted("What pose does this option replace?");
        if (plugin.HasManualPose(directory, group, option) && ImGui.Button("Use automatic detection"))
            plugin.SetManualPose(directory, group, option, null);

        var selectedKind = current?.Kind ?? PoseKind.Idle;
        ImGui.TextDisabled("Pose state");
        foreach (var kind in Enum.GetValues<PoseKind>())
        {
            if (ImGui.RadioButton(kind.ToString(), selectedKind == kind))
            {
                selectedKind = kind;
                plugin.SetManualPose(directory, group, option,
                    new PoseTarget(selectedKind, current?.Index ?? 0));
                current = plugin.GetOptionPose(directory, group, option);
            }
            if (kind != PoseKind.Doze) ImGui.SameLine();
        }

        ImGui.TextDisabled("Pose number");
        for (var index = 0; index <= PoseService.MaxPoseIndex; index++)
        {
            var label = index == 0 ? "Default" : index.ToString();
            if (ImGui.Button($"{label}##poseIndex{index}"))
            {
                plugin.SetManualPose(directory, group, option, new PoseTarget(selectedKind, (byte)index));
                ImGui.CloseCurrentPopup();
            }
            if (index < PoseService.MaxPoseIndex) ImGui.SameLine();
        }
        ImGui.EndPopup();
    }

    private static void DrawFolderDragSource(ModCategory category)
    {
        if (!ImGui.BeginDragDropSource()) return;
        ImGui.SetDragDropPayload(FolderPayload, Encoding.UTF8.GetBytes(category.Id));
        ImGui.TextUnformatted(category.Name);
        ImGui.EndDragDropSource();
    }

    private void AcceptFolderReorder(ModCategory category)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        var source = ReadPayload(ImGui.AcceptDragDropPayload(FolderPayload));
        if (source is not null) plugin.MoveCategory(source, category.Id);
        ImGui.EndDragDropTarget();
    }

    private void AcceptModDrop(string? categoryId)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        var source = ReadPayload(ImGui.AcceptDragDropPayload(ModPayload));
        if (source is not null) plugin.MoveMod(source, categoryId);
        ImGui.EndDragDropTarget();
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

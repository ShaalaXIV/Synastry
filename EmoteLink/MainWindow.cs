using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class MainWindow : Window
{
    private static readonly Vector4 EveryoneColor = new(0.35f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 SomeColor = new(1f, 0.62f, 0.2f, 1f);
    private static readonly Vector4 ClaimedColor = new(0.72f, 0.42f, 1f, 1f);
    private static readonly Vector4 PrivateColor = new(0.2f, 0.85f, 0.9f, 1f);
    private readonly Plugin plugin;
    private string search = "";
    private string newFolderName = "";
    private string roomCode = "";
    private ModTransferOfferDto? activeTransferOffer;
    private readonly Dictionary<string, string> noteBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> correctionBuffers = new(StringComparer.OrdinalIgnoreCase);
    private const string ModPayload = "EMOTELINK_MOD";
    private const string FolderPayload = "EMOTELINK_FOLDER";

    public MainWindow(Plugin plugin) : base("EmoteLink###EmoteLink")
    {
        this.plugin = plugin;
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

        ImGui.TextDisabled("Activating a mod clears the previous temporary animation first.");
        ImGui.TextWrapped(plugin.Status);
        DrawGroupPlay();
        ImGui.Separator();
        if (ImGui.Button("New folder")) ImGui.OpenPopup("Create folder");
        DrawCreateFolderPopup();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search Penumbra mods...", ref search, 128);
        DrawTransferOfferPopup();

        if (ImGui.BeginChild("mods", new Vector2(0, 0), true))
        {
            foreach (var category in plugin.Categories.ToList()) DrawCategory(category);
            DrawModGroup(null, "Uncategorized", true);
            ImGui.EndChild();
        }
    }

    private void DrawGroupPlay()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Group Play");
        ImGui.TextDisabled($"{(plugin.Sync.IsConnected ? "Connected" : "Not connected")} - {plugin.Sync.Status}");
        ImGui.TextDisabled($"Character: {plugin.SyncDisplayName}");

        if (!plugin.Sync.IsConnected)
        {
            if (ImGui.Button("Connect")) plugin.ConnectSync();
            return;
        }

        if (!plugin.Sync.IsInRoom)
        {
            if (ImGui.Button("Create room")) plugin.CreateSyncRoom();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputTextWithHint("##room", "Room code", ref roomCode, 8);
            ImGui.SameLine();
            if (ImGui.Button("Join")) plugin.JoinSyncRoom(roomCode);
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
        if (plugin.Sync.IsRoomLeader)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Force start")) plugin.ForceSyncStart();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Start the host's prepared animation now. Only members prepared on the same mod will join.");
        }
        foreach (var member in room.Members)
        {
            var marker = member.Ready ? "Ready" : "Waiting";
            ImGui.BulletText($"{member.DisplayName}{(member.IsLeader ? " (host)" : "")} — {marker}");
            if (plugin.Sync.IsRoomLeader && !plugin.Sync.IsCurrentMember(member.ConnectionId))
            {
                ImGui.SameLine();
                ImGui.PushID(member.ConnectionId);
                if (ImGui.SmallButton("Remove")) plugin.RemoveSyncMember(member);
                ImGui.PopID();
            }
        }
        ImGui.TextColored(EveryoneColor, "Green: Everyone has it");
        ImGui.SameLine();
        ImGui.TextColored(SomeColor, "Orange: Some members");
        ImGui.SameLine();
        ImGui.TextColored(ClaimedColor, "Purple: Suggested");
        ImGui.SameLine();
        ImGui.TextColored(PrivateColor, "Cyan: Private");
        ImGui.SameLine();
        ImGui.TextDisabled("White: No match");
        ImGui.TextDisabled("Choose an animation below to ready up. Playback begins when everyone is ready on the same mod.");
        ImGui.Separator();
    }

    private void DrawTransferOfferPopup()
    {
        if (activeTransferOffer is null && plugin.TryTakeTransferOffer(out var offer))
        {
            activeTransferOffer = offer;
            ImGui.OpenPopup("Animation mod received");
        }
        if (!ImGui.BeginPopupModal("Animation mod received", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var active = activeTransferOffer;
        if (active is null) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        ImGui.TextWrapped($"{active.SenderName} wants to send you:");
        ImGui.TextUnformatted(active.ModName);
        ImGui.TextDisabled($"{active.Size / 1024f / 1024f:F1} MB");
        ImGui.Spacing();
        if (ImGui.Button("Accept", new Vector2(110, 0)))
        {
            plugin.AcceptModTransfer(active);
            activeTransferOffer = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Decline", new Vector2(110, 0)))
        {
            plugin.DeclineModTransfer(active);
            activeTransferOffer = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
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
        var groups = plugin.GetOptionGroups(mod.Directory);
        var detectedPoses = plugin.GetDetectedPoses(mod.Directory);
        var detectedEmotes = plugin.GetDetectedEmotes(mod.Directory);
        var match = plugin.GetModMatch(mod.Directory);
        var selectedBy = plugin.GetRemoteModSelector(mod.Directory);
        var isPrivate = plugin.IsModPrivate(mod.Directory);
        var hasMatchColor = match.Members > 1 && match.Matches > 1;
        if (selectedBy is not null)
            ImGui.PushStyleColor(ImGuiCol.Text, ClaimedColor);
        else if (isPrivate)
            ImGui.PushStyleColor(ImGuiCol.Text, PrivateColor);
        else if (hasMatchColor)
            ImGui.PushStyleColor(ImGuiCol.Text,
                match.Matches >= match.Members ? EveryoneColor : SomeColor);
        var hasDetails = groups.Count > 0 || detectedPoses.Count > 0 || detectedEmotes.Count > 0;
        var sendWidth = plugin.Sync.IsInRoom && !isPrivate
            ? ImGui.CalcTextSize("Send").X + ImGui.GetStyle().FramePadding.X * 2
            : 0;
        var labelWidth = MathF.Max(1, ImGui.GetContentRegionAvail().X - sendWidth -
            (plugin.Sync.IsInRoom && !isPrivate ? ImGui.GetStyle().ItemSpacing.X : 0));
        var displayName = isPrivate ? $"{mod.Name} [Private]" : mod.Name;
        var open = hasDetails
            ? ImGui.TreeNodeEx(displayName, ImGuiTreeNodeFlags.None)
            : ImGui.Selectable(displayName, false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(labelWidth, 0));
        if (selectedBy is not null || isPrivate || hasMatchColor) ImGui.PopStyleColor();
        if (selectedBy is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip($"{selectedBy} selected an option in this mod.");

        if (ImGui.BeginPopupContextItem("modMenu"))
        {
            if (ImGui.MenuItem("Private", "", isPrivate)) plugin.SetModPrivate(mod.Directory, !isPrivate);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Private mods are not advertised or transferable in group play.");
            ImGui.EndPopup();
        }

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
        DrawInlineAnimationTriggers(mod, detectedPoses, detectedEmotes);
        if (plugin.Sync.IsInRoom && !isPrivate)
        {
            var currentX = ImGui.GetCursorPosX();
            var rightX = ImGui.GetWindowContentRegionMax().X - sendWidth;
            if (currentX + ImGui.GetStyle().ItemSpacing.X + sendWidth <= rightX) ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - sendWidth);
            if (ImGui.SmallButton("Send")) plugin.SendMod(mod.Directory, mod.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Offer this mod to everyone else in the room (75 MB maximum).");
        }
        if (hasDetails && open)
        {
            DrawOptions(mod, groups, detectedPoses, detectedEmotes);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private void DrawInlineAnimationTriggers((string Directory, string Name) mod,
        IReadOnlyList<PoseTarget> detectedPoses, IReadOnlyList<EmoteTarget> detectedEmotes)
    {
        foreach (var pose in detectedPoses)
        {
            ImGui.PushID($"inline-pose-{pose.Kind}-{pose.Index}");
            ImGui.SameLine();
            if (DrawRoleActionButton(mod.Directory, "$detected-pose", $"{pose.Kind}:{pose.Index}",
                    PoseDisplayName(pose)))
                plugin.ActivateDetectedPose(mod.Directory, mod.Name, pose);
            ImGui.PopID();
        }
        foreach (var emote in detectedEmotes)
        {
            ImGui.PushID($"inline-emote-{emote.Id}");
            ImGui.SameLine();
            if (DrawRoleActionButton(mod.Directory, "$detected-emote", emote.Id.ToString(),
                    $"{emote.Name} (ID {emote.Id})"))
                plugin.ActivateDetectedEmote(mod.Directory, mod.Name, emote);
            ImGui.PopID();
        }
    }

    private void DrawOptions((string Directory, string Name) mod, IReadOnlyList<ModOptionGroup> groups,
        IReadOnlyList<PoseTarget> detectedPoses, IReadOnlyList<EmoteTarget> detectedEmotes)
    {
        if (detectedPoses.Count > 0 || detectedEmotes.Count > 0)
        {
            ImGui.TextDisabled("Detected animation triggers");
            foreach (var pose in detectedPoses)
            {
                ImGui.PushID($"detected-{pose.Kind}-{pose.Index}");
                ImGui.TextUnformatted(PoseDisplayName(pose));
                ImGui.SameLine();
                if (DrawRoleActionButton(mod.Directory, "$detected-pose", $"{pose.Kind}:{pose.Index}",
                        PoseDisplayName(pose)))
                {
                    plugin.ActivateDetectedPose(mod.Directory, mod.Name, pose);
                }
                if (plugin.Sync.IsInRoom)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Solo"))
                    {
                        plugin.ActivateDetectedPoseSolo(mod.Directory, mod.Name, pose);
                    }
                }
                ImGui.PopID();
            }
            foreach (var emote in detectedEmotes)
            {
                ImGui.PushID($"emote-{emote.Id}");
                ImGui.TextUnformatted($"{emote.Name} (ID {emote.Id})");
                ImGui.SameLine();
                if (DrawRoleActionButton(mod.Directory, "$detected-emote", emote.Id.ToString(),
                        $"{emote.Name} (ID {emote.Id})"))
                    plugin.ActivateDetectedEmote(mod.Directory, mod.Name, emote);
                if (plugin.Sync.IsInRoom)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Solo"))
                        plugin.ActivateDetectedEmoteSolo(mod.Directory, mod.Name, emote);
                }
                ImGui.PopID();
            }
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
                        plugin.SetOptionSelected(mod.Directory, group.Name, option, selected, group.IsMultiSelect);
                        var appliedSelection = plugin.IsOptionSelected(mod.Directory, group.Name, option);
                        plugin.ApplyOption(mod.Directory, mod.Name, group.Name, option, appliedSelection);
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

    private bool DrawRoleActionButton(string directory, string group, string option, string animationName)
    {
        var key = NoteKey(directory, group, option);
        var savedNote = plugin.GetOptionNote(directory, group, option);
        if (!noteBuffers.TryGetValue(key, out var note) ||
            (string.IsNullOrWhiteSpace(note) && !string.IsNullOrWhiteSpace(savedNote))) note = savedNote;
        noteBuffers[key] = note;
        var hasRole = !string.IsNullOrWhiteSpace(note);
        var label = hasRole
            ? plugin.Sync.IsInRoom ? $"{note} - Ready" : note
            : plugin.Sync.IsInRoom ? "Ready" : "Activate";
        var selectedBy = plugin.GetRemoteDetectedTriggerSelector(directory, group, option);
        if (selectedBy is not null)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ClaimedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.58f, 1f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.3f, 0.88f, 1f));
        }
        var clicked = ImGui.SmallButton(label);
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
                    ImGui.SetTooltip("Applies immediately for you. Three matching reports update the community label.");
            }
            ImGui.EndPopup();
        }
        return clicked;
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

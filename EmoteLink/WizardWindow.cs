using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

/// <summary>
/// The Synastry Wizard: a guided way to ready a room animation in two steps. Step 1 picks the
/// animation (sending it to whoever is missing it) and your role and options; step 2 sorts out
/// positions and readies everyone. One member can choose for the whole room: the others then see
/// that pick, ready it with one click, or ask to swap roles. Opens only when asked for.
/// </summary>
public sealed class WizardWindow : Window
{
    private const int MaxRows = 150;
    private readonly Plugin plugin;
    private int step = 1;
    private string search = "";
    private (string Directory, string Name)? selected;
    private string? myRole;
    /// <summary>Roles chosen for other members (connection id to role) when you direct them.</summary>
    private readonly Dictionary<string, string> assignments = new(StringComparer.Ordinal);
    private bool keepPositions = true;
    private string? lineUpWith;
    /// <summary>What was sent at the last Ready (animation, your role, everyone's roles), so a later
    /// change offers "Ready again".</summary>
    private string? readiedSnapshot;
    private int readiedGeneration;
    private long readyClickedAt;

    private static readonly (Vector4 Color, string Label)[] Legend =
    [
        (Theme.Everyone, "Everyone has it"),
        (Theme.Some, "Someone needs it sent")
    ];

    public WizardWindow(Plugin plugin) : base("Synastry Wizard###SynastryWizard", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(540, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Open() => IsOpen = true;

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 18f) * Theme.Scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.Pop();
    }

    public override void Draw()
    {
        var s = Theme.Scale;
        if (plugin.Sync.Room is not { } room)
        {
            Theme.Heading("Synastry Wizard");
            Theme.Wrapped("The wizard readies an animation with your room, step by step. Join or start a room first.",
                Theme.Soft);
            ImGui.Dummy(new Vector2(0, 6f * s));
            if (Theme.Primary("Open Synastry")) plugin.ShowFullWindow();
            return;
        }

        DrawControllerLine(room);
        DrawRequests(room);
        var controller = plugin.WizardController;
        if (controller is not null && !plugin.IsWizardController)
        {
            DrawMemberView(room, controller);
            DrawStatus();
            return;
        }

        DrawSteps();
        ImGui.Dummy(new Vector2(0, 4f * s));
        if (step == 1) DrawStepOne(room);
        else DrawStepTwo(room);
        DrawStatus();
    }

    // ---- Header: who chooses ------------------------------------------------------------------

    private void DrawControllerLine(RoomStateDto room)
    {
        var s = Theme.Scale;
        Theme.Label($"Room {room.RoomCode} · {room.Members.Count} {(room.Members.Count == 1 ? "member" : "members")}");
        var controller = plugin.WizardController;
        var supported = plugin.WizardRelaySupported;
        ImGui.SameLine(0, 14f * s);
        if (controller is null)
        {
            if (!supported) ImGui.BeginDisabled();
            if (Theme.Text("Choose for the room", Theme.AzureText)) plugin.SetWizardController(true);
            if (!supported) ImGui.EndDisabled();
            Tooltip(supported
                ? "Pick the animation and everyone's role. The others see your picks, ready with one click, and can ask to swap."
                : "The relay needs updating before one member can choose for the room.");
        }
        else if (plugin.IsWizardController)
        {
            ImGui.TextColored(Theme.AzureText, "You're choosing for the room");
            ImGui.SameLine(0, 8f * s);
            if (Theme.Text("Stop")) plugin.SetWizardController(false);
        }
        else
        {
            ImGui.TextColored(Theme.AzureText, $"{controller.DisplayName} is choosing");
            if (plugin.Sync.IsRoomLeader)
            {
                ImGui.SameLine(0, 8f * s);
                if (Theme.Text("Take over", Theme.AzureText)) plugin.SetWizardController(true);
                Tooltip($"As host, choose for the room instead of {controller.DisplayName}.");
            }
        }
        ImGui.Dummy(new Vector2(0, 2f * s));
    }

    private void DrawRequests(RoomStateDto room)
    {
        var s = Theme.Scale;
        foreach (var request in plugin.WizardSwapRequests.ToList())
        {
            ImGui.PushID("swap-" + request.FromConnectionId);
            ImGui.TextColored(Theme.Some, $"{request.FromName} asks to swap roles.");
            ImGui.SameLine(0, 10f * s);
            var canSwap = CanSwapNow(room, out var reason);
            if (!canSwap) ImGui.BeginDisabled();
            if (Theme.Text("Swap", Theme.AzureText)) Swap(room, request);
            if (!canSwap) ImGui.EndDisabled();
            if (!canSwap) Tooltip(reason);
            ImGui.SameLine(0, 2f * s);
            if (Theme.Text("Not now")) plugin.DismissSwapRequest(request);
            ImGui.PopID();
        }
        if (plugin.WizardLineUpRequest is { } lineUp)
        {
            ImGui.PushID("lineup-request");
            ImGui.TextColored(Theme.Some, $"{lineUp.FromName} asks you to line up to them.");
            ImGui.SameLine(0, 10f * s);
            if (Theme.Text("Line up", Theme.AzureText)) plugin.LineUpToRequester();
            ImGui.SameLine(0, 2f * s);
            if (Theme.Text("Not now")) plugin.DismissLineUpRequest();
            ImGui.PopID();
        }
    }

    private void DrawSteps()
    {
        var s = Theme.Scale;
        if (Theme.Chip("1 · Animation and role##step1", step == 1 ? ChipState.Mine : ChipState.Open)) step = 1;
        ImGui.SameLine(0, 8f * s);
        var canContinue = CanContinue(out _);
        if (!canContinue) ImGui.BeginDisabled();
        if (Theme.Chip("2 · Position and ready##step2", step == 2 ? ChipState.Mine : ChipState.Open)) GoToStepTwo();
        if (!canContinue) ImGui.EndDisabled();
        if (!canContinue) Tooltip("Pick an animation everyone has and your role first.");
    }

    // ---- Step 1: animation and role -----------------------------------------------------------

    private void DrawStepOne(RoomStateDto room)
    {
        var s = Theme.Scale;
        var width = ImGui.GetContentRegionAvail().X;
        Theme.Label("Pick the next animation");
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##wizard-search", "Search animations", ref search, 128);

        var legendStart = ImGui.GetCursorScreenPos();
        Theme.Legend(ImGui.GetWindowDrawList(), legendStart, Legend);
        ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight() + 4f * s));

        var candidates = plugin.OrderModsForLibrary(plugin.Mods.Where(mod =>
            !plugin.IsModPrivate(mod.Directory) &&
            (search.Length == 0 ||
             mod.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             mod.Directory.Contains(search, StringComparison.OrdinalIgnoreCase))));
        var listHeight = MathF.Max(150f * s, MathF.Min(230f * s, ImGui.GetContentRegionAvail().Y * 0.36f));
        ImGui.BeginChild("wizard-list", new Vector2(width, listHeight), true, ImGuiWindowFlags.None);
        foreach (var mod in candidates.Take(MaxRows)) DrawRow(mod);
        if (candidates.Count == 0) Theme.Quiet(search.Length > 0 ? "Nothing matches that search." : "No animations yet.");
        else if (candidates.Count > MaxRows) Theme.Quiet($"Showing {MaxRows} of {candidates.Count}. Search to narrow it down.");
        ImGui.EndChild();
        Theme.Quiet("Private animations aren't listed.");

        if (selected is not { } pick || !plugin.Mods.Contains(pick))
        {
            selected = null;
            ImGui.Dummy(new Vector2(0, 4f * s));
            Theme.Quiet("Pick an animation to choose your role.");
            return;
        }

        ImGui.Dummy(new Vector2(0, 6f * s));
        using (Theme.HeadingFont()) ImGui.TextUnformatted(Theme.Truncate(pick.Name, width));
        var everyone = DrawWhoHasIt(room, pick);

        ImGui.Dummy(new Vector2(0, 4f * s));
        Theme.Label("Your role");
        if (!everyone)
        {
            Theme.Quiet("Unlocks once everyone has it.");
            return;
        }
        var triggers = plugin.RoleTriggersOf(pick.Directory, pick.Name);
        if (triggers.Count == 0)
        {
            Theme.Wrapped("Synastry can't find a role to start in this animation.", Theme.Soft);
            return;
        }
        if (myRole is not null && !triggers.Contains(myRole)) myRole = null;
        DrawRoleChips(room, pick, triggers);
        if (myRole is not null) DrawOthersRoles(room, pick, triggers);

        var groups = plugin.GetOptionGroups(pick.Directory);
        if (groups.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4f * s));
            Theme.Label("Options");
            MainWindow.DrawOptionGroups(plugin, pick.Directory, groups);
        }

        ImGui.Dummy(new Vector2(0, 8f * s));
        var canContinue = CanContinue(out var reason);
        if (!canContinue) ImGui.BeginDisabled();
        if (Theme.Primary("Next")) GoToStepTwo();
        if (!canContinue) ImGui.EndDisabled();
        if (!canContinue) Tooltip(reason);
    }

    private void DrawRow((string Directory, string Name) mod)
    {
        var s = Theme.Scale;
        var height = 28f * s;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var isSelected = selected is { } current && current.Directory == mod.Directory;
        var everyone = EveryoneHas(mod.Directory);
        var color = everyone ? Theme.Everyone : Theme.Some;

        ImGui.PushID(mod.Directory);
        if (ImGui.Selectable("##row", isSelected, ImGuiSelectableFlags.None, new Vector2(width, height)))
            Select(mod);
        ImGui.PopID();

        if (!isSelected) draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(Theme.WithAlpha(color, 0.07f)));
        draw.AddRectFilled(start, start + new Vector2(2f * s, height), ImGui.GetColorU32(color));
        var tag = everyone ? "everyone"
            : plugin.IsSendingMod(mod.Directory) ? "sending"
            : plugin.WasSentRecently(mod.Directory) ? "sent"
            : "needs sending";
        var tagWidth = ImGui.CalcTextSize(tag).X;
        var textY = start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;
        draw.AddText(new Vector2(start.X + 12f * s, textY), ImGui.GetColorU32(color),
            Theme.Truncate(mod.Name, width - tagWidth - 36f * s));
        draw.AddText(new Vector2(start.X + width - tagWidth - 10f * s, textY), ImGui.GetColorU32(Theme.Faint), tag);
    }

    private void Select((string Directory, string Name) mod)
    {
        if (selected is { } current && current.Directory == mod.Directory) return;
        selected = mod;
        myRole = null;
        assignments.Clear();
    }

    /// <summary>Who's missing the animation and the button to send it. True when everyone has it.</summary>
    private bool DrawWhoHasIt(RoomStateDto room, (string Directory, string Name) pick)
    {
        if (EveryoneHas(pick.Directory))
        {
            ImGui.TextColored(Theme.Everyone, room.Members.Count > 1 ? "Everyone in the room has it." : "Nobody else is in the room yet.");
            return true;
        }

        var (matches, members) = plugin.GetModMatch(pick.Directory);
        var names = plugin.MissingMemberNames(pick.Directory);
        var missing = Math.Max(1, members - Math.Max(1, matches));
        Theme.Wrapped(names is { Count: > 0 }
                ? $"{JoinNames(names)} {(names.Count == 1 ? "doesn't" : "don't")} have it yet."
                : $"{missing} {(missing == 1 ? "member doesn't" : "members don't")} have it yet.",
            Theme.Some);
        if (plugin.IsSendingMod(pick.Directory))
        {
            Theme.Quiet("Sending…");
        }
        else
        {
            var label = names is { Count: 1 } ? $"Send to {names[0]}" : "Send to all";
            if (plugin.WasSentRecently(pick.Directory))
            {
                Theme.Quiet("Sent. It unlocks once they install it.");
                label = "Send again";
            }
            if (Theme.Outline(label)) plugin.SendMod(pick.Directory, pick.Name);
            Tooltip("Offers it to everyone in the room who doesn't have it. Roles unlock once it's installed.");
        }
        return false;
    }

    private void DrawRoleChips(RoomStateDto room, (string Directory, string Name) pick, IReadOnlyList<string> triggers)
    {
        var s = Theme.Scale;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var directed = DirectedMembers(room).ToList();
        var modKey = plugin.ModKeyOf(pick.Directory);
        var first = true;
        foreach (var trigger in triggers)
        {
            var role = plugin.RoleName(pick.Directory, trigger);
            // Who has it: someone you direct, else someone who picked it for themselves.
            var holder = myRole is null ? null : directed.FirstOrDefault(entry => entry.Role == trigger).Member;
            holder ??= modKey is null ? null : Others(room).FirstOrDefault(member =>
                !Directs(member) && plugin.RemoteRoleOf(member.DisplayName, modKey) == trigger);
            var state = trigger == myRole ? ChipState.Mine : holder is not null ? ChipState.Theirs : ChipState.Open;
            var label = trigger == myRole ? $"{role}, you" : holder is not null ? $"{role}, {holder.DisplayName}" : role;
            var chipWidth = ImGui.CalcTextSize(label).X + 28f * s;
            if (!first)
            {
                ImGui.SameLine(0, 8f * s);
                if (ImGui.GetCursorScreenPos().X + chipWidth > right) ImGui.NewLine();
            }
            first = false;
            ImGui.PushID(trigger);
            if (Theme.Chip($"{label}##role", state) && myRole != trigger)
            {
                myRole = trigger;
                assignments.Clear();
            }
            ImGui.PopID();
        }
    }

    /// <summary>What everyone else plays: yours to change for members you direct (FREE USE, your
    /// test partner, or everyone while you choose for the room); the rest pick their own.</summary>
    private void DrawOthersRoles(RoomStateDto room, (string Directory, string Name) pick, IReadOnlyList<string> triggers)
    {
        var s = Theme.Scale;
        var others = Others(room).ToList();
        if (others.Count == 0) return;
        ImGui.Dummy(new Vector2(0, 2f * s));
        for (var index = 0; index < others.Count; index++)
        {
            var member = others[index];
            ImGui.PushID("other-" + member.ConnectionId);
            if (Directs(member))
            {
                var role = AssignedRole(member, index, triggers);
                Theme.Label($"{member.DisplayName} gets");
                ImGui.SameLine(0, 6f * s);
                if (Theme.Chip($"{plugin.RoleName(pick.Directory, role)}##assigned", ChipState.Theirs))
                    assignments[member.ConnectionId] = triggers[(IndexOf(triggers, role) + 1) % triggers.Count];
                Tooltip("Click to give them a different role.");
            }
            else
            {
                Theme.Quiet($"{member.DisplayName} picks their own role (or turns on Free Use to be readied).");
            }
            ImGui.PopID();
        }
    }

    private bool CanContinue(out string reason)
    {
        reason = "";
        if (selected is not { } pick)
        {
            reason = "Pick an animation first.";
            return false;
        }
        if (!EveryoneHas(pick.Directory))
        {
            reason = "Send it to everyone who's missing it first.";
            return false;
        }
        if (myRole is null)
        {
            reason = "Pick your role first.";
            return false;
        }
        return true;
    }

    private void GoToStepTwo()
    {
        if (!CanContinue(out _)) return;
        keepPositions = SameBase();
        step = 2;
    }

    // ---- Step 2: position and ready -----------------------------------------------------------

    private void DrawStepTwo(RoomStateDto room)
    {
        var s = Theme.Scale;
        // A role gone or the animation removed: back to step 1. Someone missing it (or the counts
        // still catching up after a join or reconnect) only holds Ready below.
        if (selected is not { } pick || myRole is not { } role || !plugin.Mods.Contains(pick))
        {
            step = 1;
            return;
        }
        var width = ImGui.GetContentRegionAvail().X;
        using (Theme.HeadingFont()) ImGui.TextUnformatted(Theme.Truncate(pick.Name, width));
        Theme.Label($"You: {plugin.RoleName(pick.Directory, role)}");
        var everyone = EveryoneHas(pick.Directory);
        if (!everyone)
        {
            ImGui.Dummy(new Vector2(0, 4f * s));
            DrawWhoHasIt(room, pick);
        }

        ImGui.Dummy(new Vector2(0, 6f * s));
        Theme.Label("Position");
        DrawPosition(plugin.BaseOf(role), SameBase(), Others(room).ToList());

        ImGui.Dummy(new Vector2(0, 8f * s));
        Theme.Label("Ready");
        var triggers = plugin.RoleTriggersOf(pick.Directory, pick.Name);
        DrawReadyList(room, pick.Directory, triggers, role, directing: true);

        ImGui.Dummy(new Vector2(0, 4f * s));
        var readied = LocallyReadyFor(room, pick.Directory);
        var assigned = AssignmentsFor(room, triggers, role);
        var snapshot = Snapshot(pick.Directory, role, assigned, plugin.IsWizardController);
        var stale = readied && (snapshot != readiedSnapshot || readiedGeneration != plugin.ReadyGeneration);
        if (!readied || stale)
        {
            // A Ready takes a moment to come back from the relay; don't send it twice meanwhile.
            var blocked = Environment.TickCount64 - readyClickedAt < 1500 || !everyone;
            if (blocked) ImGui.BeginDisabled();
            if (Theme.Primary(stale ? "Ready again" : "Ready"))
            {
                readyClickedAt = Environment.TickCount64;
                if (plugin.ReadyFromWizard(pick.Directory, role, assigned))
                {
                    // Everyone's role is fixed from here, so nobody's shifts if someone joins or leaves.
                    foreach (var (member, memberRole) in assigned) assignments[member] = memberRole;
                    readiedSnapshot = snapshot;
                    readiedGeneration = plugin.ReadyGeneration;
                }
            }
            if (blocked) ImGui.EndDisabled();
            Tooltip(!everyone
                ? "Everyone needs the animation first."
                : stale
                ? "You readied outside the wizard, or a role changed since. Ready again to send the wizard's roles."
                : plugin.IsWizardController
                    ? "Readies your role and sends everyone else theirs."
                    : "Readies your role. Members in Free Use and your test partner are readied with theirs.");
            if (readied) ImGui.SameLine(0, 6f * s);
        }
        if (readied)
        {
            if (Theme.Text("Unready")) plugin.CancelSyncReady();
            DrawStartNow(room);
        }
        ImGui.SameLine(0, 10f * s);
        if (Theme.Text("Back")) step = 1;

        ImGui.Dummy(new Vector2(0, 2f * s));
        var playing = plugin.NowPlaying;
        Theme.Wrapped(playing is not null && !readied && playing.Directory != pick.Directory
            ? $"{(playing.Animation.Length > 0 ? playing.Animation : playing.ModName)} keeps playing until everyone's ready, then it swaps."
            : "It starts for everyone together once everyone's ready.", Theme.Faint);
    }

    private void DrawStartNow(RoomStateDto room)
    {
        if (!plugin.Sync.IsRoomLeader || room.Members.Count < 2) return;
        ImGui.SameLine(0, 6f * Theme.Scale);
        if (Theme.Outline("Start now")) plugin.ForceSyncStart();
        Tooltip("As host: start everyone who's ready without waiting for the rest.");
    }

    private void DrawPosition(PoseKind nextBase, bool sameBase, IReadOnlyList<RoomMemberDto> others)
    {
        var s = Theme.Scale;
        if (sameBase)
        {
            ImGui.TextColored(Theme.Everyone, $"It uses the same {BaseName(nextBase)} as what you're doing now.");
            ImGui.Checkbox("Keep where you both are", ref keepPositions);
        }
        if (!sameBase || !keepPositions) DrawLineUp(others);
        if (!sameBase) DrawSeatReminder(nextBase);
        ImGui.Dummy(new Vector2(0, 1f * s));
    }

    /// <summary>A chair sit needs a seat and a doze a bed, unless sit and doze anywhere covers it.</summary>
    private void DrawSeatReminder(PoseKind nextBase)
    {
        if (nextBase == PoseKind.Sit)
        {
            var inPlace = plugin.SitDozeAnywhereEnabled && !plugin.DozeAnywhereOnlyEnabled && plugin.SitDozeAnywhereAvailable;
            Theme.Wrapped(inPlace
                    ? "Chair sit: sit anywhere is on, so you'll sit right where you stand."
                    : "Chair sit: move to a seat, a bed edge or a tub edge before you ready.",
                inPlace ? Theme.Faint : Theme.Some);
        }
        else if (nextBase == PoseKind.Doze && !(plugin.SitDozeAnywhereEnabled && plugin.SitDozeAnywhereAvailable))
        {
            Theme.Wrapped("Doze: lie on a bed first, or turn on sit and doze anywhere in Settings.", Theme.Some);
        }
    }

    private void DrawLineUp(IReadOnlyList<RoomMemberDto> others)
    {
        var s = Theme.Scale;
        if (others.Count == 0)
        {
            Theme.Quiet("Nobody else is in the room yet.");
            return;
        }
        var partner = others.FirstOrDefault(member => member.ConnectionId == lineUpWith) ?? others[0];
        lineUpWith = partner.ConnectionId;
        if (others.Count > 1)
        {
            Theme.Label("Line up with");
            ImGui.SameLine(0, 8f * s);
            ImGui.SetNextItemWidth(MathF.Min(220f * s, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("##wizard-line-up-with", partner.DisplayName))
            {
                foreach (var member in others)
                    if (ImGui.Selectable($"{member.DisplayName}##{member.ConnectionId}", member.ConnectionId == partner.ConnectionId))
                        lineUpWith = member.ConnectionId;
                ImGui.EndCombo();
            }
        }
        if (IsTestPartner(partner))
        {
            Theme.Quiet("Your test partner stands on your spot by itself.");
            return;
        }
        if (Theme.Text(plugin.IsAligning ? "Cancel lining up" : $"I line up to {partner.DisplayName}", Theme.AzureText))
            plugin.AlignToMember(partner.DisplayName);
        Tooltip("Walks the last couple of steps onto their spot and facing.");
        var supported = plugin.WizardRelaySupported && partner.Wizard;
        if (!supported) ImGui.BeginDisabled();
        if (Theme.Text($"{partner.DisplayName} lines up to me", Theme.AzureText)) plugin.RequestLineUpToMe(partner);
        if (!supported) ImGui.EndDisabled();
        Tooltip(supported
            ? "Asks them to walk onto your spot. Members in Free Use do it straight away."
            : !plugin.WizardRelaySupported
                ? "The relay needs updating before you can ask this."
                : $"{partner.DisplayName} is on an older Synastry and can't get this request.");
        Theme.Quiet("Stand close first: it covers the last 2 yalms, or half a yalm mid-animation.");
    }

    /// <summary>
    /// Everyone in the room, their role and whether they're ready for this animation. With
    /// <paramref name="directing"/>, roles you're about to send are shown for the members you direct;
    /// otherwise each member's own shared pick.
    /// </summary>
    private void DrawReadyList(RoomStateDto room, string directory, IReadOnlyList<string> triggers, string? myTrigger,
        bool directing)
    {
        var s = Theme.Scale;
        var draw = ImGui.GetWindowDrawList();
        var modKey = plugin.ModKeyOf(directory);
        var others = Others(room).ToList();
        foreach (var member in room.Members)
        {
            var me = plugin.Sync.IsCurrentMember(member.ConnectionId);
            string? role;
            if (me) role = myTrigger;
            else if (directing && myTrigger is not null && Directs(member))
                role = AssignedRole(member, others.IndexOf(member), triggers, myTrigger);
            else role = modKey is null ? null : plugin.RemoteRoleOf(member.DisplayName, modKey);

            var otherAnimation = member.Ready && modKey is not null && !string.IsNullOrEmpty(member.ModKey) &&
                                 !member.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase);
            var readyHere = member.Ready && !otherAnimation;
            var state = me && plugin.IsStandingUpForReady ? "standing up"
                : otherAnimation ? "ready with another animation"
                : member.Ready && member.WaitsForAssets && !member.AssetsReady ? "getting files"
                : member.Ready ? "ready"
                : "waiting";
            var start = ImGui.GetCursorScreenPos();
            var rowRight = start.X + ImGui.GetContentRegionAvail().X;
            var line = ImGui.GetTextLineHeight();
            var stateWidth = ImGui.CalcTextSize(state).X;
            Theme.Dot(draw, start + new Vector2(4f * s, line * 0.5f), me ? Theme.Rose : Theme.Azure, readyHere);
            ImGui.SetCursorScreenPos(start + new Vector2(16f * s, 0));
            var name = me ? $"{member.DisplayName} (you)" : member.DisplayName;
            if (role is not null && !otherAnimation) name += $" · {plugin.RoleName(directory, role)}";
            ImGui.TextColored(Theme.Bone, Theme.Truncate(name, MathF.Max(40f * s, rowRight - start.X - 16f * s - stateWidth - 14f * s)));
            ImGui.SameLine(0, 0);
            ImGui.SetCursorScreenPos(new Vector2(rowRight - stateWidth, start.Y));
            ImGui.TextColored(readyHere ? Theme.Everyone : otherAnimation ? Theme.Some : Theme.Faint, state);
        }
    }

    // ---- Member view: someone else is choosing ------------------------------------------------

    private void DrawMemberView(RoomStateDto room, RoomMemberDto controller)
    {
        var s = Theme.Scale;
        ImGui.Dummy(new Vector2(0, 4f * s));
        if (plugin.WizardPlan is not { } plan)
        {
            Theme.Wrapped($"Waiting for {controller.DisplayName} to pick an animation.", Theme.Soft);
            DrawAutoReadyToggle(controller, readyPlanNow: false);
            ImGui.Dummy(new Vector2(0, 6f * s));
            Theme.Label("Room");
            DrawReadyList(room, "", [], null, directing: false);
            return;
        }

        var directory = plugin.DirectoryForFingerprint(plan.Fingerprint);
        using (Theme.HeadingFont()) ImGui.TextUnformatted(Theme.Truncate(plan.ModName, ImGui.GetContentRegionAvail().X));
        var roleName = directory is null ? plan.Trigger : plugin.RoleName(directory, plan.Trigger);
        Theme.Label($"{controller.DisplayName} picked this. Your role:");
        ImGui.SameLine(0, 6f * s);
        Theme.Chip($"{roleName}##plan-role", ChipState.Mine);
        if (directory is null)
        {
            Theme.Wrapped("You don't have this animation yet. Ask them to send it from the wizard.", Theme.Some);
            return;
        }

        ImGui.Dummy(new Vector2(0, 6f * s));
        Theme.Label("Position");
        var nextBase = plugin.BaseOf(plan.Trigger);
        if (SameBase(plan.Trigger))
        {
            ImGui.TextColored(Theme.Everyone, $"Same {BaseName(nextBase)} as now: stay where you are.");
        }
        else
        {
            DrawLineUp([controller]);
            DrawSeatReminder(nextBase);
        }

        ImGui.Dummy(new Vector2(0, 8f * s));
        Theme.Label("Ready");
        var triggers = plugin.RoleTriggersOf(directory, plan.ModName);
        DrawReadyList(room, directory, triggers, plan.Trigger, directing: false);
        ImGui.Dummy(new Vector2(0, 4f * s));
        var readied = LocallyReadyFor(room, directory) && plugin.WizardReadiedTrigger is { } readiedTrigger &&
                      readiedTrigger.Trigger == plan.Trigger;
        if (readied)
        {
            if (Theme.Text("Unready")) plugin.CancelSyncReady();
            DrawStartNow(room);
        }
        else if (plugin.IsFreeUseEnabled && plugin.PreparedModKey is { } prepared &&
                 prepared.Equals(plugin.ModKeyOf(directory), StringComparison.OrdinalIgnoreCase))
        {
            // Free Use just readied this animation; the relay's Ready is on its way.
            Theme.Quiet("Free Use is on: you're readied automatically.");
        }
        else
        {
            var waiting = Environment.TickCount64 - readyClickedAt < 1500;
            if (waiting) ImGui.BeginDisabled();
            if (Theme.Primary("Ready"))
            {
                readyClickedAt = Environment.TickCount64;
                plugin.ReadyWizardPlan();
            }
            if (waiting) ImGui.EndDisabled();
        }
        ImGui.SameLine(0, 10f * s);
        if (Theme.Text("Ask to swap role", Theme.AzureText)) plugin.AskToSwapRole();
        Tooltip($"Asks {controller.DisplayName} to give you a different role.");
        DrawAutoReadyToggle(controller, readyPlanNow: !readied);
    }

    private void DrawAutoReadyToggle(RoomMemberDto controller, bool readyPlanNow)
    {
        var freeUse = plugin.IsFreeUseEnabled;
        if (Theme.Toggle("##wizard-free-use", "Ready me automatically (Free Use)", ref freeUse))
        {
            plugin.SetFreeUse(freeUse);
            // Free Use readies you for the next pick; ready the one already waiting for you now.
            if (freeUse && readyPlanNow) plugin.ReadyWizardPlan();
        }
        Tooltip($"{controller.DisplayName} (and anyone else in the room) can then pick your role and ready you.");
    }

    // ---- Swapping roles (controller) ----------------------------------------------------------

    /// <summary>A swap applies to the animation you're on; while you're readied with another one,
    /// go back to it first.</summary>
    private bool CanSwapNow(RoomStateDto room, out string reason)
    {
        reason = "";
        if (selected is not { } pick || myRole is null)
        {
            reason = "Pick the animation and your role first.";
            return false;
        }
        if (plugin.WizardReadiedTrigger is { } readied &&
            !readied.Directory.Equals(pick.Directory, StringComparison.OrdinalIgnoreCase) &&
            LocallyReadyFor(room, readied.Directory))
        {
            reason = "You're readied with another animation. Go back to it to swap.";
            return false;
        }
        if (plugin.RoleTriggersOf(pick.Directory, pick.Name).Count < 2)
        {
            reason = "This animation has only one role.";
            return false;
        }
        return true;
    }

    private void Swap(RoomStateDto room, WizardRequestDto request)
    {
        if (!CanSwapNow(room, out _) || selected is not { } pick || myRole is null) return;
        var triggers = plugin.RoleTriggersOf(pick.Directory, pick.Name);
        var wasCurrent = LocallyReadyFor(room, pick.Directory) && readiedGeneration == plugin.ReadyGeneration &&
                         readiedSnapshot == Snapshot(pick.Directory, myRole, AssignmentsFor(room, triggers, myRole),
                             plugin.IsWizardController);
        var others = Others(room).ToList();
        var requester = others.FirstOrDefault(member => member.ConnectionId == request.FromConnectionId);
        plugin.DismissSwapRequest(request);
        if (requester is null) return;

        // Fix everyone's current role first, so only the two involved change.
        for (var index = 0; index < others.Count; index++)
            assignments[others[index].ConnectionId] = AssignedRole(others[index], index, triggers);
        var theirs = assignments[requester.ConnectionId];
        var next = triggers[(IndexOf(triggers, theirs) + 1) % triggers.Count];
        var changed = new List<string> { requester.ConnectionId };
        var myRoleChanged = false;
        if (next == myRole)
        {
            myRole = theirs;
            myRoleChanged = true;
        }
        else if (others.FirstOrDefault(member => member != requester && assignments[member.ConnectionId] == next) is { } holder)
        {
            assignments[holder.ConnectionId] = theirs;
            changed.Add(holder.ConnectionId);
        }
        assignments[requester.ConnectionId] = next;

        // Readied already: only you (if your role changed) and the members whose roles changed are
        // readied again or re-sent. Not readied yet: they still hear their new role now.
        var roles = AssignmentsFor(room, triggers, myRole);
        if (LocallyReadyFor(room, pick.Directory))
        {
            var sent = true;
            if (myRoleChanged || !wasCurrent)
                sent = plugin.ReadyFromWizard(pick.Directory, myRole, roles, wasCurrent ? changed : null);
            else
                plugin.RedirectFromWizard(pick.Directory, myRole, roles, changed, plansOnly: false);
            // Only what actually went out counts as sent, so a failed send still offers "Ready again".
            if (sent)
            {
                readiedSnapshot = Snapshot(pick.Directory, myRole, roles, plugin.IsWizardController);
                readiedGeneration = plugin.ReadyGeneration;
            }
        }
        else
        {
            plugin.RedirectFromWizard(pick.Directory, myRole, roles, changed, plansOnly: true);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private IEnumerable<RoomMemberDto> Others(RoomStateDto room) =>
        room.Members.Where(member => !plugin.Sync.IsCurrentMember(member.ConnectionId));

    /// <summary>Members whose role you send: everyone while you choose for the room, else members in
    /// Free Use and your test partner.</summary>
    private bool Directs(RoomMemberDto member) =>
        member.FreeUse || IsTestPartner(member) || (plugin.IsWizardController && member.Wizard);

    private IEnumerable<(RoomMemberDto Member, string Role)> DirectedMembers(RoomStateDto room)
    {
        if (selected is not { } pick || myRole is null) yield break;
        var triggers = plugin.RoleTriggersOf(pick.Directory, pick.Name);
        var others = Others(room).ToList();
        for (var index = 0; index < others.Count; index++)
            if (Directs(others[index]))
                yield return (others[index], AssignedRole(others[index], index, triggers));
    }

    private string AssignedRole(RoomMemberDto member, int index, IReadOnlyList<string> triggers, string? ownRole = null)
    {
        if (assignments.TryGetValue(member.ConnectionId, out var assigned) && triggers.Contains(assigned)) return assigned;
        return plugin.DefaultRoleFor(triggers, ownRole ?? myRole ?? "", Math.Max(0, index));
    }

    private Dictionary<string, string> AssignmentsFor(RoomStateDto room, IReadOnlyList<string> triggers, string ownRole)
    {
        var others = Others(room).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < others.Count; index++)
            result[others[index].ConnectionId] = AssignedRole(others[index], index, triggers, ownRole);
        return result;
    }

    private static string Snapshot(string directory, string role, IReadOnlyDictionary<string, string> assigned,
        bool controller) =>
        (controller ? "controller\n" : "") + directory + "\n" + role + "\n" + string.Join("\n",
            assigned.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    private bool EveryoneHas(string directory)
    {
        var (matches, members) = plugin.GetModMatch(directory);
        return members <= 1 || matches >= members;
    }

    private bool LocallyReadyFor(RoomStateDto room, string directory)
    {
        var modKey = plugin.ModKeyOf(directory);
        if (modKey is null || !modKey.Equals(plugin.PreparedModKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (plugin.IsStandingUpForReady) return true;
        return room.Members.Any(member => plugin.Sync.IsCurrentMember(member.ConnectionId) && member.Ready);
    }

    /// <summary>The next animation starts from the seat you're in, or from the standing animation
    /// you're playing, so nobody needs to move.</summary>
    private bool SameBase() => myRole is { } role && SameBase(role);

    private bool SameBase(string trigger)
    {
        var next = plugin.BaseOf(trigger);
        var current = plugin.CurrentPoseKind;
        if (current is PoseKind.Sit or PoseKind.GroundSit or PoseKind.Doze) return current == next;
        if (next != PoseKind.Idle) return false;
        // A looping standing emote (null) or a standing idle pose (Idle) that has really started, not
        // one that's only readied or was left over.
        return current is null ? plugin.NowPlaying is not null : plugin.HasStartedAnimation;
    }

    private bool IsTestPartner(RoomMemberDto member) =>
        plugin.TestPartnerName is { } name && member.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<string> triggers, string trigger)
    {
        for (var index = 0; index < triggers.Count; index++)
            if (triggers[index] == trigger) return index;
        return -1;
    }

    private static string BaseName(PoseKind kind) => kind switch
    {
        PoseKind.Sit => "chair sit",
        PoseKind.GroundSit => "ground sit",
        PoseKind.Doze => "doze",
        _ => "standing pose"
    };

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };

    private void DrawStatus()
    {
        var status = plugin.Status;
        if (status.Length == 0 || status == "Ready.") return;
        ImGui.Dummy(new Vector2(0, 6f * Theme.Scale));
        Theme.Wrapped(status, Theme.Faint);
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }
}

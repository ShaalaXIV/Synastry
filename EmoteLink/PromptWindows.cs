using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

/// <summary>
/// A prompt that shows by itself, whether or not Synastry is open. It never takes keyboard focus, so
/// it can't interrupt someone typing in chat, and it stays wherever it was last dragged.
/// </summary>
/// <remarks>The window flags are from PoseKit's chip window. Adapted from PoseKit by RaylaPetal
/// (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
public abstract class PromptWindow : Window
{
    protected readonly Plugin plugin;
    /// <summary>Where its top centre goes the first time, as a fraction of the game view.</summary>
    private readonly Vector2 anchor;

    protected PromptWindow(Plugin plugin, string name, Vector2 anchor, ImGuiWindowFlags flags)
        : base(name, flags | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin = plugin;
        this.anchor = anchor;
        // Escape belongs to the game here: cancelling an emote or a target must not dismiss a prompt.
        RespectCloseHotkey = false;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    /// <summary>Collects what's new and drops what no longer applies; true while there is something to show.</summary>
    protected abstract bool HasPrompt();

    protected abstract void DrawPrompt();

    // Dalamud runs this every frame, also while the window is closed.
    public override void PreOpenCheck() => IsOpen = HasPrompt();

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 20f) * Theme.Scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.HairlineStrong);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * anchor, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        Theme.Pop();
    }

    public sealed override void Draw()
    {
        // A window that comes back without focus keeps its old place in the stack, which can be
        // behind the main window.
        if (ImGui.IsWindowAppearing()) ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
        DrawPrompt();
    }

    /// <summary>The line and gap above a prompt's closing button.</summary>
    protected static void DrawFooterLine()
    {
        Theme.HorizontalLine(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), ImGui.GetContentRegionAvail().X);
        ImGui.Dummy(new Vector2(0, 10f * Theme.Scale));
    }
}

/// <summary>Animations offered to this player. Closing it means later: the offers stay, and the room
/// panel's Review brings them back.</summary>
public sealed class TransferOffersWindow : PromptWindow
{
    private readonly List<ModTransferOfferDto> pending = [];
    private string? lastRoom;

    public TransferOffersWindow(Plugin plugin)
        : base(plugin, "Offered to you###SynastryOffers", new Vector2(0.76f, 0.08f), ImGuiWindowFlags.None)
    {
        Size = new Vector2(440, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public int Count => pending.Count;

    public void Review()
    {
        IsOpen = true;
        BringToFront();
    }

    // Opens for a new offer but otherwise stays as the player left it, so it can be closed for later.
    public override void PreOpenCheck()
    {
        var room = plugin.Sync.Room?.RoomCode;
        if (lastRoom is not null && room != lastRoom)
        {
            // The relay voids a member's offers when they leave, also when they go straight to another
            // room. Only on leaving: the first offer can arrive just before the room itself does.
            pending.Clear();
            while (plugin.TryTakeTransferOffer(out _)) { }
        }
        lastRoom = room;
        if (Collect()) IsOpen = true;
        if (!HasPrompt()) IsOpen = false;
    }

    protected override bool HasPrompt() => pending.Count > 0;

    private bool Collect()
    {
        var now = DateTimeOffset.UtcNow;
        pending.RemoveAll(offer => offer.ExpiresAt <= now);
        var received = false;
        while (plugin.TryTakeTransferOffer(out var offer))
        {
            // Offers wait here while the game hides Synastry, so one can have expired already.
            if (offer.ExpiresAt <= now ||
                pending.Any(existing => existing.TransferId == offer.TransferId)) continue;
            pending.Add(offer);
            received = true;
        }
        return received;
    }

    protected override void DrawPrompt()
    {
        var s = Theme.Scale;
        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < pending.Count; index++)
        {
            var offer = pending[index];
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
                pending.RemoveAt(index--);
            }
            else
            {
                ImGui.SameLine();
                if (Theme.Text("Decline"))
                {
                    plugin.DeclineModTransfer(offer);
                    pending.RemoveAt(index--);
                }
            }
            ImGui.Dummy(new Vector2(0, 4f * s));
            Theme.HorizontalLine(draw, ImGui.GetCursorScreenPos(), ImGui.GetContentRegionAvail().X);
            ImGui.Dummy(new Vector2(0, 8f * s));
            ImGui.PopID();
        }
    }
}

/// <summary>An animation someone picked for the room, with its roles to get ready as.</summary>
public sealed class SuggestionWindow : PromptWindow
{
    private readonly MainWindow mainWindow;
    private readonly Queue<AnimationSuggestion> queued = [];
    private AnimationSuggestion? active;

    public SuggestionWindow(Plugin plugin, MainWindow mainWindow)
        : base(plugin, "Picked for the room###SynastrySuggestion", new Vector2(0.5f, 0.14f), ImGuiWindowFlags.NoTitleBar)
    {
        this.mainWindow = mainWindow;
        Size = new Vector2(560, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Show(AnimationSuggestion suggestion)
    {
        if (active is null || SameSuggestion(active, suggestion))
            active = suggestion;
        else if (!queued.Any(waiting => SameSuggestion(waiting, suggestion)))
            queued.Enqueue(suggestion);
    }

    protected override bool HasPrompt()
    {
        while (active is null || !plugin.IsAnimationSuggestionActive(active))
        {
            active = null;
            if (!queued.TryDequeue(out var next)) return false;
            active = next;
        }
        return true;
    }

    protected override void DrawPrompt()
    {
        if (active is not { } suggestion) return;
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
            var theirs = MainWindow.SuggestionAnimationName(suggestion.ActivatedTrigger, detectedPoses, detectedEmotes);
            Theme.Wrapped(theirs.Length > 0
                ? $"{suggestion.SuggestedBy} is playing {theirs}. Pick your role to get ready."
                : "Pick your role to get ready.", Theme.Soft);
            ImGui.Dummy(new Vector2(0, 4f * Theme.Scale));
            animationActivated = mainWindow.DrawModDetails(localMod, suggestionMode: true);
            Theme.Quiet("Option changes here only affect your copy.");
        }
        ImGui.EndChild();

        DrawFooterLine();
        if (animationActivated)
        {
            active = null;
        }
        else if (Theme.Text("Not now"))
        {
            plugin.IgnoreAnimationSuggestion(suggestion);
            active = null;
        }
    }

    private static bool SameSuggestion(AnimationSuggestion left, AnimationSuggestion right) =>
        left.SuggestedBy.Equals(right.SuggestedBy, StringComparison.OrdinalIgnoreCase) &&
        left.ModKey.Equals(right.ModKey, StringComparison.OrdinalIgnoreCase);
}

/// <summary>After picking your own role: which role a member in Free Use Mode plays.</summary>
public sealed class FreeUseWindow : PromptWindow
{
    private readonly List<FreeUsePrompt> queued = [];
    private FreeUsePrompt? active;
    private Dictionary<string, List<string>> options = new(StringComparer.OrdinalIgnoreCase);

    public FreeUseWindow(Plugin plugin)
        : base(plugin, "Free Use Mode###SynastryFreeUsePrompt", new Vector2(0.5f, 0.14f), ImGuiWindowFlags.NoTitleBar)
    {
        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Show(FreeUsePrompt prompt)
    {
        // A newer choice for the same member replaces the older one.
        queued.RemoveAll(waiting => waiting.MemberConnectionId == prompt.MemberConnectionId);
        if (active is null || active.MemberConnectionId == prompt.MemberConnectionId) Begin(prompt);
        else queued.Add(prompt);
    }

    private void Begin(FreeUsePrompt prompt)
    {
        active = prompt;
        options = plugin.GetFreeUseOptionTemplate(prompt.Directory);
    }

    protected override bool HasPrompt()
    {
        if (active is not null && !plugin.IsFreeUseMemberAvailable(active.MemberConnectionId)) active = null;
        while (active is null && queued.Count > 0)
        {
            var next = queued[0];
            queued.RemoveAt(0);
            if (plugin.IsFreeUseMemberAvailable(next.MemberConnectionId)) Begin(next);
        }
        return active is not null;
    }

    protected override void DrawPrompt()
    {
        if (active is not { } prompt) return;
        ImGui.TextColored(Theme.AzureText, $"{prompt.MemberName} is in Free Use Mode");
        Theme.Title(prompt.ModName);
        var poses = plugin.GetDetectedPoses(prompt.Directory);
        var emotes = plugin.GetDetectedEmotes(prompt.Directory);
        var ownRole = MainWindow.SuggestionAnimationName(prompt.OwnTrigger, poses, emotes);
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
            if (RoleChip(prompt, "$detected-pose", $"{pose.Kind}:{pose.Index}", MainWindow.PoseDisplayName(pose),
                    $"pose:{pose.Kind}:{pose.Index}", Place))
                chosenTrigger = $"pose:{pose.Kind}:{pose.Index}";
        foreach (var emote in emotes)
            if (RoleChip(prompt, "$detected-emote", emote.Id.ToString(), emote.Name, $"emote:{emote.Id}", Place))
                chosenTrigger = $"emote:{emote.Id}";
        if (poses.Count == 0 && emotes.Count == 0)
            Theme.Quiet("This animation has no roles to pick.");
        DrawOptions(prompt.Directory);
        ImGui.EndChild();

        DrawFooterLine();
        if (chosenTrigger is not null)
        {
            plugin.DirectFreeUse(prompt, chosenTrigger, options);
            active = null;
        }
        else if (Theme.Text("Skip"))
        {
            active = null;
        }
    }

    private bool RoleChip(
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

    private void DrawOptions(string directory)
    {
        var groups = plugin.GetOptionGroups(directory);
        if (groups.Count == 0) return;
        var s = Theme.Scale;
        ImGui.Dummy(new Vector2(0, 6f * s));
        Theme.Label("Their options, copied from yours. Changes here are for them only.");
        foreach (var group in groups)
        {
            ImGui.PushID("free-use-" + group.Name);
            var selected = options.GetValueOrDefault(group.Name) ?? [];
            if (!group.IsMultiSelect)
            {
                var current = group.Options.FirstOrDefault(option => selected.Contains(option, StringComparer.OrdinalIgnoreCase));
                ImGui.SetNextItemWidth(MathF.Min(260f * s, ImGui.GetContentRegionAvail().X * 0.6f));
                if (ImGui.BeginCombo(group.Name, current ?? "Mod default"))
                {
                    foreach (var option in group.Options)
                        if (ImGui.Selectable(option, option.Equals(current, StringComparison.OrdinalIgnoreCase)))
                            options[group.Name] = [option];
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
                    options[group.Name] = selected;
                }
            }
            ImGui.PopID();
        }
    }
}

/// <summary>A room invitation received by tell.</summary>
public sealed class RoomInviteWindow : PromptWindow
{
    private readonly List<RoomInvite> pending = [];

    public RoomInviteWindow(Plugin plugin)
        : base(plugin, "Room invite###SynastryInvite", new Vector2(0.5f, 0.08f),
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize)
    {
    }

    protected override bool HasPrompt()
    {
        // One prompt per room, however often the invitation was sent, and none for the room you're in.
        while (plugin.TryTakeRoomInvite(out var invite))
            if (pending.All(waiting => waiting.RoomCode != invite.RoomCode)) pending.Add(invite);
        var current = plugin.Sync.Room?.RoomCode;
        pending.RemoveAll(invite => invite.RoomCode.Equals(current, StringComparison.OrdinalIgnoreCase));
        return pending.Count > 0;
    }

    protected override void DrawPrompt()
    {
        if (pending.Count == 0) return;
        var invite = pending[0];
        ImGui.TextColored(Theme.AzureText, $"{invite.SenderName} invited you to their room");
        using (Theme.CodeFont()) ImGui.TextUnformatted(invite.RoomCode);
        ImGui.Dummy(new Vector2(0, 6f * Theme.Scale));
        if (Theme.Primary("Join", 140f * Theme.Scale))
        {
            plugin.AcceptRoomInvite(invite);
            pending.Remove(invite);
        }
        ImGui.SameLine();
        if (Theme.Text("Decline"))
        {
            plugin.DeclineRoomInvite(invite);
            pending.Remove(invite);
        }
    }
}

/// <summary>The room's controller picked an animation and a role for you, shown while the Synastry
/// Wizard is closed. The wizard itself still opens only from a click.</summary>
public sealed class WizardPickWindow : PromptWindow
{
    private FreeUseDirectiveDto? answered;
    private FreeUseDirectiveDto? shown;
    private string feedback = "";

    public WizardPickWindow(Plugin plugin)
        : base(plugin, "Picked for you###SynastryWizardPick", new Vector2(0.24f, 0.08f),
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize)
    {
    }

    protected override bool HasPrompt()
    {
        if (plugin.WizardPlan is not { } plan) return false;
        // Readying it from anywhere answers it: the countdown clears that Ready before the pick is done.
        if (IsReadied(plan)) answered = plan;
        // In Free Use Mode the pick readies you by itself.
        return !plugin.IsWizardOpen && !plugin.IsFreeUseEnabled && !ReferenceEquals(plan, answered) &&
               !plugin.IsRoundStarting(plan.ModKey);
    }

    private bool IsReadied(FreeUseDirectiveDto plan) =>
        plugin.WizardReadiedTrigger is { } readied &&
        readied.Trigger.Equals(plan.Trigger, StringComparison.OrdinalIgnoreCase) &&
        readied.Directory.Equals(plugin.DirectoryForFingerprint(plan.Fingerprint), StringComparison.OrdinalIgnoreCase);

    protected override void DrawPrompt()
    {
        if (plugin.WizardPlan is not { } plan) return;
        if (!ReferenceEquals(plan, shown))
        {
            shown = plan;
            feedback = "";
        }
        var s = Theme.Scale;
        var width = 380f * s;
        ImGui.TextColored(Theme.AzureText, $"{plan.DirectedBy} picked");
        using (Theme.TitleFont()) ImGui.TextUnformatted(Theme.Truncate(plan.ModName, width));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        var directory = plugin.DirectoryForFingerprint(plan.Fingerprint);
        if (directory is not null) plugin.EnsureDetectedEmotes(directory, plan.ModName);
        Theme.Wrapped(directory is null
            ? "You don't have this animation yet. Ask them to send it."
            : $"Your role is {plugin.RoleName(directory, plan.Trigger)}.", Theme.Soft);
        if (feedback.Length > 0) Theme.Wrapped(feedback, Theme.AzureText);
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, 6f * s));
        if (directory is not null)
        {
            if (Theme.Primary("Ready", 110f * s))
            {
                // Answered at once: the room's own Ready takes a moment to come back.
                if (plugin.ReadyWizardPlan()) answered = plan;
                else feedback = plugin.Status;
            }
            ImGui.SameLine();
            if (Theme.Text("Ask to swap role", Theme.AzureText))
            {
                plugin.AskToSwapRole();
                feedback = $"Asked {plan.DirectedBy} to swap your role.";
            }
            ImGui.SameLine(0, 2f * s);
        }
        if (Theme.Text("Open wizard")) plugin.OpenWizard();
        ImGui.SameLine(0, 2f * s);
        if (Theme.Text("Not now")) answered = plan;
    }
}

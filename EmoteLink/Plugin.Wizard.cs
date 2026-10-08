using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace EmoteLink;

/// <summary>
/// The Synastry Wizard's side of the plugin: readying a pick from the wizard (and directing the rest
/// of the room), the controller who chooses for the room, members' requests to swap roles or line
/// up, and naming who is missing an animation.
/// </summary>
public sealed unsafe partial class Plugin
{
    private readonly ConcurrentQueue<FreeUseDirectiveDto> incomingWizardPlans = new();
    private readonly ConcurrentQueue<WizardRequestDto> incomingWizardRequests = new();
    private readonly List<WizardRequestDto> swapRequests = [];
    private readonly ConcurrentDictionary<string, (long At, IReadOnlyList<string> Ids)> missingMembers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> missingLookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> sendingMods = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> recentlySentMods = new(StringComparer.OrdinalIgnoreCase);
    private WizardWindow wizardWindow = null!;
    /// <summary>The role you last shared with the room for an animation (set whenever you pick one).</summary>
    private (string Directory, string Trigger)? lastSharedRole;
    private bool roleJustShared;

    /// <summary>The controller's latest pick for you, until it stops choosing, leaves, or the round starts.</summary>
    public FreeUseDirectiveDto? WizardPlan { get; private set; }
    /// <summary>Members asking you, the controller, for a different role.</summary>
    public IReadOnlyList<WizardRequestDto> WizardSwapRequests => swapRequests;
    /// <summary>A member asking you to line up to them.</summary>
    public WizardRequestDto? WizardLineUpRequest { get; private set; }

    public bool WizardRelaySupported => sync.RelaySupportsWizard;
    /// <summary>Your test partner's name in the room, while it's out.</summary>
    public string? TestPartnerName => testPartner.Active ? testPartner.Name : null;
    public void OpenWizard() => wizardWindow.Open();

    // ---- The room's controller ----------------------------------------------------------------

    public RoomMemberDto? WizardController => sync.Room?.Members.FirstOrDefault(member => member.Controller);
    public bool IsWizardController => WizardController is { } controller && sync.IsCurrentMember(controller.ConnectionId);

    /// <summary>Choose for the room (the host can also take over from someone else), or stop.</summary>
    public void SetWizardController(bool enabled)
    {
        if (!enabled) swapRequests.Clear();
        RunSync(sync.SetControllerAsync(enabled),
            enabled ? "You're choosing for the room." : "You stopped choosing for the room.");
    }

    public void AskToSwapRole()
    {
        if (WizardController is not { } controller || sync.IsCurrentMember(controller.ConnectionId)) return;
        RunSync(sync.SendWizardRequestAsync(controller.ConnectionId, "swap"),
            $"Asked {controller.DisplayName} to swap your role.");
    }

    public void DismissSwapRequest(WizardRequestDto request) =>
        swapRequests.RemoveAll(existing => existing.FromConnectionId == request.FromConnectionId);

    // ---- Lining up ----------------------------------------------------------------------------

    public void RequestLineUpToMe(RoomMemberDto member) =>
        RunSync(sync.SendWizardRequestAsync(member.ConnectionId, "lineup"),
            $"Asked {member.DisplayName} to line up to you.");

    /// <summary>
    /// Walks onto a room member's spot and facing (within 2 yalms; half a yalm mid-animation). While
    /// already lining up, this cancels instead.
    /// </summary>
    public void AlignToMember(string displayName)
    {
        if (IsAligning)
        {
            AlignTo(null);
            return;
        }
        IGameObject? target = testPartner.Active && displayName.Equals(testPartner.Name, StringComparison.OrdinalIgnoreCase)
            ? TestPartnerCharacter()
            : Objects.OfType<IPlayerCharacter>().FirstOrDefault(player =>
                player.Address != Objects.LocalPlayer?.Address &&
                player.Name.TextValue.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            Status = $"{displayName} isn't nearby.";
            return;
        }
        AlignTo(target);
    }

    public void LineUpToRequester()
    {
        if (WizardLineUpRequest is not { } request || IsAligning) return;
        WizardLineUpRequest = null;
        AlignToMember(request.FromName);
    }

    public void DismissLineUpRequest() => WizardLineUpRequest = null;

    // ---- What an animation is ----------------------------------------------------------------

    /// <summary>Your live base: standing, ground sit, chair sit or doze; null mid-transition or in a
    /// looping standing emote.</summary>
    public PoseKind? CurrentPoseKind => poses.CurrentKind();

    /// <summary>The roles of an animation as triggers: its poses ("pose:Kind:Index"), then its emotes
    /// ("emote:Id").</summary>
    public IReadOnlyList<string> RoleTriggersOf(string directory, string name)
    {
        EnsureDetectedEmotes(directory, name);
        return GetDetectedPoses(directory).Select(pose => $"pose:{pose.Kind}:{pose.Index}")
            .Concat(GetDetectedEmotes(directory).Select(emote => $"emote:{emote.Id}"))
            .ToList();
    }

    /// <summary>A role's name: its tag (yours, the room's or the community's), else the animation.</summary>
    public string RoleName(string directory, string trigger)
    {
        if (TryParsePoseTrigger(trigger, out var pose))
        {
            var note = GetOptionNote(directory, "$detected-pose", $"{pose.Kind}:{pose.Index}");
            return note.Length > 0 ? note : MainWindow.PoseDisplayName(pose);
        }
        if (trigger.StartsWith("emote:", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(trigger["emote:".Length..], out var emoteId))
        {
            var note = GetOptionNote(directory, "$detected-emote", emoteId.ToString());
            if (note.Length > 0) return note;
            return GetDetectedEmotes(directory).FirstOrDefault(emote => emote.Id == emoteId)?.Name ??
                   (emotePlaybackById.TryGetValue(emoteId, out var info) ? info.Command : trigger);
        }
        return trigger;
    }

    /// <summary>
    /// The base a role plays from: a pose's own kind; for an emote, its seat (ground sit, chair sit
    /// or doze) when it is one, else standing.
    /// </summary>
    public PoseKind BaseOf(string trigger)
    {
        if (TryParsePoseTrigger(trigger, out var pose)) return pose.Kind;
        if (trigger.StartsWith("emote:", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(trigger["emote:".Length..], out var emoteId) &&
            DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRowOrDefault(emoteId) is { } emote)
            return emote.EmoteMode.RowId switch
            {
                1 => PoseKind.GroundSit,
                2 => PoseKind.Sit,
                3 => PoseKind.Doze,
                _ => PoseKind.Idle
            };
        return PoseKind.Idle;
    }

    private static bool TryParsePoseTrigger(string trigger, out PoseTarget pose)
    {
        pose = null!;
        if (!trigger.StartsWith("pose:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = trigger["pose:".Length..].Split(':', 2);
        if (parts.Length != 2 || !Enum.TryParse<PoseKind>(parts[0], true, out var kind) ||
            !byte.TryParse(parts[1], out var index)) return false;
        pose = new PoseTarget(kind, index);
        return true;
    }

    /// <summary>The role someone else gets by default: the first role that isn't yours, cycling
    /// through the others for a bigger room.</summary>
    public string DefaultRoleFor(IReadOnlyList<string> triggers, string ownTrigger, int otherIndex)
    {
        var others = triggers.Where(trigger => !trigger.Equals(ownTrigger, StringComparison.OrdinalIgnoreCase)).ToList();
        return others.Count == 0 ? ownTrigger : others[otherIndex % others.Count];
    }

    public string? ModKeyOf(string directory) => modSyncKeys.GetValueOrDefault(directory);

    /// <summary>The role a member last picked in an animation (their "$detected-trigger"), if shared.</summary>
    public string? RemoteRoleOf(string memberName, string modKey) =>
        remoteOptionSelections.TryGetValue($"{memberName}\n{modKey}\n$detected-trigger", out var selection)
            ? selection.Option
            : null;

    /// <summary>What you're readied with in the room, and whether that Ready waits on standing up.</summary>
    public string? PreparedModKey => preparedModKey;
    public bool IsStandingUpForReady => readyAfterStandUp;

    /// <summary>The role you're readied with, while that Ready stands (still Ready on the relay, or
    /// standing up to ready). The relay clears everyone's Ready when someone joins or leaves.</summary>
    public (string Directory, string Trigger)? WizardReadiedTrigger =>
        lastSharedRole is { } shared && preparedModKey is not null &&
        preparedModKey.Equals(ModKeyOf(shared.Directory), StringComparison.OrdinalIgnoreCase) &&
        (readyAfterStandUp ||
         sync.Room?.Members.Any(member => sync.IsCurrentMember(member.ConnectionId) && member.Ready) == true)
            ? shared
            : null;

    /// <summary>Counts every Ready you make, from anywhere, so the wizard can tell whether the Ready
    /// standing now is the one it sent.</summary>
    public int ReadyGeneration { get; private set; }

    /// <summary>An animation Synastry activated has really started (not only been readied), and you
    /// haven't moved since (moving clears NowPlaying).</summary>
    public bool HasStartedAnimation => NowPlaying is not null && playingStarted;

    public bool IsSendingMod(string directory) => sendingMods.ContainsKey(directory);

    /// <summary>Sent in the last two minutes: the others may still be installing it.</summary>
    public bool WasSentRecently(string directory) =>
        recentlySentMods.TryGetValue(directory, out var at) && Environment.TickCount64 - at < 120_000;

    /// <summary>
    /// The other members who don't have an animation, by name; null while unknown (counts not in
    /// yet, or a relay that can't say). Two of you: it's simply the other one.
    /// </summary>
    public IReadOnlyList<string>? MissingMemberNames(string directory)
    {
        if (sync.Room is not { } room || !modCatalogKeys.TryGetValue(directory, out var fingerprint)) return null;
        var (matches, members) = GetModMatch(directory);
        if (members > 1 && matches >= members) return [];
        var others = room.Members.Where(member => !sync.IsCurrentMember(member.ConnectionId)).ToList();
        if (others.Count == 1 && matches > 0) return [others[0].DisplayName];

        var now = Environment.TickCount64;
        if ((!missingMembers.TryGetValue(fingerprint, out var known) || now - known.At > 4000) &&
            missingLookups.TryAdd(fingerprint, 0))
            _ = sync.GetMissingMembersAsync(fingerprint).ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully && task.Result is { } ids)
                    missingMembers[fingerprint] = (Environment.TickCount64, ids);
                missingLookups.TryRemove(fingerprint, out _);
            }, TaskScheduler.Default);
        if (!missingMembers.TryGetValue(fingerprint, out known)) return null;
        return others.Where(member => known.Ids.Contains(member.ConnectionId)).Select(member => member.DisplayName).ToList();
    }

    // ---- Readying from the wizard -------------------------------------------------------------

    /// <summary>
    /// Readies your role in the room and directs everyone else: FREE USE members (and your test
    /// partner) get their role and are readied; as the controller, everyone else gets your pick for
    /// them to ready or swap. Others see your pick as usual. <paramref name="assignments"/> maps a
    /// member's connection id to their role; anyone missing gets the first role that isn't yours.
    /// <paramref name="only"/> limits who is directed (after a swap, just the members it changed).
    /// </summary>
    public bool ReadyFromWizard(string directory, string trigger, IReadOnlyDictionary<string, string> assignments,
        IReadOnlyCollection<string>? only = null)
    {
        if (!modsByDirectory.TryGetValue(directory, out var mod))
        {
            Status = "That animation isn't in your library any more.";
            return false;
        }
        if (!TryResolveFreeUseTrigger(directory, trigger, out var pose, out var command, out _))
        {
            Status = $"{mod.Name} doesn't have that role any more.";
            return false;
        }
        var previousRole = lastSharedRole;
        PublishDetectedTriggerSelection(directory, trigger);
        if (!ActivateInternal(directory, mod.Name, pose, requestedCommand: command))
        {
            lastSharedRole = previousRole;   // the earlier Ready (if any) still stands with its old role
            return false;
        }
        DirectRoomFromWizard(directory, mod.Name, trigger, assignments, only, plansOnly: false);
        return true;
    }

    /// <summary>
    /// Sends changed roles without readying yourself again: FREE USE members and your test partner are
    /// readied with theirs; as the controller, the others get your new pick. With
    /// <paramref name="plansOnly"/>, only those picks go out (you aren't readied yet, so nobody is
    /// readied for you).
    /// </summary>
    public void RedirectFromWizard(string directory, string ownTrigger, IReadOnlyDictionary<string, string> assignments,
        IReadOnlyCollection<string> only, bool plansOnly)
    {
        if (modsByDirectory.TryGetValue(directory, out var mod))
            DirectRoomFromWizard(directory, mod.Name, ownTrigger, assignments, only, plansOnly);
    }

    private void DirectRoomFromWizard(string directory, string modName, string ownTrigger,
        IReadOnlyDictionary<string, string> assignments, IReadOnlyCollection<string>? only, bool plansOnly)
    {
        if (sync.Room is not { } room || IsModPrivate(directory) ||
            !modCatalogKeys.TryGetValue(directory, out var fingerprint) ||
            !modSyncKeys.TryGetValue(directory, out var modKey)) return;
        var triggers = RoleTriggersOf(directory, modName);
        var options = GetFreeUseOptionTemplate(directory);
        var partnerId = partnerSync?.ConnectionId;
        var controller = IsWizardController;
        var otherIndex = 0;
        var planned = 0;
        foreach (var member in room.Members.Where(member => !sync.IsCurrentMember(member.ConnectionId)))
        {
            var trigger = assignments.TryGetValue(member.ConnectionId, out var assigned)
                ? assigned
                : DefaultRoleFor(triggers, ownTrigger, otherIndex);
            otherIndex++;
            if (only is not null && !only.Contains(member.ConnectionId)) continue;
            if (member.ConnectionId == partnerId)
            {
                if (!plansOnly) FollowWithTestPartner(directory, ownTrigger, trigger);
                continue;
            }
            if (member.FreeUse)
            {
                if (!plansOnly)
                    DirectFreeUse(new FreeUsePrompt(directory, modName, ownTrigger, member.ConnectionId, member.DisplayName),
                        trigger, options);
                continue;
            }
            // Members on an older Synastry can't see a pick; they choose their own role.
            if (!controller || !member.Wizard) continue;
            planned++;
            RunSync(sync.SendWizardPlanAsync(member.ConnectionId,
                    new FreeUseDirectionRequest(fingerprint, modKey, modName, trigger, options)),
                planned == 1 ? "Sent everyone their role." : $"Sent {planned} members their roles.");
        }
    }

    /// <summary>Readies the controller's pick for you, with the controller's options for this time.</summary>
    public bool ReadyWizardPlan()
    {
        if (WizardPlan is not { } plan) return false;
        var directory = DirectoryForFingerprint(plan.Fingerprint);
        if (directory is null || IsModPrivate(directory) || !modsByDirectory.TryGetValue(directory, out var mod))
        {
            Status = $"You don't have {plan.ModName} in your shared library yet.";
            return false;
        }
        if (!TryResolveFreeUseTrigger(directory, plan.Trigger, out var pose, out var command, out _))
        {
            Status = $"{mod.Name} doesn't have the role {plan.DirectedBy} picked.";
            return false;
        }
        var selections = GetActivationSelections(directory).ToDictionary(
            pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (group, options) in plan.Options) selections[group] = options.ToList();
        var previousRole = lastSharedRole;
        PublishDetectedTriggerSelection(directory, plan.Trigger);
        if (ActivateInternal(directory, mod.Name, pose, requestedCommand: command, selectionOverride: selections)) return true;
        lastSharedRole = previousRole;   // the earlier Ready (if any) still stands with its old role
        return false;
    }

    public string? DirectoryForFingerprint(string fingerprint) =>
        modCatalogKeys.FirstOrDefault(pair => pair.Value.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>A FREE USE choice from the room's controller is also its pick for you: the wizard
    /// shows your role, position and "Ask to swap role" for it.</summary>
    private void RememberControllerDirective(FreeUseDirectiveDto directive)
    {
        if (WizardController is { } controller &&
            controller.DisplayName.Equals(directive.DirectedBy, StringComparison.OrdinalIgnoreCase))
            WizardPlan = directive;
    }

    /// <summary>The round you were picked for started: the pick is done.</summary>
    private void ForgetStartedWizardPlan(string modKey)
    {
        if (WizardPlan is { } plan && plan.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase)) WizardPlan = null;
    }

    // ---- Messages from the room ---------------------------------------------------------------

    private void ProcessWizardMessages()
    {
        while (incomingWizardPlans.TryDequeue(out var plan))
        {
            WizardPlan = plan;
            var directory = DirectoryForFingerprint(plan.Fingerprint);
            var role = directory is null ? plan.Trigger : RoleName(directory, plan.Trigger);
            if (WizardReadiedTrigger is { } readied && directory is not null &&
                readied.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase))
            {
                // Already readied this animation: the same role needs nothing; a swap readies the new one.
                if (!readied.Trigger.Equals(plan.Trigger, StringComparison.OrdinalIgnoreCase) && ReadyWizardPlan())
                    Chat.Print($"[Synastry] {plan.DirectedBy} swapped you to {role}.");
                continue;
            }
            if (!wizardWindow.IsOpen)
                Chat.Print($"[Synastry] {plan.DirectedBy} picked {plan.ModName} for the room; you're {role}. " +
                           "Open the Synastry Wizard (/syn wizard) to ready or ask to swap.");
        }

        while (incomingWizardRequests.TryDequeue(out var request))
        {
            if (request.Kind == "swap")
            {
                if (!IsWizardController ||
                    swapRequests.Any(existing => existing.FromConnectionId == request.FromConnectionId)) continue;
                swapRequests.Add(request);
                if (!wizardWindow.IsOpen)
                    Chat.Print($"[Synastry] {request.FromName} asks to swap roles. Open the Synastry Wizard (/syn wizard) to swap.");
            }
            else if (request.Kind == "lineup")
            {
                if (sync.IsFreeUse)
                {
                    // Lining up toggles, so a repeat request while already walking would cancel it.
                    if (IsAligning) continue;
                    AlignToMember(request.FromName);
                    Chat.Print(IsAligning
                        ? $"[Synastry] FREE USE: lining you up to {request.FromName}."
                        : $"[Synastry] FREE USE: couldn't line you up to {request.FromName}: {Status}");
                    continue;
                }
                var repeat = WizardLineUpRequest?.FromConnectionId == request.FromConnectionId;
                WizardLineUpRequest = request;
                if (!repeat && !wizardWindow.IsOpen)
                    Chat.Print($"[Synastry] {request.FromName} asks you to line up to them. " +
                               "Open the Synastry Wizard (/syn wizard) to line up.");
            }
        }

        // Forget what no longer applies.
        var room = sync.Room;
        if (WizardPlan is { } current &&
            (room is null || !room.Members.Any(member => member.Controller && member.DisplayName == current.DirectedBy)))
            WizardPlan = null;
        if (swapRequests.Count > 0)
            swapRequests.RemoveAll(existing => room is null || !IsWizardController ||
                                               room.Members.All(member => member.ConnectionId != existing.FromConnectionId));
        if (WizardLineUpRequest is { } lineUp &&
            (room is null || room.Members.All(member => member.ConnectionId != lineUp.FromConnectionId)))
            WizardLineUpRequest = null;
        if (room is null) lastSharedRole = null;
    }
}

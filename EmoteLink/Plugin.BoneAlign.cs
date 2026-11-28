using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using EmoteLink.Align;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace EmoteLink;

/// <summary>Bone align and its memory: who it can line up with, what is playing, and the panel.</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
public sealed unsafe partial class Plugin
{
    private const float BoneAlignReach = 2.5f;

    private AlignmentMemory alignments = null!;
    private BoneAlignService boneAlign = null!;
    private AutoAlign autoAlign = null!;
    private BoneAlignWindow boneAlignWindow = null!;
    private ulong boneAlignPartner;
    private long boneAlignPanelDrawnAt = long.MinValue / 2;

    /// <summary>Your penis-only align: whose penis goes into whose opening, for the play it was made in.</summary>
    private sealed record OwnShaftAim(Ik.ShaftAim Aim, PlayContext? Play, bool FromMemory);
    private OwnShaftAim? shaftAim;
    private bool showShaftAimStatus;
    // Aims room partners made with their own bone align, by who sent them.
    private readonly ConcurrentDictionary<string, Ik.SharedAim> sharedAims = new(StringComparer.OrdinalIgnoreCase);

    partial void InitializeBoneAlign()
    {
        alignments = new AlignmentMemory(PluginInterface.ConfigDirectory.FullName, Log);
        boneAlign = new BoneAlignService(Objects, heels, FindCharacter, BoneAlignName, RestartLoops);
        autoAlign = new AutoAlign(this, boneAlign, alignments, new AnimationReadiness(DataManager, ForgetPlayingAnimations));
        boneAlignWindow = new BoneAlignWindow(this);
        windows.AddWindow(boneAlignWindow);
    }

    partial void TickBoneAlign()
    {
        // Asked every frame: it learns which loop the animation settled into.
        var playing = IsSynastryAnimationPlaying();
        // A penis-only align lasts for the animation it was made in.
        if (shaftAim is { } aim && !ReferenceEquals(aim.Play, CurrentPlay)) ClearShaftAim();
        boneAlign.Tick();
        autoAlign.Tick(Objects.LocalPlayer, playing);
        IsBoneAligning = boneAlign.IsAligning;
    }

    // ---- The panel ------------------------------------------------------------------------------

    /// <summary>The panel is on screen, in the room column or its own window.</summary>
    internal bool IsBoneAlignOpen => Environment.TickCount64 - boneAlignPanelDrawnAt < 250;

    internal void NoteBoneAlignPanel() => boneAlignPanelDrawnAt = Environment.TickCount64;

    internal string BoneAlignProblem => AligningShaftOnly ? ShaftAimProblem : boneAlign.Problem;
    internal bool CanBoneAlignTurn => boneAlign.CanTurn;
    internal bool CanBoneAlignReset => boneAlign.CanReset || shaftAim is not null;
    internal AlignmentEntry? KnownAlignment => autoAlign.Known;
    internal string AlignmentWaiting => autoAlign.Waiting;
    internal BodyPart BoneAlignSelf => configuration.BoneAlignSelf;
    internal BodyPart BoneAlignPartner => configuration.BoneAlignPartner;
    internal float BoneAlignGap => configuration.BoneAlignGap;
    internal bool BoneAlignFaceSameWay => configuration.BoneAlignFaceSameWay;
    public string BoneAlignStatus =>
        showShaftAimStatus && shaftAim is { } aim && !boneAlign.IsAligning ? ShaftAimStatus(aim) : boneAlign.Status;
    public bool AutoAlignFromMemory => configuration.AutoAlignFromMemory;

    /// <summary>The animation a bone align can be remembered for: one Synastry started, still playing.</summary>
    internal PlayContext? RememberablePlay => IsSynastryAnimationPlaying() ? CurrentPlay : null;

    internal ICharacter? LocalCharacter => Objects.LocalPlayer;

    /// <summary>Opens the panel beside the button just drawn, or closes it.</summary>
    public void ToggleBoneAlign() => boneAlignWindow.ToggleAtLastItem();

    public void SetAutoAlignFromMemory(bool enabled)
    {
        configuration.AutoAlignFromMemory = enabled;
        configuration.Save(PluginInterface);
    }

    /// <summary>Parts picked by hand.</summary>
    internal void PickBoneAlignParts(BodyPart self, BodyPart partner)
    {
        autoAlign.KeepParts();
        SetBoneAlignParts(self, partner);
    }

    internal void SetBoneAlignParts(BodyPart self, BodyPart partner)
    {
        if (configuration.BoneAlignSelf == self && configuration.BoneAlignPartner == partner) return;
        configuration.BoneAlignSelf = self;
        configuration.BoneAlignPartner = partner;
        configuration.Save(PluginInterface);
    }

    internal void SetBoneAlignGap(float gap, bool save)
    {
        configuration.BoneAlignGap = gap;
        if (save) configuration.Save(PluginInterface);
    }

    internal void SetBoneAlignFaceSameWay(bool enabled)
    {
        configuration.BoneAlignFaceSameWay = enabled;
        configuration.Save(PluginInterface);
    }

    internal void PickBoneAlignPartner(ICharacter partner) => boneAlignPartner = partner.GameObjectId;

    internal void BoneAlignNow()
    {
        if (AligningShaftOnly)
        {
            AimShaft(configuration.BoneAlignSelf, configuration.BoneAlignPartner, null, remember: true, fromMemory: false);
            return;
        }
        StartBoneAlign(new AlignRequest(
            configuration.BoneAlignSelf, configuration.BoneAlignPartner, configuration.BoneAlignGap,
            configuration.BoneAlignFaceSameWay ? 0f : null, null, AlignOrigin.Manual));
    }

    /// <summary>Remembers how you are placed now, without moving you.</summary>
    internal void RememberAlignment() => StartBoneAlign(new AlignRequest(
        configuration.BoneAlignSelf, configuration.BoneAlignPartner, configuration.BoneAlignGap,
        null, null, AlignOrigin.Manual, AlignMode.Measure));

    /// <summary>Lines you up the remembered way, for when that didn't happen by itself.</summary>
    internal void UseKnownAlignment()
    {
        if (autoAlign.Known is not { } known) return;
        if (known.ShaftOnly) AimShaft(known.Self, known.Partner, null, remember: false, fromMemory: false);
        else StartBoneAlign(AlignRequest.FromMemory(known));
    }

    internal void ForgetKnownAlignment() => autoAlign.Forget();

    // Placing yourself by hand counts as lining up yourself, so memory doesn't move you afterwards.
    internal void BoneAlignTurn(float degrees)
    {
        autoAlign.StandDown();
        boneAlign.Turn(configuration.BoneAlignSelf, degrees);
    }

    internal void ResetBoneAlign()
    {
        autoAlign.StandDown();
        if (shaftAim is not null)
        {
            ClearShaftAim();
            boneAlign.Refuse("The penis is back to how the animation moves it.");
        }
        boneAlign.Reset();
    }

    private void StartBoneAlign(AlignRequest request)
    {
        if (Objects.LocalPlayer is not { } local) return;
        if (SelectedBoneAlignPartner(local) is not { } partner)
        {
            boneAlign.Refuse("Target your partner, or join a room with them, and stand close.");
            return;
        }
        autoAlign.StandDown();
        showShaftAimStatus = false;
        if (RememberablePlay is not null)
        {
            // Files are hashed in the background: asked for now, they're known when the result is kept.
            AnimationHashOf(local);
            AnimationHashOf(partner);
        }
        boneAlign.Start(request, partner);
        IsBoneAligning = boneAlign.IsAligning;
    }

    // ---- Penis only -----------------------------------------------------------------------------

    internal bool BoneAlignShaftOnly => configuration.BoneAlignShaftOnly;

    internal void SetBoneAlignShaftOnly(bool enabled)
    {
        configuration.BoneAlignShaftOnly = enabled;
        configuration.Save(PluginInterface);
    }

    /// <summary>The parts are a penis and an opening, either way round.</summary>
    internal static bool IsPenetration(BodyPart self, BodyPart partner) =>
        self == BodyPart.Penis && OpeningOf(partner) is not null || partner == BodyPart.Penis && OpeningOf(self) is not null;

    /// <summary>Align will bend the penis and open the partner instead of moving you.</summary>
    internal bool AligningShaftOnly =>
        configuration.BoneAlignShaftOnly && IsPenetration(configuration.BoneAlignSelf, configuration.BoneAlignPartner);

    private string ShaftAimProblem =>
        contactIk?.Available != true
            ? ContactIkStatus is { Length: > 0 } status ? status : "Bending bones isn't available on this game version."
            : !IsSynastryAnimationPlaying() ? "Penis only works during an animation started with Synastry."
            : "";

    private static ContactPart? OpeningOf(BodyPart part) => part switch
    {
        BodyPart.Mouth => ContactPart.Mouth,
        BodyPart.Vagina => ContactPart.Vagina,
        BodyPart.Anus => ContactPart.Anus,
        _ => null
    };

    private static string OpeningKey(ContactPart part) => part.ToString().ToLowerInvariant();

    private static ContactPart? OpeningFromKey(string key) => key switch
    {
        "mouth" => ContactPart.Mouth,
        "vagina" => ContactPart.Vagina,
        "anus" => ContactPart.Anus,
        _ => null
    };

    /// <summary>Remembered as penis only for this animation: aims again by itself.</summary>
    internal void AimShaftFromMemory(AlignmentEntry entry, ICharacter partner) =>
        AimShaft(entry.Self, entry.Partner, partner, remember: false, fromMemory: true);

    /// <summary>Bends whichever of you has the penis into the other's opening for the rest of this
    /// animation, and widens the opening for it. Nobody moves. Shared with the room so everyone's
    /// Synastry shows it the same.</summary>
    private void AimShaft(BodyPart self, BodyPart partnerPart, ICharacter? partner, bool remember, bool fromMemory)
    {
        if (Objects.LocalPlayer is not { } local) return;
        partner ??= SelectedBoneAlignPartner(local);
        if (partner is null)
        {
            boneAlign.Refuse("Target your partner, or join a room with them, and stand close.");
            return;
        }
        if (ShaftAimProblem is { Length: > 0 } problem)
        {
            boneAlign.Refuse(problem);
            return;
        }
        var mine = self == BodyPart.Penis;
        var giver = mine ? local : partner;
        var receiver = mine ? partner : local;
        var openingPart = mine ? partnerPart : self;
        if (OpeningOf(openingPart) is not { } opening) return;
        var name = BoneAlignName(partner);
        if (!BodyParts.HasShaft(giver))
        {
            boneAlign.Refuse(mine ? "Couldn't find your penis bones." : $"Couldn't find {name}'s penis bones. Their mods may still be loading.");
            return;
        }
        if (!BodyParts.TryLocate(receiver, openingPart, out _))
        {
            boneAlign.Refuse(mine
                ? $"Couldn't find {name}'s {BodyParts.Words(openingPart)} bones. Their mods may still be loading."
                : $"Couldn't find your {BodyParts.Words(openingPart)} bones.");
            return;
        }
        // A measured body lists the openings its mesh really uses.
        if (BodyProfileOf(BoneAlignName(receiver)) is { IsMeasured: true, Openings.Count: > 0 } body &&
            !body.Openings.ContainsKey(OpeningKey(opening)))
        {
            boneAlign.Refuse(mine
                ? $"{name}'s body mesh has no {BodyParts.Words(openingPart)} to aim at."
                : $"Your {ActiveBodyMeshName} mesh has no {BodyParts.Words(openingPart)} to aim at. Measure it again if you changed body mods.");
            return;
        }

        if (!fromMemory) autoAlign.StandDown();
        shaftAim = new OwnShaftAim(new Ik.ShaftAim(BoneAlignName(giver), BoneAlignName(receiver), opening), CurrentPlay, fromMemory);
        showShaftAimStatus = true;
        ShareOwnProfile(true);
        if (remember) autoAlign.RememberShaftAim(self, partnerPart, partner);
    }

    private void ClearShaftAim()
    {
        if (shaftAim is null) return;
        shaftAim = null;
        ShareOwnProfile(true);
    }

    private Ik.SharedAim? OwnSharedAim() => shaftAim is { } own
        ? new Ik.SharedAim { Giver = own.Aim.Giver, Receiver = own.Aim.Receiver, Opening = OpeningKey(own.Aim.Opening) }
        : null;

    /// <summary>Every penis aimed with bone align: yours first, then your room partners'.</summary>
    private List<Ik.ShaftAim> ContactAims(out Ik.ShaftAim? own)
    {
        own = shaftAim?.Aim;
        var aims = new List<Ik.ShaftAim>();
        if (own is not null) aims.Add(own);
        foreach (var (sender, shared) in sharedAims)
        {
            if (!IsRoomMemberNamed(sender) || OpeningFromKey(shared.Opening) is not { } opening) continue;
            if (aims.Any(aim => aim.Giver.Equals(shared.Giver, StringComparison.OrdinalIgnoreCase))) continue;
            aims.Add(new Ik.ShaftAim(shared.Giver, shared.Receiver, opening));
        }
        return aims;
    }

    /// <summary>How your penis-only align is doing, from what the bone bender saw over the last moments.</summary>
    private string ShaftAimStatus(OwnShaftAim own)
    {
        var mine = Objects.LocalPlayer is { } local && own.Aim.Giver == BoneAlignName(local);
        var penis = mine ? "your penis" : $"{own.Aim.Giver}'s penis";
        var opening = (mine ? $"{own.Aim.Receiver}'s " : "your ") + OpeningKey(own.Aim.Opening);
        var prefix = own.FromMemory ? "From memory: " : "";
        if (contactIk?.AimReport is not { } report || report.Aim != own.Aim)
            return $"{prefix}Aiming {penis} at {opening}...";
        var penisFirst = char.ToUpperInvariant(penis[0]) + penis[1..];
        if (!report.Engaged)
            return $"{prefix}{penisFirst} isn't close enough to {opening} to aim. Turn off Penis only and align to move closer.";
        return report.Deepest >= -0.005f
            ? $"{prefix}{penisFirst} goes {MathF.Max(0f, report.Deepest) * 100f:0} cm into {opening} at its deepest."
            : $"{prefix}{penisFirst} stops {-report.Deepest * 100f:0} cm short of {opening} at its deepest. " +
              "Turn off Penis only and align to move closer.";
    }

    // ---- Partners -------------------------------------------------------------------------------

    /// <summary>Who the panel offers: your target, then your test partner, then room members close to
    /// you, nearest first.</summary>
    internal List<ICharacter> BoneAlignPartners(ICharacter local)
    {
        var partners = new List<ICharacter>();
        if ((Targets.Target ?? Targets.SoftTarget) is IPlayerCharacter target && target.Address != local.Address &&
            Vector3.Distance(target.Position, local.Position) <= BoneAlignReach)
            partners.Add(target);
        if (BoneAlignTestPartner() is { } test) partners.Add(test);
        partners.AddRange(NearbyRoomMembers(local).Where(member => partners.All(partner => partner.Address != member.Address)));
        return partners;
    }

    /// <summary>The partner picked in the panel while they're offered, else the first offered.</summary>
    internal ICharacter? SelectedBoneAlignPartner(ICharacter local)
    {
        var partners = BoneAlignPartners(local);
        return partners.FirstOrDefault(partner => partner.GameObjectId == boneAlignPartner) ?? partners.FirstOrDefault();
    }

    /// <summary>Who memory may line you up with by itself: never a stranger.</summary>
    internal List<ICharacter> RoomPartners(ICharacter local)
    {
        var partners = new List<ICharacter>();
        if (testPartner.Playing && BoneAlignTestPartner() is { } test) partners.Add(test);
        partners.AddRange(NearbyRoomMembers(local));
        return partners;
    }

    private IEnumerable<ICharacter> NearbyRoomMembers(ICharacter local) =>
        Objects.OfType<IPlayerCharacter>()
            .Where(player => player.Address != local.Address && IsRoomMemberNamed(player.Name.TextValue))
            .Select(player => (Player: player, Distance: Vector3.Distance(player.Position, local.Position)))
            .Where(item => item.Distance <= BoneAlignReach)
            .OrderBy(item => item.Distance)
            .Select(item => item.Player);

    internal string BoneAlignName(ICharacter character) =>
        IsTestPartner(character) ? testPartner.Name : character.Name.TextValue;

    /// <summary>Playing a loop. A test partner plays its role as a plain timeline.</summary>
    internal bool IsPosing(ICharacter character) =>
        IsTestPartner(character) ? testPartner.Playing : DrawnModel.IsLooping(character);

    /// <summary>When both of you remember the animation: your test partner never lines up by itself,
    /// otherwise the name that sorts first does. Both sides compare the same two names.</summary>
    internal bool LinesUpFirst(ICharacter partner) =>
        IsTestPartner(partner) || string.CompareOrdinal(SyncDisplayName, partner.Name.TextValue) < 0;

    private bool IsTestPartner(ICharacter character) => testPartner.Active && character.Address == testPartner.Address;

    private ICharacter? FindCharacter(ulong id) =>
        BoneAlignTestPartner() is { } test && test.GameObjectId == id ? test : Objects.SearchById(id) as ICharacter;

    /// <summary>The test partner while its minion is still out. The service only notices it leaving on
    /// its own tick, after this one, and by then the minion's memory is gone.</summary>
    private ICharacter? BoneAlignTestPartner() =>
        Objects.LocalPlayer is { } local && (nint)((Character*)local.Address)->ChildObject == testPartner.Address
            ? TestPartnerCharacter()
            : null;

    // ---- What is playing ------------------------------------------------------------------------

    /// <summary>Still changing to the right pose variant, or walking onto a target's spot.</summary>
    internal bool IsSettlingIn => cyclingPose is not null || IsAligning;

    /// <summary>The animation file a character is playing, once known.</summary>
    internal string? AnimationHashOf(ICharacter character) =>
        contactMaps.Resolve(character.Address, character.GameObjectId, character.ObjectIndex,
            character.Address == Objects.LocalPlayer?.Address).Hash;

    internal void ForgetPlayingAnimations() => contactMaps.ForgetPlaying();

    /// <summary>Picks the parts the animation's contact map names, when either side has one.</summary>
    internal bool SuggestBoneAlignParts(ICharacter local, ICharacter partner)
    {
        if (OpeningPart(MappedOpeningOf(local)) is { } theirs) SetBoneAlignParts(BodyPart.Penis, theirs);
        else if (OpeningPart(MappedOpeningOf(partner)) is { } mine) SetBoneAlignParts(mine, BodyPart.Penis);
        else return false;
        return true;
    }

    private static BodyPart? OpeningPart(string? opening) => opening switch
    {
        "mouth" => BodyPart.Mouth,
        "vagina" => BodyPart.Vagina,
        "anus" => BodyPart.Anus,
        _ => null
    };

    /// <summary>Restarts both loops together, so their contact moments meet. A test partner or a locked
    /// emote plays a plain timeline, which restarts the same way.</summary>
    private static void RestartLoops(ICharacter local, ICharacter partner)
    {
        RefreshActorEmote(local.Address);
        RefreshActorEmote(partner.Address, false);
    }
}

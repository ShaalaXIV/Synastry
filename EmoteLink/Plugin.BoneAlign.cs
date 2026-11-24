using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using EmoteLink.Align;

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
        boneAlign.Tick();
        autoAlign.Tick(Objects.LocalPlayer, playing);
        IsBoneAligning = boneAlign.IsAligning;
    }

    // ---- The panel ------------------------------------------------------------------------------

    internal bool IsBoneAlignOpen => boneAlignWindow.IsOpen;
    internal string BoneAlignProblem => boneAlign.Problem;
    internal bool CanBoneAlignTurn => boneAlign.CanTurn;
    internal bool CanBoneAlignReset => boneAlign.CanReset;
    internal AlignmentEntry? KnownAlignment => autoAlign.Known;
    internal string AlignmentWaiting => autoAlign.Waiting;
    internal BodyPart BoneAlignSelf => configuration.BoneAlignSelf;
    internal BodyPart BoneAlignPartner => configuration.BoneAlignPartner;
    internal float BoneAlignGap => configuration.BoneAlignGap;
    internal bool BoneAlignFaceSameWay => configuration.BoneAlignFaceSameWay;
    public string BoneAlignStatus => boneAlign.Status;
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

    internal void BoneAlignNow() => StartBoneAlign(new AlignRequest(
        configuration.BoneAlignSelf, configuration.BoneAlignPartner, configuration.BoneAlignGap,
        configuration.BoneAlignFaceSameWay ? 0f : null, null, AlignOrigin.Manual));

    /// <summary>Remembers how you are placed now, without moving you.</summary>
    internal void RememberAlignment() => StartBoneAlign(new AlignRequest(
        configuration.BoneAlignSelf, configuration.BoneAlignPartner, configuration.BoneAlignGap,
        null, null, AlignOrigin.Manual, AlignMode.Measure));

    /// <summary>Lines you up the remembered way, for when that didn't happen by itself.</summary>
    internal void UseKnownAlignment()
    {
        if (autoAlign.Known is { } known) StartBoneAlign(AlignRequest.FromMemory(known));
    }

    internal void ForgetKnownAlignment() => autoAlign.Forget();

    internal void BoneAlignTurn(float degrees) => boneAlign.Turn(configuration.BoneAlignSelf, degrees);

    internal void ResetBoneAlign() => boneAlign.Reset();

    private void StartBoneAlign(AlignRequest request)
    {
        if (Objects.LocalPlayer is not { } local) return;
        if (SelectedBoneAlignPartner(local) is not { } partner)
        {
            boneAlign.Refuse("Target your partner, or join a room with them, and stand close.");
            return;
        }
        autoAlign.StandDown();
        boneAlign.Start(request, partner);
        IsBoneAligning = boneAlign.IsAligning;
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
        if (TestPartnerCharacter() is { } test) partners.Add(test);
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
        if (testPartner.Playing && TestPartnerCharacter() is { } test) partners.Add(test);
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
        TestPartnerCharacter() is { } test && test.GameObjectId == id ? test : Objects.SearchById(id) as ICharacter;

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

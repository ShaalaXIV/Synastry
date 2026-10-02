using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace EmoteLink;

internal enum ContactPart
{
    Mouth,
    Vagina,
    Anus,
}

/// <summary>Which part line-up aims for. A mouth already in contact always wins.</summary>
public enum ContactPreference
{
    Closest,
    Mouth,
    Vagina,
    Anus,
}

/// <summary>
/// Lines up a penis with a partner's mouth, vagina or anus during a looping couple animation.
///
/// It watches both characters for a short window after an animation starts, finds the moment the
/// receiving part comes closest to the shaft (base to tip, so any depth counts), and moves the
/// receiving character by that gap. The move goes through Simple Heels' temp offset, so it is drawn
/// only, never sent to the server, and reaches the partner the same way any temp offset does.
///
/// Only the receiving side moves, so two Synastry users never both correct the same gap. A partner
/// without Synastry can't move, so then this side moves whatever its role. The idea follows PoseKit's
/// Bone Align (RaylaPetal/PoseKit); this implementation is Synastry's own.
/// </summary>
internal sealed unsafe class ContactAlignService
{
    // Long enough for an intro to finish and the loop to start.
    private const long SettleMs = 2000;

    // Long enough to catch the contact moment of a typical loop.
    private const long SampleMs = 1600;

    // Closer than this is already lined up.
    private const float AlignedDistance = 0.02f;

    // Farther than this is not a contact animation, so automatic line-up leaves it alone.
    private const float AutomaticMaxDistance = 0.5f;

    // Past this the drawn model visibly drifts from the real character.
    private const float ManualMaxDistance = 1.0f;

    private const float PartnerSearchRadius = 2.5f;

    // A mouth this close to the shaft is in contact, and wins over any preference.
    private const float MouthContactDistance = 0.08f;

    // YAS gives every body the full genital set; on a body that doesn't use it the shaft bones
    // collapse onto one point. A shaft shorter than this is one of those.
    private const float MinShaftLength = 0.05f;

    private static readonly string[] Shaft =
        ["iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f"];

    // Most faces have four lip-corner bones; some older faces use a single pair.
    private static readonly string[][] MouthGroups =
    [
        ["j_f_ulip_01_l", "j_f_ulip_01_r", "j_f_dlip_01_l", "j_f_dlip_01_r"],
        ["j_f_ulip_a", "j_f_dlip_a"],
        ["j_f_lip_l", "j_f_lip_r"],
    ];

    private static readonly HashSet<string> WantedBones = new(
        Shaft.Concat(MouthGroups.SelectMany(group => group)).Concat(["iv_omanko", "iv_koumon"]),
        StringComparer.Ordinal);

    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly IPluginLog log;
    private readonly Func<string, bool> usesSynastry;
    private readonly Action<string> executeCommand;
    private readonly Func<ContactPreference> preference;

    private string handledSignature = "";
    private string pendingSignature = "";
    private long pendingSince;
    private bool measuring;
    private bool manual;
    private long measureStarted;
    private ulong partnerId;
    private readonly Dictionary<ContactPart, Contact> bestByPart = [];

    public ContactAlignService(
        IObjectTable objects,
        ITargetManager targets,
        IPluginLog log,
        Func<string, bool> usesSynastry,
        Action<string> executeCommand,
        Func<ContactPreference> preference)
    {
        this.preference = preference;
        this.objects = objects;
        this.targets = targets;
        this.log = log;
        this.usesSynastry = usesSynastry;
        this.executeCommand = executeCommand;
    }

    public string Status { get; private set; } = "";
    public bool IsMeasuring => measuring;

    private sealed record Contact(
        float Distance,
        bool LocalHasShaft,
        ContactPart Part,
        Vector3 ShaftPoint,
        Vector3 PartPoint,
        float LocalModelYaw);

    /// <summary>The Line up button: measure now, with a wider reach than the automatic check.</summary>
    public void LineUpNow(bool simpleHeels)
    {
        if (!simpleHeels)
        {
            Status = "Line up needs Simple Heels.";
            return;
        }
        if (objects.LocalPlayer is not { } local || !IsLooping(local))
        {
            Status = "Start a looping animation with your partner first.";
            return;
        }
        if (FindPartner(local) is not { } partner)
        {
            Status = "Nobody is close enough to line up with.";
            return;
        }
        Begin(partner, true);
    }

    public void Tick(bool automatic, bool simpleHeels)
    {
        var local = objects.LocalPlayer;
        if (local is null) return;

        if (measuring)
        {
            Sample(local);
            return;
        }

        if (!automatic || !simpleHeels || !IsLooping(local))
        {
            pendingSignature = "";
            return;
        }

        var partner = FindPartner(local);
        if (partner is null)
        {
            pendingSignature = "";
            return;
        }

        // A new animation on either side starts a fresh wait; each animation is measured once.
        var signature = $"{Signature(local)}|{partner.GameObjectId}|{Signature(partner)}";
        if (signature == handledSignature) return;
        if (signature != pendingSignature)
        {
            pendingSignature = signature;
            pendingSince = Environment.TickCount64;
            return;
        }
        if (Environment.TickCount64 - pendingSince < SettleMs) return;

        handledSignature = signature;
        Begin(partner, false);
    }

    private void Begin(IPlayerCharacter partner, bool fromButton)
    {
        measuring = true;
        manual = fromButton;
        measureStarted = Environment.TickCount64;
        partnerId = partner.GameObjectId;
        bestByPart.Clear();
        if (manual) Status = $"Measuring against {partner.Name.TextValue}...";
    }

    private void Sample(IPlayerCharacter local)
    {
        if (objects.SearchById(partnerId) is not IPlayerCharacter partner || !IsLooping(local))
        {
            measuring = false;
            if (manual) Status = "Line up stopped: the animation or your partner changed.";
            return;
        }

        var mine = ReadBones(local);
        var theirs = ReadBones(partner);
        if (TryGetModelYaw(local, out var yaw))
        {
            foreach (var contact in Measure(mine, theirs, true, yaw)) Consider(contact);
            foreach (var contact in Measure(theirs, mine, false, yaw)) Consider(contact);
        }

        if (Environment.TickCount64 - measureStarted >= SampleMs)
            Finish(partner);
    }

    private void Consider(Contact contact)
    {
        if (!bestByPart.TryGetValue(contact.Part, out var current) || contact.Distance < current.Distance)
            bestByPart[contact.Part] = contact;
    }

    /// <summary>
    /// The part to line up: a mouth already in contact, then the preferred part if it is within
    /// reach, then whichever came closest.
    /// </summary>
    private Contact? Choose(float limit)
    {
        if (bestByPart.Count == 0) return null;
        if (bestByPart.TryGetValue(ContactPart.Mouth, out var mouth) && mouth.Distance <= MouthContactDistance)
            return mouth;
        ContactPart? wanted = preference() switch
        {
            ContactPreference.Mouth => ContactPart.Mouth,
            ContactPreference.Vagina => ContactPart.Vagina,
            ContactPreference.Anus => ContactPart.Anus,
            _ => null
        };
        if (wanted is { } part && bestByPart.TryGetValue(part, out var preferred) && preferred.Distance <= limit)
            return preferred;
        return bestByPart.Values.MinBy(contact => contact.Distance);
    }

    /// <summary>How close each receiving part of <paramref name="receiver"/> comes to the shaft of
    /// <paramref name="giver"/> on this frame.</summary>
    private static IEnumerable<Contact> Measure(
        IReadOnlyDictionary<string, Vector3> giver,
        IReadOnlyDictionary<string, Vector3> receiver,
        bool localIsGiver,
        float localYaw)
    {
        var shaft = RealShaft(giver);
        if (shaft is null) yield break;

        foreach (var (part, point) in ReceivingParts(receiver, RealShaft(receiver) is not null))
        {
            var onShaft = ClosestPointOnPolyline(shaft, point);
            yield return new Contact(Vector3.Distance(onShaft, point), localIsGiver, part, onShaft, point, localYaw);
        }
    }

    /// <summary>The shaft from base to tip, or null when the body has none or only the collapsed set.</summary>
    private static List<Vector3>? RealShaft(IReadOnlyDictionary<string, Vector3> bones)
    {
        var shaft = Shaft.Where(bones.ContainsKey).Select(name => bones[name]).ToList();
        if (shaft.Count < 2) return null;
        var length = 0f;
        for (var i = 0; i + 1 < shaft.Count; i++) length += Vector3.Distance(shaft[i], shaft[i + 1]);
        return length >= MinShaftLength ? shaft : null;
    }

    /// <summary>A body with a real shaft carries an unused vagina bone beside the anus, so its vagina
    /// is never a target.</summary>
    private static IEnumerable<(ContactPart Part, Vector3 Point)> ReceivingParts(
        IReadOnlyDictionary<string, Vector3> bones,
        bool hasShaft)
    {
        foreach (var group in MouthGroups)
        {
            var present = group.Where(bones.ContainsKey).ToList();
            if (present.Count == 0) continue;
            yield return (ContactPart.Mouth, present.Aggregate(Vector3.Zero, (sum, name) => sum + bones[name]) / present.Count);
            break;
        }
        if (!hasShaft && bones.TryGetValue("iv_omanko", out var vagina)) yield return (ContactPart.Vagina, vagina);
        if (bones.TryGetValue("iv_koumon", out var anus)) yield return (ContactPart.Anus, anus);
    }

    private void Finish(IPlayerCharacter partner)
    {
        measuring = false;
        var name = partner.Name.TextValue;
        var limit = manual ? ManualMaxDistance : AutomaticMaxDistance;
        if (Choose(limit) is not { } contact)
        {
            if (manual) Status = $"Couldn't find the bones to line up with {name}. Both of you need an IVCS body.";
            return;
        }

        var part = PartName(contact.Part);
        if (contact.Distance <= AlignedDistance)
        {
            if (manual) Status = $"Already lined up with {name}.";
            return;
        }
        if (contact.Distance > limit)
        {
            if (manual) Status = $"Too far apart to line up ({contact.Distance:0.00}y). Move closer first.";
            return;
        }

        // Automatically only the receiving side moves, unless the partner has no Synastry to do it.
        // A press of Line up always moves this side: the person asked for it.
        var localMoves = manual || !contact.LocalHasShaft || !usesSynastry(name);
        if (!localMoves)
        {
            Status = $"{name} is lining up their {part} with you.";
            return;
        }

        var world = contact.LocalHasShaft
            ? contact.PartPoint - contact.ShaftPoint
            : contact.ShaftPoint - contact.PartPoint;
        // Simple Heels offsets are in the drawn model's own frame: +X left, +Z forward.
        var local = Vector3.Transform(world, Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(contact.LocalModelYaw, 0, 0)));
        executeCommand(string.Create(CultureInfo.InvariantCulture,
            $"/heels temp add left {local.X:0.####} up {local.Y:0.####} forward {local.Z:0.####} silent"));
        Status = contact.LocalHasShaft
            ? $"Lined up with {name}'s {part} ({contact.Distance:0.00}y)."
            : $"Lined up your {part} with {name} ({contact.Distance:0.00}y).";
        log.Debug("Contact line-up: {Part} {Distance:0.000}y, moved {Local}.", part, contact.Distance, local);
    }

    private static string PartName(ContactPart part) => part switch
    {
        ContactPart.Mouth => "mouth",
        ContactPart.Vagina => "vagina",
        _ => "anus",
    };

    private IPlayerCharacter? FindPartner(IPlayerCharacter local)
    {
        if ((targets.Target ?? targets.SoftTarget) is IPlayerCharacter target &&
            target.GameObjectId != local.GameObjectId &&
            Vector3.Distance(target.Position, local.Position) <= PartnerSearchRadius &&
            IsLooping(target))
            return target;

        return objects.OfType<IPlayerCharacter>()
            .Where(player => player.GameObjectId != local.GameObjectId && IsLooping(player))
            .Select(player => (Player: player, Distance: Vector3.Distance(player.Position, local.Position)))
            .Where(item => item.Distance <= PartnerSearchRadius)
            .OrderByDescending(item => usesSynastry(item.Player.Name.TextValue))
            .ThenBy(item => item.Distance)
            .Select(item => item.Player)
            .FirstOrDefault();
    }

    private static bool IsLooping(IPlayerCharacter player)
    {
        var character = (Character*)player.Address;
        return character is not null &&
               character->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop;
    }

    /// <summary>What the character is playing: changes whenever a new emote, pose or timeline starts.</summary>
    private static string Signature(IPlayerCharacter player)
    {
        var character = (Character*)player.Address;
        if (character is null) return "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{(byte)character->Mode}:{character->ModeParam}:{character->EmoteController.EmoteId}:{character->Timeline.BaseOverride}");
    }

    private static bool TryGetModelYaw(IPlayerCharacter player, out float yaw)
    {
        yaw = 0;
        var character = (Character*)player.Address;
        if (character is null || character->DrawObject is null ||
            character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)character->DrawObject)->Skeleton;
        if (skeleton is null) return false;
        var rotation = skeleton->Transform.Rotation;
        var forward = Vector3.Transform(Vector3.UnitZ, new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
        yaw = MathF.Atan2(forward.X, forward.Z);
        return true;
    }

    /// <summary>World positions of the bones this service needs, as currently drawn.</summary>
    private static Dictionary<string, Vector3> ReadBones(IPlayerCharacter player)
    {
        var found = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var character = (Character*)player.Address;
        if (character is null || character->DrawObject is null ||
            character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return found;
        var characterBase = (CharacterBase*)character->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return found;
        var skeleton = characterBase->Skeleton;
        if (skeleton is null) return found;

        var transform = skeleton->Transform;
        var origin = new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
        var rotation = new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        var scale = new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z);

        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (pose is null || pose->Skeleton is null) continue;
            var bones = pose->Skeleton->Bones;
            for (var i = 0; i < bones.Length; i++)
            {
                var name = bones[i].Name.String;
                if (name is null || !WantedBones.Contains(name) || found.ContainsKey(name)) continue;
                var model = pose->AccessBoneModelSpace(i, hkaPose.PropagateOrNot.DontPropagate);
                if (model is null) continue;
                var offset = new Vector3(model->Translation.X, model->Translation.Y, model->Translation.Z);
                found[name] = origin + Vector3.Transform(offset * scale, rotation);
            }
        }
        return found;
    }

    private static Vector3 ClosestPointOnPolyline(IReadOnlyList<Vector3> points, Vector3 target)
    {
        var closest = points[0];
        var closestDistance = float.MaxValue;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var segment = points[i + 1] - a;
            var lengthSquared = segment.LengthSquared();
            var t = lengthSquared < 1e-8f ? 0f : Math.Clamp(Vector3.Dot(target - a, segment) / lengthSquared, 0f, 1f);
            var candidate = a + segment * t;
            var distance = Vector3.DistanceSquared(candidate, target);
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closest = candidate;
        }
        return closest;
    }
}

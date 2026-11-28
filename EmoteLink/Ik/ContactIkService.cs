using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EmoteLink.Ik;

/// <summary>A character near you this frame, gathered on the framework thread, with the animation
/// file it's playing (SHA-256) and that file's contact map when one exists.</summary>
internal sealed record IkActor(nint Address, ulong ObjectId, string Name, bool IsLocal, bool Looping,
    string? Hash = null, ContactMap? Map = null);

/// <summary>A penis aimed at an opening with bone align, by character name. It bends and the opening
/// widens whatever the animation's contact map says.</summary>
internal sealed record ShaftAim(string Giver, string Receiver, ContactPart Opening);

/// <summary>How the player's own aim is doing: whether the opening was in reach, and how far past it
/// the tip went at its deepest over the last few seconds (negative: short of it), in yalms.</summary>
internal sealed record ShaftAimReport(ShaftAim Aim, bool Engaged, float Deepest);

/// <summary>What the player has switched on, read once per frame: the automatic bending, the aims
/// from bone align (yours and your room's), and which of them is yours to report on.</summary>
internal sealed record IkSettings(bool Shaft, bool Openings, bool Hands, IReadOnlyList<ShaftAim> Aims,
    ShaftAim? Reported = null)
{
    public bool Any => Shaft || Openings || Hands || Aims.Count > 0;
}

/// <summary>
/// Bends bones to make couple animations meet: the shaft aims through the opening bone align or the
/// animation's contact map chose, the opening widens for it, and hands that hover near a partner's body settle onto it and
/// close their fingers. It runs just before the game draws each frame, on top of the finished
/// animation, for you and the room partners near you (every Synastry in the room does the same, so
/// everyone sees it). Bones only ever rotate, within joint limits; a limb that can't reach stops
/// short rather than stretching, and every effect eases in and out.
/// </summary>
internal sealed unsafe class ContactIkService : IDisposable
{
    // The same draw hook Customize+ uses, so it also works outside GPose.
    private const string RenderSignature = "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED";

    // A shaft engages when its tip comes this close (model units) to an opening. Real pairs sit
    // about 7 cm from the opening (sex-animation-corpus, 241 pairs); 10 cm catches them all without
    // bending a shaft toward an opening it was never aimed at.
    private const float ShaftEngageDistance = 0.10f;
    // A penis aimed with bone align bends toward its opening from further away, since the player chose
    // it, but never toward one off to the side or behind it.
    private const float AimEngageDistance = 0.25f;
    private const float MaxAimAngleDegrees = 75f;
    // A shaft shorter than this is the collapsed set every YAS body carries.
    private const float MinShaftLength = 0.05f;
    // Hands, measured on 1,142 hands in 262 scenes (sex-animation-corpus): a hand an animator put on
    // a partner sits within 3 cm of the skin for most of the loop, while hands meant for a table, the
    // floor or the air hover 3-15 cm away. Engaging at 3 cm after a quarter second, and letting go
    // past 6 cm, catches 86.5% of intended contact and grabs 1% of the rest (9 cm grabbed 25.5%).
    private const float HandEngageDistance = 0.03f;   // world yalms to skin
    private const float HandReleaseDistance = 0.06f;
    private const float HandHoldSeconds = 0.27f;      // eight animation frames at 30 fps
    // A held hand the animation pulls away fast (a slap's wind-up, a pat) is let go at once and eases
    // off quickly, rather than being held until 6 cm and easing off over 0.3 s. On the same hands:
    // hands meant to stay on open the gap faster than 0.3 y/s in under 1% of held frames (p99 0.28),
    // while hands that touch and leave depart at 0.6-1 y/s. Only once the palm is past 3 cm, so a
    // hand that stays on keeps 99% of its contact.
    private const float HandLeaveSpeed = 0.3f;        // world yalms per second, gap opening
    private const float HandQuickEaseSeconds = 0.08f;
    // A palm this low (model space, above the feet) is propping on the floor, not on a partner.
    private const float FloorHeight = 0.12f;
    // The whole penis pivots at its root first, keeping the shape the animation gave it; only what that
    // can't cover is shared out along it as a gentle curve.
    private const float ShaftRootLimitDegrees = 30f;
    private const float ShaftBendLimitDegrees = 8f;
    // An opening starts to part when the tip is 2 cm away, is fully open once it is 1 cm in, and stays
    // shut while the shaft passes more than 3 cm beside it.
    private const float OpenBefore = 0.02f;
    private const float OpenWhenIn = 0.01f;
    private const float OpenMiss = 0.03f;
    private const float AimReportSeconds = 2.5f;
    // Inside a partner the shaft can't be seen, but a long one pokes out of the back of the head, under
    // the chin, or out of the belly or back. So past a little way in, the rest is drawn shorter and
    // thinner: full size up to Keep (so the opening closes round it), squeezed after that and never past
    // Limit, the tip down to TipSize. Distances are fractions of the receiver's hip width, so a small
    // body has less room than a large one.
    private const float MouthKeep = 0.09f, MouthLimit = 0.2f, MouthTipSize = 0.35f;
    private const float BodyKeep = 0.3f, BodyLimit = 0.45f, BodyTipSize = 0.5f;
    private const float InsideSqueeze = 0.35f;
    // Hip width when the hip bones can't be read, and the range a read one is trusted in.
    private const float DefaultHipWidth = 0.17f;
    private const float PalmTurnLimitDegrees = 40f;
    private const float EaseSeconds = 0.3f;

    private static readonly string[] Shaft =
        ["iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f"];

    private static readonly string[][] MouthGroups =
    [
        ["j_f_ulip_01_l", "j_f_ulip_01_r", "j_f_dlip_01_l", "j_f_dlip_01_r"],
        ["j_f_ulip_a", "j_f_dlip_a"],
        ["j_f_lip_l", "j_f_lip_r"],
    ];

    private static readonly string[] UpperLips = ["j_f_ulip_01_l", "j_f_ulip_01_r", "j_f_ulip_a"];
    private static readonly string[] LowerLips = ["j_f_dlip_01_l", "j_f_dlip_01_r", "j_f_dlip_a"];

    private static readonly string[] Fingers = ["hito", "naka", "kusu", "ko"];

    /// <summary>Body surfaces a hand can settle on: a bone and the bone its segment runs to (or
    /// itself for a single point), with the resting radius used when no body profile is shared.</summary>
    internal static readonly (string From, string To, float DefaultRadius)[] Surfaces =
    [
        ("j_asi_a_l", "j_asi_c_l", 0.075f), ("j_asi_a_r", "j_asi_c_r", 0.075f),
        ("j_asi_c_l", "j_asi_d_l", 0.05f), ("j_asi_c_r", "j_asi_d_r", 0.05f),
        ("j_kosi", "j_sebo_a", 0.11f), ("j_sebo_a", "j_sebo_b", 0.10f), ("j_sebo_b", "j_sebo_c", 0.11f),
        ("iv_shiri_l", "iv_shiri_l", 0.07f), ("iv_shiri_r", "iv_shiri_r", 0.07f),
        ("j_mune_l", "j_mune_l", 0.06f), ("j_mune_r", "j_mune_r", 0.06f),
        ("j_ude_a_l", "j_ude_b_l", 0.045f), ("j_ude_a_r", "j_ude_b_r", 0.045f),
        ("j_kubi", "j_kao", 0.05f),
    ];

    private static readonly string[] Wanted = BuildWanted();

    private delegate nint RenderDelegate(nint a1, nint a2, nint a3, int a4);

    private readonly IPluginLog log;
    private readonly Func<string, BodyProfile?> profileOf;
    private readonly Hook<RenderDelegate>? renderHook;
    private readonly Dictionary<string, Quaternion[]> shaftDeltas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpeningTarget> openings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandState> hands = new(StringComparer.Ordinal);

    /// <summary>Per hand: whether it holds on, how long it has waited inside the engage distance, and
    /// the surface it last held, so letting go eases out instead of snapping back.</summary>
    private sealed class HandState
    {
        public bool Engaged;
        public float Waiting;
        /// <summary>The animated palm's gap to skin last frame, for how fast it's leaving.</summary>
        public float? LastGap;
        /// <summary>Let go because the animation pulled away: ease off fast.</summary>
        public bool QuickRelease;
        public (string Actor, string From, string To, float Radius)? Surface;
    }
    private volatile IReadOnlyList<IkActor> actors = [];
    private volatile IkSettings settings = new(false, false, false, []);
    private volatile ShaftAimReport? aimReport;
    private ShaftAim? trackedAim;
    private float trackedTime;
    private float? trackedDeepest;
    private long lastFrame;
    private bool failed;

    public ContactIkService(ISigScanner scanner, IGameInteropProvider interop, IPluginLog log,
        Func<string, BodyProfile?> profileOf)
    {
        this.log = log;
        this.profileOf = profileOf;
        try
        {
            renderHook = interop.HookFromAddress<RenderDelegate>(scanner.ScanText(RenderSignature), OnRender);
            renderHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Contact IK could not hook drawing; bending bones is unavailable.");
        }
    }

    public bool Available => renderHook is not null && !failed;
    public string Status { get; private set; } = "";

    /// <summary>How the player's own bone align aim is doing; null until it has been watched a moment.</summary>
    public ShaftAimReport? AimReport
    {
        get => aimReport;
        private set => aimReport = value;
    }

    /// <summary>Called each framework tick with the characters to work on.</summary>
    public void Update(IReadOnlyList<IkActor> nearby, IkSettings current)
    {
        actors = nearby;
        settings = current;
    }

    private nint OnRender(nint a1, nint a2, nint a3, int a4)
    {
        if (!failed)
        {
            try
            {
                Apply();
            }
            catch (Exception ex)
            {
                failed = true;
                Status = "Bending bones stopped after an error; reload Synastry to try again.";
                log.Error(ex, "Contact IK disabled itself after an unexpected error.");
            }
        }
        return renderHook!.Original(a1, a2, a3, a4);
    }

    private void Apply()
    {
        var now = Environment.TickCount64;
        var deltaSeconds = lastFrame == 0 ? 0f : Math.Clamp((now - lastFrame) / 1000f, 0f, 0.1f);
        lastFrame = now;

        var current = settings;
        var list = actors;
        // Your aim ended: what it reached last time no longer applies.
        if (current.Reported is null && trackedAim is not null)
        {
            trackedAim = null;
            AimReport = null;
        }
        if (list.Count == 0 || !current.Any)
        {
            weights.Clear();
            return;
        }

        var views = new List<(IkActor Actor, SkeletonView View)>(list.Count);
        foreach (var actor in list)
        {
            var gameObject = (GameObject*)actor.Address;
            if (gameObject is null || gameObject->DrawObject is null) continue;
            if (SkeletonView.Read((nint)gameObject->DrawObject, actor.Name, actor.ObjectId, Wanted) is { } view)
                views.Add((actor, view));
        }
        if (views.Count < 2)
        {
            DecayAll(deltaSeconds);
            return;
        }

        // Decide everything from the untouched animation first, then write.
        openings.Clear();
        var shaftWork = new List<Action>();
        if (current.Shaft || current.Openings || current.Aims.Count > 0)
            foreach (var (actor, view) in views)
                if (actor.Looping && PlanShaft(actor, view, views, current, deltaSeconds) is { } work)
                    shaftWork.Add(work);

        var handWork = new List<Action>();
        if (current.Hands)
            foreach (var (actor, view) in views)
                if (actor.Looping)
                    foreach (var side in new[] { "l", "r" })
                        if (PlanHand(actor, view, side, views, deltaSeconds) is { } work)
                            handWork.Add(work);

        foreach (var work in shaftWork) work();
        foreach (var (_, view) in views)
            if (openings.TryGetValue(view.Name, out var opening))
                Open(view, opening);
        foreach (var work in handWork) work();
    }

    // ---- Shaft ----------------------------------------------------------------------------------

    /// <summary>An opening to widen this frame: by how much on each side, and the receiver-space
    /// direction its sides part along (square to the shaft).</summary>
    private sealed record OpeningTarget(ContactPart Part, float Amount, Vector3 Across);

    private Action? PlanShaft(IkActor giver, SkeletonView view, List<(IkActor Actor, SkeletonView View)> all,
        IkSettings current, float deltaSeconds)
    {
        var key = "shaft|" + giver.Name;
        // Bone align's aim is the player's own choice and always wins. Otherwise only an animation whose
        // contact map says where the shaft goes is bent, and only when that is switched on.
        var aim = current.Aims.FirstOrDefault(candidate => candidate.Giver == giver.Name);
        var reported = aim is not null && aim == current.Reported;
        var profile = profileOf(giver.Name);
        var names = ShaftChainOf(view, profile);
        var joints = names.Select(view.Model).ToList();
        if (joints.Count < 2 || Length(joints) < MinShaftLength)
            return Missed(reported, aim, key, deltaSeconds, view, names);

        ContactPart part;
        var mapPartner = giver.Map?.Shaft?.Partner;
        if (aim is not null) part = aim.Opening;
        else if ((current.Shaft || current.Openings) && giver.Map?.Shaft is { } shaftMap)
            part = shaftMap.Opening switch
            {
                "mouth" => ContactPart.Mouth,
                "anus" => ContactPart.Anus,
                _ => ContactPart.Vagina
            };
        else return Decay(key, deltaSeconds, view, names);
        var bend = aim is not null || current.Shaft;
        var open = aim is not null || current.Openings;
        var engage = aim is not null ? AimEngageDistance : ShaftEngageDistance;

        var lastDirection = Vector3.Normalize(joints[^1] - joints[^2]);
        var tipExtra = profile?.Shaft?.TipBeyondLastBone is > 0 and var extra ? extra : Vector3.Distance(joints[^1], joints[^2]);
        var chain = new List<Vector3>(joints) { joints[^1] + lastDirection * tipExtra };
        var basePoint = chain[0];
        var tip = chain[^1];
        var pointing = tip - basePoint;
        var maxAngle = IkSolver.DegreesToRadians(MaxAimAngleDegrees);

        // The opening to aim for, in this giver's own space: the aimed partner's, or the closest one on
        // a partner the contact map names. Never one off to the side or behind the shaft.
        (SkeletonView Receiver, Vector3 Point, float Distance)? target = null;
        foreach (var (receiverActor, receiver) in all)
        {
            if (receiverActor.ObjectId == giver.ObjectId) continue;
            if (aim is not null ? receiverActor.Name != aim.Receiver
                : !giver.Map!.IsPartner(mapPartner!, receiverActor.Hash)) continue;
            foreach (var (found, world) in OpeningsOf(receiver, aim is not null))
            {
                if (found != part) continue;
                var point = view.ToModel(world);
                var distance = MathF.Min(Vector3.Distance(point, tip), Vector3.Distance(point, ClosestOnPolyline(chain, point)));
                if (distance > engage || Angle(pointing, point - basePoint) > maxAngle) continue;
                if (target is null || distance < target.Value.Distance) target = (receiver, point, distance);
            }
        }
        if (target is not { } goal) return Missed(reported, aim, key, deltaSeconds, view, names);

        var through = goal.Point - basePoint;
        if (through.LengthSquared() < 1e-8f) return Missed(reported, aim, key, deltaSeconds, view, names);
        var direction = Vector3.Normalize(through);
        var weight = Ease(key, 1f, deltaSeconds);

        // Aim through the opening without changing how deep the animation takes it: the tip goes on the
        // line from the base through the opening, at its current reach. The penis pivots at its root
        // first, so it keeps its shape instead of curling at the tip.
        Action? work = null;
        var finalTip = tip;
        if (bend && IkSolver.Aim(chain, basePoint + direction * pointing.Length(), ShaftRootLimitDegrees,
                ShaftBendLimitDegrees) is { } final)
        {
            var deltas = SequentialDeltas(chain, final);
            shaftDeltas[key] = deltas;
            finalTip = final[^1];
            var room = RoomInside(view, goal.Receiver, part);
            var opening = goal.Point;
            work = () =>
            {
                ApplyChain(view, names, deltas, weight);
                KeepInside(view, names, chain, deltas, weight, opening, direction, room);
            };
        }
        else
        {
            shaftDeltas.Remove(key);
        }

        // How far past the opening the tip goes (negative: short of it), and how far the opening sits
        // off the shaft, both after aiming.
        var insertion = Vector3.Dot(finalTip - basePoint, direction) - through.Length();
        var miss = Vector3.Distance(goal.Point, ClosestOnSegment(basePoint, finalTip, goal.Point));
        if (reported) Track(aim!, true, insertion * view.Scale, deltaSeconds);

        if (open)
        {
            var receiverProfile = profileOf(goal.Receiver.Name);
            var rest = receiverProfile?.Openings.GetValueOrDefault(PartKey(part));
            var restRadius = rest?.Radius is > 0 and var r ? r : 0.004f;
            var maxOpen = rest?.MaxOpen is > 0 and var m ? m : part == ContactPart.Mouth ? 0.03f : 0.012f;
            var tuning = receiverProfile?.Tuning.OpeningAmount ?? 1f;
            // The shaft's thickness in the receiver's own space.
            var shaftRadius = (profile?.Shaft?.Radius is > 0 and var radius ? radius : 0.018f) * view.Scale / goal.Receiver.Scale;
            // Lips start closed, so the jaw drops for the whole width; the other openings part on each
            // side by what their resting radius lacks.
            var needed = part == ContactPart.Mouth ? shaftRadius * 2f - 0.006f : shaftRadius - restRadius + 0.003f;
            // Only once the tip has arrived and while it's on target: a shaft hovering nearby or passing
            // beside the opening doesn't open anything.
            var arrival = Math.Clamp((insertion + OpenBefore) / (OpenBefore + OpenWhenIn), 0f, 1f) *
                          Math.Clamp(1f - miss / OpenMiss, 0f, 1f);
            var amount = Math.Clamp(needed, 0f, maxOpen) * tuning * arrival * weight;
            if (amount > 0.0005f)
            {
                // The receiver's left and right, squared to the shaft as it enters.
                var along = goal.Receiver.ToModel(view.ToWorld(basePoint + direction)) - goal.Receiver.ToModel(view.ToWorld(basePoint));
                along = along.LengthSquared() > 1e-10f ? Vector3.Normalize(along) : Vector3.UnitZ;
                var across = Vector3.UnitX - along * Vector3.Dot(Vector3.UnitX, along);
                across = across.LengthSquared() > 1e-4f ? Vector3.Normalize(across) : Vector3.UnitX;
                openings[goal.Receiver.Name] = new OpeningTarget(part, amount, across);
            }
        }
        return work;
    }

    /// <summary>How far in the shaft stays full size, how far in it may reach at all, and how small its
    /// tip gets, in the giver's own space, for this receiver's opening.</summary>
    private static (float Keep, float Limit, float TipSize) RoomInside(SkeletonView giver, SkeletonView receiver, ContactPart part)
    {
        var hips = receiver.TryModel("j_asi_a_l", out var left) && receiver.TryModel("j_asi_a_r", out var right)
            ? Vector3.Distance(left, right)
            : DefaultHipWidth;
        if (hips is < 0.06f or > 0.4f) hips = DefaultHipWidth;
        var toGiver = hips * receiver.Scale / giver.Scale;
        return part == ContactPart.Mouth
            ? (MouthKeep * toGiver, MouthLimit * toGiver, MouthTipSize)
            : (BodyKeep * toGiver, BodyLimit * toGiver, BodyTipSize);
    }

    /// <summary>
    /// Keeps the part of the shaft that went in from poking out the far side: each joint past
    /// <c>Keep</c> slides back along the shaft (never past <c>Limit</c>) and shrinks toward
    /// <c>TipSize</c>, eased in and out with the aim. The opening hides it, so nothing visible changes.
    /// </summary>
    private static void KeepInside(SkeletonView view, string[] names, List<Vector3> chain, Quaternion[] deltas,
        float weight, Vector3 opening, Vector3 direction, (float Keep, float Limit, float TipSize) room)
    {
        var eased = IkSolver.Ease(weight);
        if (eased <= 0f) return;
        // Where the joints are after the aim at this weight: the same turns ApplyChain just made.
        var points = chain.ToArray();
        for (var joint = 0; joint < deltas.Length; joint++)
        {
            var turn = Quaternion.Slerp(Quaternion.Identity, deltas[joint], eased);
            for (var after = joint + 1; after < points.Length; after++)
                points[after] = points[joint] + Vector3.Transform(points[after] - points[joint], turn);
        }
        foreach (var (index, position, size) in InsidePlacements(points, names.Length, opening, direction, room))
            view.Place(names[index], points[index] + (position - points[index]) * eased, 1f + (size - 1f) * eased);
    }

    /// <summary>Where each joint past <c>Keep</c> goes and how big it is drawn. The base never moves.</summary>
    internal static IEnumerable<(int Index, Vector3 Position, float Size)> InsidePlacements(
        Vector3[] points, int joints, Vector3 opening, Vector3 direction, (float Keep, float Limit, float TipSize) room)
    {
        var span = MathF.Max(room.Limit - room.Keep, 1e-4f);
        for (var i = 1; i < joints && i < points.Length; i++)
        {
            var depth = Vector3.Dot(points[i] - opening, direction);
            if (depth <= room.Keep) continue;
            var squeezed = MathF.Min(room.Keep + (depth - room.Keep) * InsideSqueeze, room.Limit);
            var size = 1f - (1f - room.TipSize) * Math.Clamp((depth - room.Keep) / span, 0f, 1f);
            yield return (i, points[i] - direction * (depth - squeezed), size);
        }
    }

    /// <summary>No opening to aim at this frame: eases out, and tells the player when it's their aim.</summary>
    private Action? Missed(bool reported, ShaftAim? aim, string key, float deltaSeconds, SkeletonView view, string[] names)
    {
        if (reported) Track(aim!, false, 0f, deltaSeconds);
        return Decay(key, deltaSeconds, view, names);
    }

    /// <summary>Keeps the deepest the player's own aim reached over a short window, for the panel.</summary>
    private void Track(ShaftAim aim, bool engaged, float insertion, float deltaSeconds)
    {
        if (aim != trackedAim)
        {
            trackedAim = aim;
            trackedTime = 0f;
            trackedDeepest = null;
            AimReport = null;
        }
        trackedTime += deltaSeconds;
        if (engaged) trackedDeepest = MathF.Max(trackedDeepest ?? float.MinValue, insertion);
        if (trackedTime < AimReportSeconds) return;
        AimReport = new ShaftAimReport(aim, trackedDeepest is not null, trackedDeepest ?? 0f);
        trackedTime = 0f;
        trackedDeepest = null;
    }

    private Action? Decay(string key, float deltaSeconds, SkeletonView view, string[] names)
    {
        var weight = Ease(key, 0f, deltaSeconds);
        if (weight <= 0f || !shaftDeltas.TryGetValue(key, out var deltas) || deltas.Length > names.Length)
        {
            shaftDeltas.Remove(key);
            return null;
        }
        return () => ApplyChain(view, names, deltas, weight);
    }

    private static void ApplyChain(SkeletonView view, string[] names, Quaternion[] deltas, float weight)
    {
        for (var i = 0; i < deltas.Length && i < names.Length; i++)
            view.Rotate(names[i], Quaternion.Slerp(Quaternion.Identity, deltas[i], IkSolver.Ease(weight)));
    }

    /// <summary>Rotations that, applied base first with children following, move the chain from
    /// <paramref name="start"/> onto <paramref name="goal"/>. Each turns one segment only.</summary>
    private static Quaternion[] SequentialDeltas(List<Vector3> start, Vector3[] goal)
    {
        var points = start.ToArray();
        var deltas = new Quaternion[points.Length - 1];
        for (var joint = 0; joint < deltas.Length; joint++)
        {
            var delta = IkSolver.FromTo(points[joint + 1] - points[joint], goal[joint + 1] - goal[joint]);
            deltas[joint] = delta;
            for (var after = joint + 1; after < points.Length; after++)
                points[after] = points[joint] + Vector3.Transform(points[after] - points[joint], delta);
        }
        return deltas;
    }

    /// <summary>The shaft chain this body's mesh follows: what body setup measured, otherwise the
    /// iv_ochinko chain when it's real, otherwise the iv_funyachin_phy chain some bodies use.</summary>
    private static string[] ShaftChainOf(SkeletonView view, BodyProfile? profile)
    {
        if (profile?.Shaft?.Chain is { Count: >= 2 } measured && measured.All(view.Has)) return measured.ToArray();
        foreach (var chain in BodyMeasurer.ShaftChains)
        {
            var names = chain.Where(view.Has).ToArray();
            if (names.Length >= 2 && Length(names.Select(view.Model).ToList()) >= MinShaftLength) return names;
        }
        return [];
    }

    /// <param name="chosen">The player picked this partner's opening in bone align, so a body that also
    /// has a shaft offers its vagina without a profile saying so.</param>
    private IEnumerable<(ContactPart Part, Vector3 World)> OpeningsOf(SkeletonView receiver, bool chosen)
    {
        var profile = profileOf(receiver.Name);
        // A measured body lists the openings its mesh really uses; one it doesn't is never aimed at.
        bool Allowed(ContactPart part) =>
            profile is null || profile.Openings.Count == 0 || profile.Openings.ContainsKey(PartKey(part));

        foreach (var group in MouthGroups)
        {
            var present = group.Where(receiver.Has).ToList();
            if (present.Count == 0) continue;
            if (Allowed(ContactPart.Mouth))
                yield return (ContactPart.Mouth, present.Aggregate(Vector3.Zero, (sum, name) => sum + receiver.World(name)) / present.Count);
            break;
        }
        var receiverShaft = ShaftChainOf(receiver, profile).Select(receiver.Model).ToList();
        var hasShaft = receiverShaft.Count >= 2 && Length(receiverShaft) >= MinShaftLength;
        // A body with a real shaft carries an unused vagina bone, so only a profile (or the player) can
        // say it has one.
        if (receiver.TryWorld("iv_omanko", out var vagina) && Allowed(ContactPart.Vagina) &&
            (!hasShaft || chosen || profile?.Openings.ContainsKey("vagina") == true))
            yield return (ContactPart.Vagina, vagina);
        if (receiver.TryWorld("iv_koumon", out var anus) && Allowed(ContactPart.Anus))
            yield return (ContactPart.Anus, anus);
    }

    // ---- Openings -------------------------------------------------------------------------------

    private static void Open(SkeletonView receiver, OpeningTarget target)
    {
        // Each side moves out by the full amount, so the opening's radius grows by it. Character model
        // space faces +Z with its left side toward +X, and Across keeps that sign.
        var left = target.Across * target.Amount;
        switch (target.Part)
        {
            case ContactPart.Vagina:
                receiver.Shift("iv_inshin_l", left);
                receiver.Shift("iv_inshin_r", -left);
                break;
            case ContactPart.Anus:
                receiver.Shift("iv_koumon_l", left);
                receiver.Shift("iv_koumon_r", -left);
                break;
            case ContactPart.Mouth:
                OpenJaw(receiver, target.Amount);
                break;
        }
    }

    /// <summary>Drops the jaw by up to 16°, turning whichever way parts the lips.</summary>
    private static void OpenJaw(SkeletonView receiver, float amount)
    {
        if (!receiver.TryModel("j_ago", out var pivot)) return;
        var upper = Average(receiver, UpperLips);
        var lower = Average(receiver, LowerLips);
        if (upper is null || lower is null) return;
        var angle = IkSolver.DegreesToRadians(Math.Clamp(amount / 0.03f, 0f, 1f) * 16f);
        var opens = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle);
        var closes = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -angle);
        var openGap = Vector3.Distance(upper.Value, pivot + Vector3.Transform(lower.Value - pivot, opens));
        var closeGap = Vector3.Distance(upper.Value, pivot + Vector3.Transform(lower.Value - pivot, closes));
        receiver.Rotate("j_ago", openGap >= closeGap ? opens : closes);
    }

    // ---- Hands ----------------------------------------------------------------------------------

    private Action? PlanHand(IkActor actor, SkeletonView view, string side,
        List<(IkActor Actor, SkeletonView View)> all, float deltaSeconds)
    {
        var key = $"hand|{actor.Name}|{side}";
        string Bone(string name) => $"{name}_{side}";
        var upper = Bone("j_ude_a");
        var lower = Bone("j_ude_b");
        var wrist = Bone("j_te");
        var middleBase = Bone("j_naka_a");
        if (!view.HasAll(upper, lower, wrist, middleBase))
        {
            Ease(key, 0f, deltaSeconds);
            return null;
        }

        var palmModel = (view.Model(wrist) + view.Model(middleBase)) * 0.5f;
        var palmWorld = view.ToWorld(palmModel);
        var profile = profileOf(actor.Name);
        var palmDepth = (profile?.Hands.GetValueOrDefault(side)?.PalmDepth is > 0 and var depth ? depth : 0.018f) * view.Scale;
        var gap = (profile?.Tuning.ContactGap ?? 0f) * view.Scale;

        if (!hands.TryGetValue(key, out var state)) hands[key] = state = new HandState();

        // Only the surfaces this animation's contact map says the hand grips, on the partner playing the
        // role it names. No map, or a hand the map leaves free, never grabs anything.
        var handMap = actor.Map?.Hands.GetValueOrDefault(side);

        // The closest mapped surface, within the release distance.
        (Vector3 Point, Vector3 Normal, float Gap, (string, string, string, float) Surface)? best = null;
        foreach (var (otherActor, other) in all)
        {
            if (otherActor.ObjectId == actor.ObjectId || handMap is null) continue;
            if (!actor.Map!.IsPartner(handMap.Partner, otherActor.Hash)) continue;
            var otherProfile = profileOf(other.Name);
            foreach (var (from, to, defaultRadius) in Surfaces)
            {
                if (!handMap.Surfaces.Contains(from, StringComparer.Ordinal)) continue;
                if (!other.TryWorld(from, out var a) || !other.TryWorld(to, out var b)) continue;
                var radius = (otherProfile?.SurfaceRadius.GetValueOrDefault(from) is > 0 and var measured ? measured : defaultRadius) * other.Scale;
                if (SurfaceUnder(palmWorld, a, b, radius) is not { } hit) continue;
                if (hit.Gap > HandReleaseDistance * view.Scale || hit.Gap < -radius) continue;
                if (best is null || hit.Gap < best.Value.Gap)
                    best = (hit.Point, hit.Normal, hit.Gap, (other.Name, from, to, radius));
            }
        }

        // Engage only after staying within 3 cm for a moment; hold on until past 6 cm, or until the
        // animation pulls the hand away fast; never on the floor.
        var onFloor = palmModel.Y < FloorHeight;
        var opening = best is { } now && state.LastGap is { } before && deltaSeconds > 0f
            ? (now.Gap - before) / deltaSeconds
            : 0f;
        state.LastGap = best?.Gap;
        if (onFloor || best is null)
        {
            state.Engaged = false;
            state.Waiting = 0f;
        }
        else if (state.Engaged)
        {
            var leaving = opening > HandLeaveSpeed * view.Scale && best.Value.Gap > HandEngageDistance * view.Scale;
            state.Engaged = best.Value.Gap <= HandReleaseDistance * view.Scale && !leaving;
            if (leaving) state.QuickRelease = true;
        }
        else if (best.Value.Gap <= HandEngageDistance * view.Scale)
        {
            state.Waiting += deltaSeconds;
            state.Engaged = state.Waiting >= HandHoldSeconds;
        }
        else
        {
            state.Waiting = 0f;
        }
        if (state.Engaged && best is { } held)
        {
            state.Surface = held.Surface;
            state.QuickRelease = false;
        }

        var weight = Ease(key, state.Engaged ? 1f : 0f, deltaSeconds,
            !state.Engaged && state.QuickRelease ? HandQuickEaseSeconds : EaseSeconds);
        if (weight <= 0f)
        {
            state.Surface = null;
            return null;
        }

        // While letting go, keep resting on the surface it last held so the hand eases off it.
        (Vector3 Point, Vector3 Normal)? surface = null;
        if (state.Surface is { } last &&
            all.FirstOrDefault(entry => entry.View.Name == last.Actor).View is { } holder &&
            holder.TryWorld(last.From, out var lastA) && holder.TryWorld(last.To, out var lastB) &&
            SurfaceUnder(palmWorld, lastA, lastB, last.Radius) is { } kept)
            surface = (kept.Point, kept.Normal);
        if (surface is null) return null;

        // Where the wrist must go for the palm to rest on the surface, in this character's space.
        var restingPalm = surface.Value.Point + surface.Value.Normal * (palmDepth + gap);
        var wristTarget = view.ToModel(restingPalm + (view.World(wrist) - palmWorld));
        var shoulder = view.Model(upper);
        var elbow = view.Model(lower);
        var hand = view.Model(wrist);
        // Keep the elbow folding the way the animation already has it.
        var pole = elbow + Vector3.Normalize(elbow - (shoulder + hand) * 0.5f + new Vector3(0, -0.01f, 0)) * 0.3f;
        if (IkSolver.TwoBone(shoulder, elbow, hand, wristTarget, pole) is not { } arm) return null;

        var surfaceNormalModel = Vector3.Normalize(view.ToModel(view.ToWorld(palmModel) - surface.Value.Normal) - palmModel);
        var grip = profile?.Tuning.GripStrength ?? 1f;
        return () => ApplyHand(view, side, arm, surfaceNormalModel, grip, IkSolver.Ease(weight));
    }

    /// <summary>The skin point on a bone segment under the palm: where on the segment, which way out,
    /// and how far the palm is from the skin.</summary>
    private static (Vector3 Point, Vector3 Normal, float Gap)? SurfaceUnder(Vector3 palm, Vector3 from, Vector3 to, float radius)
    {
        var onBone = ClosestOnSegment(from, to, palm);
        var offset = palm - onBone;
        var distance = offset.Length();
        if (distance < 1e-5f) return null;
        var normal = offset / distance;
        return (onBone + normal * radius, normal, distance - radius);
    }

    private static void ApplyHand(SkeletonView view, string side, (Quaternion Root, Quaternion Middle) arm,
        Vector3 towardSurface, float grip, float weight)
    {
        string Bone(string name) => $"{name}_{side}";
        var tracked = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var name in new[] { "j_ude_a", "j_ude_b", "j_te", "j_oya_a", "j_oya_b" }
                     .Concat(Fingers.SelectMany(finger => new[] { $"j_{finger}_a", $"j_{finger}_b" })))
            if (view.TryModel(Bone(name), out var position)) tracked[name] = position;

        void Turn(string bone, Quaternion delta, params string[] children)
        {
            view.Rotate(Bone(bone), delta);
            var pivot = tracked[bone];
            foreach (var child in children)
                if (tracked.TryGetValue(child, out var point))
                    tracked[child] = pivot + Vector3.Transform(point - pivot, delta);
        }

        var handChildren = new[] { "j_oya_a", "j_oya_b" }.Concat(Fingers.SelectMany(f => new[] { $"j_{f}_a", $"j_{f}_b" })).ToArray();
        Turn("j_ude_a", Quaternion.Slerp(Quaternion.Identity, arm.Root, weight), ["j_ude_b", "j_te", .. handChildren]);
        Turn("j_ude_b", Quaternion.Slerp(Quaternion.Identity, arm.Middle, weight), ["j_te", .. handChildren]);

        // Turn the palm to face the surface, a limited amount.
        if (!tracked.ContainsKey("j_naka_a") || !tracked.ContainsKey("j_oya_a")) return;
        var palmNormal = PalmNormal(tracked, side);
        if (palmNormal is null) return;
        var face = IkSolver.ClampAngle(IkSolver.FromTo(palmNormal.Value, towardSurface),
            IkSolver.DegreesToRadians(PalmTurnLimitDegrees));
        Turn("j_te", Quaternion.Slerp(Quaternion.Identity, face, weight), handChildren);

        // Close the fingers around it: each joint bends toward the palm side, within a natural grip.
        palmNormal = PalmNormal(tracked, side);
        if (palmNormal is null || grip <= 0f) return;
        foreach (var finger in Fingers.Append("oya"))
        {
            var thumb = finger == "oya";
            var baseJoint = $"j_{finger}_a";
            var midJoint = $"j_{finger}_b";
            if (!tracked.ContainsKey(baseJoint) || !tracked.ContainsKey(midJoint)) continue;
            Curl(view, side, tracked, baseJoint, midJoint, palmNormal.Value, (thumb ? 18f : 30f) * grip * weight);
            Curl(view, side, tracked, midJoint, null, palmNormal.Value, (thumb ? 18f : 40f) * grip * weight);
        }
    }

    private static void Curl(SkeletonView view, string side, Dictionary<string, Vector3> tracked,
        string joint, string? child, Vector3 palmNormal, float degrees)
    {
        if (degrees <= 0.01f) return;
        var pivot = tracked[joint];
        var along = child is not null && tracked.TryGetValue(child, out var next)
            ? next - pivot
            : pivot - tracked[joint.Replace("_b", "_a", StringComparison.Ordinal)];
        if (along.LengthSquared() < 1e-10f) return;
        var axis = Vector3.Cross(Vector3.Normalize(along), palmNormal);
        if (axis.LengthSquared() < 1e-8f) return;
        axis = Vector3.Normalize(axis);
        var angle = IkSolver.DegreesToRadians(Math.Min(degrees, 60f));
        var forward = Quaternion.CreateFromAxisAngle(axis, angle);
        var backward = Quaternion.CreateFromAxisAngle(axis, -angle);
        var tip = pivot + along;
        // Bend the way that brings the tip toward the palm side.
        var delta = Vector3.Dot(Vector3.Transform(along, forward), palmNormal) >=
                    Vector3.Dot(Vector3.Transform(along, backward), palmNormal) ? forward : backward;
        view.Rotate($"{joint}_{side}", delta);
        if (child is not null) tracked[child] = pivot + Vector3.Transform(tip - pivot, delta);
    }

    /// <summary>The direction the palm faces, from the finger and thumb directions. Model space is
    /// right-handed with the character's left toward +X, so the sign flips between hands.</summary>
    private static Vector3? PalmNormal(Dictionary<string, Vector3> tracked, string side)
    {
        var wrist = tracked["j_te"];
        var fingers = tracked["j_naka_a"] - wrist;
        var thumb = tracked["j_oya_a"] - wrist;
        if (fingers.LengthSquared() < 1e-10f || thumb.LengthSquared() < 1e-10f) return null;
        var forward = Vector3.Normalize(fingers);
        var across = thumb - forward * Vector3.Dot(thumb, forward);
        if (across.LengthSquared() < 1e-10f) return null;
        var normal = Vector3.Cross(forward, Vector3.Normalize(across));
        normal = side == "l" ? normal : -normal;

        // Cross-check against the animation itself: resting fingers almost always flex a little
        // toward the palm, so when the index finger is visibly bent, its bend decides the side.
        if (tracked.TryGetValue("j_hito_a", out var indexBase) && tracked.TryGetValue("j_hito_b", out var indexMid))
        {
            var root = Vector3.Normalize(indexBase - wrist);
            var segment = indexMid - indexBase;
            var flex = segment - root * Vector3.Dot(segment, root);
            if (flex.Length() > segment.Length() * 0.15f && Vector3.Dot(flex, normal) < 0) normal = -normal;
        }
        return normal;
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private float Ease(string key, float target, float deltaSeconds, float seconds = EaseSeconds)
    {
        var weight = IkSolver.Approach(weights.GetValueOrDefault(key), target, deltaSeconds, seconds);
        if (weight <= 0f) weights.Remove(key);
        else weights[key] = weight;
        return weight;
    }

    private void DecayAll(float deltaSeconds)
    {
        foreach (var key in weights.Keys.ToList()) Ease(key, 0f, deltaSeconds);
    }

    private static string PartKey(ContactPart part) => part switch
    {
        ContactPart.Mouth => "mouth",
        ContactPart.Vagina => "vagina",
        _ => "anus"
    };

    private static Vector3? Average(SkeletonView view, string[] names)
    {
        var present = names.Where(view.Has).ToList();
        return present.Count == 0 ? null : present.Aggregate(Vector3.Zero, (sum, name) => sum + view.Model(name)) / present.Count;
    }

    private static float Angle(Vector3 a, Vector3 b)
    {
        if (a.LengthSquared() < 1e-10f || b.LengthSquared() < 1e-10f) return 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));
    }

    private static float Length(IReadOnlyList<Vector3> points)
    {
        var length = 0f;
        for (var i = 0; i + 1 < points.Count; i++) length += Vector3.Distance(points[i], points[i + 1]);
        return length;
    }

    private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-10f) return a;
        return a + ab * Math.Clamp(Vector3.Dot(point - a, ab) / lengthSquared, 0f, 1f);
    }

    private static Vector3 ClosestOnPolyline(IReadOnlyList<Vector3> points, Vector3 target)
    {
        var closest = points[0];
        var closestDistance = float.MaxValue;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var candidate = ClosestOnSegment(points[i], points[i + 1], target);
            var distance = Vector3.DistanceSquared(candidate, target);
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closest = candidate;
        }
        return closest;
    }

    private static string[] BuildWanted()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in BodyMeasurer.ShaftChains) names.UnionWith(chain);
        foreach (var group in MouthGroups) names.UnionWith(group);
        names.UnionWith(["iv_omanko", "iv_inshin_l", "iv_inshin_r", "iv_koumon", "iv_koumon_l", "iv_koumon_r", "j_ago"]);
        foreach (var (from, to, _) in Surfaces) { names.Add(from); names.Add(to); }
        foreach (var side in new[] { "l", "r" })
        {
            names.UnionWith([$"j_ude_a_{side}", $"j_ude_b_{side}", $"j_te_{side}", $"j_oya_a_{side}", $"j_oya_b_{side}"]);
            foreach (var finger in Fingers) names.UnionWith([$"j_{finger}_a_{side}", $"j_{finger}_b_{side}"]);
        }
        return names.ToArray();
    }

    public void Dispose()
    {
        renderHook?.Disable();
        renderHook?.Dispose();
    }
}

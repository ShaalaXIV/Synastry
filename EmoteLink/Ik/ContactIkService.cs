using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EmoteLink.Ik;

/// <summary>A character near you this frame, gathered on the framework thread.</summary>
/// <summary>A character near you this frame, gathered on the framework thread, with the animation
/// file it's playing (SHA-256) and that file's contact map when one exists.</summary>
internal sealed record IkActor(nint Address, ulong ObjectId, string Name, bool IsLocal, bool Looping,
    string? Hash = null, ContactMap? Map = null);

/// <summary>What the player has switched on, read once per frame.</summary>
internal sealed record IkSettings(bool Shaft, bool Openings, bool Hands, ContactPreference Preference);

/// <summary>
/// Bends bones to make couple animations meet: the shaft aims through the chosen opening, the
/// opening widens a little for it, and hands that hover near a partner's body settle onto it and
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
    // A mouth this close always wins, whatever the preference.
    private const float MouthContactDistance = 0.08f;
    // A shaft shorter than this is the collapsed set every YAS body carries.
    private const float MinShaftLength = 0.05f;
    // Hands, measured on 1,142 hands in 262 scenes (sex-animation-corpus): a hand an animator put on
    // a partner sits within 3 cm of the skin for most of the loop, while hands meant for a table, the
    // floor or the air hover 3-15 cm away. Engaging at 3 cm after a quarter second, and letting go
    // past 6 cm, catches 86.5% of intended contact and grabs 1% of the rest (9 cm grabbed 25.5%).
    private const float HandEngageDistance = 0.03f;   // world yalms to skin
    private const float HandReleaseDistance = 0.06f;
    private const float HandHoldSeconds = 0.27f;      // eight animation frames at 30 fps
    // A palm this low (model space, above the feet) is propping on the floor, not on a partner.
    private const float FloorHeight = 0.12f;
    private const float ShaftJointLimitDegrees = 14f;
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
        public (string Actor, string From, string To, float Radius)? Surface;
    }
    private volatile IReadOnlyList<IkActor> actors = [];
    private volatile IkSettings settings = new(false, false, false, ContactPreference.Closest);
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
        if (list.Count == 0 || (!current.Shaft && !current.Openings && !current.Hands))
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
        if (current.Shaft || current.Openings)
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
        if (current.Openings)
            foreach (var (_, view) in views)
                if (openings.TryGetValue(view.Name, out var opening))
                    Open(view, opening);
        foreach (var work in handWork) work();
    }

    // ---- Shaft ----------------------------------------------------------------------------------

    private sealed record OpeningTarget(ContactPart Part, float Amount);

    private Action? PlanShaft(IkActor giver, SkeletonView view, List<(IkActor Actor, SkeletonView View)> all,
        IkSettings current, float deltaSeconds)
    {
        var key = "shaft|" + giver.Name;
        var profile = profileOf(giver.Name);
        var names = ShaftChainOf(view, profile);
        var joints = names.Select(view.Model).ToList();
        if (joints.Count < 2 || Length(joints) < MinShaftLength) return Decay(key, deltaSeconds, view, names);
        // Only an animation whose contact map says where the shaft goes is bent.
        if (giver.Map?.Shaft is not { } shaftMap) return Decay(key, deltaSeconds, view, names);
        var mappedPart = shaftMap.Opening switch
        {
            "mouth" => ContactPart.Mouth,
            "anus" => ContactPart.Anus,
            _ => ContactPart.Vagina
        };

        var lastDirection = Vector3.Normalize(joints[^1] - joints[^2]);
        var tipExtra = profile?.Shaft?.TipBeyondLastBone is > 0 and var extra ? extra : Vector3.Distance(joints[^1], joints[^2]);
        var tip = joints[^1] + lastDirection * tipExtra;

        // Every opening in reach on everyone else, in this giver's own space.
        (SkeletonView Receiver, ContactPart Part, Vector3 Point, float Distance)? mouth = null, chosen = null, preferred = null;
        var wanted = current.Preference switch
        {
            ContactPreference.Mouth => ContactPart.Mouth,
            ContactPreference.Vagina => ContactPart.Vagina,
            ContactPreference.Anus => ContactPart.Anus,
            _ => (ContactPart?)null
        };
        foreach (var (receiverActor, receiver) in all)
        {
            if (receiverActor.ObjectId == giver.ObjectId) continue;
            if (!giver.Map.IsPartner(shaftMap.Partner, receiverActor.Hash)) continue;
            foreach (var (part, world) in OpeningsOf(receiver))
            {
                if (part != mappedPart) continue;
                var point = view.ToModel(world);
                var distance = MathF.Min(Vector3.Distance(point, tip), Vector3.Distance(point, ClosestOnPolyline(joints, point)));
                if (distance > ShaftEngageDistance) continue;
                var candidate = (receiver, part, point, distance);
                if (part == ContactPart.Mouth && distance <= MouthContactDistance &&
                    (mouth is null || distance < mouth.Value.Distance)) mouth = candidate;
                // The local player's preference applies to pairs it is part of.
                if (wanted == part && (giver.IsLocal || receiverActor.IsLocal) &&
                    (preferred is null || distance < preferred.Value.Distance)) preferred = candidate;
                if (chosen is null || distance < chosen.Value.Distance) chosen = candidate;
            }
        }
        var target = mouth ?? preferred ?? chosen;
        if (target is not { } aim) return Decay(key, deltaSeconds, view, names);

        // Aim the shaft through the opening without changing how deep it is: the tip goes on the
        // line from the base through the opening, at its current reach.
        var basePoint = joints[0];
        var through = aim.Point - basePoint;
        if (through.LengthSquared() < 1e-8f) return Decay(key, deltaSeconds, view, names);
        var tipTarget = basePoint + Vector3.Normalize(through) * Vector3.Distance(basePoint, tip);

        var chain = new List<Vector3>(joints) { tip };
        var solved = IkSolver.Chain(chain, tipTarget, ShaftJointLimitDegrees);
        if (solved is null) return Decay(key, deltaSeconds, view, names);
        var finalPoints = SimulateChain(chain, solved);
        var deltas = SequentialDeltas(chain, finalPoints);

        var weight = Ease(key, 1f, deltaSeconds);
        shaftDeltas[key] = deltas;

        if (settings.Openings)
        {
            var shaftRadius = profile?.Shaft?.Radius is > 0 and var radius ? radius : 0.018f;
            var receiverProfile = profileOf(aim.Receiver.Name);
            var openingName = PartKey(aim.Part);
            var rest = receiverProfile?.Openings.GetValueOrDefault(openingName);
            var restRadius = rest?.Radius is > 0 and var r ? r : aim.Part == ContactPart.Mouth ? 0.012f : 0.004f;
            var maxOpen = rest?.MaxOpen is > 0 and var m ? m : aim.Part == ContactPart.Mouth ? 0.03f : 0.012f;
            var tuning = receiverProfile?.Tuning.OpeningAmount ?? 1f;
            // Only when the tip has actually arrived: a shaft hovering nearby doesn't open anything.
            var arrival = Math.Clamp(1f - aim.Distance / 0.04f, 0f, 1f);
            var amount = Math.Clamp(shaftRadius - restRadius + 0.004f, 0f, maxOpen) * tuning * arrival * weight;
            if (amount > 0.0005f) openings[aim.Receiver.Name] = new OpeningTarget(aim.Part, amount);
        }

        return () => ApplyChain(view, names, deltas, weight);
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

    /// <summary>The final joint positions after the solver's rotations, applied base first.</summary>
    private static Vector3[] SimulateChain(List<Vector3> chain, Quaternion[] rotations)
    {
        var points = chain.ToArray();
        for (var joint = 0; joint < rotations.Length; joint++)
            for (var after = joint + 1; after < points.Length; after++)
                points[after] = points[joint] + Vector3.Transform(points[after] - points[joint], rotations[joint]);
        return points;
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

    private IEnumerable<(ContactPart Part, Vector3 World)> OpeningsOf(SkeletonView receiver)
    {
        var profile = profileOf(receiver.Name);
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
        // A body with a real shaft carries an unused vagina bone, so only a profile can say it has one.
        if (receiver.TryWorld("iv_omanko", out var vagina) && Allowed(ContactPart.Vagina) &&
            (!hasShaft || profile?.Openings.ContainsKey("vagina") == true))
            yield return (ContactPart.Vagina, vagina);
        if (receiver.TryWorld("iv_koumon", out var anus) && Allowed(ContactPart.Anus))
            yield return (ContactPart.Anus, anus);
    }

    // ---- Openings -------------------------------------------------------------------------------

    private static void Open(SkeletonView receiver, OpeningTarget target)
    {
        // Character model space faces +Z with its left side toward +X.
        var left = Vector3.UnitX * (target.Amount * 0.5f);
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

        // Engage only after staying within 3 cm for a moment; hold on until past 6 cm; never on the floor.
        var onFloor = palmModel.Y < FloorHeight;
        if (onFloor || best is null)
        {
            state.Engaged = false;
            state.Waiting = 0f;
        }
        else if (state.Engaged)
        {
            state.Engaged = best.Value.Gap <= HandReleaseDistance * view.Scale;
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
        if (state.Engaged && best is { } held) state.Surface = held.Surface;

        var weight = Ease(key, state.Engaged ? 1f : 0f, deltaSeconds);
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

    private float Ease(string key, float target, float deltaSeconds)
    {
        var weight = IkSolver.Approach(weights.GetValueOrDefault(key), target, deltaSeconds, EaseSeconds);
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

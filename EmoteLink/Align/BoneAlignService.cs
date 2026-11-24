using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace EmoteLink.Align;

/// <summary>
/// Lines up one of your body parts with your partner's by moving your drawn offset. Bones and the real
/// character are never moved.
///
/// Optionally turns you first, restarts both loops, watches both parts for a moment and uses the
/// closest one, since many loops only bring the parts together partway through. Never picks a facing
/// or a part by itself. Framework thread only.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
internal sealed class BoneAlignService(
    IObjectTable objects,
    HeelsOffset heels,
    Func<ulong, ICharacter?> find,
    Func<ICharacter, string> nameOf,
    Action<ICharacter, ICharacter> restartLoops)
{
    private enum Phase { Idle, Waiting, Settling, Sampling }

    // Long enough for the restarted loops to take effect before measuring.
    private const long SettleMs = 300;

    // Couple animations make about 1.4 strokes a second (sex-animation-corpus), so this sees the
    // deepest moment of at least two.
    private const long SampleMs = 2000;

    // An offset change never takes longer than this to land.
    private const long LandMs = 2000;

    // Past this the drawn model visibly drifts from the real character.
    private const float MaximumDistance = 1.0f;

    private const float TouchingDistance = 0.005f;
    private const float OneDegree = MathF.PI / 180f;

    private Phase phase = Phase.Idle;
    private long phaseStartedAt;

    // Kept from the start, so a change of pose, offset or partner partway through cancels cleanly.
    private AlignRequest request = new(BodyPart.Penis, BodyPart.Vagina, 0f, null, null, AlignOrigin.Manual);
    private (byte Mode, byte Param) loopAtStart;
    private byte variantAtStart;
    private int generationAtStart;
    private ulong partnerId;

    // A turn made at the start is taken back if the align then fails.
    private OffsetLayer boneAtStart;
    private bool turned;
    private string turnNote = "";

    // The loop the bone layer was set in. Simple Heels drops its offset when the loop changes.
    private (byte Mode, byte Param) offsetLoop;

    // The closest moment seen.
    private bool sampled;
    private float bestDistance;
    private Vector3 bestMine;
    private Vector3 bestTheirs;
    private float bestMyYaw;
    private float bestTheirYaw;

    public string Status { get; private set; } = "";
    public bool IsAligning => phase != Phase.Idle;

    /// <summary>An align moved you, or a measure finished.</summary>
    public event Action<AlignRequest, AlignResult, ICharacter>? Succeeded;

    private bool Measuring => request.Mode == AlignMode.Measure;

    /// <summary>Simple Heels is there to show an offset.</summary>
    public bool Available => heels.Available;

    /// <summary>Why bone align can't run right now, or empty.</summary>
    public string Problem =>
        !Available ? "Bone align needs Simple Heels."
        : objects.LocalPlayer is not { } local || !DrawnModel.IsLooping(local) ? "Bone align needs a looping emote or sit/doze."
        : "";

    /// <summary>A turn or reset can be made: nothing is being measured and the last change has landed.</summary>
    public bool CanTurn => !IsAligning && heels.Settled && Problem.Length == 0;

    public bool CanReset => !IsAligning && heels.Settled && heels.Applied && heels.Bone != OffsetLayer.None;

    public void Start(AlignRequest alignRequest, ICharacter partner)
    {
        if (IsAligning || objects.LocalPlayer is not { } local) return;
        request = alignRequest;
        turned = false;
        if (Problem is { Length: > 0 } problem)
        {
            Say(problem);
            return;
        }
        if (!BodyParts.TryLocate(local, request.Self, out _))
        {
            Say($"Couldn't find your {BodyParts.Words(request.Self)} bones.");
            return;
        }
        if (!BodyParts.TryLocate(partner, request.Partner, out _))
        {
            Say($"Couldn't find {nameOf(partner)}'s {BodyParts.Words(request.Partner)} bones. Their mods may still be loading.");
            return;
        }

        partnerId = partner.GameObjectId;
        loopAtStart = PoseSignature.Loop(local);
        variantAtStart = PoseSignature.Variant(local);
        generationAtStart = heels.Generation;
        sampled = false;
        bestDistance = float.MaxValue;
        turnNote = "";
        Enter(Phase.Waiting);
        Say($"{(Measuring ? "Measuring" : "Lining up")} your {BodyParts.Words(request.Self)} with " +
            $"{nameOf(partner)}'s {BodyParts.Words(request.Partner)}...");
    }

    /// <summary>Turns you around your own part, so a contact already made stays roughly in place.</summary>
    public void Turn(BodyPart self, float degrees)
    {
        if (!CanTurn || objects.LocalPlayer is not { } local) return;
        AdoptOffset(local);
        AddTurn(local, BodyParts.TryLocate(local, self, out var part) ? part : local.Position, degrees * OneDegree);
    }

    /// <summary>Back to how you were drawn before bone align.</summary>
    public void Reset()
    {
        if (!CanReset || objects.LocalPlayer is not { } local) return;
        if (OffsetIsStale(local)) heels.Reset();
        else heels.SetBone(OffsetLayer.None);
        Status = "Back to how you were before bone align.";
    }

    /// <summary>Shows a message that isn't part of an align.</summary>
    public void Say(string text) => Status = request.Origin == AlignOrigin.Manual ? text : $"From memory: {text}";

    /// <summary>Shows why a button press couldn't start anything.</summary>
    public void Refuse(string text) => Status = text;

    public void Tick()
    {
        var local = objects.LocalPlayer;
        if (phase == Phase.Idle)
        {
            // Simple Heels dropped it with the loop it was made in; kept, it would be sent again into
            // the next one by whatever moves you then.
            if (local is not null && OffsetIsStale(local)) heels.Reset();
            return;
        }

        if (local is null || PoseSignature.Loop(local) != loopAtStart || PoseSignature.Variant(local) != variantAtStart ||
            heels.Generation != generationAtStart)
        {
            Fail("Bone align stopped: your pose or offset changed.");
            return;
        }
        if (find(partnerId) is not { } partner)
        {
            Fail("Bone align stopped: your partner isn't nearby any more.");
            return;
        }

        var elapsed = Environment.TickCount64 - phaseStartedAt;
        switch (phase)
        {
            case Phase.Waiting:
                // Measuring from an offset that hasn't landed yet would count it twice.
                if (!heels.Settled && elapsed < LandMs) return;
                Begin(local, partner);
                break;
            case Phase.Settling:
                if (elapsed < SettleMs || (!heels.Settled && elapsed < LandMs)) return;
                Enter(Phase.Sampling);
                break;
            case Phase.Sampling:
                Sample(local, partner);
                if (elapsed >= SampleMs) Finish(local, partner);
                break;
        }
    }

    private void Enter(Phase next)
    {
        phase = next;
        phaseStartedAt = Environment.TickCount64;
    }

    private void Begin(ICharacter local, ICharacter partner)
    {
        AdoptOffset(local);
        generationAtStart = heels.Generation;
        boneAtStart = heels.Bone;
        if (!Measuring && request.RelativeYaw is { } relativeYaw) TurnToFace(local, partner, relativeYaw);
        // The second partner's check only reads; restarting both animations for it would be jarring.
        if (request.Origin != AlignOrigin.Check) restartLoops(local, partner);
        Enter(Phase.Settling);
    }

    private void Sample(ICharacter local, ICharacter partner)
    {
        // A redraw can take either skeleton away for a few frames; those are skipped.
        if (!BodyParts.TryLocate(local, request.Self, out var mine) ||
            !BodyParts.TryLocate(partner, request.Partner, out var theirs) ||
            !DrawnModel.TryGetRoot(local, out _, out var myYaw) ||
            !DrawnModel.TryGetRoot(partner, out _, out var theirYaw)) return;
        var distance = Vector3.Distance(theirs, mine);
        if (!(distance < bestDistance)) return;
        sampled = true;
        bestDistance = distance;
        bestMine = mine;
        bestTheirs = theirs;
        bestMyYaw = myYaw;
        bestTheirYaw = theirYaw;
    }

    private void Finish(ICharacter local, ICharacter partner)
    {
        if (!sampled)
        {
            Fail("Couldn't read the bones while measuring.");
            return;
        }
        if (bestDistance > MaximumDistance)
        {
            Fail($"Too far apart ({bestDistance:0.00}y, at most {MaximumDistance:0.0}y). Move closer first.");
            return;
        }

        phase = Phase.Idle;
        turned = false;
        var name = nameOf(partner);
        var parts = $"your {BodyParts.Words(request.Self)} with {name}'s {BodyParts.Words(request.Partner)}";
        var relativeYaw = DrawnModel.Wrap(bestMyYaw - bestTheirYaw);
        var intoPartnerFrame = Quaternion.Inverse(DrawnModel.YawRotation(bestTheirYaw));

        if (Measuring)
        {
            var measured = Vector3.Transform(bestMine - bestTheirs, intoPartnerFrame);
            if (request.Origin == AlignOrigin.Manual)
                Say($"Remembered {parts} ({measured.Length():0.00}y apart, {FacingText(relativeYaw)}).");
            Succeeded?.Invoke(request, new AlignResult(relativeYaw, measured), partner);
            return;
        }

        var target = request.ContactOffset is { } contact
            ? bestTheirs + Vector3.Transform(contact, DrawnModel.YawRotation(bestTheirYaw))
            : StopShort(local, partner);

        // The drawn model sits at the real position, facing its drawn yaw, with the offset in that
        // frame. So moving the part from B to T adds R(yaw)^-1 (T - B) to the offset. The bones were
        // measured with the current offset applied, so it is added to.
        heels.AddBone(Vector3.Transform(target - bestMine, Quaternion.Inverse(DrawnModel.YawRotation(bestMyYaw))));
        offsetLoop = PoseSignature.Loop(local);
        Say($"Lined up {parts} (moved {Vector3.Distance(target, bestMine):0.00}y{turnNote}).");
        Succeeded?.Invoke(request, new AlignResult(relativeYaw, Vector3.Transform(target - bestTheirs, intoPartnerFrame)), partner);
    }

    /// <summary>The gap short of your partner's part, along the line the parts approach on. Parts that
    /// already touch back off level, away from your partner.</summary>
    private Vector3 StopShort(ICharacter local, ICharacter partner)
    {
        if (request.Gap <= 0f) return bestTheirs;
        Vector3 approach;
        if (bestDistance > TouchingDistance)
        {
            approach = (bestTheirs - bestMine) / bestDistance;
        }
        else
        {
            var toward = partner.Position - local.Position;
            toward.Y = 0f;
            if (toward.LengthSquared() < 1e-6f) return bestTheirs;
            approach = Vector3.Normalize(toward);
        }
        return bestTheirs - approach * request.Gap;
    }

    /// <summary>Turns you on the spot to the wanted facing relative to your partner.</summary>
    private void TurnToFace(ICharacter local, ICharacter partner, float relativeYaw)
    {
        if (!DrawnModel.TryGetRoot(local, out _, out var mine) || !DrawnModel.TryGetRoot(partner, out _, out var theirs)) return;
        var turn = DrawnModel.Wrap(theirs + relativeYaw - mine);
        if (MathF.Abs(turn) < OneDegree) return;
        AddTurn(local, local.Position, turn);
        turned = true;
        turnNote = $", turned {turn / OneDegree:0}°";
    }

    /// <summary>Adds a turn, with the move that keeps <paramref name="pivot"/> where it is: the offset
    /// is in the drawn frame, which the turn changes.</summary>
    private void AddTurn(ICharacter local, Vector3 pivot, float turn)
    {
        if (!DrawnModel.TryGetRoot(local, out _, out var facing)) return;
        var arm = pivot - local.Position;
        heels.AddBone(
            Vector3.Transform(arm, Quaternion.Inverse(DrawnModel.YawRotation(facing + turn))) -
            Vector3.Transform(arm, Quaternion.Inverse(DrawnModel.YawRotation(facing))),
            turn);
        offsetLoop = PoseSignature.Loop(local);
    }

    /// <summary>Starts from what is really drawn: an offset changed in Simple Heels' own window is
    /// kept, and one from another loop, which Simple Heels has already dropped, is forgotten.</summary>
    private void AdoptOffset(ICharacter local)
    {
        if (!heels.Applied) return;
        if (OffsetIsStale(local)) heels.Reset();
        else heels.Rebase();
    }

    private bool OffsetIsStale(ICharacter local) =>
        heels.Bone != OffsetLayer.None && PoseSignature.Loop(local) != offsetLoop;

    /// <summary>Ends without moving you: a turn made at the start is taken back. Not once you've left
    /// the loop: Simple Heels has dropped it, and an offset sent while standing stays on you.</summary>
    private void Fail(string reason)
    {
        phase = Phase.Idle;
        if (turned && heels.Generation == generationAtStart && objects.LocalPlayer is { } local &&
            PoseSignature.Loop(local) == loopAtStart) heels.SetBone(boneAtStart);
        turned = false;
        Say(reason);
    }

    /// <summary>How you face relative to your partner, for status lines and the panel.</summary>
    public static string FacingText(float relativeYaw)
    {
        var degrees = MathF.Round(relativeYaw / OneDegree);
        if (degrees <= -180f) degrees += 360f;
        return degrees == 0f ? "facing the same way" : $"{degrees:0}° from their facing";
    }
}

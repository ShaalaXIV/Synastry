using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace EmoteLink.Align;

/// <summary>
/// Remembers each bone align you make, and when a remembered animation starts again with someone in
/// your room, repeats it once both of you have settled into the loop. Each play is handled once.
///
/// Both players may remember the same animation, so the one whose name sorts first lines up, and the
/// other only moves if that didn't bring the parts together.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
internal sealed class AutoAlign
{
    // Room for an intro, a redraw on each side, and the partner starting later.
    private const long GiveUpMs = 15000;

    // Long enough for the first partner to measure and move, and for their offset to arrive here.
    private const long YieldMs = 4000;

    // Closer to the remembered contact than this is already lined up.
    private const float AlignedDistance = 0.05f;

    private const long TickMs = 200;

    private readonly Plugin plugin;
    private readonly BoneAlignService align;
    private readonly AlignmentMemory memory;
    private readonly AnimationReadiness readiness;

    private PlayContext? handled;
    private bool done;
    private bool partsFilled;
    private long yieldUntil;
    private long nextTick;

    public AutoAlign(Plugin plugin, BoneAlignService align, AlignmentMemory memory, AnimationReadiness readiness)
    {
        this.plugin = plugin;
        this.align = align;
        this.memory = memory;
        this.readiness = readiness;
        align.Succeeded += OnSucceeded;
    }

    /// <summary>What is remembered for the playing animation with the partner chosen in the panel.</summary>
    public AlignmentEntry? Known { get; private set; }

    /// <summary>What a pending line-up is waiting for, or empty.</summary>
    public string Waiting { get; private set; } = "";

    /// <summary>You started lining up yourself, so nothing happens by itself for this play.</summary>
    public void StandDown() => Stop("");

    /// <summary>You picked parts yourself, so this play leaves them alone.</summary>
    public void KeepParts() => partsFilled = true;

    /// <summary>Drops what is remembered for the playing animation.</summary>
    public void Forget()
    {
        if (Known is not { } known) return;
        memory.Forget(known.Key);
        Known = null;
        StandDown();
    }

    /// <param name="playing">The animation Synastry started is still the one you're in.</param>
    public void Tick(ICharacter? local, bool playing)
    {
        var play = plugin.CurrentPlay;
        if (!ReferenceEquals(play, handled))
        {
            handled = play;
            done = false;
            partsFilled = false;
            yieldUntil = 0;
            nextTick = 0;
            Waiting = "";
            readiness.Reset();
            // Another mod or option on the same timeline looks the same to the resolver.
            plugin.ForgetPlayingAnimations();
        }
        if (play is null || local is null)
        {
            Known = null;
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextTick) return;
        nextTick = now + TickMs;

        // Everyone is looked at every time, so each settling time counts from when it really started.
        var ownReady = readiness.IsReady(local);
        var partners = plugin.RoomPartners(local);
        var settled = partners.Where(readiness.IsReady).ToList();
        var chosen = plugin.IsBoneAlignOpen ? plugin.SelectedBoneAlignPartner(local) : null;
        if (chosen is not null && partners.All(partner => partner.Address != chosen.Address)) readiness.IsReady(chosen);

        Known = playing && chosen is not null && !memory.IsEmpty
            ? Remembered(local, chosen) ?? memory.TryGet(AlignmentMemory.ModKey(play))
            : null;
        if (!partsFilled && playing && chosen is not null)
            partsFilled = Known is { } known
                ? Fill(known)
                : plugin.AnimationHashOf(chosen) is not null && plugin.SuggestBoneAlignParts(local, chosen);

        if (!done) TickPending(play, local, playing && ownReady, partners, settled, now);
    }

    private void TickPending(PlayContext play, ICharacter local, bool ready, IReadOnlyList<ICharacter> partners,
        IReadOnlyList<ICharacter> settled, long now)
    {
        if (!plugin.AutoAlignFromMemory || plugin.HasPartnerPlace || !align.Available)
        {
            Stop("");
            return;
        }
        if (now - play.StartedAt > (yieldUntil == 0 ? GiveUpMs : GiveUpMs + YieldMs))
        {
            Stop(Waiting.Length > 0 ? "Not lined up from memory. " + Waiting : "");
            return;
        }
        if (align.IsAligning) return;
        if (Find(play, local, partners) is not var (partner, entry))
        {
            Waiting = "";
            return;
        }

        if (!partsFilled) partsFilled = Fill(entry);
        var name = plugin.BoneAlignName(partner);
        if (!ready || plugin.IsSettlingIn)
        {
            Waiting = "Waiting for your animation to start.";
            return;
        }
        if (!plugin.IsPosing(partner) || settled.All(candidate => candidate.Address != partner.Address))
        {
            Waiting = $"Waiting for {name}'s animation to start.";
            return;
        }
        if (plugin.IsPlacing)
        {
            Waiting = "Waiting for your saved place.";
            return;
        }

        var first = plugin.LinesUpFirst(partner);
        if (!first && yieldUntil == 0)
        {
            yieldUntil = now + YieldMs;
            Waiting = $"{name} lines up first.";
        }
        if (now < yieldUntil) return;
        Stop("");
        var request = AlignRequest.FromMemory(entry);
        align.Start(first ? request : request with { Origin = AlignOrigin.Check, Mode = AlignMode.Measure }, partner);
    }

    /// <summary>The room partner whose animation is remembered: the one playing the very file first,
    /// else the nearest when the mod itself is.</summary>
    private (ICharacter Partner, AlignmentEntry Entry)? Find(PlayContext play, ICharacter local, IReadOnlyList<ICharacter> partners)
    {
        if (memory.IsEmpty || partners.Count == 0) return null;
        foreach (var partner in partners)
            if (Remembered(local, partner) is { } entry)
                return (partner, entry);
        return memory.TryGet(AlignmentMemory.ModKey(play)) is { } byMod ? (partners[0], byMod) : null;
    }

    private AlignmentEntry? Remembered(ICharacter local, ICharacter partner) =>
        plugin.AnimationHashOf(local) is { } own && plugin.AnimationHashOf(partner) is { } theirs
            ? memory.TryGet(AlignmentMemory.FileKey(own, theirs))
            : null;

    private bool Fill(AlignmentEntry entry)
    {
        plugin.SetBoneAlignParts(entry.Self, entry.Partner);
        return true;
    }

    private void Stop(string waiting)
    {
        done = true;
        Waiting = waiting;
    }

    private void OnSucceeded(AlignRequest request, AlignResult result, ICharacter partner)
    {
        switch (request.Origin)
        {
            case AlignOrigin.Manual:
                Record(request, result, partner);
                break;
            case AlignOrigin.Check when request.ContactOffset is { } contact:
                if (Vector3.Distance(contact, result.ContactOffset) <= AlignedDistance)
                    align.Say($"{plugin.BoneAlignName(partner)} is already lined up with you.");
                else
                    align.Start(request with { Origin = AlignOrigin.Memory, Mode = AlignMode.Align }, partner);
                break;
        }
    }

    private void Record(AlignRequest request, AlignResult result, ICharacter partner)
    {
        if (plugin.RememberablePlay is not { } play || plugin.LocalCharacter is not { } local) return;
        var own = plugin.AnimationHashOf(local);
        var theirs = plugin.AnimationHashOf(partner);
        var entry = new AlignmentEntry
        {
            Key = own is not null && theirs is not null ? AlignmentMemory.FileKey(own, theirs) : AlignmentMemory.ModKey(play),
            ModDirectory = play.Directory,
            ModName = play.ModName,
            Label = plugin.RoleName(play.Directory, play.Trigger) is { Length: > 0 } role ? role : play.Animation,
            Self = request.Self,
            Partner = request.Partner,
            Gap = request.Gap,
            RelativeYaw = result.RelativeYaw,
            ContactOffset = StoredVector.From(result.ContactOffset),
            PartnerHash = theirs,
            Updated = DateTime.UtcNow,
        };
        memory.Record(entry);
        Known = entry;
        partsFilled = true;
    }
}

using System.Numerics;
using EmoteLink.Camera;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace EmoteLink;

/// <summary>
/// Keeps your character still while a Synastry animation plays, so a stray movement key can't end it.
/// Unlocking only frees the movement keys; moving then ends the animation as usual.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
public sealed unsafe class AnimationLock(MovementSuppression suppression)
{
    private const string Owner = "animation-lock";
    private const float StillSeconds = 0.25f;

    // A change of pose this soon after a play is that play arriving (pose steps, taking a seat), not you
    // starting something else.
    private const long ArrivalMs = 8000;
    private const long EndedMs = 300;

    private long armedAt;
    private long notPlayingSince;
    private string signature = "";
    private Vector3 observedPosition;
    private float stillFor;
    private bool hinted;

    public bool Locked { get; private set; }

    /// <summary>A Synastry animation really started: lock once it has settled.</summary>
    public void Arm()
    {
        Release();
        armedAt = Environment.TickCount64;
        notPlayingSince = 0;
    }

    /// <summary>Synastry is about to stand you up or change your pose. The lock comes back by itself.</summary>
    public void LetGo()
    {
        if (!Locked) return;
        Locked = false;
        suppression.Release(Owner);
        armedAt = Environment.TickCount64;
    }

    /// <summary>The animation is over, or the player asked to move. Nothing locks until the next one.</summary>
    public void Release()
    {
        armedAt = 0;
        notPlayingSince = 0;
        stillFor = 0;
        if (!Locked) return;
        Locked = false;
        suppression.Release(Owner);
    }

    /// <param name="position">Your position, or null while you aren't in the world.</param>
    /// <param name="playing">The animation is still going.</param>
    /// <param name="busy">Synastry itself is moving or posing you, so the lock waits.</param>
    /// <param name="currentSignature">Which emote or pose you're in.</param>
    public void Tick(Vector3? position, bool playing, bool busy, string currentSignature, float seconds)
    {
        if (position is not { } here)
        {
            Release();
            return;
        }
        stillFor = Vector3.DistanceSquared(here, observedPosition) < 0.00000001f ? stillFor + seconds : 0;
        observedPosition = here;
        if (armedAt == 0 && !Locked) return;

        var now = Environment.TickCount64;
        if (busy)
        {
            LetGo();
            return;
        }
        if (!playing)
        {
            // Not yet started, or over. Give a seat time to be taken and a pose to be stepped to.
            if (Locked)
            {
                if (notPlayingSince == 0) notPlayingSince = now;
                if (now - notPlayingSince > EndedMs) Release();
            }
            else if (now - armedAt > ArrivalMs)
            {
                Release();
            }
            return;
        }
        notPlayingSince = 0;

        if (!Locked)
        {
            if (stillFor < StillSeconds || InputManager.IsAutoRunning() || !suppression.Hold(Owner)) return;
            Locked = true;
            hinted = false;
            signature = currentSignature;
            return;
        }
        if (signature == currentSignature) return;
        if (now - armedAt < ArrivalMs) signature = currentSignature;
        else Release();   // You started a pose of your own over Synastry's.
    }

    /// <summary>True once, when a movement key is pressed while locked, so the player can be told how to
    /// get free.</summary>
    public bool TakeHint(bool keysInUse)
    {
        if (!Locked || hinted || keysInUse || !suppression.IsMovementKeyDown()) return false;
        hinted = true;
        return true;
    }
}

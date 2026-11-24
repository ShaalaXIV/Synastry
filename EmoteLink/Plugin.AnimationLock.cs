using Dalamud.Game.ClientState.Conditions;
using EmoteLink.Align;

namespace EmoteLink;

/// <summary>The animation lock: you stay still while a Synastry animation plays, until you unlock.</summary>
public sealed unsafe partial class Plugin
{
    private AnimationLock? animationLock;
    private PlayContext? lockedPlay;
    private bool movementUnlocked;

    /// <summary>Your movement keys are locked because an animation is playing.</summary>
    public bool MovementLocked => animationLock?.Locked == true;

    public bool LockMovementEnabled => configuration.LockMovementWhilePlaying;
    public bool LockMovementAvailable => animationLock is not null;

    public void SetLockMovement(bool enabled)
    {
        configuration.LockMovementWhilePlaying = enabled;
        configuration.Save(PluginInterface);
        if (!enabled) animationLock?.Release();
    }

    /// <summary>Frees the movement keys until the next animation; moving then ends this one.</summary>
    public void UnlockMovement()
    {
        if (!MovementLocked) return;
        movementUnlocked = true;
        animationLock!.Release();
        Status = "Unlocked. Moving ends the animation.";
    }

    private void TickAnimationLock(float seconds)
    {
        if (animationLock is not { } hold) return;
        try
        {
            TickAnimationLock(hold, seconds);
        }
        catch (Exception exception)
        {
            // Never leave you stuck, nor take the rest of the update down with it every frame.
            Log.Error(exception, "The animation lock failed and was turned off.");
            animationLock = null;
            hold.Release();
        }
    }

    private void TickAnimationLock(AnimationLock hold, float seconds)
    {
        if (!ReferenceEquals(CurrentPlay, lockedPlay))
        {
            lockedPlay = CurrentPlay;
            movementUnlocked = false;
            if (lockedPlay is null) hold.Release();
            else if (configuration.LockMovementWhilePlaying && !CannotLockMovement()) hold.Arm();
        }
        if (!configuration.LockMovementWhilePlaying || movementUnlocked || CurrentPlay is null ||
            Objects.LocalPlayer is not { } local || CannotLockMovement())
        {
            hold.Release();
            return;
        }
        // Standing up, stepping to a pose, a start on its way and walking onto someone's spot are all
        // Synastry's own doing, and the hold would stand in their way.
        var busy = standUpStartedAt != 0 || PendingStart() is not null || cyclingPose is not null || movement.IsWalking;
        hold.Tick(local.Position, IsLockablePlay(local), busy, PoseSignature.Of(local), seconds);
        if (hold.TakeHint(FreeCamOn || Camera.FreeCamService.NavigationPaused()))
            Chat.Print("[Synastry] Your movement is locked so the animation doesn't end. " +
                       "Use the lock button, or type /syn unlock, then move to end it.");
    }

    // A looping emote or pose, the standing idle that was played, or a locked emote's own timeline.
    // The last two only while standing normally: nothing says when they're over.
    private bool IsLockablePlay(Dalamud.Game.ClientState.Objects.Types.ICharacter local) =>
        DrawnModel.IsLooping(local) ||
        poses.CurrentKind() == PoseKind.Idle && (activeDirectPlayback is not null ||
            CurrentPlay!.Trigger.StartsWith($"pose:{PoseKind.Idle}:", StringComparison.OrdinalIgnoreCase));

    private static bool CannotLockMovement() =>
        Conditions.Any(ConditionFlag.InCombat, ConditionFlag.Unconscious,
            ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
            ConditionFlag.Mounting, ConditionFlag.Mounting71, ConditionFlag.Mounted, ConditionFlag.RidingPillion,
            ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78);

    /// <summary>Lets go of your character so Synastry can stand you up or change your pose.</summary>
    private void LetGoOfCharacter()
    {
        freeCam?.LetGoOfCharacter();
        animationLock?.LetGo();
    }
}

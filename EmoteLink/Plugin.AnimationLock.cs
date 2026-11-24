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
    private bool IsLockablePlay(Dalamud.Game.ClientState.Objects.Types.ICharacter local) =>
        DrawnModel.IsLooping(local) || activeDirectPlayback is not null ||
        CurrentPlay!.Trigger.StartsWith($"pose:{PoseKind.Idle}:", StringComparison.OrdinalIgnoreCase) &&
        poses.CurrentKind() == PoseKind.Idle;

    private static bool CannotLockMovement() =>
        Conditions.Any(ConditionFlag.InCombat, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
            ConditionFlag.Mounted, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene);

    /// <summary>Lets go of your character so Synastry can stand you up or change your pose.</summary>
    private void LetGoOfCharacter()
    {
        freeCam?.LetGoOfCharacter();
        animationLock?.LetGo();
    }
}

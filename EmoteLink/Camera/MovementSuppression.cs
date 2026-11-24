using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace EmoteLink.Camera;

/// <summary>
/// Keeps your character still for the free cam and the animation lock: movement inputs are blocked, the
/// game's ForceDisableMovement counter is held, and its "cancel your emote?" check answers no. All
/// three are needed: the counter is what stops movement, and without the emote check a key press
/// half-cancels the emote under the counter and leaves the character sunk and circling afterwards.
/// One shared set and one counter contribution, so the two can't let go of each other's hold.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
public sealed unsafe class MovementSuppression : IDisposable
{
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool InputQuery(InputData* data, InputId id);
    private delegate int AxisQuery(InputData* data, uint axis);
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool CancelEmoteDelegate(EmoteController* controller, nint unknown);

    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly List<Hook<InputQuery>> queries = [];
    // Who blocks the movement keys, and who of them also holds the character and its emote.
    private readonly HashSet<string> blockers = [];
    private readonly HashSet<string> holders = [];
    private Hook<AxisQuery>? axisHook;
    private Hook<InputManager.Delegates.GetInputStatus>? statusHook;
    private Hook<InputQuery>? downHook;
    private Hook<CancelEmoteDelegate>? cancelEmoteHook;
    private int* movementCounter;
    private bool blocking;
    private bool ownsCounter;

    /// <summary>False when a signature didn't resolve; free cam and the animation lock then can't hold
    /// the character.</summary>
    public bool Available { get; private set; }

    public MovementSuppression(ISigScanner scanner, IGameInteropProvider interop, IPluginLog log)
    {
        this.interop = interop;
        this.log = log;
        try
        {
            // The movss operand is a float; the ForceDisableMovement counter is the int 4 bytes past it.
            var instruction = scanner.ScanText("F3 0F 10 05 ?? ?? ?? ?? 0F 2E C7");
            movementCounter = (int*)((byte*)(instruction + 8 + *(int*)(instruction + 4)) + 4);
            if (*movementCounter < 0 || *movementCounter > 100)
                throw new InvalidOperationException("Unexpected movement counter.");
            AddQuery(InputData.Addresses.IsInputIdPressed.Value);
            downHook = AddQuery(InputData.Addresses.IsInputIdDown.Value);
            AddQuery(InputData.Addresses.IsInputIdHeld.Value);
            AddQuery(InputData.Addresses.IsInputIdReleased.Value);
            axisHook = interop.HookFromSignature<AxisQuery>("E8 ?? ?? ?? ?? 66 44 0F 6E C3",
                (data, axis) => axis is 3 or 4 && BlocksKeys ? 0 : axisHook!.Original(data, axis));
            var address = InputManager.Addresses.GetInputStatus.Value;
            if (address == 0) throw new InvalidOperationException("Gameplay input support is unavailable.");
            statusHook = interop.HookFromAddress<InputManager.Delegates.GetInputStatus>(address,
                (manager, code) => !(Blocks(code) && BlocksKeys) && statusHook!.Original(manager, code));
            // Only your own emotes are kept going; everyone else's end as the game decides.
            cancelEmoteHook = interop.HookFromAddress<CancelEmoteDelegate>(
                scanner.ScanText("E8 ?? ?? ?? ?? 48 8B 7B 08 45 33 C0"),
                (controller, unknown) => controller is not null &&
                    (nint)controller->OwnerObject != (nint)Control.GetLocalPlayer() &&
                    cancelEmoteHook!.Original(controller, unknown));
            Available = true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "The character can't be held still on this game version.");
            DisposeHooks();
        }
    }

    private Hook<InputQuery> AddQuery(nint address)
    {
        if (address == 0) throw new InvalidOperationException("Movement binding support is unavailable.");
        Hook<InputQuery>? hook = null;
        hook = interop.HookFromAddress<InputQuery>(address,
            (data, id) => !(Blocks(id) && BlocksKeys) && hook!.Original(data, id));
        queries.Add(hook);
        return hook;
    }

    /// <summary>Someone else keeps the character from moving too, such as Cammy's free cam.</summary>
    public bool HeldByOthers => movementCounter is not null && *movementCounter > (ownsCounter ? 1 : 0);

    // Cammy's free cam flies with the keys the animation lock blocks, and reads them through these same
    // hooks. Its own hold keeps the character still meanwhile, so the lock leaves the keys to it.
    private bool BlocksKeys => blocking && (blockers.Contains(FreeCamService.Owner) || !HeldByOthers);

    // Character movement only; looking around stays with the game.
    private static bool Blocks(InputId id) => id is
        >= InputId.MOVE_FORE and <= InputId.MOVE_AND_STEER or
        >= InputId.JUMP and <= InputId.AUTORUN_PAD or
        >= InputId.MOVE_DESCENT and <= InputId.MOVE_ANGLE_DESCENT;

    private static bool Blocks(InputCode code) => code is
        >= InputCode.MOVE_DESCENT and <= InputCode.MOVE_STRIFE_R;

    /// <summary>Whether a key is down right now, past the block. "Held" is the game's hold/repeat state,
    /// which starts late and pulses.</summary>
    public bool IsDown(InputData* data, InputId id) => downHook is not null && downHook.Original(data, id);

    /// <summary>Whether any movement, strafe, jump or descend key is physically down, past the block.</summary>
    public bool IsMovementKeyDown()
    {
        var framework = GameFramework.Instance();
        if (framework is null || framework->UIModule is null) return false;
        var data = (InputData*)framework->UIModule->GetUIInputData();
        return data is not null && (
            IsDown(data, InputId.MOVE_FORE) || IsDown(data, InputId.MOVE_BACK) ||
            IsDown(data, InputId.MOVE_LEFT) || IsDown(data, InputId.MOVE_RIGHT) ||
            IsDown(data, InputId.MOVE_STRIFE_L) || IsDown(data, InputId.MOVE_STRIFE_R) ||
            IsDown(data, InputId.JUMP) || IsDown(data, InputId.MOVE_DESCENT));
    }

    /// <summary>
    /// Holds the character. Only call it while they stand still: taking the counter mid-movement can
    /// leave the game's movement out of step. False when unavailable or the counter looks wrong.
    /// </summary>
    public bool Hold(string owner)
    {
        if (!Available || movementCounter is null) return false;
        try
        {
            if (!ownsCounter)
            {
                if (*movementCounter < 0 || *movementCounter >= 100) return false;
                ++*movementCounter;
                ownsCounter = true;
                cancelEmoteHook!.Enable();
            }
            holders.Add(owner);
            blockers.Add(owner);
            if (blocking) return true;
            blocking = true;
            foreach (var hook in queries) hook.Enable();
            axisHook!.Enable();
            statusHook!.Enable();
            return true;
        }
        catch (Exception ex)
        {
            // Half a hold must not stay behind: the caller hears no, so nobody would let go of it.
            log.Error(ex, "The character couldn't be held still.");
            Available = false;
            Release(owner);
            return false;
        }
    }

    /// <summary>Lets the character stand up or change emote once nobody holds it; the movement keys stay
    /// blocked.</summary>
    public void LetGo(string owner)
    {
        holders.Remove(owner);
        if (!ownsCounter || holders.Count > 0) return;
        ownsCounter = false;
        // Take back only our own part: the counter is shared, so it is never reset to zero.
        if (movementCounter is not null && *movementCounter > 0) --*movementCounter;
        if (cancelEmoteHook is not null) TryCleanup(cancelEmoteHook.Disable, log);
    }

    /// <summary>Gives back everything this owner held; the character is free once nobody holds it.</summary>
    public void Release(string owner)
    {
        LetGo(owner);
        blockers.Remove(owner);
        if (!blocking || blockers.Count > 0) return;
        blocking = false;
        foreach (var hook in queries) TryCleanup(hook.Disable, log);
        if (axisHook is not null) TryCleanup(axisHook.Disable, log);
        if (statusHook is not null) TryCleanup(statusHook.Disable, log);
    }

    public void Dispose()
    {
        foreach (var owner in blockers.Union(holders).ToList()) Release(owner);
        DisposeHooks();
    }

    private void DisposeHooks()
    {
        foreach (var hook in queries) TryCleanup(hook.Dispose, log);
        queries.Clear();
        if (axisHook is not null) TryCleanup(axisHook.Dispose, log);
        if (statusHook is not null) TryCleanup(statusHook.Dispose, log);
        if (cancelEmoteHook is not null) TryCleanup(cancelEmoteHook.Dispose, log);
        axisHook = null;
        statusHook = null;
        cancelEmoteHook = null;
        downHook = null;
        movementCounter = null;
    }

    internal static void TryCleanup(Action action, IPluginLog log)
    {
        try { action(); }
        catch (Exception ex) { log.Warning(ex, "A movement hook could not be cleaned up."); }
    }
}

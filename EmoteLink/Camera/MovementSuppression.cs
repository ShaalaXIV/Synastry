using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace EmoteLink.Camera;

/// <summary>
/// Keeps your character still while the movement keys fly the camera: movement inputs are blocked, the
/// game's ForceDisableMovement counter is held, and its "cancel your emote?" check answers no. All
/// three are needed: the counter is what stops movement, and without the emote check a key press
/// half-cancels the emote under the counter and leaves the character sunk and circling afterwards.
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
    private Hook<AxisQuery>? axisHook;
    private Hook<InputManager.Delegates.GetInputStatus>? statusHook;
    private Hook<InputQuery>? downHook;
    private Hook<CancelEmoteDelegate>? cancelEmoteHook;
    private int* movementCounter;
    private bool blocking;
    private bool ownsCounter;

    /// <summary>False when a signature didn't resolve; free cam then can't hold the character.</summary>
    public bool Available { get; }

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
                (data, axis) => blocking && axis is 3 or 4 ? 0 : axisHook!.Original(data, axis));
            var address = InputManager.Addresses.GetInputStatus.Value;
            if (address == 0) throw new InvalidOperationException("Gameplay input support is unavailable.");
            statusHook = interop.HookFromAddress<InputManager.Delegates.GetInputStatus>(address,
                (manager, code) => !(blocking && Blocks(code)) && statusHook!.Original(manager, code));
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
            log.Warning(ex, "Free cam can't hold the character still on this game version.");
            DisposeHooks();
        }
    }

    private Hook<InputQuery> AddQuery(nint address)
    {
        if (address == 0) throw new InvalidOperationException("Movement binding support is unavailable.");
        Hook<InputQuery>? hook = null;
        hook = interop.HookFromAddress<InputQuery>(address,
            (data, id) => !(blocking && Blocks(id)) && hook!.Original(data, id));
        queries.Add(hook);
        return hook;
    }

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

    /// <summary>
    /// Holds the character. Only call it while they stand still: taking the counter mid-movement can
    /// leave the game's movement out of step. False when unavailable or the counter looks wrong.
    /// </summary>
    public bool Hold()
    {
        if (!Available || movementCounter is null) return false;
        if (!ownsCounter)
        {
            if (*movementCounter < 0 || *movementCounter >= 100) return false;
            ++*movementCounter;
            ownsCounter = true;
            cancelEmoteHook!.Enable();
        }
        if (blocking) return true;
        blocking = true;
        foreach (var hook in queries) hook.Enable();
        axisHook!.Enable();
        statusHook!.Enable();
        return true;
    }

    /// <summary>Lets the character stand up or change emote; the movement keys stay blocked.</summary>
    public void LetGo()
    {
        if (!ownsCounter) return;
        ownsCounter = false;
        // Take back only our own part: the counter is shared, so it is never reset to zero.
        if (movementCounter is not null && *movementCounter > 0) --*movementCounter;
        if (cancelEmoteHook is not null) TryCleanup(cancelEmoteHook.Disable, log);
    }

    /// <summary>Gives everything back.</summary>
    public void Release()
    {
        LetGo();
        if (!blocking) return;
        blocking = false;
        foreach (var hook in queries) TryCleanup(hook.Disable, log);
        if (axisHook is not null) TryCleanup(axisHook.Disable, log);
        if (statusHook is not null) TryCleanup(statusHook.Disable, log);
    }

    public void Dispose()
    {
        Release();
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
        catch (Exception ex) { log.Warning(ex, "A free cam hook could not be cleaned up."); }
    }
}

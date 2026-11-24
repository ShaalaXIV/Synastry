using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace EmoteLink.Camera;

/// <summary>
/// The free camera's own keys on top of <see cref="MovementSuppression"/>: E and Q for up and down, and
/// Shift for faster. Takes game actions, not typed characters, so chat keeps working.
/// </summary>
/// <remarks>
/// Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit
/// The raw keyboard layout is the one Ktisis uses, https://github.com/ktisis-tools/Ktisis
/// </remarks>
internal sealed unsafe class FreeCamInput : IDisposable
{
    private delegate void DeviceUpdate(nint manager, nint a2, nint controller, nint mouse, KeyboardDeviceData* keyboard);

    // Raw keyboard state the game fills each frame before hotbars read it.
    [StructLayout(LayoutKind.Explicit)]
    private struct KeyboardDeviceData
    {
        internal const int KeyCount = 160;
        [FieldOffset(4)] public fixed uint KeyMap[KeyCount];
    }

    private const int KeyShift = 0x10;
    private const int KeyE = 0x45;
    private const int KeyQ = 0x51;
    private readonly MovementSuppression suppression;
    private readonly IPluginLog log;
    private Hook<DeviceUpdate>? deviceHook;
    private bool active;

    /// <summary>Set each tick by the service; while true, the raw keys are neither read nor cleared.</summary>
    public bool NavigationPaused { get; set; } = true;

    public bool Up { get; private set; }
    public bool Down { get; private set; }
    public bool Faster { get; private set; }

    public FreeCamInput(MovementSuppression suppression, IGameInteropProvider interop, IPluginLog log)
    {
        this.suppression = suppression;
        this.log = log;
        deviceHook = interop.HookFromSignature<DeviceUpdate>("E8 ?? ?? ?? ?? 83 7B 58 00", DeviceUpdateDetour);
    }

    public bool IsDown(InputData* data, InputId id) => suppression.IsDown(data, id);

    // Reads E and Q, then clears them so hotbar slots on those keys never fire. Left alone while
    // paused, so typing in chat is unaffected.
    private void DeviceUpdateDetour(nint manager, nint a2, nint controller, nint mouse, KeyboardDeviceData* keyboard)
    {
        deviceHook!.Original(manager, a2, controller, mouse, keyboard);
        if (!active || NavigationPaused || keyboard is null)
        {
            Up = Down = Faster = false;
            return;
        }
        Up = keyboard->KeyMap[KeyE] != 0;
        Down = keyboard->KeyMap[KeyQ] != 0;
        Faster = keyboard->KeyMap[KeyShift] != 0;
        keyboard->KeyMap[KeyE] = 0;
        keyboard->KeyMap[KeyQ] = 0;
    }

    /// <summary>Takes the movement keys. Only call it while the character stands still.</summary>
    public void Enable()
    {
        if (!suppression.Hold(FreeCamService.Owner)) throw new InvalidOperationException("Movement can't be held.");
        active = true;
        deviceHook!.Enable();
    }

    public void Dispose()
    {
        active = false;
        Up = Down = Faster = false;
        suppression.Release(FreeCamService.Owner);
        if (deviceHook is null) return;
        MovementSuppression.TryCleanup(deviceHook.Disable, log);
        MovementSuppression.TryCleanup(deviceHook.Dispose, log);
        deviceHook = null;
    }
}

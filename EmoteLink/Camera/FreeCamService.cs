using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

namespace EmoteLink.Camera;

/// <summary>
/// Synastry's own free camera: the movement keys fly the view while your character stays put and any
/// animation keeps playing. Looking around is the game's own right-drag. Main thread only.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
public sealed unsafe class FreeCamService : IDisposable
{
    internal const string Owner = "freecam";
    public const float MinimumSpeed = 0.1f;
    public const float MaximumSpeed = 5f;
    private const float FasterScale = 3f;
    // How much one notch of the mouse wheel changes the speed.
    private const float SpeedStep = 1.15f;
    // Just short of straight up and down: the view has no "right" at exactly 90 degrees.
    private const float PitchLimit = 1.55f;
    private const float StillSeconds = 0.25f;
    private const int LetGoMs = 1000;
    // Lining up, chair snaps and standing up all move you a little; a teleport moves you more.
    private const float ActorDriftLimit = 3f;
    // Running average of render intervals; raw intervals vary with CPU timing while frames are shown
    // at a steady rate, which looks like uneven travel.
    private const float FrameAverageWeight = 0.1f;
    private const float MaxFrameSeconds = 0.05f;

    private delegate nint CalculateView(SceneCamera* camera);

    private readonly MovementSuppression suppression;
    private readonly ISigScanner scanner;
    private readonly IGameInteropProvider interop;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly IPluginLog log;
    private readonly FreeCamMotion motion = new();
    private readonly Stopwatch frameClock = new();
    private FreeCamInput? input;
    private Hook<CalculateView>? viewHook;
    private Hook<GameCamera.Delegates.Update>? updateHook;
    private NativeCameraView.Loader? loadView;
    private GameCamera* camera;
    private nint actor;
    private Vector3 actorPosition;
    private uint territory;
    private nint observedActor;
    private Vector3 observedPosition;
    private float stationarySeconds;
    // Sampled on the framework tick; applied at render time so motion and drawing share one clock.
    private Vector3 moveInput;
    private float moveSpeed;
    private float frameSeconds;
    private bool letGo;
    private long holdAgainAt;
    private string? endReason;
    private bool disposed;
    private float dirH, dirV, dirVMin, dirVMax, distance, interpDistance, fov, pinnedDistance;
    private Vector3 scenePosition, sceneLookAt;

    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "";

    /// <summary>How fast it flies, where 1 is normal. The mouse wheel changes it.</summary>
    public float Speed { get; private set; }

    public FreeCamService(MovementSuppression suppression, float speed, ISigScanner scanner, IGameInteropProvider interop,
        IClientState clientState, IObjectTable objects, ICondition conditions, IPluginLog log)
    {
        this.suppression = suppression;
        this.scanner = scanner;
        this.interop = interop;
        this.clientState = clientState;
        this.objects = objects;
        this.conditions = conditions;
        this.log = log;
        Speed = float.IsFinite(speed) ? Math.Clamp(speed, MinimumSpeed, MaximumSpeed) : 1f;
        conditions.ConditionChange += OnConditionChange;
        clientState.TerritoryChanged += OnTerritoryChanged;
        clientState.Logout += OnLogout;
    }

    /// <summary>Still for a moment and not autorunning: the only time movement may be taken from you.</summary>
    private bool CharacterStill => stationarySeconds >= StillSeconds && !InputManager.IsAutoRunning();

    public void Start()
    {
        if (disposed || Enabled) return;
        if (StartBlockedReason() is { } reason)
        {
            Status = reason;
            return;
        }
        try
        {
            if (!CharacterStill)
            {
                Status = "Stop moving before turning on free cam.";
                return;
            }

            var candidate = CameraManager.Instance()->Camera;
            NativeCameraView.Validate((nint)candidate->SceneCamera.RenderCamera, (Matrix4x4*)&candidate->SceneCamera.ViewMatrix);
            viewHook?.Dispose();
            viewHook = null;
            updateHook?.Dispose();
            updateHook = null;
            // Create all hooks before taking input or changing the live camera.
            input = new FreeCamInput(suppression, interop, log);
            // ScanText already follows the E8 to the function itself.
            loadView = Marshal.GetDelegateForFunctionPointer<NativeCameraView.Loader>(
                scanner.ScanText("E8 ?? ?? ?? ?? 48 8B 17 48 8D 4D E0"));
            viewHook = interop.HookFromSignature<CalculateView>(
                "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? F6 81 ?? ?? ?? ?? ?? 48 8B D9 48 89 B4 24 ?? ?? ?? ??",
                CalculateViewDetour);
            updateHook = interop.HookFromAddress<GameCamera.Delegates.Update>(
                (nint)candidate->VirtualTable->Update, UpdateCameraDetour);

            var view = (Matrix4x4)candidate->SceneCamera.ViewMatrix;
            // The game's affine view storage doesn't guarantee the homogeneous element.
            view.M44 = 1f;
            if (!Matrix4x4.Invert(view, out var inverse))
                throw new InvalidOperationException("The camera view cannot be read.");
            motion.Initialize(inverse.Translation);
            dirH = candidate->DirH;
            dirV = candidate->DirV;
            dirVMin = candidate->DirVMin;
            dirVMax = candidate->DirVMax;
            distance = candidate->Distance;
            interpDistance = candidate->InterpDistance;
            fov = candidate->FoV;
            scenePosition = candidate->SceneCamera.Position;
            sceneLookAt = candidate->SceneCamera.LookAtVector;
            // Pinned mid-range: at its shortest, turning the wheel would switch to first person.
            pinnedDistance = (candidate->MinDistance + candidate->MaxDistance) * 0.5f;
            actor = objects.LocalPlayer!.Address;
            actorPosition = objects.LocalPlayer.Position;
            territory = clientState.TerritoryType;
            moveInput = Vector3.Zero;
            frameSeconds = 0f;
            frameClock.Restart();
            letGo = false;
            endReason = null;
            camera = candidate;
            input.Enable();
            updateHook.Enable();
            viewHook.Enable();
            candidate->Distance = pinnedDistance;
            candidate->InterpDistance = pinnedDistance;
            Enabled = true;
            Status = "Free cam is on.";
        }
        catch (Exception ex)
        {
            log.Error(ex, "Free cam could not start.");
            Stop();
            Status = "Free cam couldn't start. The plugin log has the details.";
        }
    }

    /// <summary>Why the world isn't in a state to fly in, or null. The wording is for starting.</summary>
    private string? UnsupportedReason()
    {
        if (!clientState.IsLoggedIn || objects.LocalPlayer is null) return "Log in before turning on free cam.";
        if (clientState.IsGPosing) return "Leave group pose before turning on free cam.";
        if (conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51] ||
            conditions[ConditionFlag.WatchingCutscene] || conditions[ConditionFlag.WatchingCutscene78] ||
            conditions[ConditionFlag.OccupiedInCutSceneEvent])
            return "Free cam isn't available during cutscenes and zone changes.";
        var manager = CameraManager.Instance();
        if (manager is null || manager->ActiveCameraIndex != 0 || manager->Camera is null ||
            manager->GetActiveCamera() != manager->Camera || manager->Camera->SceneCamera.RenderCamera is null)
            return "Free cam needs the normal game camera.";
        return null;
    }

    private string? StartBlockedReason()
    {
        if (UnsupportedReason() is { } unsupported) return unsupported;
        if (conditions[ConditionFlag.InCombat]) return "Free cam isn't available in combat.";
        // In first person your own character isn't drawn, so there'd be nothing to look at.
        if (CameraManager.Instance()->Camera->ZoomMode == CameraZoomMode.FirstPerson)
            return "Zoom out of first person before turning on free cam.";
        return null;
    }

    /// <summary>Why a running session can't go on, or null.</summary>
    private string? SessionOverReason()
    {
        if (UnsupportedReason() is not null || camera is null || CameraManager.Instance()->Camera != camera ||
            clientState.TerritoryType != territory)
            return "the game needed its camera";
        if (conditions[ConditionFlag.InCombat]) return "you're in combat";
        if (camera->ZoomMode == CameraZoomMode.FirstPerson) return "the camera went to first person";
        if (objects.LocalPlayer is not { } player || player.Address != actor ||
            Vector3.DistanceSquared(actorPosition, player.Position) > ActorDriftLimit * ActorDriftLimit)
            return "your character moved";
        return null;
    }

    public void Tick(float seconds)
    {
        var player = objects.LocalPlayer;
        if (player is null)
        {
            observedActor = 0;
            stationarySeconds = 0;
        }
        else
        {
            stationarySeconds = observedActor == player.Address && Vector3.DistanceSquared(observedPosition, player.Position) < 0.00000001f
                ? stationarySeconds + Math.Clamp(seconds, 0, MaxFrameSeconds)
                : 0;
            observedActor = player.Address;
            observedPosition = player.Position;
        }
        if (!Enabled) return;
        try
        {
            if (SessionOverReason() is { } reason)
            {
                End(reason);
                return;
            }
            if (letGo && Environment.TickCount64 >= holdAgainAt && CharacterStill) letGo = !suppression.Hold(Owner);
            SampleInput();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// Lets your character stand up or change animation while the camera keeps flying. They are held
    /// again once they've been still for a moment after the last call.
    /// </summary>
    public void LetGoOfCharacter()
    {
        if (!Enabled) return;
        letGo = true;
        holdAgainAt = Environment.TickCount64 + LetGoMs;
        suppression.LetGo(Owner);
    }

    // Typing and other programs keep the keys to themselves.
    internal static bool NavigationPaused()
    {
        var framework = GameFramework.Instance();
        var atk = RaptureAtkModule.Instance();
        return framework is null || framework->UIModule is null || !framework->CursorInputs.IsGameWindowFocused ||
               atk is null || atk->IsTextInputActive() || ImGui.GetIO().WantTextInput;
    }

    // Reads the game's input rather than ImGui's, which misses keys during a camera drag.
    private void SampleInput()
    {
        moveInput = Vector3.Zero;
        if (input is null) return;
        input.NavigationPaused = NavigationPaused();
        if (input.NavigationPaused) return;
        var data = (InputData*)GameFramework.Instance()->UIModule->GetUIInputData();
        moveInput = new Vector3(
            (input.IsDown(data, InputId.MOVE_RIGHT) || input.IsDown(data, InputId.MOVE_STRIFE_R) ? 1 : 0) -
            (input.IsDown(data, InputId.MOVE_LEFT) || input.IsDown(data, InputId.MOVE_STRIFE_L) ? 1 : 0),
            (input.Up || input.IsDown(data, InputId.JUMP) ? 1 : 0) -
            (input.Down || input.IsDown(data, InputId.MOVE_DESCENT) ? 1 : 0),
            (input.IsDown(data, InputId.MOVE_FORE) ? 1 : 0) - (input.IsDown(data, InputId.MOVE_BACK) ? 1 : 0));
        moveSpeed = Speed * (input.Faster ? FasterScale : 1f);
    }

    private void UpdateCameraDetour(GameCamera* current)
    {
        if (Enabled && current == camera)
        {
            // The game stops you looking up early; a free camera shouldn't.
            current->DirVMin = -PitchLimit;
            current->DirVMax = PitchLimit;
        }
        // The normal update drives scene and render work too; never skip it.
        updateHook!.Original(current);
        if (!Enabled || current != camera) return;
        try
        {
            if (SessionOverReason() is { } reason)
            {
                End(reason);
                return;
            }
            // The wheel changes the speed instead of the zoom. The wheel itself is read, where the game
            // reads it: how far the zoom moved says nothing near a wall, where the game limits it. The
            // direction stays under the game's control so mouse look keeps working.
            var framework = GameFramework.Instance();
            var wheel = framework is null || framework->UIModule is null
                ? 0
                : framework->UIModule->GetUIInputData()->UIFilteredCursorInputs.MouseWheel;
            if (wheel != 0)
                Speed = Math.Clamp(Speed * (wheel > 0 ? SpeedStep : 1f / SpeedStep), MinimumSpeed, MaximumSpeed);
            current->Distance = pinnedDistance;
            current->InterpDistance = pinnedDistance;
            current->FoV = fov;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private nint CalculateViewDetour(SceneCamera* current)
    {
        var result = viewHook!.Original(current);
        if (!Enabled || camera is null || current != &camera->SceneCamera) return result;
        try
        {
            if (SessionOverReason() is { } reason)
            {
                End(reason);
                return result;
            }
            var matrix = (Matrix4x4*)&current->ViewMatrix;
            NativeCameraView.Validate((nint)current->RenderCamera, matrix);
            // Advance by the time since the last render, not the framework tick, so each drawn frame
            // moves by exactly its own duration.
            var raw = Math.Clamp((float)frameClock.Elapsed.TotalSeconds, 0f, MaxFrameSeconds);
            frameClock.Restart();
            frameSeconds = frameSeconds <= 0f ? raw : frameSeconds + (raw - frameSeconds) * FrameAverageWeight;
            // DirH runs opposite to FreeCamMotion's convention.
            motion.Step(moveInput, -camera->DirH, camera->DirV, frameSeconds, moveSpeed);
            var position = motion.Position;
            var lookAt = position + FreeCamMotion.Forward(-camera->DirH, camera->DirV);
            current->Position = position;
            current->LookAtVector = lookAt;
            *matrix = Matrix4x4.CreateLookAt(position, lookAt, Vector3.UnitY);
            // Update derived render state through the game's own matrix loader.
            NativeCameraView.Load(loadView!, (nint)current->RenderCamera, matrix);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        return result;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (Enabled && value && flag is ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51 or
            ConditionFlag.WatchingCutscene or ConditionFlag.WatchingCutscene78 or ConditionFlag.OccupiedInCutSceneEvent)
            End("the game needed its camera");
    }

    private void OnTerritoryChanged(uint _) => End("you changed area");
    private void OnLogout(int _, int __) => Stop();

    private void Fail(Exception ex)
    {
        log.Error(ex, "Free cam failed and was turned off.");
        End("something went wrong");
    }

    /// <summary>Turns it off without being asked to; <see cref="TakeEndReason"/> then says why, once.</summary>
    public void End(string reason)
    {
        if (!Enabled) return;
        Stop();
        endReason = reason;
        Status = $"Free cam ended: {reason}.";
    }

    public string? TakeEndReason()
    {
        var reason = endReason;
        endReason = null;
        return reason;
    }

    public void Stop()
    {
        Enabled = false;
        moveInput = Vector3.Zero;
        frameClock.Reset();
        try
        {
            var manager = CameraManager.Instance();
            if (camera is not null && manager is not null && manager->Camera == camera)
            {
                // The camera object outlives zone changes and cutscenes, so what was changed on it goes
                // back on every exit.
                camera->DirVMin = dirVMin;
                camera->DirVMax = dirVMax;
                camera->DirH = dirH;
                camera->DirV = dirV;
                camera->Distance = distance;
                camera->InterpDistance = interpDistance;
                // The game recomputes these each frame; in a transition its own values are already newer.
                if (UnsupportedReason() is null && clientState.TerritoryType == territory)
                {
                    camera->FoV = fov;
                    camera->SceneCamera.Position = scenePosition;
                    camera->SceneCamera.LookAtVector = sceneLookAt;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "The camera could not be restored after free cam.");
        }
        finally
        {
            camera = null;
            letGo = false;
            // Hooks can be disabled from their own detours; disposal waits until the next start or unload.
            if (viewHook is not null) MovementSuppression.TryCleanup(viewHook.Disable, log);
            if (updateHook is not null) MovementSuppression.TryCleanup(updateHook.Disable, log);
            input?.Dispose();
            input = null;
            Status = "Free cam is off.";
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        conditions.ConditionChange -= OnConditionChange;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        clientState.Logout -= OnLogout;
        Stop();
        if (viewHook is not null) MovementSuppression.TryCleanup(viewHook.Dispose, log);
        if (updateHook is not null) MovementSuppression.TryCleanup(updateHook.Dispose, log);
    }
}

using System.Text.RegularExpressions;

namespace EmoteLink;

/// <summary>
/// The free camera button: Cammy's free cam when Cammy is loaded, otherwise Synastry's own. Never both.
/// </summary>
public sealed unsafe partial class Plugin
{
    private Camera.MovementSuppression? movementSuppression;
    private Camera.FreeCamService? freeCam;
    private bool freeCamHintShown;
    private bool cammyLoaded;
    private long cammyCheckedAt;

    /// <summary>Synastry's own free cam is on. Cammy doesn't say whether its own is.</summary>
    public bool FreeCamOn => freeCam?.Enabled == true;

    public string FreeCamTooltip =>
        FreeCamOn ? "Free cam is on. Click to go back to the normal camera." :
        IsCammyLoadedCached() ? "Toggle Cammy's free cam." :
        "Fly the camera around: WASD, E/Q up and down, right-drag to look, Shift for faster, mouse wheel for speed.\n" +
        "Uses Cammy instead when it's on.";

    partial void InitializeFreeCam()
    {
        try
        {
            movementSuppression = new Camera.MovementSuppression(SigScanner, Interop, Log);
            if (!movementSuppression.Available) return;
            animationLock = new AnimationLock(movementSuppression);
            freeCam = new Camera.FreeCamService(movementSuppression, configuration.FreeCamSpeed, SigScanner, Interop,
                ClientState, Objects, Conditions, Log);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Free cam could not be initialized.");
        }
    }

    public void ToggleFreeCam()
    {
        if (FreeCamOn)
        {
            freeCam!.Stop();
            Status = freeCam.Status;
            return;
        }
        if (IsCammyLoaded())
        {
            Status = Commands.ProcessCommand("/cammy freecam")
                ? "Toggled Cammy's free cam."
                : "Cammy is on but didn't take its free cam command.";
            return;
        }
        freeCam?.Start();
        Status = freeCam?.Status ?? "Free cam isn't available on this game version.";
        if (!FreeCamOn) Chat.PrintError($"[Synastry] {Status}");
        else if (!freeCamHintShown)
        {
            freeCamHintShown = true;
            Chat.Print("[Synastry] Free cam: WASD to fly, E and Q for up and down, right-drag to look, Shift for faster, " +
                       "mouse wheel for speed. /syn freecam goes back to the normal camera.");
        }
    }

    partial void HandleFreeCamCommand(string arguments, ref bool handled)
    {
        if (Regex.IsMatch(arguments, @"^\s*unlock\s*$", RegexOptions.IgnoreCase))
        {
            handled = true;
            if (MovementLocked) UnlockMovement();
            else Chat.Print("[Synastry] Your movement isn't locked.");
            return;
        }
        if (!Regex.IsMatch(arguments, @"^\s*(?:freecam|cam)\s*$", RegexOptions.IgnoreCase)) return;
        handled = true;
        ToggleFreeCam();
    }

    partial void TickFreeCam(float seconds)
    {
        if (freeCam is not { } cam) return;
        if (cam.Enabled)
        {
            if (IsCammyLoadedCached()) cam.End("Cammy has its own free cam");
            // Aligning walks you with the movement this holds.
            else if (movement.IsWalking) cam.End("you're walking to your target");
            // Standing up, starting an animation and stepping to a pose are Synastry's own doing: the
            // hold that keeps your emote going would stand in their way.
            else if (standUpStartedAt != 0 || PendingStart() is not null || cyclingPose is not null)
                cam.LetGoOfCharacter();
        }
        cam.Tick(seconds);
        movement.IgnoreKeys = cam.Enabled;
        if (cam.TakeEndReason() is not null)
        {
            Status = cam.Status;
            Chat.Print($"[Synastry] {Status}");
        }
        if (!cam.Enabled) SaveFreeCamSpeed(cam);
    }

    private void SaveFreeCamSpeed(Camera.FreeCamService cam)
    {
        if (configuration.FreeCamSpeed == cam.Speed) return;
        configuration.FreeCamSpeed = cam.Speed;
        configuration.Save(PluginInterface);
    }

    private bool IsCammyLoaded()
    {
        try
        {
            return PluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded && plugin.InternalName.Equals("Cammy", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private bool IsCammyLoadedCached()
    {
        var now = Environment.TickCount64;
        if (now - cammyCheckedAt < 1000) return cammyLoaded;
        cammyCheckedAt = now;
        cammyLoaded = IsCammyLoaded();
        return cammyLoaded;
    }

    partial void DisposeFreeCam()
    {
        var cam = freeCam;
        var suppression = movementSuppression;
        var lockHold = animationLock;
        freeCam = null;
        animationLock = null;
        movementSuppression = null;

        // Synastry can unload off the main thread; the camera and movement go back on it.
        void Restore()
        {
            cam?.Dispose();
            lockHold?.Release();
            suppression?.Dispose();
            movement.IgnoreKeys = false;
            if (cam is not null) SaveFreeCamSpeed(cam);
        }

        try
        {
            if (!Framework.RunOnFrameworkThread(Restore).Wait(TimeSpan.FromSeconds(1))) Restore();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Free cam could not be cleaned up on the main thread.");
            Restore();
        }
    }
}

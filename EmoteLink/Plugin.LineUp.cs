using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace EmoteLink;

/// <summary>
/// Lining up before a room start: during the countdown, everyone but the room host walks onto the
/// host's spot and turns to the host's facing, the way Align to target does, so the animation starts
/// already lined up. Couple and group scenes are made around one shared spot, so this is where every
/// role belongs. Animations everyone plays the same way, like a group dance, keep their own spots.
/// </summary>
public sealed unsafe partial class Plugin
{
    // Walking further than this onto someone isn't lining up any more.
    private const float LineUpReach = 3f;
    // A start never waits longer than this for the walk.
    private const long LineUpTimeoutMs = 3000;
    // Closer than this and already facing the same way is lined up.
    private const float LinedUpDistance = 0.01f;
    private const float LinedUpYaw = 0.01f;

    // Each ready member's animation, with the race left out of its path, as the room last was while
    // anyone was ready: a start clears them on the relay before this client hears of it. Replaced
    // whole, since room updates arrive off the framework thread.
    private volatile IReadOnlyDictionary<string, string> readyAnimations = new Dictionary<string, string>();
    private string lineUpHost = "";
    private bool lineUpPending;
    private long lineUpStartedAt;
    private nint lineUpAnchor;

    public bool LineUpBeforeStartEnabled => configuration.LineUpBeforeStart;

    public void SetLineUpBeforeStart(bool enabled)
    {
        configuration.LineUpBeforeStart = enabled;
        configuration.Save(PluginInterface);
        if (!enabled) StopLineUp();
    }

    /// <summary>Notes who is ready with what, on every room update.</summary>
    private void RememberReadyAnimations(RoomStateDto room)
    {
        var ready = room.Members.Where(member => member.Ready).ToList();
        if (ready.Count == 0) return;
        var animations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in ready) animations[member.DisplayName] = AnimationKey(member.AssetPath);
        readyAnimations = animations;
    }

    /// <summary>The part of an animation path every race shares: "a0001/bt_common/emote/x.pap" of
    /// "chara/human/c0201/animation/a0001/bt_common/emote/x.pap".</summary>
    private static string AnimationKey(string path)
    {
        const string marker = "/animation/";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? path : path[(at + marker.Length)..];
    }

    /// <summary>A room start was scheduled: line up with the host during its countdown.</summary>
    private void ScheduleLineUp()
    {
        StopLineUp();
        if (!configuration.LineUpBeforeStart || sync.Room is not { } room || sync.IsRoomLeader) return;
        if (room.Members.FirstOrDefault(member => member.IsLeader) is not { } host) return;
        // The host isn't in this start (it can start without everyone).
        var ready = readyAnimations;
        if (ready.Count > 0 && !ready.ContainsKey(host.DisplayName)) return;
        // Everyone plays the very same animation, like a group dance: each keeps their own spot.
        var animations = ready.Values.ToList();
        if (animations.Count > 1 && animations.All(animation => animation.Length > 0) &&
            animations.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1) return;
        lineUpHost = host.DisplayName;
        lineUpPending = true;
    }

    private void StopLineUp()
    {
        lineUpPending = false;
        lineUpStartedAt = 0;
        lineUpAnchor = 0;
    }

    /// <summary>
    /// Walks you onto the host's spot while a room start counts down, after standing up if that was
    /// needed. True while it holds the start back: only if the walk outlasts the countdown, and never
    /// longer than a few seconds.
    /// </summary>
    private bool LineUpFirst()
    {
        if (!lineUpPending) return false;
        if (PendingStart() is null)
        {
            // The start was cancelled.
            StopLineUp();
            return false;
        }
        var now = Environment.TickCount64;
        if (lineUpStartedAt != 0)
        {
            if (IsAligning && now - lineUpStartedAt < LineUpTimeoutMs) return true;
            if (IsAligning)
            {
                movement.Cancel();
                alignmentFramesRemaining = 0;
            }
            lineUpPending = false;
            return false;
        }

        var local = Objects.LocalPlayer;
        var host = Objects.OfType<IPlayerCharacter>().FirstOrDefault(player =>
            player.Address != local?.Address && player.Name.TextValue.Equals(lineUpHost, StringComparison.OrdinalIgnoreCase));
        if (local is null || host is null)
        {
            StopLineUp();
            return false;
        }
        var distance = Vector3.Distance(local.Position, host.Position);
        if (distance > LineUpReach)
        {
            StopLineUp();
            Status = $"Not lining up with {lineUpHost}: more than {LineUpReach:0} yalms away.";
            return false;
        }
        lineUpAnchor = host.Address;
        lineUpStartedAt = now;
        if (distance <= LinedUpDistance &&
            MathF.Abs(MathF.IEEERemainder(local.Rotation - host.Rotation, MathF.Tau)) <= LinedUpYaw) return false;
        if (!WalkOntoForStart(local, host, distance))
        {
            StopLineUp();
            return false;
        }
        Status = $"Lining up with {lineUpHost}...";
        return true;
    }

    /// <summary>Like Align to target, except a standing loop is walked out of, since the start
    /// replaces it anyway. A seat is only shifted, and only a little.</summary>
    private bool WalkOntoForStart(IPlayerCharacter local, IPlayerCharacter host, float distance)
    {
        var player = (Character*)local.Address;
        if (player is null) return false;
        var position = host.Position;
        var rotation = host.Rotation;
        var address = host.Address;
        void Snap()
        {
            alignmentTargetAddress = address;
            alignmentFramesRemaining = 12;
            alignmentStableFrames = 0;
            SuppressMovementCleanupForAlignment();
            ApplyAlignment(position, rotation);
        }

        if (player->Mode == CharacterModes.InPositionLoop)
        {
            if (distance > MaxMidEmoteAlignDistance) return false;
            Snap();
            return true;
        }
        if (player->Mode is not (CharacterModes.Normal or CharacterModes.EmoteLoop)) return false;
        // The short walk onto their spot isn't walking away from the readied animation.
        movementTrackingStart = Math.Max(movementTrackingStart, Environment.TickCount64 + LineUpTimeoutMs + 600);
        hasMovementSample = false;
        movementFrames = 0;
        movement.WalkTo(position, Snap);
        return true;
    }

    /// <summary>The moment the start fires: one last snap onto the host, who may have shifted while
    /// getting up from a seat.</summary>
    private void SnapToLineUpHost()
    {
        if (lineUpAnchor == 0) return;
        var anchor = lineUpAnchor;
        lineUpAnchor = 0;
        if (Objects.LocalPlayer is not { } local ||
            Objects.FirstOrDefault(gameObject => gameObject.Address == anchor) is not { } host ||
            Vector3.Distance(local.Position, host.Position) > MaxMidEmoteAlignDistance) return;
        ApplyAlignment(host.Position, host.Rotation);
    }
}

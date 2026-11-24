using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using EmoteLink.Align;

namespace EmoteLink.Places;

/// <summary>
/// Draws you back where a saved place was made. The place waits until its animation has really started
/// and settled into its loop, is applied as one layer of your Simple Heels offset, checked and corrected
/// once, and dropped when the loop ends. Never moves the real character. Framework thread only.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class PlaceService(
    IObjectTable objects,
    HeelsOffset heels,
    FurnitureScanner furniture,
    Func<uint> currentTerritory,
    Func<bool> changingPose,
    Func<bool> measuring,
    Action<string> report)
{
    /// <summary>How far from you a place can be, in yalms.</summary>
    public const float MaximumReach = 15f;

    public const string NeedsSimpleHeels =
        "Places need Simple Heels to move you. Without it the animation plays where you stand.";

    // Entering a pose or emote can turn you or slide you into a seat first.
    private const long SettleMs = 700;
    private const long StableMs = 300;
    private const long GiveUpMs = 10000;
    private const long LoopLostMs = 500;
    private const float MaximumCorrection = 2f;

    private enum Phase
    {
        Waiting,
        Settling,
        Adjusting,
        Held
    }

    private readonly record struct Target(Vector3 Position, float Yaw);

    private SavedPlace? place;
    private Phase phase;
    private uint territory;
    private long startedAt;
    private long stableSince;
    private string signature = "";
    private Vector3 stablePosition;
    private float stableYaw;
    private string problem = "";
    private long loopSeenAt;
    private int generation;
    private (byte Mode, byte Param) loop;
    private Target target;
    private Vector3 basePosition;
    private float baseYaw;
    private OffsetLayer correction;
    private PartnerTracker tracker = new();

    /// <summary>A place is waiting for its animation or still being adjusted.</summary>
    public bool IsPlacing => place is not null && phase != Phase.Held;

    /// <summary>A place next to someone is holding you.</summary>
    public bool HoldsPartner => place?.Kind == PlaceKind.Partner && phase is Phase.Adjusting or Phase.Held;

    /// <summary>Waits for the animation that was just activated, which a room may start much later.</summary>
    public void Begin(SavedPlace chosen)
    {
        Forget();
        place = chosen.Copy();   // so "Update to here" doesn't move the place that is holding you
        phase = Phase.Waiting;
        territory = currentTerritory();
    }

    /// <summary>The animation really started.</summary>
    public void Started()
    {
        if (place is null || phase != Phase.Waiting) return;
        phase = Phase.Settling;
        startedAt = Environment.TickCount64;
    }

    /// <summary>Lets go of the place without touching your offset.</summary>
    public void Forget()
    {
        place = null;
        phase = Phase.Waiting;
        stableSince = 0;
        loopSeenAt = 0;
        problem = "";
        correction = OffsetLayer.None;
        tracker = new PartnerTracker();
    }

    public void Tick()
    {
        if (place is not { } current) return;
        if (currentTerritory() != territory)
        {
            Drop();
            return;
        }
        if (phase == Phase.Waiting) return;

        var now = Environment.TickCount64;
        var local = objects.LocalPlayer;
        if (local is not null && !DrawnModel.IsLooping(local)) local = null;
        if (phase == Phase.Settling)
        {
            Settle(current, local, now);
            return;
        }
        if (heels.Generation != generation)
        {
            Forget();   // someone else already let go of the offset
            return;
        }
        // Another loop ends it too: Simple Heels has already dropped an offset set for this one.
        if (local is null || PoseSignature.Loop(local) != loop)
        {
            if (now - loopSeenAt > LoopLostMs) Drop();
            return;
        }
        loopSeenAt = now;
        if (measuring()) return;
        if (current.Kind == PlaceKind.Partner) Follow(current, local, now);
        if (phase == Phase.Adjusting && heels.Settled) Correct(local);
    }

    /// <summary>Whether a place can be reached from here, given the furniture that is placed here.</summary>
    public PlaceReach ReachOf(SavedPlace candidate, IReadOnlyList<NearbyFurniture> placed)
    {
        if (objects.LocalPlayer is not { } local || Resolve(candidate, placed) is not { } found) return PlaceReach.Elsewhere;
        return Vector3.Distance(found.Position, local.Position) <= MaximumReach ? PlaceReach.Here : PlaceReach.TooFar;
    }

    /// <summary>Stores where you're drawn right now, held to the spot, the furniture or the partner.</summary>
    public bool Capture(SavedPlace into, PlaceKind kind, NearbyFurniture? on, IPlayerCharacter? partner)
    {
        // The drawn root rather than the real position: it already holds any offset you made in Simple
        // Heels, by line-up or by bone align.
        if (objects.LocalPlayer is not { } local || !DrawnModel.TryGetRoot(local, out var position, out var yaw)) return false;
        into.TerritoryType = 0;
        into.HouseId = 0;
        switch (kind)
        {
            case PlaceKind.Furniture when on is { } item:
                into.FurnitureId = item.EntryId;
                into.FurnitureName = item.Name;
                (position, yaw) = Relative(position, yaw, item.Position, item.Rotation);
                break;
            case PlaceKind.Partner when partner is not null:
                into.PartnerName = partner.Name.TextValue;
                into.PartnerWorldId = partner.HomeWorld.RowId;
                (position, yaw) = Relative(position, yaw, partner.Position, partner.Rotation);
                break;
            case PlaceKind.Spot:
                into.TerritoryType = currentTerritory();
                into.HouseId = FurnitureScanner.CurrentHouseId();
                break;
            default:
                return false;
        }
        into.Kind = kind;
        into.Position = position;
        into.Rotation = DrawnModel.Wrap(yaw);
        return true;
    }

    /// <summary>The loop you're in has ended, or you left: the offset goes with it.</summary>
    private void Drop()
    {
        var applied = phase is Phase.Adjusting or Phase.Held;
        Forget();
        if (applied) heels.Reset();
    }

    private void Settle(SavedPlace current, IPlayerCharacter? local, long now)
    {
        if (now - startedAt > GiveUpMs)
        {
            report(problem.Length > 0 ? problem : $"{current.Name} couldn't be placed because its animation doesn't loop.");
            Forget();
            return;
        }
        if (local is null || changingPose() || measuring())
        {
            stableSince = 0;
            return;
        }
        var playing = PoseSignature.Of(local);
        if (stableSince == 0 || playing != signature ||
            Vector3.Distance(local.Position, stablePosition) > PartnerTracker.PositionThreshold ||
            MathF.Abs(DrawnModel.Wrap(local.Rotation - stableYaw)) > PartnerTracker.RotationThreshold)
        {
            stableSince = now;
            signature = playing;
            stablePosition = local.Position;
            stableYaw = local.Rotation;
            return;
        }
        if (now - startedAt < SettleMs || now - stableSince < StableMs) return;
        Apply(current, local, now);
    }

    private void Apply(SavedPlace current, IPlayerCharacter local, long now)
    {
        problem = "";
        var placed = current.Kind == PlaceKind.Furniture ? furniture.Scan(local.Position, float.MaxValue) : [];
        if (Resolve(current, placed) is not { } found)
            problem = current.Kind switch
            {
                PlaceKind.Furniture => $"There's no {current.FurnitureName} here, so you're playing where you stand.",
                PlaceKind.Partner => $"{current.PartnerName} isn't nearby, so you're playing where you stand.",
                _ => $"{current.Name} was saved somewhere else, so you're playing where you stand."
            };
        else if (Vector3.Distance(found.Position, local.Position) > MaximumReach)
            problem = $"{current.Name} is more than {MaximumReach:F0} yalms away, so you're playing where you stand.";
        else if (!heels.Available)
            problem = NeedsSimpleHeels;
        else
            target = found;

        if (problem.Length > 0)
        {
            // A partner may still be loading or on their way; the rest won't change.
            if (current.Kind == PlaceKind.Partner) return;
            report(problem);
            Forget();
            return;
        }
        if (!DrawnModel.TryGetRoot(local, out _, out var yaw)) return;
        var held = heels.Applied ? heels.Place.Position + heels.Bone.Position : Vector3.Zero;
        basePosition = heels.Applied ? heels.ExpectedOffset - held : DrawnModel.DrawOffset(local);
        baseYaw = DrawnModel.Wrap(yaw - local.Rotation - heels.Place.Rotation - heels.Bone.Rotation);
        if (current.Kind == PlaceKind.Partner &&
            PartnerTracker.Find(objects, current.PartnerName, current.PartnerWorldId) is { } partner)
            tracker.Remember(partner, local, now);
        heels.SetPlace(LayerFor(local));
        generation = heels.Generation;
        loop = PoseSignature.Loop(local);
        loopSeenAt = now;
        phase = Phase.Adjusting;
    }

    // Anything left over (a seat's own shift, an offset the game adds) shows in where you're drawn.
    private void Correct(IPlayerCharacter local)
    {
        phase = Phase.Held;
        if (heels.Bone != OffsetLayer.None || !DrawnModel.TryGetRoot(local, out var drawn, out var drawnYaw)) return;
        var off = LocalOffset(local.Position, target.Position, target.Yaw) - LocalOffset(local.Position, drawn, drawnYaw);
        var turn = DrawnModel.Wrap(target.Yaw - drawnYaw);
        if (off.Length() > MaximumCorrection) return;
        if (off.Length() <= PartnerTracker.PositionThreshold && MathF.Abs(turn) <= PartnerTracker.RotationThreshold) return;
        correction = new OffsetLayer(off, turn);
        heels.SetPlace(LayerFor(local));
    }

    private void Follow(SavedPlace current, IPlayerCharacter local, long now)
    {
        if (tracker.Frozen ||
            PartnerTracker.Find(objects, current.PartnerName, current.PartnerWorldId) is not { } partner ||
            tracker.CheckLeft(partner, now) || !tracker.ShouldFollow(partner, local, now)) return;
        tracker.Remember(partner, local, now);
        var next = Around(current, partner.Position, partner.Rotation);
        if (Vector3.Distance(next.Position, local.Position) > MaximumReach)
        {
            tracker.Frozen = true;
            report($"{current.PartnerName} moved too far away, so you stay where you are.");
            return;
        }
        target = next;
        var layer = LayerFor(local);
        if (Vector3.Distance(layer.Position, heels.Place.Position) <= 0.001f &&
            MathF.Abs(DrawnModel.Wrap(layer.Rotation - heels.Place.Rotation)) <= 0.0001f) return;
        heels.SetPlace(layer);
    }

    private Target? Resolve(SavedPlace candidate, IReadOnlyList<NearbyFurniture> placed)
    {
        switch (candidate.Kind)
        {
            case PlaceKind.Spot:
                if (candidate.TerritoryType != currentTerritory() ||
                    candidate.HouseId != 0 && candidate.HouseId != FurnitureScanner.CurrentHouseId()) return null;
                return new Target(candidate.Position, candidate.Rotation);
            case PlaceKind.Furniture:
                foreach (var item in placed)   // nearest first
                    if (item.EntryId == candidate.FurnitureId) return Around(candidate, item.Position, item.Rotation);
                return null;
            case PlaceKind.Partner:
                return PartnerTracker.Find(objects, candidate.PartnerName, candidate.PartnerWorldId) is { } partner
                    ? Around(candidate, partner.Position, partner.Rotation)
                    : null;
            default:
                return null;
        }
    }

    private static Target Around(SavedPlace candidate, Vector3 position, float yaw) => new(
        position + Vector3.Transform(candidate.Position, DrawnModel.YawRotation(yaw)),
        DrawnModel.Wrap(yaw + candidate.Rotation));

    private static (Vector3 Position, float Yaw) Relative(Vector3 position, float yaw, Vector3 from, float fromYaw) =>
        (LocalOffset(from, position, fromYaw), yaw - fromYaw);

    // Simple Heels offsets are in the drawn frame, so the turn has to be in them already.
    private static Vector3 LocalOffset(Vector3 from, Vector3 to, float yaw) =>
        Vector3.Transform(to - from, Quaternion.Inverse(DrawnModel.YawRotation(yaw)));

    private OffsetLayer LayerFor(IGameObject local) => new(
        LocalOffset(local.Position, target.Position, target.Yaw) - basePosition + correction.Position,
        DrawnModel.Wrap(target.Yaw - local.Rotation - baseYaw + correction.Rotation));
}

using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace EmoteLink.Align;

/// <summary>One part of your drawn offset: left (+X), up (+Y) and forward (+Z) in your drawn frame,
/// and a turn in radians.</summary>
public readonly record struct OffsetLayer(Vector3 Position, float Rotation)
{
    public static readonly OffsetLayer None = default;
}

/// <summary>
/// The only place Synastry moves your drawn character: a Simple Heels temporary offset, which your
/// sync plugin shares with others. Saved places and bone align each own one layer, and the layers are
/// added together. A temporary offset replaces Simple Heels' own heel height, so the offset you had is
/// captured first and kept underneath. Never moves the real character. Framework thread only.
/// </summary>
/// <remarks>Layering adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
public sealed class HeelsOffset(
    IObjectTable objects,
    Func<string, bool> runCommand,
    Func<bool> simpleHeelsLoaded,
    IPluginLog log)
{
    private const int CommandIntervalMs = 120;
    private const int VerifyDelayMs = 400;
    private const float PositionTolerance = 0.005f;
    private const float YawTolerance = 0.0175f;
    private const float MaximumOffset = 30f;

    private OffsetLayer place;
    private OffsetLayer bone;
    private Vector3 basePosition;
    private float baseYaw;
    // What the game adds under a temporary offset, found by measuring. Kept apart from the base so a
    // later change doesn't find and subtract it a second time.
    private OffsetLayer gameOffset;
    private bool pending;
    private bool corrected;
    private long sentAt;
    private long verifyAt;

    /// <summary>Simple Heels is loaded, so an offset can be shown.</summary>
    public bool Available => simpleHeelsLoaded();

    /// <summary>Synastry has moved you since the last reset.</summary>
    public bool Applied { get; private set; }

    /// <summary>Changes whenever the offset is dropped, so work started before can tell it is stale.</summary>
    public int Generation { get; private set; }

    public OffsetLayer Place => place;
    public OffsetLayer Bone => bone;

    /// <summary>What the game's draw offset should read once the last command has landed.</summary>
    public Vector3 ExpectedOffset => basePosition + place.Position + bone.Position;

    /// <summary>The last command has landed and been checked.</summary>
    public bool Settled => !pending && verifyAt == 0;

    public void SetPlace(OffsetLayer value)
    {
        place = value;
        Changed();
    }

    public void SetBone(OffsetLayer value)
    {
        bone = value;
        Changed();
    }

    /// <summary>Adds to the bone layer, so a turn and a move can be made in one step.</summary>
    public void AddBone(Vector3 position, float yaw = 0f)
    {
        bone = new OffsetLayer(bone.Position + position, DrawnModel.Wrap(bone.Rotation + yaw));
        Changed();
    }

    /// <summary>Drops both layers, and clears the Simple Heels offset if Synastry set one.</summary>
    public void Reset()
    {
        Generation++;
        place = OffsetLayer.None;
        bone = OffsetLayer.None;
        gameOffset = OffsetLayer.None;
        pending = false;
        corrected = false;
        verifyAt = 0;
        if (!Applied) return;
        Applied = false;
        runCommand("/heels temp reset");
    }

    /// <summary>Adopts an offset the player changed in Simple Heels itself, keeping the layers.</summary>
    public void Rebase()
    {
        if (!Applied || objects.LocalPlayer is not { } local) return;
        basePosition = DrawnModel.DrawOffset(local) - place.Position - bone.Position;
        if (DrawnModel.TryGetRoot(local, out _, out var yaw))
            baseYaw = DrawnModel.Wrap(yaw - local.Rotation - place.Rotation - bone.Rotation);
    }

    public void Tick()
    {
        var now = Environment.TickCount64;
        if (pending && now - sentAt >= CommandIntervalMs) Send(now);
        if (verifyAt != 0 && now >= verifyAt) Verify(now);
    }

    private void Changed()
    {
        corrected = false;
        if (!Available || objects.LocalPlayer is not { } local) return;
        if (!Applied && !Capture(local)) return;
        pending = true;
        var now = Environment.TickCount64;
        if (now - sentAt >= CommandIntervalMs) Send(now);
    }

    private bool Capture(ICharacter local)
    {
        if (!DrawnModel.TryGetRoot(local, out _, out var yaw)) return false;
        basePosition = DrawnModel.DrawOffset(local);
        baseYaw = DrawnModel.Wrap(yaw - local.Rotation);
        return true;
    }

    private void Send(long now)
    {
        pending = false;
        var total = ExpectedOffset - gameOffset.Position;
        var yaw = DrawnModel.Wrap(baseYaw + place.Rotation + bone.Rotation - gameOffset.Rotation);
        if (!float.IsFinite(yaw) || !float.IsFinite(total.LengthSquared()) || total.Length() > MaximumOffset)
        {
            log.Warning("Ignored an offset that was out of range ({Offset}).", total);
            return;
        }
        Applied = true;
        sentAt = now;
        verifyAt = now + VerifyDelayMs;
        runCommand(string.Create(CultureInfo.InvariantCulture,
            $"/heels temp set height {total.Y:0.####} left {total.X:0.####} forward {total.Z:0.####} " +
            $"rotate {yaw * 180f / MathF.PI:0.####} silent"));
    }

    // The game can add an offset of its own under the temporary one (some seats and races do), so what
    // was asked for can land a little off. Corrected once, after Simple Heels has had time to apply it.
    private void Verify(long now)
    {
        verifyAt = 0;
        if (corrected || !Applied || objects.LocalPlayer is not { } local || !DrawnModel.IsLooping(local) ||
            !DrawnModel.TryGetRoot(local, out _, out var drawnYaw)) return;
        var offset = DrawnModel.DrawOffset(local) - ExpectedOffset;
        var expectedYaw = DrawnModel.Wrap(baseYaw + place.Rotation + bone.Rotation);
        var yaw = DrawnModel.Wrap(drawnYaw - local.Rotation - expectedYaw);
        if (offset.Length() > 2f) return;
        if (offset.Length() <= PositionTolerance && MathF.Abs(yaw) <= YawTolerance) return;
        corrected = true;
        gameOffset = new OffsetLayer(gameOffset.Position + offset, DrawnModel.Wrap(gameOffset.Rotation + yaw));
        Send(now);
    }
}

using System.Numerics;

namespace EmoteLink.Camera;

/// <summary>The free camera's position and speed. Camera math only; never reads or writes an actor.</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class FreeCamMotion
{
    private const float BaseSpeed = 14f;
    // Up and down is much easier to overshoot than horizontal travel, so it runs slower.
    private const float VerticalScale = 0.3f;
    // Four half-lives reach about 94%: roughly 0.2 s to full speed and 0.3 s to rest.
    private const float StartHalfLifeSeconds = 0.05f;
    private const float StopHalfLifeSeconds = 0.07f;
    private const float MaxStepSeconds = 0.05f;
    // A stopping camera below this speed is set at rest, cutting the exponential's long creep.
    private const float RestSpeed = 0.5f;

    private Vector3 velocity;
    public Vector3 Position { get; private set; }

    public void Initialize(Vector3 position)
    {
        if (!IsFinite(position)) throw new ArgumentException("The current camera view is invalid.");
        Position = position;
        velocity = Vector3.Zero;
    }

    /// <summary>
    /// Moves along the camera's live rotation, so the game's own mouse look keeps working. Input is X
    /// strafe, Y up, Z forward. Velocity approaches the input's target exponentially: the same at any
    /// frame rate, eased in and out, never overshooting.
    /// </summary>
    public void Step(Vector3 input, float hRotation, float vRotation, float seconds, float speed)
    {
        if (!IsFinite(input) || !float.IsFinite(hRotation) || !float.IsFinite(vRotation) ||
            !float.IsFinite(seconds) || !float.IsFinite(speed))
            return;
        var dt = Math.Clamp(seconds, 0f, MaxStepSeconds);
        var target = Target(input, hRotation, vRotation) * speed;
        var halfLife = target == Vector3.Zero ? StopHalfLifeSeconds : StartHalfLifeSeconds;
        velocity = Vector3.Lerp(velocity, target, 1f - MathF.Pow(0.5f, dt / halfLife));
        var rest = RestSpeed * speed;
        if (target == Vector3.Zero && velocity.LengthSquared() < rest * rest) velocity = Vector3.Zero;
        Position += velocity * dt;
    }

    public static Vector3 Forward(float hRotation, float vRotation) =>
        new(MathF.Sin(hRotation) * MathF.Cos(vRotation), MathF.Sin(vRotation), -MathF.Cos(hRotation) * MathF.Cos(vRotation));

    private static Vector3 Target(Vector3 input, float hRotation, float vRotation)
    {
        if (input == Vector3.Zero) return Vector3.Zero;
        var right = new Vector3(MathF.Cos(hRotation), 0, MathF.Sin(hRotation));
        var planar = right * input.X + Forward(hRotation, vRotation) * input.Z;
        if (planar.LengthSquared() > 1f) planar = Vector3.Normalize(planar);
        // Vertical is added after normalizing so its slower speed isn't diluted by diagonal travel.
        return (planar + Vector3.UnitY * (input.Y * VerticalScale)) * BaseSpeed;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

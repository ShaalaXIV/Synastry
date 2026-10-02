using System.Numerics;

namespace EmoteLink.Ik;

/// <summary>
/// Rotation-only inverse kinematics. Every solve keeps bone lengths exactly as they are: limbs
/// bend at their joints and stop short when a target is out of reach, they never stretch.
/// All positions and rotations here are in one shared space (model or world); callers convert.
/// </summary>
internal static class IkSolver
{
    /// <summary>
    /// Two-bone solve for an upper limb or leg: root (shoulder/hip), middle (elbow/knee), end
    /// (wrist/ankle). Returns the rotations to apply on top of the current root and middle
    /// rotations, or null when nothing should change.
    /// </summary>
    /// <param name="pole">A point the middle joint should bend toward, such as behind and below the
    /// elbow. It decides which way the joint folds, so elbows never bend backwards.</param>
    /// <param name="maxReach">Fraction of the full limb length the solve may use; below 1 keeps the
    /// limb from locking perfectly straight.</param>
    /// <param name="minBendDegrees">The smallest angle the middle joint may close to, so a forearm
    /// can't fold through the upper arm.</param>
    public static (Quaternion Root, Quaternion Middle)? TwoBone(
        Vector3 root, Vector3 middle, Vector3 end, Vector3 target, Vector3 pole,
        float maxReach = 0.98f, float minBendDegrees = 20f)
    {
        var upper = Vector3.Distance(root, middle);
        var lower = Vector3.Distance(middle, end);
        if (upper < 1e-5f || lower < 1e-5f) return null;

        var toTarget = target - root;
        var targetDistance = toTarget.Length();
        if (targetDistance < 1e-5f) return null;

        // Never ask for more than the limb has (no stretching), nor so little that it folds shut.
        var minimum = MathF.Sqrt(upper * upper + lower * lower -
                                 2f * upper * lower * MathF.Cos(DegreesToRadians(minBendDegrees)));
        var reach = Math.Clamp(targetDistance, minimum, (upper + lower) * maxReach);
        var direction = toTarget / targetDistance;

        // Law of cosines: the angle at the root between the target direction and the upper bone.
        var cosRoot = Math.Clamp((upper * upper + reach * reach - lower * lower) / (2f * upper * reach), -1f, 1f);
        var rootAngle = MathF.Acos(cosRoot);

        // The plane the limb bends in comes from the pole, so the joint folds the natural way.
        var poleDirection = pole - root;
        var bendAxis = Vector3.Cross(direction, poleDirection);
        if (bendAxis.LengthSquared() < 1e-8f)
        {
            bendAxis = Vector3.Cross(direction, middle - root);
            if (bendAxis.LengthSquared() < 1e-8f) return null;
        }
        bendAxis = Vector3.Normalize(bendAxis);

        var newMiddle = root + Vector3.Transform(direction, Quaternion.CreateFromAxisAngle(bendAxis, rootAngle)) * upper;
        var newEnd = root + direction * reach;

        var rootRotation = FromTo(middle - root, newMiddle - root);
        // The middle joint's correction is measured after the root has already turned.
        var turnedEnd = root + Vector3.Transform(end - root, rootRotation);
        var turnedMiddle = newMiddle;
        var middleRotation = FromTo(turnedEnd - turnedMiddle, newEnd - newMiddle);
        return (rootRotation, middleRotation);
    }

    /// <summary>
    /// Bends a chain (base to tip) so its tip moves toward <paramref name="target"/>, by rotation
    /// only, with each joint turning at most <paramref name="maxDegreesPerJoint"/> from where the
    /// animation put it. Returns one rotation per joint (excluding the tip), applied in order from
    /// the base, or null when the chain is too short to bend.
    /// </summary>
    public static Quaternion[]? Chain(
        IReadOnlyList<Vector3> joints, Vector3 target, float maxDegreesPerJoint = 14f, int iterations = 12)
    {
        if (joints.Count < 2) return null;
        var points = joints.ToArray();
        var count = points.Length - 1;
        var applied = Enumerable.Repeat(Quaternion.Identity, count).ToArray();
        var limit = DegreesToRadians(maxDegreesPerJoint);

        // Cyclic coordinate descent from the joint nearest the tip back to the base: each pass
        // turns one joint to aim the tip at the target, within that joint's remaining budget.
        for (var pass = 0; pass < iterations; pass++)
        {
            for (var joint = count - 1; joint >= 0; joint--)
            {
                var pivot = points[joint];
                var toTip = points[^1] - pivot;
                var toTarget = target - pivot;
                if (toTip.LengthSquared() < 1e-10f || toTarget.LengthSquared() < 1e-10f) continue;

                var step = FromTo(toTip, toTarget);
                var total = Quaternion.Normalize(step * applied[joint]);
                total = ClampAngle(total, limit);
                step = Quaternion.Normalize(total * Quaternion.Inverse(applied[joint]));
                applied[joint] = total;

                for (var after = joint + 1; after < points.Length; after++)
                    points[after] = pivot + Vector3.Transform(points[after] - pivot, step);
            }
            if (Vector3.DistanceSquared(points[^1], target) < 1e-8f) break;
        }
        return applied;
    }

    /// <summary>The shortest rotation taking direction <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var a = Vector3.Normalize(from);
        var b = Vector3.Normalize(to);
        var dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 0.999999f) return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, b)), MathF.Acos(dot));
    }

    /// <summary>Limits a rotation to at most <paramref name="maxRadians"/> around its own axis.</summary>
    public static Quaternion ClampAngle(Quaternion rotation, float maxRadians)
    {
        rotation = Quaternion.Normalize(rotation);
        if (rotation.W < 0) rotation = new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
        var angle = 2f * MathF.Acos(Math.Clamp(rotation.W, -1f, 1f));
        if (angle <= maxRadians) return rotation;
        var axis = new Vector3(rotation.X, rotation.Y, rotation.Z);
        if (axis.LengthSquared() < 1e-12f) return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), maxRadians);
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> without overshooting,
    /// so effects ease in and out over <paramref name="seconds"/>.</summary>
    public static float Approach(float current, float target, float deltaSeconds, float seconds = 0.25f)
    {
        var step = seconds <= 0 ? 1f : deltaSeconds / seconds;
        return current < target ? MathF.Min(target, current + step) : MathF.Max(target, current - step);
    }

    /// <summary>Smoothstep easing, so a 0→1 weight starts and ends gently.</summary>
    public static float Ease(float weight) => weight * weight * (3f - 2f * weight);

    public static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;
}

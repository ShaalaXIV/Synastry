using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace EmoteLink.Ik;

/// <summary>
/// One character's skeleton for a single frame: where its bones are, in the character's own
/// (model) space and in the world, and the means to turn them. Positions are read once, after
/// the game has finished animating; every change is a rotation written back with propagation,
/// so children follow rigidly and no bone ever changes length.
/// </summary>
internal sealed unsafe class SkeletonView
{
    private static readonly Dictionary<nint, Dictionary<string, int>> NameCache = [];

    private readonly CharacterBase* character;
    private readonly Dictionary<string, (int Partial, int Index)> bones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector3> positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Quaternion> rotations = new(StringComparer.Ordinal);
    private readonly Vector3 origin;
    private readonly Quaternion orientation;
    private readonly Vector3 scale;

    private SkeletonView(CharacterBase* character, Vector3 origin, Quaternion orientation, Vector3 scale)
    {
        this.character = character;
        this.origin = origin;
        this.orientation = orientation;
        this.scale = scale;
    }

    public string Name { get; private init; } = "";
    public ulong ObjectId { get; private init; }

    /// <summary>Uniform size of this character, for converting measured distances to the world.</summary>
    public float Scale => (scale.X + scale.Y + scale.Z) / 3f;

    public static SkeletonView? Read(nint drawObject, string name, ulong objectId, IReadOnlyCollection<string> wanted)
    {
        if (drawObject == nint.Zero) return null;
        var baseObject = (DrawObject*)drawObject;
        if (baseObject->GetObjectType() != ObjectType.CharacterBase) return null;
        var characterBase = (CharacterBase*)drawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return null;
        var skeleton = characterBase->Skeleton;
        if (skeleton is null) return null;

        var transform = skeleton->Transform;
        var view = new SkeletonView(characterBase,
            new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z),
            Quaternion.Normalize(new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W)),
            new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z))
        {
            Name = name,
            ObjectId = objectId
        };
        if (MathF.Abs(view.scale.X) < 1e-4f || MathF.Abs(view.scale.Y) < 1e-4f || MathF.Abs(view.scale.Z) < 1e-4f)
            return null;

        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (pose is null || pose->Skeleton is null) continue;
            var names = NamesOf(pose->Skeleton);
            pose->SyncModelSpace();
            foreach (var wantedName in wanted)
            {
                if (view.bones.ContainsKey(wantedName) || !names.TryGetValue(wantedName, out var index)) continue;
                if (index >= pose->ModelPose.Length) continue;
                var model = pose->ModelPose[index];
                view.bones[wantedName] = (p, index);
                view.positions[wantedName] = new Vector3(model.Translation.X, model.Translation.Y, model.Translation.Z);
                view.rotations[wantedName] = Quaternion.Normalize(
                    new Quaternion(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W));
            }
        }
        return view;
    }

    /// <summary>Bone names by index, cached per skeleton; checked against the bone count each time
    /// because a reloaded model can reuse the same memory.</summary>
    private static Dictionary<string, int> NamesOf(hkaSkeleton* skeleton)
    {
        var key = (nint)skeleton;
        var count = skeleton->Bones.Length;
        if (NameCache.TryGetValue(key, out var cached) && cached.Count > 0 && cached.Count <= count &&
            cached.TryGetValue(skeleton->Bones[0].Name.String ?? "", out var first) && first == 0)
            return cached;
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var boneName = skeleton->Bones[i].Name.String;
            if (boneName is not null) names.TryAdd(boneName, i);
        }
        if (NameCache.Count > 256) NameCache.Clear();
        NameCache[key] = names;
        return names;
    }

    public bool Has(string bone) => positions.ContainsKey(bone);

    public bool HasAll(params string[] names) => names.All(positions.ContainsKey);

    /// <summary>Model-space position as the animation left it.</summary>
    public Vector3 Model(string bone) => positions[bone];

    public Quaternion ModelRotation(string bone) => rotations[bone];

    public bool TryModel(string bone, out Vector3 position) => positions.TryGetValue(bone, out position);

    public Vector3 World(string bone) => ToWorld(positions[bone]);

    public bool TryWorld(string bone, out Vector3 world)
    {
        if (positions.TryGetValue(bone, out var model))
        {
            world = ToWorld(model);
            return true;
        }
        world = default;
        return false;
    }

    public Vector3 ToWorld(Vector3 model) => origin + Vector3.Transform(model * scale, orientation);

    public Vector3 ToModel(Vector3 world)
    {
        var unrotated = Vector3.Transform(world - origin, Quaternion.Inverse(orientation));
        return new Vector3(unrotated.X / scale.X, unrotated.Y / scale.Y, unrotated.Z / scale.Z);
    }

    /// <summary>Turns a bone by <paramref name="delta"/> (model space) about its own position. Its
    /// children follow, keeping their lengths; nothing is translated or scaled.</summary>
    public void Rotate(string bone, Quaternion delta)
    {
        if (!bones.TryGetValue(bone, out var location)) return;
        if (Quaternion.Dot(delta, Quaternion.Identity) is > 0.9999999f or < -0.9999999f) return;
        var skeleton = character->Skeleton;
        if (skeleton is null || location.Partial >= skeleton->PartialSkeletonCount) return;
        var pose = skeleton->PartialSkeletons[location.Partial].GetHavokPose(0);
        if (pose is null || pose->Skeleton is null || location.Index >= pose->Skeleton->Bones.Length) return;
        var transform = pose->AccessBoneModelSpace(location.Index, hkaPose.PropagateOrNot.Propagate);
        if (transform is null) return;
        var current = Quaternion.Normalize(new Quaternion(
            transform->Rotation.X, transform->Rotation.Y, transform->Rotation.Z, transform->Rotation.W));
        var result = Quaternion.Normalize(delta * current);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.W)) return;
        transform->Rotation.X = result.X;
        transform->Rotation.Y = result.Y;
        transform->Rotation.Z = result.Z;
        transform->Rotation.W = result.W;
    }

    /// <summary>Slides an opening bone by a small model-space offset. Used only for the IVCS/YAS
    /// opening bones, whose job is exactly this; limbs are never moved this way.</summary>
    public void Shift(string bone, Vector3 offset)
    {
        if (!bones.TryGetValue(bone, out var location) || offset.LengthSquared() < 1e-12f) return;
        var skeleton = character->Skeleton;
        if (skeleton is null || location.Partial >= skeleton->PartialSkeletonCount) return;
        var pose = skeleton->PartialSkeletons[location.Partial].GetHavokPose(0);
        if (pose is null || pose->Skeleton is null || location.Index >= pose->Skeleton->Bones.Length) return;
        var transform = pose->AccessBoneModelSpace(location.Index, hkaPose.PropagateOrNot.Propagate);
        if (transform is null) return;
        transform->Translation.X += offset.X;
        transform->Translation.Y += offset.Y;
        transform->Translation.Z += offset.Z;
    }
}

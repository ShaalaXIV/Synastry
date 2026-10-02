using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using EmoteLink.Ik.Mdl;

namespace EmoteLink.Ik;

/// <summary>Everything body setup needs from the game, captured on the framework thread.</summary>
internal sealed record BodyCapture(
    Dictionary<string, Vector3> BindBones,
    IReadOnlyList<string> Models,
    string Rig,
    string Signature);

/// <summary>
/// Body setup: reads the skeleton in its neutral pose and the model files Penumbra has loaded
/// for the character (their modded body included), then measures how far the skin sits from
/// each bone, the shaft, the openings that exist, and the palms. Measuring happens in the bind
/// pose, the same pose the model files are stored in.
/// </summary>
internal static class BodyMeasurer
{
    // A bone needs at least this many vertices it mostly drives to be measured.
    private const int MinimumVertices = 24;
    private const float DominantWeight = 0.5f;

    // The two chains a shaft's mesh can follow; setup keeps whichever the mesh really uses.
    internal static readonly string[][] ShaftChains =
    [
        ["iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f"],
        ["iv_funyachin_phy_a", "iv_funyachin_phy_b", "iv_funyachin_phy_c", "iv_funyachin_phy_d"],
    ];

    /// <summary>Reads the neutral skeleton and the loaded model list. Framework thread only.</summary>
    public static unsafe BodyCapture? Capture(nint drawObject, ushort objectIndex, PenumbraService penumbra)
    {
        if (drawObject == nint.Zero) return null;
        var baseObject = (DrawObject*)drawObject;
        if (baseObject->GetObjectType() != ObjectType.CharacterBase) return null;
        var character = (CharacterBase*)drawObject;
        if (character->GetModelType() != CharacterBase.ModelType.Human || character->Skeleton is null) return null;

        var bind = new Dictionary<string, (Vector3 Position, Quaternion Rotation)>(StringComparer.Ordinal);
        var skeleton = character->Skeleton;
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (pose is null || pose->Skeleton is null) continue;
            var havok = pose->Skeleton;
            var count = Math.Min(havok->Bones.Length, Math.Min(havok->ReferencePose.Length, havok->ParentIndices.Length));
            var names = new string[count];
            var model = new (Vector3 Position, Quaternion Rotation)[count];
            for (var i = 0; i < count; i++)
            {
                names[i] = havok->Bones[i].Name.String ?? "";
                var local = havok->ReferencePose[i];
                var position = new Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z);
                var rotation = Quaternion.Normalize(new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W));
                var parent = havok->ParentIndices[i];
                model[i] = parent >= 0 && parent < i
                    ? (model[parent].Position + Vector3.Transform(position, model[parent].Rotation),
                       Quaternion.Normalize(model[parent].Rotation * rotation))
                    : (position, rotation);
            }

            // Face, hair and other partial skeletons attach at a bone the body also has; move them
            // onto it so every bone ends up in the one model space the meshes use.
            var offset = (Position: Vector3.Zero, Rotation: Quaternion.Identity);
            if (p > 0)
                for (var i = 0; i < count; i++)
                    if (havok->ParentIndices[i] < 0 && bind.TryGetValue(names[i], out var anchor))
                    {
                        var rotation = Quaternion.Normalize(anchor.Rotation * Quaternion.Inverse(model[i].Rotation));
                        offset = (anchor.Position - Vector3.Transform(model[i].Position, rotation), rotation);
                        break;
                    }
            for (var i = 0; i < count; i++)
            {
                if (names[i].Length == 0 || bind.ContainsKey(names[i])) continue;
                bind[names[i]] = (offset.Position + Vector3.Transform(model[i].Position, offset.Rotation),
                    Quaternion.Normalize(offset.Rotation * model[i].Rotation));
            }
        }
        if (bind.Count == 0) return null;

        var models = penumbra.GetLoadedModels(objectIndex);
        if (models.Count == 0) return null;
        var rig = bind.ContainsKey("iv_ochinko_a") || bind.ContainsKey("iv_omanko") ? "ivcs" : "vanilla";
        var signature = SignatureOf(models);
        return new BodyCapture(bind.ToDictionary(pair => pair.Key, pair => pair.Value.Position, StringComparer.Ordinal),
            models, rig, signature);
    }

    /// <summary>Fingerprint of a set of loaded models, so a saved mesh can be recognised again.</summary>
    public static string SignatureOf(IReadOnlyList<string> models) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", models.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)))))[..16];

    /// <summary>Loads the models and measures them. Safe to run off the framework thread.</summary>
    public static BodyProfile Measure(BodyCapture capture, IDataManager data, IPluginLog log)
    {
        var buckets = new Dictionary<string, List<Vector3>>(StringComparer.Ordinal);
        var readModels = 0;
        foreach (var path in capture.Models)
        {
            try
            {
                // Read the bytes ourselves: Lumina can't parse Dawntrail (v6) models.
                var bytes = Path.IsPathRooted(path) ? File.ReadAllBytes(path) : data.GetFile(path)?.Data;
                if (bytes is null) continue;
                foreach (var (position, bone, weight) in SkinnedVertices.Read(bytes))
                {
                    if (weight < DominantWeight) continue;
                    if (!buckets.TryGetValue(bone, out var list)) buckets[bone] = list = [];
                    list.Add(position);
                }
                readModels++;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Body setup skipped a model it couldn't read: {Path}", path);
            }
        }
        log.Information("Body setup read {Read} of {Total} models, {Bones} bones with skin.",
            readModels, capture.Models.Count, buckets.Count);

        var bind = capture.BindBones;
        var profile = new BodyProfile { Rig = capture.Rig, BodySignature = capture.Signature };

        // How far the skin sits from each surface bone.
        foreach (var (from, to, _) in ContactIkService.Surfaces)
        {
            if (!bind.TryGetValue(from, out var a) || !bind.TryGetValue(to, out var b) ||
                !buckets.TryGetValue(from, out var vertices) || vertices.Count < MinimumVertices) continue;
            profile.SurfaceRadius[from] = Median(vertices.Select(vertex => Vector3.Distance(vertex, ClosestOnSegment(a, b, vertex))));
        }

        // Palms: wrist bone to palm skin.
        foreach (var side in new[] { "l", "r" })
        {
            if (!bind.TryGetValue($"j_te_{side}", out var wrist) || !bind.TryGetValue($"j_naka_a_{side}", out var knuckle) ||
                !buckets.TryGetValue($"j_te_{side}", out var vertices) || vertices.Count < MinimumVertices) continue;
            var depth = Median(vertices.Select(vertex => Vector3.Distance(vertex, ClosestOnSegment(wrist, knuckle, vertex))));
            profile.Hands[side] = new HandShape { PalmDepth = depth, GripWidth = depth * 2f + 0.02f };
        }

        // The shaft: whichever chain its mesh follows, its thickness, and how far the tip runs past
        // the last bone.
        foreach (var chain in ShaftChains)
        {
            var names = chain.Where(bind.ContainsKey).ToList();
            var shaftBones = names.Select(name => bind[name]).ToList();
            var shaftVertices = names.SelectMany(name => buckets.GetValueOrDefault(name) ?? []).ToList();
            if (shaftBones.Count < 2 || PolylineLength(shaftBones) < 0.03f || shaftVertices.Count < MinimumVertices) continue;
            if (profile.Shaft is not null && shaftVertices.Count <= profile.Shaft.Chain.Sum(name => buckets.GetValueOrDefault(name)?.Count ?? 0))
                continue;
            var direction = Vector3.Normalize(shaftBones[^1] - shaftBones[^2]);
            profile.Shaft = new ShaftShape
            {
                Chain = names,
                Length = PolylineLength(shaftBones),
                Radius = Median(shaftVertices.Select(vertex => Vector3.Distance(vertex, ClosestOnPolyline(shaftBones, vertex)))),
                TipBeyondLastBone = MathF.Max(0f, shaftVertices.Max(vertex => Vector3.Dot(vertex - shaftBones[^1], direction)))
            };
        }

        // Openings that really exist on this body.
        AddOpening(profile, "vagina", "iv_omanko", ["iv_omanko", "iv_inshin_l", "iv_inshin_r"], bind, buckets, 0.02f);
        AddOpening(profile, "anus", "iv_koumon", ["iv_koumon", "iv_koumon_l", "iv_koumon_r"], bind, buckets, 0.012f);
        var lipLeft = new[] { "j_f_ulip_01_l", "j_f_lip_l" }.FirstOrDefault(bind.ContainsKey);
        var lipRight = new[] { "j_f_ulip_01_r", "j_f_lip_r" }.FirstOrDefault(bind.ContainsKey);
        if (lipLeft is not null && lipRight is not null)
            profile.Openings["mouth"] = new OpeningShape
            {
                Bone = "j_ago",
                Radius = Vector3.Distance(bind[lipLeft], bind[lipRight]) * 0.5f,
                MaxOpen = 0.03f,
                Depth = 0.05f
            };
        return profile;
    }

    private static void AddOpening(BodyProfile profile, string key, string centre, string[] bones,
        Dictionary<string, Vector3> bind, Dictionary<string, List<Vector3>> buckets, float maxOpen)
    {
        if (!bind.TryGetValue(centre, out var point)) return;
        var vertices = bones.SelectMany(name => buckets.GetValueOrDefault(name) ?? []).ToList();
        // Fewer vertices than this means the bone exists but the mesh doesn't use it.
        if (vertices.Count < MinimumVertices) return;
        var distances = vertices.Select(vertex => Vector3.Distance(vertex, point)).OrderBy(value => value).ToList();
        var radius = distances[(int)(distances.Count * 0.2f)];
        profile.Openings[key] = new OpeningShape
        {
            Bone = centre,
            Radius = radius,
            MaxOpen = Math.Clamp(radius * 1.5f, 0.006f, maxOpen),
            Depth = Math.Clamp(distances[^1], 0.03f, 0.12f)
        };
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Where(float.IsFinite).OrderBy(value => value).ToList();
        return sorted.Count == 0 ? 0f : sorted[sorted.Count / 2];
    }

    private static float PolylineLength(IReadOnlyList<Vector3> points)
    {
        var length = 0f;
        for (var i = 0; i + 1 < points.Count; i++) length += Vector3.Distance(points[i], points[i + 1]);
        return length;
    }

    private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-10f) return a;
        return a + ab * Math.Clamp(Vector3.Dot(point - a, ab) / lengthSquared, 0f, 1f);
    }

    private static Vector3 ClosestOnPolyline(IReadOnlyList<Vector3> points, Vector3 target)
    {
        var closest = points[0];
        var closestDistance = float.MaxValue;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var candidate = ClosestOnSegment(points[i], points[i + 1], target);
            var distance = Vector3.DistanceSquared(candidate, target);
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closest = candidate;
        }
        return closest;
    }
}

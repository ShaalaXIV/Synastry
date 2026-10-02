using System.Numerics;

namespace EmoteLink.Ik.Mdl;

/// <summary>
/// The skinned vertices of a model's highest-detail level: each vertex's bind-pose position and
/// the bone that drives most of it. Dawntrail models can carry eight weights per vertex in two
/// sets of four, so both sets are read.
/// </summary>
internal static class SkinnedVertices
{
    public static IEnumerable<(Vector3 Position, string Bone, float Weight)> Read(byte[] bytes)
    {
        var format = MdlFormat.Read(bytes);
        if (format.Lods.Length == 0 || format.BoneNames.Length == 0) yield break;
        var lod = format.Lods[0];
        var vertexBase = (int)format.VertexOffset[0];

        for (var meshIndex = lod.MeshIndex; meshIndex < lod.MeshIndex + lod.MeshCount && meshIndex < format.Meshes.Length; meshIndex++)
        {
            var mesh = format.Meshes[meshIndex];
            if (meshIndex >= format.Declarations.Length || mesh.BoneTableIndex >= format.BoneTables.Length) continue;
            var table = format.BoneTables[mesh.BoneTableIndex].BoneIndices
                .Select(index => index < format.BoneNames.Length ? format.BoneNames[index] : "")
                .ToArray();

            MdlFormat.VertexElement? position = null;
            var weights = new MdlFormat.VertexElement?[2];
            var indices = new MdlFormat.VertexElement?[2];
            foreach (var element in format.Declarations[meshIndex].Elements)
            {
                if (!VertexCodec.IsSupported(element.Type) && element.Type != EightBytes) continue;
                switch ((MdlVertexUsage)element.Usage)
                {
                    case MdlVertexUsage.Position: position ??= element; break;
                    case MdlVertexUsage.BlendWeights when element.UsageIndex < 2: weights[element.UsageIndex] = element; break;
                    case MdlVertexUsage.BlendIndices when element.UsageIndex < 2: indices[element.UsageIndex] = element; break;
                }
            }
            if (position is not { } positionElement || weights[0] is null || indices[0] is null) continue;

            for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                var point = ReadElement(bytes, vertexBase, mesh, positionElement, vertex);
                if (point is not { } p) continue;
                var bestBone = "";
                var bestWeight = 0f;
                for (var set = 0; set < 2; set++)
                {
                    if (weights[set] is not { } weightElement || indices[set] is not { } indexElement) continue;
                    var w = ReadBytes(bytes, vertexBase, mesh, weightElement, vertex);
                    var i = ReadBytes(bytes, vertexBase, mesh, indexElement, vertex);
                    if (w is null || i is null) continue;
                    for (var slot = 0; slot < Math.Min(w.Length, i.Length); slot++) Consider(w[slot], i[slot]);
                }
                if (bestBone.Length > 0) yield return (new Vector3(p.X, p.Y, p.Z), bestBone, bestWeight);

                void Consider(float weight, float index)
                {
                    var slot = (int)index;
                    if (weight <= bestWeight || slot < 0 || slot >= table.Length || table[slot].Length == 0) return;
                    bestWeight = weight;
                    bestBone = table[slot];
                }
            }
        }
    }

    // Dawntrail's eight-influence format: eight unorm weight bytes, or eight bone-index bytes.
    private const byte EightBytes = 17;

    /// <summary>Blend weights (0..1) or bone indices of one vertex, four or eight of them.</summary>
    private static float[]? ReadBytes(byte[] bytes, int vertexBase, MdlFormat.Mesh mesh, MdlFormat.VertexElement element, int vertex)
    {
        if (element.Type != EightBytes)
            return ReadElement(bytes, vertexBase, mesh, element, vertex) is { } v ? [v.X, v.Y, v.Z, v.W] : null;
        if (element.Stream >= mesh.VertexBufferStride.Length) return null;
        var offset = vertexBase + (int)mesh.VertexBufferOffset[element.Stream] +
                     vertex * mesh.VertexBufferStride[element.Stream] + element.Offset;
        if (offset < 0 || offset + 8 > bytes.Length) return null;
        var isWeight = element.Usage == (byte)MdlVertexUsage.BlendWeights;
        var values = new float[8];
        for (var k = 0; k < 8; k++) values[k] = isWeight ? bytes[offset + k] / 255f : bytes[offset + k];
        return values;
    }

    private static Vector4? ReadElement(byte[] bytes, int vertexBase, MdlFormat.Mesh mesh, MdlFormat.VertexElement element, int vertex)
    {
        if (element.Stream >= mesh.VertexBufferStride.Length) return null;
        var stride = mesh.VertexBufferStride[element.Stream];
        var offset = vertexBase + (int)mesh.VertexBufferOffset[element.Stream] + vertex * stride + element.Offset;
        var type = (MdlVertexType)element.Type;
        if (offset < 0 || offset + VertexCodec.SizeOf(type) > bytes.Length) return null;
        return VertexCodec.Read(bytes.AsSpan(offset), type);
    }
}

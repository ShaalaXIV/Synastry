using System;
using System.Numerics;

namespace EmoteLink.Ik.Mdl;

/// <summary>
/// Vertex element storage formats used by FFXIV .mdl vertex declarations.
/// Values verified against Lumina's Vertex.VertexType; the ones Lumina omits are
/// left out deliberately - an unknown format is reported rather than guessed at,
/// because silently mis-decoding a stream corrupts the file on save.
/// </summary>
internal enum MdlVertexType : byte
{
    Single1    = 0,
    Single2    = 1,
    Single3    = 2,
    Single4    = 3,
    UInt       = 5,
    ByteFloat4 = 8,
    Half2      = 13,
    Half4      = 14,
}

internal enum MdlVertexUsage : byte
{
    Position     = 0,
    BlendWeights = 1,
    BlendIndices = 2,
    Normal       = 3,
    UV           = 4,
    Tangent2     = 5,
    Tangent1     = 6,
    Color        = 7,
}

internal static class VertexCodec
{
    public static bool IsSupported(byte type) => type is 0 or 1 or 2 or 3 or 5 or 8 or 13 or 14;

    public static int SizeOf(MdlVertexType t) => t switch
    {
        MdlVertexType.Single1    => 4,
        MdlVertexType.Single2    => 8,
        MdlVertexType.Single3    => 12,
        MdlVertexType.Single4    => 16,
        MdlVertexType.UInt       => 4,
        MdlVertexType.ByteFloat4 => 4,
        MdlVertexType.Half2      => 4,
        MdlVertexType.Half4      => 8,
        _ => throw new NotSupportedException($"vertex type {(byte)t}"),
    };

    /// <summary>Decodes one element to a Vector4. Missing components read as 0, W defaults to 1.</summary>
    public static Vector4 Read(ReadOnlySpan<byte> src, MdlVertexType type)
    {
        switch (type)
        {
            case MdlVertexType.Single1:
                return new Vector4(BitConverter.ToSingle(src), 0, 0, 1);
            case MdlVertexType.Single2:
                return new Vector4(BitConverter.ToSingle(src), BitConverter.ToSingle(src[4..]), 0, 1);
            case MdlVertexType.Single3:
                return new Vector4(BitConverter.ToSingle(src), BitConverter.ToSingle(src[4..]),
                                   BitConverter.ToSingle(src[8..]), 1);
            case MdlVertexType.Single4:
                return new Vector4(BitConverter.ToSingle(src), BitConverter.ToSingle(src[4..]),
                                   BitConverter.ToSingle(src[8..]), BitConverter.ToSingle(src[12..]));
            case MdlVertexType.Half2:
                return new Vector4(Hf(src, 0), Hf(src, 2), 0, 1);
            case MdlVertexType.Half4:
                return new Vector4(Hf(src, 0), Hf(src, 2), Hf(src, 4), Hf(src, 6));
            case MdlVertexType.ByteFloat4:
                // unorm bytes; callers that need signed data (normals/tangents) expand via ToSigned.
                return new Vector4(src[0] / 255f, src[1] / 255f, src[2] / 255f, src[3] / 255f);
            case MdlVertexType.UInt:
                return new Vector4(src[0], src[1], src[2], src[3]);
            default:
                throw new NotSupportedException($"vertex type {(byte)type}");
        }
    }

    /// <summary>ByteFloat4 stores direction vectors as unorm; expand to [-1,1].</summary>
    public static Vector3 ToSigned(Vector4 v) => new(v.X * 2f - 1f, v.Y * 2f - 1f, v.Z * 2f - 1f);

    private static float Hf(ReadOnlySpan<byte> s, int o) =>
        (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(s[o..]));

}

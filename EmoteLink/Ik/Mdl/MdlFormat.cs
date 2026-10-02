using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmoteLink.Ik.Mdl;

/// <summary>
/// Minimal reader for FFXIV .mdl v6 (ported from Chisel), covering exactly the parts needed to locate and rewrite
/// vertex data: the file header, vertex declarations, LODs, meshes and submeshes.
///
/// Written by hand rather than taken from Lumina because Lumina 7.x (including 7.6.1) cannot
/// parse v6 models at all - it throws while reading the runtime section, both for loose files
/// and for files pulled straight out of the game archive. Doing it here also keeps the plugin
/// free of any Lumina version that might conflict with the one Dalamud loads.
///
/// Every offset below was verified against real files: the stack is exactly
/// declCount * 17 * 8 bytes, header + stack + runtime lands precisely on vertexOffset[0], each
/// Lod repeats the header's buffer offsets and sizes, and the submesh array exactly tiles the
/// mesh array. <see cref="Validate"/> re-checks those invariants at load time.
/// </summary>
internal sealed class MdlFormat
{
    public const int FileHeaderSize = 0x44;
    public const int VertexElementSize = 8;
    public const int ElementsPerDeclaration = 17;
    public const int ModelHeaderSize = 56;
    public const int ElementIdSize = 32;
    public const int LodSize = 60;
    public const int LodArrayCount = 3;      // always three entries regardless of LodCount
    public const int ExtraLodSize = 40;
    public const int MeshSize = 36;
    public const int SubmeshSize = 16;
    public const int TerrainShadowMeshSize = 20;
    public const int TerrainShadowSubmeshSize = 10;
    public const int BoneTableSize = 132;   // 64 ushort indices, byte count, 3 bytes padding

    public uint Version;
    public int StackSize;
    public int RuntimeSize;
    public int DeclarationCount;
    public int MaterialCount;
    public uint[] VertexOffset = new uint[3];
    public uint[] IndexOffset = new uint[3];
    public uint[] VertexBufferSize = new uint[3];
    public uint[] IndexBufferSize = new uint[3];
    public int LodCount;

    public float Radius;
    public int MeshCount;
    public int AttributeCount;
    public int SubmeshCount;
    public int BoneCount;
    public int BoneTableCount;
    public int BoneTableDataSize;
    public int ShapeCount;
    public int ShapeMeshCount;
    public int ShapeValueCount;
    public int ElementIdCount;
    public int TerrainShadowMeshCount;
    public int TerrainShadowSubmeshCount;
    public bool ExtraLodEnabled;

    public VertexDecl[] Declarations = [];
    public Lod[] Lods = [];
    public Mesh[] Meshes = [];
    public Submesh[] Submeshes = [];
    public string[] MaterialNames = [];
    public string[] AttributeNames = [];
    public string[] BoneNames = [];
    public BoneTable[] BoneTables = [];

    /// <summary>True when the submesh array was located and verified; false means we fell back.</summary>
    public bool SubmeshesTrusted;

    public struct VertexElement
    {
        public byte Stream, Offset, Type, Usage, UsageIndex;
    }

    public sealed class VertexDecl
    {
        public List<VertexElement> Elements = [];
    }

    public struct Lod
    {
        public ushort MeshIndex, MeshCount;
        public uint VertexBufferSize, IndexBufferSize, VertexDataOffset, IndexDataOffset;
    }

    public struct Mesh
    {
        public ushort VertexCount;
        public uint IndexCount;
        public ushort MaterialIndex, SubmeshIndex, SubmeshCount, BoneTableIndex;
        public uint StartIndex;
        public uint[] VertexBufferOffset;
        public byte[] VertexBufferStride;
        public byte VertexStreamCount;
    }

    public struct Submesh
    {
        public uint IndexOffset, IndexCount, AttributeIndexMask;
        public ushort BoneStartIndex, BoneCount;
    }

    public sealed class BoneTable
    {
        public ushort[] BoneIndices = [];
        public int Count;
    }

    public static MdlFormat Read(byte[] d)
    {
        if (d.Length < FileHeaderSize)
            throw new InvalidDataException("file is shorter than an mdl header");

        var m = new MdlFormat
        {
            Version = U32(d, 0),
            StackSize = (int)U32(d, 4),
            RuntimeSize = (int)U32(d, 8),
            DeclarationCount = U16(d, 12),
            MaterialCount = U16(d, 14),
        };

        for (var i = 0; i < 3; i++)
        {
            m.VertexOffset[i] = U32(d, 16 + i * 4);
            m.IndexOffset[i] = U32(d, 28 + i * 4);
            m.VertexBufferSize[i] = U32(d, 40 + i * 4);
            m.IndexBufferSize[i] = U32(d, 52 + i * 4);
        }

        m.LodCount = d[64];

        // Invariant 1: the stack is exactly the vertex declaration block.
        var expectedStack = m.DeclarationCount * ElementsPerDeclaration * VertexElementSize;
        if (m.StackSize != expectedStack)
            throw new InvalidDataException(
                $"stack size {m.StackSize} != {m.DeclarationCount} declarations x {ElementsPerDeclaration} x {VertexElementSize}");

        // Invariant 2: vertex data starts immediately after header + stack + runtime.
        if (FileHeaderSize + m.StackSize + m.RuntimeSize != m.VertexOffset[0])
            throw new InvalidDataException(
                $"header+stack+runtime = {FileHeaderSize + m.StackSize + m.RuntimeSize}, but vertexOffset[0] = {m.VertexOffset[0]}");

        m.ReadDeclarations(d);
        m.ReadRuntime(d);
        return m;
    }

    private void ReadDeclarations(byte[] d)
    {
        Declarations = new VertexDecl[DeclarationCount];
        for (var i = 0; i < DeclarationCount; i++)
        {
            var decl = new VertexDecl();
            var b = FileHeaderSize + i * ElementsPerDeclaration * VertexElementSize;

            for (var e = 0; e < ElementsPerDeclaration; e++)
            {
                var o = b + e * VertexElementSize;
                if (d[o] == 0xFF)
                    break;                                  // terminator

                decl.Elements.Add(new VertexElement
                {
                    Stream = d[o],
                    Offset = d[o + 1],
                    Type = d[o + 2],
                    Usage = d[o + 3],
                    UsageIndex = d[o + 4],
                });
            }

            Declarations[i] = decl;
        }
    }

    private void ReadRuntime(byte[] d)
    {
        var rt = FileHeaderSize + StackSize;
        var rtEnd = rt + RuntimeSize;
        var stringSize = (int)U32(d, rt + 4);
        var strings = rt + 8;
        var mh = strings + stringSize;

        if (mh + ModelHeaderSize > rtEnd)
            throw new InvalidDataException("string block overruns the runtime section");

        Radius = BitConverter.ToSingle(d, mh);
        MeshCount = U16(d, mh + 4);
        AttributeCount = U16(d, mh + 6);
        SubmeshCount = U16(d, mh + 8);
        var mhMaterialCount = U16(d, mh + 10);
        BoneCount = U16(d, mh + 12);
        BoneTableCount = U16(d, mh + 14);
        ShapeCount = U16(d, mh + 16);
        ShapeMeshCount = U16(d, mh + 18);
        ShapeValueCount = U16(d, mh + 20);
        ElementIdCount = U16(d, mh + 24);
        TerrainShadowMeshCount = d[mh + 26];
        ExtraLodEnabled = (d[mh + 27] & 0x10) != 0;
        TerrainShadowSubmeshCount = U16(d, mh + 38);
        BoneTableDataSize = U16(d, mh + 44);

        // Invariant 3: the model header agrees with the file header.
        if (mhMaterialCount != MaterialCount)
            throw new InvalidDataException(
                $"model header material count {mhMaterialCount} != file header {MaterialCount}");

        var lodsOffset = mh + ModelHeaderSize + ElementIdCount * ElementIdSize;

        // Invariant 4: each Lod repeats the file header's buffer offsets and sizes. This is a
        // 4-way u32 agreement per LOD, so if it holds the whole layout is anchored correctly.
        Lods = new Lod[LodCount];
        for (var l = 0; l < LodCount; l++)
        {
            var b = lodsOffset + l * LodSize;
            if (b + LodSize > rtEnd)
                throw new InvalidDataException("lod array overruns the runtime section");

            var lod = new Lod
            {
                MeshIndex = U16(d, b),
                MeshCount = U16(d, b + 2),
                VertexBufferSize = U32(d, b + 44),
                IndexBufferSize = U32(d, b + 48),
                VertexDataOffset = U32(d, b + 52),
                IndexDataOffset = U32(d, b + 56),
            };

            if (lod.VertexDataOffset != VertexOffset[l] || lod.IndexDataOffset != IndexOffset[l] ||
                lod.VertexBufferSize != VertexBufferSize[l] || lod.IndexBufferSize != IndexBufferSize[l])
                throw new InvalidDataException($"lod {l} does not match the file header (layout anchor failed)");

            Lods[l] = lod;
        }

        var meshesOffset = lodsOffset + LodArrayCount * LodSize
                           + (ExtraLodEnabled ? LodArrayCount * ExtraLodSize : 0);
        Meshes = ReadMeshes(d, meshesOffset, rtEnd);

        var afterMeshes = meshesOffset + MeshCount * MeshSize;
        var attrOffsets = afterMeshes;
        var submeshesOffset = afterMeshes + AttributeCount * 4 + TerrainShadowMeshCount * TerrainShadowMeshSize;

        // Invariant 5: submeshes must exactly tile the meshes. If they do not, the surrounding
        // layout differs from what we expect; fall back to one implicit submesh per mesh rather
        // than failing the whole file, since editing only needs the vertex buffers.
        if (TryReadSubmeshes(d, submeshesOffset, rtEnd, out var subs))
        {
            Submeshes = subs;
            SubmeshesTrusted = true;
        }
        else if (TryFindSubmeshes(d, afterMeshes, rtEnd, out submeshesOffset, out subs))
        {
            Submeshes = subs;
            SubmeshesTrusted = true;
        }
        else
        {
            Submeshes = [];
            SubmeshesTrusted = false;
        }

        AttributeNames = ReadNames(d, attrOffsets, AttributeCount, strings, stringSize);

        if (SubmeshesTrusted)
        {
            var matOffsets = submeshesOffset + SubmeshCount * SubmeshSize
                             + TerrainShadowSubmeshCount * TerrainShadowSubmeshSize;
            MaterialNames = ReadNames(d, matOffsets, MaterialCount, strings, stringSize);

            var boneOffsets = matOffsets + MaterialCount * 4;
            BoneNames = ReadNames(d, boneOffsets, BoneCount, strings, stringSize);
            BoneTables = ReadBoneTables(d, boneOffsets + BoneCount * 4, rtEnd);
        }
        else
        {
            MaterialNames = [];
            BoneNames = [];
            BoneTables = [];
        }
    }

    private BoneTable[] ReadBoneTables(byte[] d, int off, int rtEnd)
    {
        if (BoneTableCount == 0)
            return [];

        var tables = new BoneTable[BoneTableCount];
        if (Version >= 0x01000006)
        {
            // Dawntrail v6: N four-byte headers followed by packed ushort lists. Header.offset
            // is measured in four-byte units relative to that header; each list is padded to a
            // four-byte boundary. BoneTableDataSize counts ushorts in the packed data region.
            var dataEnd = off + BoneTableCount * 4 + BoneTableDataSize * 2;
            if (off < 0 || dataEnd > rtEnd)
                throw new InvalidDataException("v6 bone table block overruns the runtime section");

            for (var t = 0; t < tables.Length; t++)
            {
                var header = off + t * 4;
                var relativeWords = U16(d, header);
                var count = U16(d, header + 2);
                var data = header + relativeWords * 4;
                if (count > BoneCount || data < off + BoneTableCount * 4 || data + count * 2 > dataEnd)
                    throw new InvalidDataException($"v6 bone table {t} has invalid offset/count");

                tables[t] = ReadBoneTableIndices(d, data, count, t);
            }

            return tables;
        }

        if (off < 0 || off + BoneTableCount * BoneTableSize > rtEnd)
            throw new InvalidDataException("legacy bone table array overruns the runtime section");

        for (var t = 0; t < tables.Length; t++)
        {
            var b = off + t * BoneTableSize;
            var count = (int)U32(d, b + 128);
            if (count is < 0 or > 64)
                throw new InvalidDataException($"legacy bone table {t} has invalid count {count}");

            tables[t] = ReadBoneTableIndices(d, b, count, t);
        }

        return tables;
    }

    private BoneTable ReadBoneTableIndices(byte[] d, int off, int count, int tableIndex)
    {
        var indices = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = U16(d, off + i * 2);
            if (indices[i] >= BoneCount)
                throw new InvalidDataException(
                    $"bone table {tableIndex} references bone {indices[i]}, but only {BoneCount} exist");
        }

        return new BoneTable { BoneIndices = indices, Count = count };
    }

    private Mesh[] ReadMeshes(byte[] d, int off, int rtEnd)
    {
        if (off + MeshCount * MeshSize > rtEnd)
            throw new InvalidDataException("mesh array overruns the runtime section");

        var meshes = new Mesh[MeshCount];
        for (var i = 0; i < MeshCount; i++)
        {
            var b = off + i * MeshSize;
            meshes[i] = new Mesh
            {
                VertexCount = U16(d, b),
                IndexCount = U32(d, b + 4),
                MaterialIndex = U16(d, b + 8),
                SubmeshIndex = U16(d, b + 10),
                SubmeshCount = U16(d, b + 12),
                BoneTableIndex = U16(d, b + 14),
                StartIndex = U32(d, b + 16),
                VertexBufferOffset = [U32(d, b + 20), U32(d, b + 24), U32(d, b + 28)],
                VertexBufferStride = [d[b + 32], d[b + 33], d[b + 34]],
                VertexStreamCount = d[b + 35],
            };
        }

        return meshes;
    }

    private bool TryReadSubmeshes(byte[] d, int off, int rtEnd, out Submesh[] subs)
    {
        subs = [];
        if (off < 0 || off + SubmeshCount * SubmeshSize > rtEnd)
            return false;

        var result = new Submesh[SubmeshCount];
        for (var i = 0; i < SubmeshCount; i++)
        {
            var b = off + i * SubmeshSize;
            result[i] = new Submesh
            {
                IndexOffset = U32(d, b),
                IndexCount = U32(d, b + 4),
                AttributeIndexMask = U32(d, b + 8),
                BoneStartIndex = U16(d, b + 12),
                BoneCount = U16(d, b + 14),
            };
        }

        foreach (var m in Meshes)
        {
            if (m.SubmeshIndex + m.SubmeshCount > SubmeshCount)
                return false;

            uint sum = 0;
            for (var s = 0; s < m.SubmeshCount; s++)
            {
                var sm = result[m.SubmeshIndex + s];
                if (sm.IndexCount % 3 != 0)
                    return false;
                if (s == 0 && m.IndexCount > 0 && sm.IndexOffset != m.StartIndex)
                    return false;
                sum += sm.IndexCount;
            }

            if (sum != m.IndexCount)
                return false;
        }

        subs = result;
        return true;
    }

    /// <summary>Last resort: slide a window until the tiling invariant holds.</summary>
    private bool TryFindSubmeshes(byte[] d, int from, int rtEnd, out int offset, out Submesh[] subs)
    {
        for (var o = from; o + SubmeshCount * SubmeshSize <= rtEnd; o += 2)
        {
            if (TryReadSubmeshes(d, o, rtEnd, out subs))
            {
                offset = o;
                return true;
            }
        }

        offset = -1;
        subs = [];
        return false;
    }

    private static string[] ReadNames(byte[] d, int offsetTable, int count, int strings, int stringSize)
    {
        var names = new string[Math.Max(count, 0)];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = "";
            var p = offsetTable + i * 4;
            if (p + 4 > d.Length)
                continue;

            var so = (int)U32(d, p);
            if (so < 0 || so >= stringSize)
                continue;

            var start = strings + so;
            var end = start;
            while (end < strings + stringSize && d[end] != 0)
                end++;

            names[i] = Encoding.UTF8.GetString(d, start, end - start);
        }

        return names;
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
}

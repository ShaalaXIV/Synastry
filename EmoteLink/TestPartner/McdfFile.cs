using System.Text;
using System.Text.Json;

namespace EmoteLink.TestPartner;

/// <summary>
/// Reads a Mare character data file (.mcdf): the legacy lz4net chunked stream Mare writes, holding
/// "MCDF", a version byte, a JSON header (Glamourer state, Penumbra manipulations, the file list) and
/// then every file's bytes in list order. Read-only, no outside libraries.
/// </summary>
internal sealed class McdfFile
{
    public string Description { get; private init; } = "";
    /// <summary>Glamourer's state as Mare stores it (base64), for Glamourer.ApplyState.</summary>
    public string GlamourerData { get; private init; } = "";
    /// <summary>Penumbra's meta manipulation string.</summary>
    public string ManipulationData { get; private init; } = "";
    /// <summary>Game path → file extracted to disk.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Game path → another game path (Mare's file swaps).</summary>
    public Dictionary<string, string> Swaps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unpacks <paramref name="path"/> into <paramref name="folder"/> (created, emptied first).</summary>
    public static McdfFile Extract(string path, string folder)
    {
        using var input = new LegacyLz4Stream(File.OpenRead(path));
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "MCDF")
            throw new InvalidDataException("This isn't a Mare character data file (.mcdf).");
        var version = reader.ReadByte();
        if (version is < 1 or > 3)
            throw new InvalidDataException($"This .mcdf is version {version}, which Synastry doesn't know.");
        var headerLength = reader.ReadInt32();
        if (headerLength is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("The .mcdf header is damaged.");
        using var header = JsonDocument.Parse(reader.ReadBytes(headerLength));
        var root = header.RootElement;

        var file = new McdfFile
        {
            Description = Text(root, "Description"),
            GlamourerData = Text(root, "GlamourerData"),
            ManipulationData = Text(root, "ManipulationData")
        };

        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var index = 0;
        if (root.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var entry in files.EnumerateArray())
            {
                var length = entry.TryGetProperty("Length", out var l) ? l.GetInt64() : 0;
                if (length is < 0 or > 512L * 1024 * 1024) throw new InvalidDataException("The .mcdf lists a damaged file.");
                var gamePaths = entry.TryGetProperty("GamePaths", out var g) && g.ValueKind == JsonValueKind.Array
                    ? g.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToList()
                    : [];
                var extension = gamePaths.Count > 0 ? Path.GetExtension(gamePaths[0]) : ".bin";
                var target = Path.Combine(folder, $"{index++:D5}{extension}");
                using (var output = File.Create(target))
                    Copy(input, output, length);
                foreach (var gamePath in gamePaths) file.Files[gamePath.Replace('\\', '/').ToLowerInvariant()] = target;
            }
        if (root.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Array)
            foreach (var swap in swaps.EnumerateArray())
            {
                var to = Text(swap, "FileSwapPath");
                if (to.Length == 0 || !swap.TryGetProperty("GamePaths", out var g) || g.ValueKind != JsonValueKind.Array) continue;
                foreach (var gamePath in g.EnumerateArray())
                    if (gamePath.GetString() is { Length: > 0 } from) file.Swaps[from.Replace('\\', '/').ToLowerInvariant()] = to;
            }
        return file;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static void Copy(Stream input, Stream output, long length)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read <= 0) throw new EndOfStreamException("The .mcdf ended early.");
            output.Write(buffer, 0, read);
            length -= read;
        }
    }

    /// <summary>
    /// lz4net's legacy LZ4Stream, read-only: chunks of [flags varint][original length varint]
    /// [compressed length varint if flag 1][data], each an LZ4 block.
    /// </summary>
    private sealed class LegacyLz4Stream(Stream inner) : Stream
    {
        private byte[] chunk = [];
        private int position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= chunk.Length && !NextChunk()) return 0;
            var take = Math.Min(count, chunk.Length - position);
            Buffer.BlockCopy(chunk, position, buffer, offset, take);
            position += take;
            return take;
        }

        private bool NextChunk()
        {
            var flags = ReadVarInt(allowEnd: true);
            if (flags < 0) return false;
            var originalLength = ReadVarInt();
            var compressed = (flags & 1) != 0;
            var storedLength = compressed ? ReadVarInt() : originalLength;
            if (originalLength is < 0 or > 64 * 1024 * 1024 || storedLength is < 0 or > 64 * 1024 * 1024)
                throw new InvalidDataException("The .mcdf is damaged.");
            var stored = new byte[storedLength];
            inner.ReadExactly(stored);
            chunk = compressed ? Lz4Block.Decode(stored, originalLength) : stored;
            position = 0;
            return true;
        }

        private int ReadVarInt(bool allowEnd = false)
        {
            var result = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var value = inner.ReadByte();
                if (value < 0)
                {
                    if (allowEnd && shift == 0) return -1;
                    throw new EndOfStreamException("The .mcdf ended early.");
                }
                result |= (value & 0x7F) << shift;
                if ((value & 0x80) == 0) return result;
            }
            throw new InvalidDataException("The .mcdf is damaged.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Plain LZ4 block decompression.</summary>
internal static class Lz4Block
{
    public static byte[] Decode(byte[] source, int outputLength)
    {
        var output = new byte[outputLength];
        int s = 0, d = 0;
        while (s < source.Length)
        {
            var token = source[s++];
            var literal = token >> 4;
            if (literal == 15)
            {
                byte b;
                do { b = source[s++]; literal += b; } while (b == 255);
            }
            if (d + literal > output.Length || s + literal > source.Length) throw new InvalidDataException("The .mcdf is damaged.");
            Buffer.BlockCopy(source, s, output, d, literal);
            s += literal;
            d += literal;
            if (s >= source.Length) break;

            var offset = source[s] | (source[s + 1] << 8);
            s += 2;
            var match = (token & 15) + 4;
            if ((token & 15) == 15)
            {
                byte b;
                do { b = source[s++]; match += b; } while (b == 255);
            }
            var from = d - offset;
            if (offset == 0 || from < 0 || d + match > output.Length) throw new InvalidDataException("The .mcdf is damaged.");
            for (var i = 0; i < match; i++) output[d++] = output[from + i];
        }
        return output;
    }
}

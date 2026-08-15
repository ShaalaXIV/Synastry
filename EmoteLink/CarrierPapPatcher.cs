using System.Text;

namespace EmoteLink;

internal static class CarrierPapPatcher
{
    public static byte[] Patch(byte[] sourceBytes, byte[] carrierBytes)
    {
        var source = ReadHeader(sourceBytes);
        var carrier = ReadHeader(carrierBytes);
        var carrierName = ReadPaddedAscii(carrierBytes, carrier.InfoOffset, 32);
        if (string.IsNullOrWhiteSpace(carrierName))
            throw new InvalidDataException("The carrier PAP has no animation name.");

        var resultPrefix = (byte[])sourceBytes.Clone();
        WritePaddedAscii(resultPrefix, source.InfoOffset, 32, carrierName);

        var oldTmbSize = ReadTmbSize(sourceBytes, source.TmbOffset);
        var oldTmb = sourceBytes.AsSpan(source.TmbOffset, oldTmbSize).ToArray();
        var patchedTmb = PatchTmbAnimationNames(oldTmb, carrierName);
        if (patchedTmb.Length == oldTmb.Length)
            return resultPrefix;

        using var output = new MemoryStream(sourceBytes.Length + patchedTmb.Length - oldTmb.Length + 4);
        output.Write(resultPrefix, 0, source.TmbOffset);
        output.Write(patchedTmb);

        var sourceRemainder = source.TmbOffset + oldTmbSize;
        if (source.AnimationCount > 1)
        {
            while (sourceRemainder < sourceBytes.Length && sourceRemainder % 4 != source.TmbOffset % 4)
                sourceRemainder++;
            while (output.Position % 4 != source.TmbOffset % 4)
                output.WriteByte(0);
        }

        output.Write(sourceBytes, sourceRemainder, sourceBytes.Length - sourceRemainder);
        return output.ToArray();
    }

    private static byte[] PatchTmbAnimationNames(byte[] tmbBytes, string carrierName)
    {
        if (tmbBytes.Length < 12 || Encoding.ASCII.GetString(tmbBytes, 0, 4) != "TMLB")
            throw new InvalidDataException("The PAP's embedded timeline is invalid.");

        var itemCount = BitConverter.ToInt32(tmbBytes, 8);
        if (itemCount < 0)
            throw new InvalidDataException("The PAP's embedded timeline item count is invalid.");

        var c009Offsets = new List<int>();
        var position = 12;
        for (var index = 0; index < itemCount; index++)
        {
            if (position + 8 > tmbBytes.Length)
                throw new InvalidDataException("The PAP's embedded timeline is truncated.");
            var itemSize = BitConverter.ToInt32(tmbBytes, position + 4);
            if (itemSize < 8 || position + itemSize > tmbBytes.Length)
                throw new InvalidDataException("The PAP contains an invalid timeline item.");
            if (itemSize >= 24 && Encoding.ASCII.GetString(tmbBytes, position, 4) == "C009")
                c009Offsets.Add(position);
            position += itemSize;
        }

        if (c009Offsets.Count == 0)
            return tmbBytes;

        var encodedName = Encoding.ASCII.GetBytes(carrierName);
        var result = new byte[tmbBytes.Length + encodedName.Length + 1];
        Buffer.BlockCopy(tmbBytes, 0, result, 0, tmbBytes.Length);
        Buffer.BlockCopy(encodedName, 0, result, tmbBytes.Length, encodedName.Length);
        foreach (var itemOffset in c009Offsets)
            WriteInt32(result, itemOffset + 20, tmbBytes.Length - (itemOffset + 8));
        WriteInt32(result, 4, result.Length);
        return result;
    }

    private static PapHeader ReadHeader(byte[] bytes)
    {
        if (bytes.Length < 26 || Encoding.ASCII.GetString(bytes, 0, 4) != "pap ")
            throw new InvalidDataException("This does not look like a PAP file.");
        var animationCount = BitConverter.ToInt16(bytes, 8);
        var infoOffset = BitConverter.ToInt32(bytes, 14);
        var havokOffset = BitConverter.ToInt32(bytes, 18);
        var tmbOffset = BitConverter.ToInt32(bytes, 22);
        if (animationCount <= 0 || infoOffset < 26 || infoOffset + 40 > bytes.Length ||
            havokOffset < infoOffset || tmbOffset < havokOffset || tmbOffset >= bytes.Length)
            throw new InvalidDataException("The PAP header offsets are invalid.");
        _ = ReadTmbSize(bytes, tmbOffset);
        return new PapHeader(animationCount, infoOffset, tmbOffset);
    }

    private static int ReadTmbSize(byte[] bytes, int offset)
    {
        if (offset + 12 > bytes.Length || Encoding.ASCII.GetString(bytes, offset, 4) != "TMLB")
            throw new InvalidDataException("The PAP does not contain an embedded timeline.");
        var size = BitConverter.ToInt32(bytes, offset + 4);
        if (size < 12 || offset + size > bytes.Length)
            throw new InvalidDataException("The PAP's embedded timeline size is invalid.");
        return size;
    }

    private static string ReadPaddedAscii(byte[] bytes, int offset, int length)
    {
        var end = offset;
        while (end < offset + length && bytes[end] != 0) end++;
        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private static void WritePaddedAscii(byte[] bytes, int offset, int length, string value)
    {
        Array.Clear(bytes, offset, length);
        var encoded = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(encoded, 0, bytes, offset, Math.Min(encoded.Length, length - 1));
    }

    private static void WriteInt32(byte[] bytes, int offset, int value) =>
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, sizeof(int));

    private readonly record struct PapHeader(short AnimationCount, int InfoOffset, int TmbOffset);
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace EmoteLink.TestPartner;

/// <summary>
/// The look inside a Mare file: Glamourer's state, base64 of a version byte and gzip JSON. Decoded
/// here so the test partner can be dressed even when Glamourer won't dress a minion: the 26-byte
/// body description in the game's order, and each gear slot's model with its dyes.
/// </summary>
internal sealed class GlamourerState
{
    public byte[] Customize { get; } = new byte[26];
    public bool HasCustomize { get; private set; }
    /// <summary>Gear slots 0-9 (head, body, hands, legs, feet, ears, neck, wrists, right and left ring)
    /// as the game's packed model id: set | variant &lt;&lt; 16 | dye &lt;&lt; 24 | second dye &lt;&lt; 32.</summary>
    public ulong?[] Equipment { get; } = new ulong?[10];

    private static readonly string[] Slots = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    public static GlamourerState? Parse(string base64, IDataManager data)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length < 2) return null;
            using var document = JsonDocument.Parse(Inflate(bytes));
            var root = document.RootElement;
            var state = new GlamourerState();
            if (root.TryGetProperty("Customize", out var customize)) state.ReadCustomize(customize);
            if (root.TryGetProperty("Equipment", out var equipment)) state.ReadEquipment(equipment, data);
            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The JSON after the version byte. Glamourer's gzip often ends without its trailer, which a strict
    /// read rejects, so this keeps everything that inflated before the stream ran out.
    /// </summary>
    private static string Inflate(byte[] bytes)
    {
        using var gzip = new GZipStream(new MemoryStream(bytes, 1, bytes.Length - 1), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        }
        catch (InvalidDataException) { /* no trailer; what came out is complete */ }
        catch (EndOfStreamException) { }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private void ReadCustomize(JsonElement customize)
    {
        // Most entries are {"Value": n, "Apply": b}; a few (ModelId) are a bare number.
        int Value(string name)
        {
            if (!customize.TryGetProperty(name, out var entry)) return 0;
            if (entry.ValueKind == JsonValueKind.Number) return entry.TryGetInt32(out var bare) ? bare : 0;
            return entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("Value", out var value) &&
                   value.TryGetInt32(out var number) ? number : 0;
        }
        bool On(string name) => Value(name) != 0;
        if (Value("ModelId") != 0) return;   // not a human model; leave the minion as it is
        if (Value("Race") == 0) return;

        var c = Customize;
        c[0] = (byte)Value("Race");
        c[1] = (byte)Value("Gender");
        c[2] = (byte)Value("BodyType");
        c[3] = (byte)Value("Height");
        c[4] = (byte)Value("Clan");
        c[5] = (byte)Value("Face");
        c[6] = (byte)Value("Hairstyle");
        c[7] = (byte)(On("Highlights") ? 0x80 : 0);
        c[8] = (byte)Value("SkinColor");
        c[9] = (byte)Value("EyeColorRight");
        c[10] = (byte)Value("HairColor");
        c[11] = (byte)Value("HighlightsColor");
        var features = 0;
        for (var i = 1; i <= 7; i++)
            if (On($"FacialFeature{i}")) features |= 1 << (i - 1);
        if (On("LegacyTattoo")) features |= 0x80;
        c[12] = (byte)features;
        c[13] = (byte)Value("TattooColor");
        c[14] = (byte)Value("Eyebrows");
        c[15] = (byte)Value("EyeColorLeft");
        c[16] = (byte)((Value("EyeShape") & 0x7F) | (On("SmallIris") ? 0x80 : 0));
        c[17] = (byte)Value("Nose");
        c[18] = (byte)Value("Jaw");
        c[19] = (byte)((Value("Mouth") & 0x7F) | (On("Lipstick") ? 0x80 : 0));
        c[20] = (byte)Value("LipColor");
        c[21] = (byte)Value("MuscleMass");
        c[22] = (byte)Value("TailShape");
        c[23] = (byte)Value("BustSize");
        c[24] = (byte)((Value("FacePaint") & 0x7F) | (On("FacePaintReversed") ? 0x80 : 0));
        c[25] = (byte)Value("FacePaintColor");
        HasCustomize = true;
    }

    private void ReadEquipment(JsonElement equipment, IDataManager data)
    {
        var items = data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            if (!equipment.TryGetProperty(Slots[slot], out var entry) || entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("ItemId", out var idElement) || !idElement.TryGetUInt64(out var itemId)) continue;
            var dye = entry.TryGetProperty("Stain", out var s) && s.TryGetInt32(out var stain) ? (ulong)(byte)stain : 0;
            var dye2 = entry.TryGetProperty("Stain2", out var s2) && s2.TryGetInt32(out var stain2) ? (ulong)(byte)stain2 : 0;
            // Glamourer writes "nothing" as ids near the top of the range.
            ulong model = 0;
            if (itemId is > 0 and < 0x1000000 && items?.GetRowOrDefault((uint)itemId) is { } item)
            {
                var main = item.ModelMain;
                model = (main & 0xFFFF) | ((main >> 16 & 0xFF) << 16);
            }
            Equipment[slot] = model | dye << 24 | dye2 << 32;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmoteLink.Ik;

/// <summary>
/// One character's measured shape, from body setup. Shared with the room through the relay so
/// every Synastry in it can place hands on skin and aim at openings that really exist. All
/// distances are in yalms at the character's own scale, measured in the neutral (bind) pose.
/// </summary>
internal sealed class BodyProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>"ivcs", "yas" or "vanilla": which genital and face bones exist.</summary>
    public string Rig { get; set; } = "vanilla";

    /// <summary>When the measurement was taken; used to notice a changed body.</summary>
    public string BodySignature { get; set; } = "";

    /// <summary>Average distance from a bone to the skin around it, by bone name. A hand reaching
    /// for a bone stops this far out, so it rests on the surface instead of sinking in.</summary>
    public Dictionary<string, float> SurfaceRadius { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The shaft, when the body has a real one.</summary>
    public ShaftShape? Shaft { get; set; }

    /// <summary>Openings that exist on this body, by "mouth", "vagina", "anus".</summary>
    public Dictionary<string, OpeningShape> Openings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Palm depth (wrist bone to palm surface) and grip width for each hand, by "l"/"r".</summary>
    public Dictionary<string, HandShape> Hands { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The player's own adjustments from the setup page.</summary>
    public BodyTuning Tuning { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static BodyProfile? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var profile = JsonSerializer.Deserialize<BodyProfile>(json, Options);
            return profile is { Version: >= 1 } ? profile.Sanitized() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Profiles arrive from other players, so every number is clamped to something sane.</summary>
    private BodyProfile Sanitized()
    {
        static float Clamp(float value, float max) => float.IsFinite(value) ? Math.Clamp(value, 0f, max) : 0f;
        foreach (var key in SurfaceRadius.Keys.ToList()) SurfaceRadius[key] = Clamp(SurfaceRadius[key], 0.5f);
        if (Shaft is { } shaft)
        {
            shaft.Length = Clamp(shaft.Length, 1f);
            shaft.Radius = Clamp(shaft.Radius, 0.2f);
            shaft.TipBeyondLastBone = Clamp(shaft.TipBeyondLastBone, 0.3f);
        }
        foreach (var opening in Openings.Values)
        {
            opening.Radius = Clamp(opening.Radius, 0.2f);
            opening.MaxOpen = Clamp(opening.MaxOpen, 0.2f);
            opening.Depth = Clamp(opening.Depth, 0.5f);
        }
        foreach (var hand in Hands.Values)
        {
            hand.PalmDepth = Clamp(hand.PalmDepth, 0.2f);
            hand.GripWidth = Clamp(hand.GripWidth, 0.4f);
        }
        Tuning.ContactGap = Math.Clamp(float.IsFinite(Tuning.ContactGap) ? Tuning.ContactGap : 0f, -0.05f, 0.05f);
        Tuning.GripStrength = Math.Clamp(float.IsFinite(Tuning.GripStrength) ? Tuning.GripStrength : 1f, 0f, 1.5f);
        Tuning.OpeningAmount = Math.Clamp(float.IsFinite(Tuning.OpeningAmount) ? Tuning.OpeningAmount : 1f, 0f, 1.5f);
        return this;
    }
}

internal sealed class ShaftShape
{
    public float Length { get; set; }
    public float Radius { get; set; }
    /// <summary>How far the mesh tip extends past the last shaft bone.</summary>
    public float TipBeyondLastBone { get; set; }
}

internal sealed class OpeningShape
{
    /// <summary>The bone the opening is centred on.</summary>
    public string Bone { get; set; } = "";
    /// <summary>Resting radius of the opening.</summary>
    public float Radius { get; set; }
    /// <summary>How much wider it can open by moving its own bones, within the rig's range.</summary>
    public float MaxOpen { get; set; }
    /// <summary>How deep the shaft may go along the opening before it would pass through the body.</summary>
    public float Depth { get; set; }
}

internal sealed class HandShape
{
    public float PalmDepth { get; set; }
    public float GripWidth { get; set; }
}

internal sealed class BodyTuning
{
    /// <summary>Extra space between touching surfaces; negative presses in a little.</summary>
    public float ContactGap { get; set; }
    /// <summary>How far fingers close when gripping, 1 = a natural grip.</summary>
    public float GripStrength { get; set; } = 1f;
    /// <summary>How much openings may widen, 1 = the measured amount.</summary>
    public float OpeningAmount { get; set; } = 1f;
}

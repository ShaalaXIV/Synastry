using System.Numerics;

namespace EmoteLink.Places;

/// <summary>What a saved place is held to. Saved as a number, so only add to the end.</summary>
public enum PlaceKind
{
    Spot,
    Furniture,
    Partner
}

/// <summary>Whether a saved place can be played from where you are.</summary>
internal enum PlaceReach
{
    Here,
    TooFar,
    Elsewhere
}

/// <summary>
/// An animation and where it landed: a spot in the world, a piece of furniture or next to someone.
/// Every property needs a public setter, or the settings file loses it on load.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
public sealed class SavedPlace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public PlaceKind Kind { get; set; }

    // What to play: "pose:Kind:Index" or "emote:Id", with the mod options it was saved with.
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public string Animation { get; set; } = "";
    public string Trigger { get; set; } = "";
    public Dictionary<string, List<string>> Selections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Spot. Houses of one size share a territory, so inside one the house has to match too (0 is outdoors).
    public uint TerritoryType { get; set; }
    public ulong HouseId { get; set; }

    // Furniture: the item's sheet row, shared by every placed copy.
    public uint FurnitureId { get; set; }
    public string FurnitureName { get; set; } = "";

    public string PartnerName { get; set; } = "";
    public uint PartnerWorldId { get; set; }

    /// <summary>Where you were drawn: in the world for a spot, otherwise in the furniture's or partner's frame.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Which way you faced, in radians: in the world, or relative to the furniture or partner.</summary>
    public float Rotation { get; set; }

    internal SavedPlace Copy() => (SavedPlace)MemberwiseClone();
}

/// <summary>Someone nearby a place can be saved next to.</summary>
internal sealed record PartnerChoice(string Name, uint WorldId, float Distance, bool InRoom);

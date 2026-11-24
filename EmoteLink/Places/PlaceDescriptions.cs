namespace EmoteLink.Places;

/// <summary>How a saved place reads in the list and its tooltip.</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal static class PlaceDescriptions
{
    /// <summary>Where it lands, in a few words.</summary>
    public static string Line(SavedPlace place, string zone) => place.Kind switch
    {
        PlaceKind.Furniture => $"On {place.FurnitureName}",
        PlaceKind.Partner => $"Next to {place.PartnerName}",
        _ => $"Spot in {zone}"
    };

    /// <summary>What playing it does.</summary>
    public static string Detail(SavedPlace place, string zone) => place.Kind switch
    {
        PlaceKind.Furniture =>
            $"Puts you in the same place on any {place.FurnitureName} within {PlaceService.MaximumReach:F0} yalms,\n" +
            "however the room is laid out.",
        PlaceKind.Partner =>
            $"Puts you in the same place around {place.PartnerName}, wherever you both are.\nThey stay put; only you move.",
        _ =>
            $"Puts you back on the same spot in {zone}, facing the same way,\n" +
            $"when you play it within {PlaceService.MaximumReach:F0} yalms of there."
    };

    /// <summary>What the row's colour means.</summary>
    public static string Reach(SavedPlace place, PlaceReach reach) => (reach, place.Kind) switch
    {
        (PlaceReach.Here, _) => "You can play it from here.",
        (PlaceReach.TooFar, _) => $"Too far away. Come within {PlaceService.MaximumReach:F0} yalms.",
        (_, PlaceKind.Furniture) => $"There's no {place.FurnitureName} here.",
        (_, PlaceKind.Partner) => $"{place.PartnerName} isn't nearby.",
        _ => "You're somewhere else."
    };

    /// <summary>A name to start from when saving.</summary>
    public static string SuggestedName(string animation, PlaceKind kind, string zone, string? furniture, string? partner) => kind switch
    {
        PlaceKind.Furniture when furniture is not null => $"{animation} on {furniture}",
        PlaceKind.Partner when partner is not null => $"{animation} with {partner}",
        PlaceKind.Spot when zone.Length > 0 => $"{animation} in {zone}",
        _ => animation
    };
}

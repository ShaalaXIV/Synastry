using System.Text.RegularExpressions;

namespace EmoteLink.Library;

/// <summary>
/// Spots names that declare two or more roles, following the Nightlife+ and GoonersLife+ conventions:
/// "[Dom&amp;Sub]", "[Sub&amp;Dog]", "(Dom-Csit1/Sub-Csit2)", "[1-∞]", "Threesome".
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal static class CoupleNames
{
    private static readonly Regex[] Markers =
    [
        new(@"\bf?dom\d*\s*&\s*sub", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\[[^\]]*\w\s*&\s*\w[^\]]*\]", RegexOptions.Compiled),
        new(@"\bf?dom\d*\s*-[^/)]*/\s*sub", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Standalone mods: "[Sub-Gdance, Dom-Hdance]", "[Dom-Doze1, Sub-Doze2]".
        new(@"\b(f?dom|sub)\d*\s*-[^,\])/]*[,/]\s*(f?dom|sub)\d*\s*-", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\[1-∞\]", RegexOptions.Compiled),
        new(@"\b(threesome|3sum|foursome|4some|spitroast|bukkake)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static bool IsCouple(IEnumerable<string> names) =>
        names.Any(name => name.Length > 0 && Markers.Any(marker => marker.IsMatch(name)));
}

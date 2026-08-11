namespace EmoteLink.Relay;

internal static class CatalogSearchSyntax
{
    public static bool TryBuildTrigramQuery(string term, out string query)
    {
        var tokens = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Take(12)
            .ToArray();
        if (tokens.Length == 0)
        {
            query = "";
            return false;
        }
        query = string.Join(" AND ", tokens.Select(token =>
            "\"" + token.Replace("\"", "\"\"") + "\""));
        return true;
    }
}

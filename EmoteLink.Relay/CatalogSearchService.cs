namespace EmoteLink.Relay;

/// <summary>
/// Bounded server-side search across durable content metadata. Results never contain a user,
/// room, local path, or private/public ownership flag.
/// </summary>
public sealed class CatalogSearchService
{
    private readonly CommunityRoleLabelStore labels;
    private readonly ITransferModerationRepository moderation;

    public CatalogSearchService(CommunityRoleLabelStore labels, ITransferModerationRepository moderation)
    {
        this.labels = labels;
        this.moderation = moderation;
    }

    public IReadOnlyList<CatalogSearchResultDto> Search(string query, int limit = 100)
    {
        var cleanLimit = Math.Clamp(limit, 1, 500);
        var labelResults = labels.Search(query, cleanLimit).Select(label =>
            new CatalogSearchResultDto(
                "community-label",
                label.Key,
                label.ModName.Length == 0 ? label.AnimationName : label.ModName,
                label.AnimationName,
                "",
                label.AcceptedLabel,
                label.Fingerprint,
                false,
                false));
        var banResults = moderation.GetTransferBans(false, query).Take(cleanLimit).Select(ban =>
            new CatalogSearchResultDto(
                "transfer-ban",
                ban.Id.ToString(),
                ban.DisplayName.Length == 0 ? ban.MatchValue : ban.DisplayName,
                $"{ban.ReasonCode}: {ban.Note}".TrimEnd(' ', ':'),
                "",
                "",
                ban.MatchValue,
                true,
                false));

        return labelResults.Concat(banResults).Take(cleanLimit).ToList();
    }
}

public sealed record CatalogSearchResultDto(
    string Kind,
    string Key,
    string DisplayName,
    string Detail,
    string Classification,
    string CommunityTag,
    string Signature,
    bool SharingBlocked,
    bool CatalogOnlyNonEnforcing);

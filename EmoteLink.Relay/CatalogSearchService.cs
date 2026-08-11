namespace EmoteLink.Relay;

/// <summary>
/// Bounded server-side search across durable content metadata. Results never contain a user,
/// room, local path, or private/public ownership flag.
/// </summary>
public sealed class CatalogSearchService
{
    private readonly AnimationCatalogStore animations;
    private readonly CommunityRoleLabelStore labels;
    private readonly ITransferModerationRepository moderation;

    public CatalogSearchService(AnimationCatalogStore animations, CommunityRoleLabelStore labels,
        ITransferModerationRepository moderation)
    {
        this.animations = animations;
        this.labels = labels;
        this.moderation = moderation;
    }

    public IReadOnlyList<CatalogSearchResultDto> Search(string query, int limit = 100)
    {
        var cleanLimit = Math.Clamp(limit, 1, 500);
        var animationResults = animations.Search(query, cleanLimit).Select(artifact =>
            new CatalogSearchResultDto(
                "animation-artifact",
                artifact.ArtifactKey,
                artifact.Names.FirstOrDefault() ?? artifact.Signature,
                string.Join(" · ", artifact.Names.Skip(1).Take(3)),
                artifact.EffectiveClassification.ToString(),
                "",
                artifact.Signature,
                false,
                artifact.SharingPolicy == AnimationSharingPolicy.CatalogOnlyBlocked));
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

        return animationResults.Concat(labelResults).Concat(banResults).Take(cleanLimit).ToList();
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

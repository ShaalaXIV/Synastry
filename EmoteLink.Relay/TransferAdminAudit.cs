namespace EmoteLink.Relay;

internal static class TransferAdminAudit
{
    public static string Actor(HttpRequest request)
    {
        var supplied = request.Headers[TransferStore.AdminActorHeaderName].ToString();
        return string.IsNullOrWhiteSpace(supplied) ? "local-admin" : supplied;
    }

    public static void RecordBanAction(
        ITransferModerationRepository moderation,
        TransferSharingBanDto ban,
        string eventType,
        string? actor)
    {
        var packageSha256 = ban.Scope == TransferBanScope.ExactPackageSha256 ? ban.MatchValue : "";
        var catalogFingerprint = ban.Scope == TransferBanScope.AnimationCatalogFingerprint ? ban.MatchValue : "";
        var modNameHash = ban.Scope == TransferBanScope.ModFamilyNameHash ? ban.MatchValue : "";
        moderation.RecordAuditEvent(new TransferAuditEventWrite(
            "",
            eventType,
            DateTimeOffset.UtcNow,
            packageSha256,
            catalogFingerprint,
            modNameHash,
            $"actorHash={TransferStore.ComputeAdministratorHash(actor)}; ban={ban.Id}; " +
            $"scope={ban.Scope}; reason={ban.ReasonCode}"));
    }
}

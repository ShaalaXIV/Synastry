using System.Text;
using System.Text.Json;

namespace EmoteLink;

internal sealed record ModRefreshWorkItem(
    (string Directory, string Name) Mod,
    string Path,
    CachedAnimationMod? Cached);

internal sealed record PreparedModRefresh(
    ModRefreshWorkItem Work,
    ManifestFileSet FileSet,
    AnimationManifestSnapshot? Snapshot,
    bool CacheValid);

internal sealed record PendingCatalogReport(
    (string Directory, string Name) Mod,
    AnimationArtifactReportSubmissionDto Submission);

internal enum ModRefreshResultKind
{
    Cached,
    PortableAnimation,
    NonAnimation,
    CatalogReported,
    Failed,
    Completed
}

internal sealed record ModRefreshResult(
    int Generation,
    ModRefreshResultKind Kind,
    (string Directory, string Name) Mod,
    string SourceStamp = "",
    string Signature = "",
    int ManifestFileCount = 0,
    long ManifestBytes = 0,
    CachedAnimationMod? Cached = null,
    PortableAnimationIndexPayload? Payload = null,
    string PortablePayloadJson = "",
    bool CacheHit = false,
    bool RelayHit = false,
    string Error = "");

/// <summary>
/// Owns the one bounded filesystem/network worker used by a library refresh. This class is
/// deliberately outside Plugin's unsafe context so its asynchronous work cannot accidentally
/// migrate any game pointers or Penumbra IPC calls away from the framework thread.
/// </summary>
internal sealed class AnimationCatalogRefreshWorker(
    AnimationSyncService sync,
    string reporterId,
    Func<CancellationToken, Task> waitForScanSlot,
    Action<ModRefreshResult> publish)
{
    private const int MaximumEstimatedReportFrameBytes = 220 * 1024;
    private static readonly TimeSpan CatalogReportTtl = TimeSpan.FromDays(30);

    public async Task RunAsync(
        int generation,
        IReadOnlyList<ModRefreshWorkItem> work,
        CancellationToken cancellationToken)
    {
        try
        {
            var reports = new List<PendingCatalogReport>(128);
            var estimatedReportBytes = 0;
            foreach (var item in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await waitForScanSlot(cancellationToken);
                try
                {
                    var fileSet = AnimationManifestScanner.Inspect(item.Path, item.Mod.Name, cancellationToken);
                    var cacheValid = item.Cached is not null &&
                        item.Cached.SourceStamp.Equals(fileSet.SourceStamp, StringComparison.Ordinal) &&
                        item.Cached.IsAnimationMod == fileSet.ContainsPapFiles &&
                        HasPortableSignature(item.Cached);
                    var snapshot = cacheValid
                        ? null
                        : AnimationManifestScanner.Capture(fileSet, cancellationToken);
                    var prepared = new PreparedModRefresh(item, fileSet, snapshot, cacheValid);
                    var analyzed = AnalyzePreparedMod(generation, prepared);
                    var result = analyzed.Result;
                    // Relay data may fill missing portable metadata, but only after the local
                    // recursive PAP scan has positively identified the installed mod. Relay
                    // classifications never remove or veto a local animation.
                    if (fileSet.ContainsPapFiles && result.Payload is { PapGamePaths.Count: 0 } &&
                        result.Signature.Length == 64)
                    {
                        var catalog = await sync.LookupAnimationArtifactsAsync(
                            [new AnimationArtifactLookupKeyDto(
                                AnimationManifestScanner.SignatureAlgorithm, result.Signature)],
                            cancellationToken);
                        var relayEntry = catalog?.FirstOrDefault();
                        if (IsStrongRelayAnimation(relayEntry) &&
                            AnimationManifestScanner.TryReadRelayPayload(relayEntry!.Payload!, out var relayPayload))
                            result = result with
                            {
                                Payload = relayPayload,
                                PortablePayloadJson = relayEntry.Payload!.Json,
                                RelayHit = true
                            };
                    }
                    publish(result);
                    if (analyzed.Report is null) continue;

                    var reportBytes = EstimateSerializedBytes(analyzed.Report.Submission);
                    if (reports.Count > 0 && (reports.Count == 128 ||
                            estimatedReportBytes + reportBytes > MaximumEstimatedReportFrameBytes))
                    {
                        await SubmitReportBatchAsync(generation, reports, cancellationToken);
                        reports.Clear();
                        estimatedReportBytes = 0;
                    }
                    reports.Add(analyzed.Report);
                    estimatedReportBytes += reportBytes;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    publish(new ModRefreshResult(
                        generation, ModRefreshResultKind.Failed, item.Mod,
                        Error: ex.GetBaseException().Message));
                }
            }

            if (reports.Count > 0)
                await SubmitReportBatchAsync(generation, reports, cancellationToken);

            publish(new ModRefreshResult(generation, ModRefreshResultKind.Completed, default));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dispose owns cancellation. Results already published are safe; the framework
            // generation gate discards anything stale.
        }
        catch (Exception ex)
        {
            publish(new ModRefreshResult(
                generation, ModRefreshResultKind.Completed, default,
                Error: ex.GetBaseException().Message));
        }
    }

    private static (ModRefreshResult Result, PendingCatalogReport? Report) AnalyzePreparedMod(
        int generation,
        PreparedModRefresh prepared)
    {
        var signature = RefreshSignature(prepared);
        var count = prepared.FileSet.Files.Count;
        var bytes = prepared.Snapshot?.ManifestBytes ??
                    (prepared.Work.Cached?.ManifestBytes > 0
                        ? prepared.Work.Cached.ManifestBytes
                        : prepared.FileSet.Files.Sum(file => file.Length));
        var cached = prepared.Work.Cached;

        if (prepared.CacheValid && prepared.Snapshot is null && cached is not null)
        {
            var cachedReport = BuildCachedReport(prepared.Work.Mod, cached, signature, count, bytes);
            return (new ModRefreshResult(generation, ModRefreshResultKind.Cached, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes, Cached: cached, CacheHit: true), cachedReport);
        }

        var snapshot = prepared.Snapshot ??
            AnimationManifestScanner.Capture(prepared.FileSet, CancellationToken.None);
        var payload = AnimationManifestScanner.Extract(snapshot);
        var classification = prepared.FileSet.ContainsPapFiles
            ? AnimationArtifactClassificationDto.Animation
            : AnimationArtifactClassificationDto.NonAnimation;
        string portableJson = "";
        var submission = classification == AnimationArtifactClassificationDto.Animation
            ? AnimationManifestScanner.CreateSubmission(payload, out portableJson)
            : null;
        var localSubmission = new AnimationArtifactReportSubmissionDto(
            signature,
            AnimationManifestScanner.SignatureAlgorithm,
            prepared.Work.Mod.Name,
            classification,
            count,
            bytes,
            submission);
        if (EstimateSerializedBytes(localSubmission) > MaximumEstimatedReportFrameBytes)
            localSubmission = localSubmission with { Payload = null };
        var localReport = new PendingCatalogReport(prepared.Work.Mod, localSubmission);
        return classification == AnimationArtifactClassificationDto.Animation
            ? (new ModRefreshResult(generation, ModRefreshResultKind.PortableAnimation, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes, Payload: payload,
                PortablePayloadJson: submission is null ? "" : portableJson), localReport)
            : (new ModRefreshResult(generation, ModRefreshResultKind.NonAnimation, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes), localReport);
    }

    private async Task SubmitReportBatchAsync(
        int generation,
        IReadOnlyList<PendingCatalogReport> batch,
        CancellationToken cancellationToken)
    {
        var accepted = await sync.SubmitAnimationArtifactReportsAsync(
            reporterId, batch.Select(report => report.Submission).ToList(), cancellationToken);
        if (accepted is null) return;
        foreach (var report in batch)
            publish(new ModRefreshResult(
                generation,
                ModRefreshResultKind.CatalogReported,
                report.Mod,
                Signature: report.Submission.Signature));
    }

    private static int EstimateSerializedBytes(AnimationArtifactReportSubmissionDto report) =>
        256 + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(report));

    private static PendingCatalogReport? BuildCachedReport(
        (string Directory, string Name) mod,
        CachedAnimationMod cached,
        string signature,
        int manifestFileCount,
        long manifestBytes)
    {
        if (signature.Length != 64) return null;
        if (cached.LastCatalogReportSignature.Equals(signature, StringComparison.OrdinalIgnoreCase) &&
            cached.LastCatalogReportSchemaVersion == AnimationManifestScanner.PayloadSchemaVersion &&
            cached.LastCatalogReportExtractorVersion.Equals(
                AnimationManifestScanner.ExtractorVersion, StringComparison.Ordinal) &&
            cached.LastCatalogReportName.Equals(mod.Name, StringComparison.Ordinal) &&
            cached.LastCatalogReportUtc >= DateTimeOffset.UtcNow - CatalogReportTtl)
            return null;
        PortableAnimationPayloadSubmissionDto? payload = null;
        if (cached.IsAnimationMod && cached.PortablePayloadJson.Length > 0 &&
            Encoding.UTF8.GetByteCount(cached.PortablePayloadJson) <= AnimationManifestScanner.MaximumPortablePayloadBytes)
            payload = new PortableAnimationPayloadSubmissionDto(
                AnimationManifestScanner.PayloadSchemaVersion,
                AnimationManifestScanner.ExtractorVersion,
                cached.PortablePayloadJson);
        var submission = new AnimationArtifactReportSubmissionDto(
            signature,
            AnimationManifestScanner.SignatureAlgorithm,
            mod.Name,
            cached.IsAnimationMod
                ? AnimationArtifactClassificationDto.Animation
                : AnimationArtifactClassificationDto.NonAnimation,
            manifestFileCount,
            manifestBytes,
            payload);
        if (EstimateSerializedBytes(submission) > MaximumEstimatedReportFrameBytes)
            submission = submission with { Payload = null };
        return new PendingCatalogReport(mod, submission);
    }

    private static bool HasPortableSignature(CachedAnimationMod cached) =>
        cached.SignatureAlgorithm.Equals(AnimationManifestScanner.SignatureAlgorithm, StringComparison.Ordinal) &&
        cached.ManifestSignature.Length == 64 && cached.ManifestSignature.All(Uri.IsHexDigit);

    private static string RefreshSignature(PreparedModRefresh prepared) =>
        prepared.Snapshot?.Signature ?? prepared.Work.Cached?.ManifestSignature ?? "";

    private static bool IsStrongRelayAnimation(AnimationArtifactCatalogEntryDto? entry) =>
        entry is
        {
            EffectiveClassification: AnimationArtifactClassificationDto.Animation,
            Payload: not null,
            IsModeratorVerified: true,
            IsPayloadModeratorVerified: true
        };

}

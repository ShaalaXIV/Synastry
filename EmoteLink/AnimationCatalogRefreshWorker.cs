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
    Func<bool> useFastRate,
    Action<ModRefreshResult> publish)
{
    private const int LookupBatchSize = 64;
    private const long SnapshotBatchBytes = 16L * 1024 * 1024;
    private const int MaximumEstimatedReportFrameBytes = 220 * 1024;
    private static readonly TimeSpan CatalogReportTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan BackgroundUncachedScanDelay = TimeSpan.FromSeconds(1);

    public async Task RunAsync(
        int generation,
        IReadOnlyList<ModRefreshWorkItem> work,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var offset = 0; offset < work.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = new List<PreparedModRefresh>(LookupBatchSize);
                long capturedBytes = 0;
                while (offset < work.Count && prepared.Count < LookupBatchSize &&
                       (capturedBytes < SnapshotBatchBytes || prepared.Count == 0))
                {
                    var item = work[offset++];
                    try
                    {
                        var fileSet = AnimationManifestScanner.Inspect(item.Path, item.Mod.Name, cancellationToken);
                        var cacheValid = item.Cached is not null &&
                            item.Cached.SourceStamp.Equals(fileSet.SourceStamp, StringComparison.Ordinal);
                        AnimationManifestSnapshot? snapshot = null;
                        if (!cacheValid || !HasPortableSignature(item.Cached!))
                        {
                            if (!useFastRate())
                                await Task.Delay(BackgroundUncachedScanDelay, cancellationToken);
                            snapshot = AnimationManifestScanner.Capture(fileSet, cancellationToken);
                            capturedBytes += snapshot.ManifestBytes;
                        }
                        prepared.Add(new PreparedModRefresh(item, fileSet, snapshot, cacheValid));
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

                // A source-stamp-valid v2 cache is already exact local evidence and needs no
                // relay round trip. Lookup is reserved for new/changed manifests and the one-
                // time v1 cache migration, keeping steady-state refresh traffic near zero.
                var keys = prepared
                    .Where(item => !item.CacheValid || item.Snapshot is not null)
                    .Select(item => new AnimationArtifactLookupKeyDto(
                        AnimationManifestScanner.SignatureAlgorithm, RefreshSignature(item)))
                    .Where(key => key.Signature.Length == 64)
                    .Distinct()
                    .ToList();
                var catalog = await sync.LookupAnimationArtifactsAsync(keys, cancellationToken);
                var catalogBySignature = catalog?.ToDictionary(
                    entry => entry.Signature, StringComparer.OrdinalIgnoreCase);
                var reports = new List<PendingCatalogReport>();
                foreach (var item in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var signature = RefreshSignature(item);
                    AnimationArtifactCatalogEntryDto? catalogEntry = null;
                    catalogBySignature?.TryGetValue(signature, out catalogEntry);
                    var analyzed = AnalyzePreparedMod(generation, item, catalogEntry);
                    publish(analyzed.Result);
                    if (analyzed.Report is not null) reports.Add(analyzed.Report);
                }
                await SubmitCatalogReportsAsync(generation, reports, cancellationToken);
            }

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
        PreparedModRefresh prepared,
        AnimationArtifactCatalogEntryDto? catalog)
    {
        var signature = RefreshSignature(prepared);
        var count = prepared.FileSet.Files.Count;
        var bytes = prepared.Snapshot?.ManifestBytes ??
                    (prepared.Work.Cached?.ManifestBytes > 0
                        ? prepared.Work.Cached.ManifestBytes
                        : prepared.FileSet.Files.Sum(file => file.Length));
        var cached = prepared.Work.Cached;

        if (prepared.CacheValid && prepared.Snapshot is null && cached is { IsAnimationMod: true })
        {
            var cachedReport = BuildCachedReport(prepared.Work.Mod, cached, signature, count, bytes);
            return (new ModRefreshResult(generation, ModRefreshResultKind.Cached, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes, Cached: cached, CacheHit: true), cachedReport);
        }

        if (IsStrongRelayAnimation(catalog) &&
            AnimationManifestScanner.TryReadRelayPayload(catalog!.Payload!, out var relayPayload))
        {
            return (new ModRefreshResult(generation, ModRefreshResultKind.PortableAnimation, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes, Payload: relayPayload,
                PortablePayloadJson: catalog.Payload!.Json, RelayHit: true), null);
        }

        if (prepared.CacheValid && prepared.Snapshot is null && cached is { IsAnimationMod: false })
        {
            var cachedReport = BuildCachedReport(prepared.Work.Mod, cached, signature, count, bytes);
            return (new ModRefreshResult(generation, ModRefreshResultKind.Cached, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes, Cached: cached, CacheHit: true), cachedReport);
        }

        if (IsStrongRelayNonAnimation(catalog))
        {
            return (new ModRefreshResult(generation, ModRefreshResultKind.NonAnimation, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes, RelayHit: true), null);
        }

        var snapshot = prepared.Snapshot ??
            AnimationManifestScanner.Capture(prepared.FileSet, CancellationToken.None);
        var payload = AnimationManifestScanner.Extract(snapshot);
        var classification = payload.PapGamePaths.Count > 0
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
                PortablePayloadJson: portableJson), localReport)
            : (new ModRefreshResult(generation, ModRefreshResultKind.NonAnimation, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes), localReport);
    }

    private async Task SubmitCatalogReportsAsync(
        int generation,
        IReadOnlyList<PendingCatalogReport> reports,
        CancellationToken cancellationToken)
    {
        if (reports.Count == 0) return;
        var batch = new List<PendingCatalogReport>(128);
        var estimatedBytes = 0;
        foreach (var report in reports)
        {
            var reportBytes = EstimateSerializedBytes(report.Submission);
            if (batch.Count > 0 && (batch.Count == 128 ||
                    estimatedBytes + reportBytes > MaximumEstimatedReportFrameBytes))
            {
                await SubmitReportBatchAsync(generation, batch, cancellationToken);
                batch.Clear();
                estimatedBytes = 0;
            }
            batch.Add(report);
            estimatedBytes += reportBytes;
        }
        if (batch.Count > 0)
            await SubmitReportBatchAsync(generation, batch, cancellationToken);
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

    private static bool IsStrongRelayNonAnimation(AnimationArtifactCatalogEntryDto? entry) =>
        entry is
        {
            EffectiveClassification: AnimationArtifactClassificationDto.NonAnimation,
            IsModeratorVerified: true
        };
}

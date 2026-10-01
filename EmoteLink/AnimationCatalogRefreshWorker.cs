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

internal enum ModRefreshResultKind
{
    Cached,
    PortableAnimation,
    NonAnimation,
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
    string Error = "");

/// <summary>
/// Owns the one bounded filesystem worker used by a library refresh. Everything it reads stays
/// on this computer: nothing about installed mods is sent to the relay. This class is
/// deliberately outside Plugin's unsafe context so its asynchronous work cannot accidentally
/// migrate any game pointers or Penumbra IPC calls away from the framework thread.
/// </summary>
internal sealed class AnimationCatalogRefreshWorker(
    Func<CancellationToken, Task> waitForScanSlot,
    Action<ModRefreshResult> publish)
{
    public async Task RunAsync(
        int generation,
        IReadOnlyList<ModRefreshWorkItem> work,
        CancellationToken cancellationToken)
    {
        try
        {
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
                    publish(AnalyzePreparedMod(generation, prepared));
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

    private static ModRefreshResult AnalyzePreparedMod(int generation, PreparedModRefresh prepared)
    {
        var signature = RefreshSignature(prepared);
        var count = prepared.FileSet.Files.Count;
        var bytes = prepared.Snapshot?.ManifestBytes ??
                    (prepared.Work.Cached?.ManifestBytes > 0
                        ? prepared.Work.Cached.ManifestBytes
                        : prepared.FileSet.Files.Sum(file => file.Length));
        var cached = prepared.Work.Cached;

        if (prepared.CacheValid && prepared.Snapshot is null && cached is not null)
            return new ModRefreshResult(generation, ModRefreshResultKind.Cached, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes, Cached: cached, CacheHit: true);

        if (!prepared.FileSet.ContainsPapFiles)
            return new ModRefreshResult(generation, ModRefreshResultKind.NonAnimation, prepared.Work.Mod,
                prepared.FileSet.SourceStamp, signature, count, bytes);

        var snapshot = prepared.Snapshot ??
            AnimationManifestScanner.Capture(prepared.FileSet, CancellationToken.None);
        return new ModRefreshResult(generation, ModRefreshResultKind.PortableAnimation, prepared.Work.Mod,
            prepared.FileSet.SourceStamp, signature, count, bytes, Payload: AnimationManifestScanner.Extract(snapshot));
    }

    private static bool HasPortableSignature(CachedAnimationMod cached) =>
        cached.SignatureAlgorithm.Equals(AnimationManifestScanner.SignatureAlgorithm, StringComparison.Ordinal) &&
        cached.ManifestSignature.Length == 64 && cached.ManifestSignature.All(Uri.IsHexDigit);

    private static string RefreshSignature(PreparedModRefresh prepared) =>
        prepared.Snapshot?.Signature ?? prepared.Work.Cached?.ManifestSignature ?? "";

}

using Dalamud.Plugin.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmoteLink;

internal sealed class AnimationIndexCache
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string path;
    private readonly IPluginLog log;
    private readonly Dictionary<string, CachedAnimationMod> entries;
    private readonly object gate = new();
    private long revision;
    private bool dirty;
    private Task saveTask = Task.CompletedTask;

    private AnimationIndexCache(string path, IPluginLog log, Dictionary<string, CachedAnimationMod> entries)
    {
        this.path = path;
        this.log = log;
        this.entries = new Dictionary<string, CachedAnimationMod>(entries, StringComparer.OrdinalIgnoreCase);
    }

    public static AnimationIndexCache Load(string path, IPluginLog log)
    {
        try
        {
            if (File.Exists(path))
            {
                var document = JsonSerializer.Deserialize<CachedAnimationIndex>(
                    File.ReadAllText(path), SerializerOptions);
                // Version 2 adds the portable manifest signature and relay payload. Version 1
                // entries remain useful as the last-known local library and are upgraded in
                // place by the background validator instead of forcing a cold rescan.
                if (document?.Version is 1 or CurrentVersion)
                    return new AnimationIndexCache(path, log, document.Mods);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not load the local animation index; mods will be scanned normally.");
        }

        return new AnimationIndexCache(path, log, []);
    }

    public bool TryGet(string directory, string sourceStamp, out CachedAnimationMod cached)
    {
        lock (gate)
        {
            if (entries.TryGetValue(directory, out var candidate) &&
                candidate.SourceStamp.Equals(sourceStamp, StringComparison.Ordinal))
            {
                cached = Clone(candidate);
                return true;
            }

            cached = null!;
            return false;
        }
    }

    public bool TryGetLastKnown(string directory, out CachedAnimationMod cached)
    {
        lock (gate)
        {
            if (entries.TryGetValue(directory, out var candidate))
            {
                cached = Clone(candidate);
                return true;
            }
            cached = null!;
            return false;
        }
    }

    public void Set(CachedAnimationMod cached)
    {
        lock (gate)
        {
            entries[cached.Directory] = Clone(cached);
            dirty = true;
            revision++;
        }
    }

    public void RemoveExcept(IEnumerable<string> currentDirectories)
    {
        var current = currentDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            foreach (var directory in entries.Keys.Where(directory => !current.Contains(directory)).ToList())
            {
                entries.Remove(directory);
                dirty = true;
                revision++;
            }
        }
    }

    public void MarkCatalogReported(
        string directory,
        string signature,
        string displayName,
        DateTimeOffset reportedUtc)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(directory, out var cached) ||
                !cached.ManifestSignature.Equals(signature, StringComparison.OrdinalIgnoreCase)) return;
            var updated = Clone(cached);
            updated.LastCatalogReportSignature = signature;
            updated.LastCatalogReportSchemaVersion = AnimationManifestScanner.PayloadSchemaVersion;
            updated.LastCatalogReportExtractorVersion = AnimationManifestScanner.ExtractorVersion;
            updated.LastCatalogReportName = displayName;
            updated.LastCatalogReportUtc = reportedUtc;
            entries[directory] = updated;
            dirty = true;
            revision++;
        }
    }

    public void MarkEmotesIndexed(string directory, IReadOnlyList<EmoteTarget> emotes)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(directory, out var cached) || !cached.IsAnimationMod) return;
            var updated = Clone(cached);
            updated.EmotesIndexed = true;
            updated.Emotes = emotes.ToList();
            entries[directory] = updated;
            dirty = true;
            revision++;
        }
    }

    public void Save()
    {
        SaveInBackgroundAsync().GetAwaiter().GetResult();
    }

    public Task SaveInBackgroundAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!dirty) return saveTask;
            if (!saveTask.IsCompleted) return saveTask;
            saveTask = Task.Run(() => SaveLatestSnapshots(cancellationToken), cancellationToken);
            return saveTask;
        }
    }

    private void SaveLatestSnapshots(CancellationToken cancellationToken)
    {
        while (true)
        {
            CachedAnimationIndex document;
            long saveRevision;
            lock (gate)
            {
                if (!dirty) return;
                saveRevision = revision;
                document = Snapshot();
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteSnapshot(document);
                lock (gate)
                {
                    if (revision == saveRevision) dirty = false;
                    if (!dirty) return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Could not save the local animation index.");
                return;
            }
        }
    }

    private CachedAnimationIndex Snapshot() => new()
    {
        Version = CurrentVersion,
        Mods = entries.ToDictionary(pair => pair.Key, pair => Clone(pair.Value),
            StringComparer.OrdinalIgnoreCase)
    };

    private static CachedAnimationMod Clone(CachedAnimationMod source) => new()
    {
        Directory = source.Directory,
        SourceStamp = source.SourceStamp,
        ManifestSignature = source.ManifestSignature,
        SignatureAlgorithm = source.SignatureAlgorithm,
        ManifestFileCount = source.ManifestFileCount,
        ManifestBytes = source.ManifestBytes,
        PortablePayloadJson = source.PortablePayloadJson,
        LastCatalogReportSignature = source.LastCatalogReportSignature,
        LastCatalogReportSchemaVersion = source.LastCatalogReportSchemaVersion,
        LastCatalogReportExtractorVersion = source.LastCatalogReportExtractorVersion,
        LastCatalogReportName = source.LastCatalogReportName,
        LastCatalogReportUtc = source.LastCatalogReportUtc,
        IsAnimationMod = source.IsAnimationMod,
        SyncKey = source.SyncKey,
        EmotesIndexed = source.EmotesIndexed,
        OptionGroups = source.OptionGroups.Select(group => new CachedOptionGroup
        {
            Name = group.Name,
            Options = group.Options.ToList(),
            IsMultiSelect = group.IsMultiSelect
        }).ToList(),
        OptionPoses = source.OptionPoses.Select(pose => new CachedOptionPose
        {
            Group = pose.Group,
            Option = pose.Option,
            Kind = pose.Kind,
            Index = pose.Index
        }).ToList(),
        Poses = source.Poses.ToList(),
        Emotes = source.Emotes.ToList()
    };

    private void WriteSnapshot(CachedAnimationIndex document)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, SerializerOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
    }

    public static string BuildSourceStamp(string modName, IReadOnlyList<FileInfo> manifestFiles)
    {
        var source = new StringBuilder();
        source.Append(modName.Trim()).Append('\n');

        foreach (var file in manifestFiles.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            source.Append(file.Name.ToLowerInvariant()).Append('\0')
                .Append(file.Length).Append('\0')
                .Append(file.LastWriteTimeUtc.Ticks).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }
}

internal sealed class CachedAnimationIndex
{
    public int Version { get; set; }
    public Dictionary<string, CachedAnimationMod> Mods { get; set; } = [];
}

internal sealed class CachedAnimationMod
{
    public string Directory { get; set; } = "";
    public string SourceStamp { get; set; } = "";
    public string ManifestSignature { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "";
    public int ManifestFileCount { get; set; }
    public long ManifestBytes { get; set; }
    public string PortablePayloadJson { get; set; } = "";
    public string LastCatalogReportSignature { get; set; } = "";
    public int LastCatalogReportSchemaVersion { get; set; }
    public string LastCatalogReportExtractorVersion { get; set; } = "";
    public string LastCatalogReportName { get; set; } = "";
    public DateTimeOffset LastCatalogReportUtc { get; set; }
    public bool IsAnimationMod { get; set; }
    public string SyncKey { get; set; } = "";
    public bool EmotesIndexed { get; set; }
    public List<CachedOptionGroup> OptionGroups { get; set; } = [];
    public List<CachedOptionPose> OptionPoses { get; set; } = [];
    public List<PoseTarget> Poses { get; set; } = [];
    public List<EmoteTarget> Emotes { get; set; } = [];
}

internal sealed class CachedOptionGroup
{
    public string Name { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public bool IsMultiSelect { get; set; }
}

internal sealed class CachedOptionPose
{
    public string Group { get; set; } = "";
    public string Option { get; set; } = "";
    public PoseKind Kind { get; set; }
    public byte Index { get; set; }
}

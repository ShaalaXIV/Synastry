using Dalamud.Plugin.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmoteLink;

internal sealed class AnimationIndexCache
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string path;
    private readonly IPluginLog log;
    private readonly Dictionary<string, CachedAnimationMod> entries;
    private bool dirty;

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
                if (document?.Version == CurrentVersion)
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
        if (entries.TryGetValue(directory, out var candidate) &&
            candidate.SourceStamp.Equals(sourceStamp, StringComparison.Ordinal))
        {
            cached = candidate;
            return true;
        }

        cached = null!;
        return false;
    }

    public void Set(CachedAnimationMod cached)
    {
        entries[cached.Directory] = cached;
        dirty = true;
    }

    public void RemoveExcept(IEnumerable<string> currentDirectories)
    {
        var current = currentDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in entries.Keys.Where(directory => !current.Contains(directory)).ToList())
        {
            entries.Remove(directory);
            dirty = true;
        }
    }

    public void Save()
    {
        if (!dirty) return;

        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            var temporaryPath = path + ".tmp";
            var document = new CachedAnimationIndex
            {
                Version = CurrentVersion,
                Mods = entries
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, SerializerOptions));
            File.Move(temporaryPath, path, true);
            dirty = false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not save the local animation index.");
        }
    }

    public static string BuildSourceStamp(string modPath, string modName)
    {
        var source = new StringBuilder();
        source.Append(modName.Trim()).Append('\n');
        source.Append(Directory.GetLastWriteTimeUtc(modPath).Ticks).Append('\n');

        foreach (var file in Directory.EnumerateFiles(modPath, "*.json", SearchOption.TopDirectoryOnly)
                     .Select(file => new FileInfo(file))
                     .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
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
    public bool IsAnimationMod { get; set; }
    public string SyncKey { get; set; } = "";
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

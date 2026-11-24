using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace EmoteLink.Align;

/// <summary>How bone align last ended on one animation.</summary>
internal sealed class AlignmentEntry
{
    public string Key { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    /// <summary>The role or animation it was made in, for showing.</summary>
    public string Label { get; set; } = "";
    public BodyPart Self { get; set; }
    public BodyPart Partner { get; set; }
    public float Gap { get; set; }
    /// <summary>Your drawn facing minus your partner's, in radians.</summary>
    public float RelativeYaw { get; set; }
    /// <summary>Your part minus your partner's, in their drawn frame. Relative to the parts, so it
    /// holds for any two bodies.</summary>
    public StoredVector ContactOffset { get; set; } = new();
    /// <summary>The animation file your partner was playing; null when it wasn't known.</summary>
    public string? PartnerHash { get; set; }
    public DateTime Updated { get; set; }
}

/// <summary>A Vector3 as JSON properties; System.Numerics.Vector3 only has fields.</summary>
internal sealed class StoredVector
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3 ToVector3() => new(X, Y, Z);

    public static StoredVector From(Vector3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };
}

/// <summary>
/// Remembers each bone align you make, per animation, so it can be repeated the next time. It has its
/// own file, alignments.json, so a bad one can't break the settings: an unreadable file starts empty
/// and is kept as .bad, and every save goes through a temporary file.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
internal sealed class AlignmentMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class FileDto
    {
        public int Version { get; set; } = 1;
        public List<AlignmentEntry> Entries { get; set; } = [];
    }

    private readonly string path;
    private readonly IPluginLog log;
    private readonly Dictionary<string, AlignmentEntry> entries = new(StringComparer.Ordinal);
    private bool loadFailed;

    public AlignmentMemory(string configDirectory, IPluginLog log)
    {
        path = Path.Combine(configDirectory, "alignments.json");
        this.log = log;
        Load();
    }

    /// <summary>The two animation files that are really playing. They survive a renamed mod, tell roles
    /// and options apart, and are the same for every player.</summary>
    public static string FileKey(string own, string partner) => $"mine={own}|with={partner}";

    /// <summary>The mod, its options and the role, for when a file isn't known.</summary>
    public static string ModKey(PlayContext play)
    {
        var mod = play.SyncKey.Length > 0 ? play.SyncKey : play.Directory;
        var options = string.Join(";", play.Selections.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={string.Join(",", pair.Value.Order(StringComparer.Ordinal))}"));
        return $"mod={mod}|{options}|{play.Trigger}";
    }

    public bool IsEmpty => entries.Count == 0;

    public AlignmentEntry? TryGet(string key) => entries.GetValueOrDefault(key);

    public void Record(AlignmentEntry entry)
    {
        entries[entry.Key] = entry;
        Save();
    }

    public void Forget(string key)
    {
        if (entries.Remove(key)) Save();
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var entry in JsonSerializer.Deserialize<FileDto>(text, JsonOptions)?.Entries ?? [])
                if (entry.Key.Length > 0)
                    entries[entry.Key] = entry;
        }
        catch (Exception exception)
        {
            loadFailed = true;
            entries.Clear();
            log.Warning(exception, "Couldn't read {Path}; alignment memory starts empty.", path);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (loadFailed && File.Exists(path)) File.Move(path, path + ".bad", overwrite: true);
            loadFailed = false;

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new FileDto { Entries = [.. entries.Values] }, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Couldn't save alignment memory to {Path}.", path);
        }
    }
}

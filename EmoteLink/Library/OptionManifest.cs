using System.Text.Json;

namespace EmoteLink.Library;

/// <summary>
/// A mod's option groups in the author's order, as its manifests list them. Kept in the local index
/// only: room fingerprints, community labels and transfers are built from the portable payload,
/// which this leaves alone. Never changed after the scan, so cache copies share it.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class OptionManifest
{
    /// <summary>Raise when the contents change, so cached mods are scanned again.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; }
    /// <summary>Every .pap game path the options replace, once. Options refer to them by index, since a
    /// pack repeats the same few hundred paths across its whole list.</summary>
    public List<string> PapPaths { get; set; } = [];
    public List<ManifestGroup> Groups { get; set; } = [];
}

internal sealed class ManifestGroup
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsMulti { get; set; }
    /// <summary>The mod's always-active files, which aren't a group in Penumbra.</summary>
    public bool IsImplicit { get; set; }
    public List<ManifestOption> Options { get; set; } = [];
}

internal sealed class ManifestOption
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<int> Paths { get; set; } = [];
}

/// <summary>Collects a mod's groups while the scanner reads its manifests (on the refresh worker).</summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed class OptionManifestBuilder
{
    private readonly List<string> paths = [];
    private readonly Dictionary<string, int> pathIndex = new(StringComparer.Ordinal);
    private readonly List<ManifestGroup> metaGroups = [];
    private readonly List<ManifestGroup> fileGroups = [];
    private readonly ManifestOption defaults = new();

    /// <summary>The always-active files: default_mod.json itself, or meta.json's DefaultData.</summary>
    public void AddDefaults(JsonElement data) => AddFiles(data, defaults, []);

    public void AddGroup(JsonElement group, bool fromMeta)
    {
        if (group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("Options", out var options) || options.ValueKind != JsonValueKind.Array) return;
        var name = Text(group, "Name").Trim();
        if (name.Length == 0) return;

        // Only Single groups hold one option; Multi, Combining and Imc all switch options separately.
        var type = Text(group, "Type");
        var result = new ManifestGroup
        {
            Name = name,
            Description = Text(group, "Description").Trim(),
            IsMulti = type.Length > 0 && !type.Equals("Single", StringComparison.OrdinalIgnoreCase)
        };
        // Penumbra also writes an empty Containers list on other groups, so only the type says
        // whether the files live there.
        var containers = type.Equals("Combining", StringComparison.OrdinalIgnoreCase) &&
                         group.TryGetProperty("Containers", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : null;

        var byName = new Dictionary<string, ManifestOption>(StringComparer.OrdinalIgnoreCase);
        var index = -1;
        foreach (var element in options.EnumerateArray())
        {
            index++;
            var optionName = Text(element, "Name").Trim();
            if (optionName.Length == 0) continue;
            // Penumbra finds options by name, so a repeated name is one choice.
            if (!byName.TryGetValue(optionName, out var option))
            {
                byName[optionName] = option = new ManifestOption
                {
                    Name = optionName,
                    Description = Text(element, "Description").Trim()
                };
                result.Options.Add(option);
            }

            var known = option.Paths.ToHashSet();
            if (containers is null)
            {
                AddFiles(element, option, known);
                continue;
            }
            // One container per combination; bit i of its index means option i is on.
            for (var combination = 0; combination < containers.Count && index < 31; combination++)
                if ((combination & (1 << index)) != 0)
                    AddFiles(containers[combination], option, known);
        }
        (fromMeta ? metaGroups : fileGroups).Add(result);
    }

    public OptionManifest Build()
    {
        var groups = new List<ManifestGroup>();
        if (defaults.Paths.Count > 0)
            groups.Add(new ManifestGroup { IsImplicit = true, Options = [defaults] });
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Groups inside meta.json are Penumbra's newer layout; group files beside them are leftovers.
        foreach (var group in metaGroups.Concat(metaGroups.Count > 0
                     ? fileGroups.Where(file => metaGroups.All(meta => !meta.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                     : fileGroups))
        {
            if (positions.TryGetValue(group.Name, out var position))
            {
                groups[position] = group;
                continue;
            }
            positions[group.Name] = groups.Count;
            groups.Add(group);
        }
        return new OptionManifest { Version = OptionManifest.CurrentVersion, PapPaths = paths, Groups = groups };
    }

    private void AddFiles(JsonElement owner, ManifestOption option, HashSet<int> known)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Object) return;
        foreach (var file in files.EnumerateObject())
        {
            var path = file.Name.Replace('\\', '/').Trim().ToLowerInvariant();
            // Some mods keep leftovers that aren't game paths.
            if (!path.EndsWith(".pap", StringComparison.Ordinal) ||
                !path.StartsWith("chara/", StringComparison.Ordinal)) continue;
            if (!pathIndex.TryGetValue(path, out var id))
            {
                pathIndex[path] = id = paths.Count;
                paths.Add(path);
            }
            if (known.Add(id)) option.Paths.Add(id);
        }
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

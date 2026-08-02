using Dalamud.Configuration;
using Dalamud.Plugin;

namespace EmoteLink;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public List<TemporaryAssignment> ActiveAssignments { get; set; } = [];
    public List<ModCategory> Categories { get; set; } = [];
    public List<string> UncategorizedOrder { get; set; } = [];
    public Dictionary<string, Dictionary<string, List<string>>> ModOptionSelections { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ManualPoseAssignment> ManualPoseAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OptionNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Save(IDalamudPluginInterface pluginInterface) => pluginInterface.SavePluginConfig(this);
}

public sealed record TemporaryAssignment(Guid CollectionId, string ModDirectory, string ModName);

public sealed class ModCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Folder";
    public List<string> ModDirectories { get; set; } = [];
}

public sealed class ManualPoseAssignment
{
    public PoseKind Kind { get; set; }
    public byte Index { get; set; }
}

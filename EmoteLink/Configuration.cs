using Dalamud.Configuration;
using Dalamud.Plugin;

namespace EmoteLink;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 9;
    public bool HasSeenHowTo { get; set; }
    public List<TemporaryAssignment> ActiveAssignments { get; set; } = [];
    public List<ModCategory> Categories { get; set; } = [];
    public List<string> UncategorizedOrder { get; set; } = [];
    public Dictionary<string, Dictionary<string, List<string>>> ModOptionSelections { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ManualPoseAssignment> ManualPoseAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OptionNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PrivateMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CommunityRoleKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string CommunityReporterId { get; set; } = Guid.NewGuid().ToString("N");
    // Catalog evidence deliberately uses a separate pseudonymous identifier. The relay
    // derives a per-signature hash from it, so reports cannot be joined into a mod inventory.
    public string CatalogReporterId { get; set; } = Guid.NewGuid().ToString("N");
    // Localhost-only override for testing a relay build before public deployment.
    public string LocalRelayUrl { get; set; } = "";
    // Penumbra mod-list organization path. Empty keeps received mods at the top level.
    public string ReceivedModFolder { get; set; } = "";
    public bool AutomaticEmoteSync { get; set; } = true;
    public bool SitDozeAnywhere { get; set; }
    public List<CustomAnimationCommand> CustomAnimationCommands { get; set; } = [];

    public void Save(IDalamudPluginInterface pluginInterface) => pluginInterface.SavePluginConfig(this);
}

public sealed record TemporaryAssignment(Guid CollectionId, string ModDirectory, string ModName);

public sealed class ModCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Folder";
    // Null is a root Animation Library folder. Existing configurations deserialize as roots.
    public string? ParentId { get; set; }
    public List<string> ModDirectories { get; set; } = [];
}

public sealed class ManualPoseAssignment
{
    public PoseKind Kind { get; set; }
    public byte Index { get; set; }
}

public enum CustomAnimationTriggerKind
{
    Pose,
    Emote
}

public sealed class CustomAnimationCommand
{
    public string Command { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public CustomAnimationTriggerKind TriggerKind { get; set; }
    public string TriggerValue { get; set; } = "";
    public string AnimationName { get; set; } = "";
}

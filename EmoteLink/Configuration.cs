using Dalamud.Configuration;
using Dalamud.Plugin;

namespace EmoteLink;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 10;
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
    // The last moderator revision applied to each tag. A moderator's tag replaces the player's
    // own once; after that it's theirs to change, until a moderator acts on it again.
    public Dictionary<string, int> AppliedTagModerations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string CommunityReporterId { get; set; } = Guid.NewGuid().ToString("N");
    // Catalog evidence deliberately uses a separate pseudonymous identifier. The relay
    // derives a per-signature hash from it, so reports cannot be joined into a mod inventory.
    // Was the identity used to report every installed mod to the relay (before 1.0.78). Nothing
    // reads it any more; it stays so older settings files still load and can be cleared.
    public string CatalogReporterId { get; set; } = "";
    // Localhost-only override for testing a relay build before public deployment.
    public string LocalRelayUrl { get; set; } = "";
    // Penumbra mod-list organization path. Empty keeps received mods at the top level.
    public string ReceivedModFolder { get; set; } = "";
    public bool AutomaticEmoteSync { get; set; } = true;
    public bool SitDozeAnywhere { get; set; }
    // With SitDozeAnywhere on: only doze plays in place; sitting still needs real furniture.
    public bool DozeAnywhereOnly { get; set; }
    // Open Synastry as the small mini player instead of the full window.
    public bool UseMiniPlayer { get; set; }
    // Line up a couple animation's contact (penis with mouth, vagina or anus) through Simple Heels.
    public bool AutomaticLineUp { get; set; } = true;
    // Which part line-up aims for when more than one is in reach.
    public ContactPreference LineUpPreference { get; set; } = ContactPreference.Closest;
    // Bending bones to make couple animations meet.
    public bool BendShaft { get; set; } = true;
    public bool BendOpenings { get; set; } = true;
    public bool BendHands { get; set; } = true;
    // Body setup results from 1.0.80-1.0.81, one per character; moved into BodyMeshes as "My body".
    public Dictionary<string, string> BodyProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Saved meshes per character (for example Male, Female, Futa), each a body profile as JSON.
    public Dictionary<string, Dictionary<string, string>> BodyMeshes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Which saved mesh each character is using.
    public Dictionary<string, string> ActiveBodyMesh { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CustomAnimationCommand> CustomAnimationCommands { get; set; } = [];
    public Dictionary<uint, string> TypedEmoteDefaults { get; set; } = [];

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

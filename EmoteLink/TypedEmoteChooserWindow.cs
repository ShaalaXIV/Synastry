using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class TypedEmoteChooserWindow : Window
{
    private static readonly Vector4 AccentColor = new(0.72f, 0.46f, 0.88f, 1f);
    private readonly Plugin plugin;
    private string command = "";
    private IReadOnlyList<TypedEmoteCandidate> candidates = [];

    public TypedEmoteChooserWindow(Plugin plugin)
        : base("Choose a Synastry Animation###SynastryTypedEmoteChooser")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(760, 720)
        };
    }

    public void Show(string emoteCommand, IReadOnlyList<TypedEmoteCandidate> available)
    {
        command = emoteCommand;
        candidates = available;
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextColored(AccentColor, command);
        ImGui.TextWrapped(
            "This emote is locked, and more than one installed Synastry animation uses it. " +
            "Choose the animation to activate. Your choice becomes the default the next time you use this emote.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var listHeight = MathF.Max(90f, ImGui.GetContentRegionAvail().Y - 42f);
        if (ImGui.BeginChild("typed-emote-candidates", new Vector2(0, listHeight), true))
        {
            foreach (var candidate in candidates)
            {
                ImGui.PushID(candidate.Directory);
                ImGui.TextUnformatted(candidate.ModName);
                ImGui.TextDisabled(candidate.Emote.Name);
                ImGui.SameLine();
                var buttonWidth = 92f;
                ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - buttonWidth - 18f));
                if (ImGui.Button("Use this", new Vector2(buttonWidth, 0)))
                {
                    plugin.ActivateTypedEmote(candidate);
                    candidates = [];
                    IsOpen = false;
                    ImGui.PopID();
                    break;
                }
                ImGui.Separator();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Ignore", new Vector2(110f, 0)))
        {
            plugin.IgnoreTypedEmote(command);
            candidates = [];
            IsOpen = false;
        }
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class TypedEmoteChooserWindow : Window
{
    private readonly Plugin plugin;
    private string command = "";
    private IReadOnlyList<TypedEmoteCandidate> candidates = [];

    public TypedEmoteChooserWindow(Plugin plugin)
        : base("Which animation?###SynastryTypedEmoteChooser")
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

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 18f) * Theme.Scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.Pop();
    }

    public override void Draw()
    {
        Theme.Heading(command);
        Theme.Wrapped(
            "You don't have this emote, and more than one of your animations uses it. " +
            "Pick one. Next time this emote plays that one.", Theme.Soft);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var listHeight = MathF.Max(90f, ImGui.GetContentRegionAvail().Y - 42f);
        if (ImGui.BeginChild("typed-emote-candidates", new Vector2(0, listHeight), false))
        {
            foreach (var candidate in candidates)
            {
                ImGui.PushID(candidate.Directory);
                ImGui.TextUnformatted(candidate.ModName);
                Theme.Label(candidate.Emote.Name);
                ImGui.SameLine();
                var buttonWidth = 92f;
                ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - buttonWidth - 18f));
                if (Theme.Outline("Use this", buttonWidth))
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

        if (Theme.Text("Not now"))
        {
            plugin.IgnoreTypedEmote(command);
            candidates = [];
            IsOpen = false;
        }
    }
}

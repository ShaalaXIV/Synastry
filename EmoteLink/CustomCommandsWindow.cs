using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class CustomCommandsWindow : Window
{
    private readonly Plugin plugin;
    private List<AnimationCommandTarget> targets = [];
    private string commandInput = "";
    private string search = "";
    private bool refreshTargets = true;

    public CustomCommandsWindow(Plugin plugin)
        : base("Emote commands###SynastryCustomCommands")
    {
        this.plugin = plugin;
        Size = new Vector2(780, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 430),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Open()
    {
        refreshTargets = true;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24f, 18f) * Theme.Scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.Pop();
    }

    public override void Draw()
    {
        if (refreshTargets)
        {
            targets = plugin.GetAvailableAnimationCommandTargets();
            refreshTargets = false;
        }

        DrawHeading("Your commands");
        Theme.Wrapped("Give any animation its own slash command. Typing it does the same as clicking the animation.", Theme.Soft);
        ImGui.Spacing();
        DrawAssignments();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawHeading("Add one");
        Theme.Label("Type a command, find the animation, then press Assign. Reusing a command moves it.");

        ImGui.SetNextItemWidth(210f);
        ImGui.InputTextWithHint("##customCommand", "/wicked", ref commandInput, 33);
        ImGui.SameLine();
        Theme.Quiet("Letters, numbers, _ and - only");
        if (!string.IsNullOrWhiteSpace(plugin.Status))
            ImGui.TextColored(Theme.Ash, plugin.Status);

        var refreshWidth = ImGui.CalcTextSize("Reload list").X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetNextItemWidth(MathF.Max(180f, ImGui.GetContentRegionAvail().X - refreshWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##animationSearch", "Search by animation or mod", ref search, 128);
        ImGui.SameLine();
        if (Theme.Text("Reload list")) refreshTargets = true;

        var filtered = targets.Where(target => MatchesSearch(target, search)).Take(300).ToList();
        var totalMatches = targets.Count(target => MatchesSearch(target, search));
        ImGui.TextDisabled(totalMatches > filtered.Count
            ? $"Showing {filtered.Count:N0} of {totalMatches:N0} matches. Refine the search to see the rest."
            : $"{totalMatches:N0} animation{(totalMatches == 1 ? "" : "s")} available");

        ImGui.BeginChild("command-targets", Vector2.Zero, false);
        if (filtered.Count == 0)
        {
            ImGui.TextDisabled(targets.Count == 0
                ? "No indexed animations are available yet. Refresh Synastry's animation library, then refresh this list."
                : "No animations match this search.");
            ImGui.EndChild();
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("available-command-targets", 3, flags))
        {
            ImGui.TableSetupColumn("Animation", ImGuiTableColumnFlags.WidthStretch, 0.42f);
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 0.46f);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 82f);
            ImGui.TableHeadersRow();
            foreach (var target in filtered)
            {
                ImGui.PushID($"{target.ModDirectory}\n{target.TriggerKind}\n{target.TriggerValue}");
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(target.AnimationName);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextDisabled(target.ModName);
                ImGui.TableSetColumnIndex(2);
                var canAssign = !string.IsNullOrWhiteSpace(commandInput);
                if (!canAssign) ImGui.BeginDisabled();
                if (Theme.Text("Assign"))
                {
                    if (plugin.AssignCustomAnimationCommand(commandInput, target)) commandInput = "";
                }
                if (!canAssign) ImGui.EndDisabled();
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private void DrawAssignments()
    {
        var assignments = plugin.CustomAnimationCommands;
        if (assignments.Count == 0)
        {
            Theme.Quiet("None yet.");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("assigned-commands", 4, flags, new Vector2(0, MathF.Min(190f, 30f + assignments.Count * 27f))))
            return;
        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Animation", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 112f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var assignment in assignments)
        {
            ImGui.PushID(assignment.Command);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(Theme.RoseHover, assignment.Command);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(assignment.AnimationName);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextDisabled(assignment.ModName);
            ImGui.TableSetColumnIndex(3);
            if (Theme.Text("Run")) plugin.RunCustomAnimationCommand(assignment.Command);
            ImGui.SameLine();
            if (Theme.Text("Remove", Theme.Ash)) plugin.RemoveCustomAnimationCommand(assignment.Command);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static bool MatchesSearch(AnimationCommandTarget target, string value)
    {
        var clean = value.Trim();
        return clean.Length == 0 ||
               target.AnimationName.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
               target.ModName.Contains(clean, StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawHeading(string label) => Theme.Heading(label);
}

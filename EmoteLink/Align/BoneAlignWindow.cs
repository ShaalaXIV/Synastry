using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink.Align;

/// <summary>
/// The bone align panel: who to line up with, which parts should meet and how far apart, and what is
/// remembered for the playing animation. Its own small window rather than a menu, so it stays open
/// while you turn the camera to look.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
internal sealed class BoneAlignWindow : Window
{
    private const float Width = 300f;
    private const float LabelWidth = 72f;

    private static readonly (string Label, float Degrees)[] Turns = [("-90°", -90f), ("180°", 180f), ("+90°", 90f)];

    private readonly Plugin plugin;
    private Vector2? anchor;
    private Vector2 pivot;

    public BoneAlignWindow(Plugin plugin) : base("Bone align###SynastryBoneAlign")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoScrollbar;
    }

    /// <summary>Opens beside the button just drawn, on the side with more room; closes when open.</summary>
    public void ToggleAtLastItem()
    {
        if (IsOpen)
        {
            IsOpen = false;
            return;
        }
        var top = ImGui.GetItemRectMin();
        var above = top.Y > ImGui.GetMainViewport().Pos.Y + ImGui.GetMainViewport().Size.Y * 0.5f;
        anchor = above ? top : new Vector2(top.X, ImGui.GetItemRectMax().Y);
        pivot = above ? new Vector2(0f, 1f) : Vector2.Zero;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f, 14f) * Theme.Scale);
        if (anchor is not { } position) return;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always, pivot);
        anchor = null;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.Pop();
    }

    public override void Draw()
    {
        var s = Theme.Scale;
        var width = Width * s;
        ImGui.Dummy(new Vector2(width, 0f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);

        var problem = plugin.BoneAlignProblem;
        DrawKnown(width, problem.Length > 0);
        DrawPartner(width);
        DrawParts(width);
        DrawTurns(width);

        ImGui.Dummy(new Vector2(0f, 2f * s));
        DrawButtons(width, problem.Length > 0);

        if (problem.Length > 0) Theme.Wrapped(problem, Theme.Faint);
        else if (plugin.BoneAlignStatus.Length > 0) Theme.Wrapped(plugin.BoneAlignStatus, Theme.Ash);
        ImGui.PopTextWrapPos();
    }

    /// <summary>What is remembered for the playing animation, with Use it and Forget.</summary>
    private void DrawKnown(float width, bool blocked)
    {
        var s = Theme.Scale;
        if (plugin.KnownAlignment is { } known)
        {
            var start = ImGui.GetCursorScreenPos();
            Theme.Dot(ImGui.GetWindowDrawList(), start + new Vector2(4f * s, ImGui.GetFrameHeight() * 0.5f), Theme.Everyone, true);
            ImGui.SetCursorScreenPos(start + new Vector2(14f * s, 0f));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Everyone, "Known animation");
            Tooltip((known.Label.Length > 0 ? $"{known.ModName}, {known.Label}" : known.ModName) +
                    $"\nRemembered {known.Updated.ToLocalTime():d}.");

            const string use = "Use it";
            const string forget = "Forget";
            var buttons = ImGui.CalcTextSize(use).X + ImGui.CalcTextSize(forget).X + 26f * s;
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - buttons, start.Y));
            Disabled(blocked || plugin.IsBoneAligning, () =>
            {
                if (Theme.Text(use, Theme.AzureText)) plugin.UseKnownAlignment();
            });
            Tooltip("Line up the remembered way now.");
            ImGui.SameLine(0, 2f * s);
            if (Theme.Text(forget, Theme.Ash)) plugin.ForgetKnownAlignment();
            Tooltip("Stop lining up this animation until you align or remember it again.");

            Theme.Wrapped($"{BodyParts.DisplayName(known.Self)} to {BodyParts.Words(known.Partner)}, " +
                          $"{known.ContactOffset.ToVector3().Length():0.00}y apart, " +
                          BoneAlignService.FacingText(known.RelativeYaw), Theme.Soft);
        }
        if (plugin.AlignmentWaiting.Length > 0) Theme.Wrapped(plugin.AlignmentWaiting, Theme.Some);
        if (plugin.KnownAlignment is not null || plugin.AlignmentWaiting.Length > 0) ImGui.Dummy(new Vector2(0f, 2f * s));
    }

    private void DrawPartner(float width)
    {
        if (plugin.LocalCharacter is not { } local) return;
        var partners = plugin.BoneAlignPartners(local);
        var chosen = plugin.SelectedBoneAlignPartner(local);
        Row("With", width);
        Disabled(partners.Count == 0, () =>
        {
            if (!ImGui.BeginCombo("##bone-align-with", chosen is null ? "Nobody close" : plugin.BoneAlignName(chosen))) return;
            foreach (var partner in partners)
                if (ImGui.Selectable($"{plugin.BoneAlignName(partner)}##{partner.GameObjectId}", partner.Address == chosen?.Address))
                    plugin.PickBoneAlignPartner(partner);
            ImGui.EndCombo();
        });
        Tooltip("Your target, or someone in your room who is close to you.");
    }

    private void DrawParts(float width)
    {
        var self = plugin.BoneAlignSelf;
        var partner = plugin.BoneAlignPartner;
        Row("Self", width);
        if (PartCombo("##bone-align-self", ref self)) plugin.PickBoneAlignParts(self, partner);
        Tooltip("Your body part that should meet your partner's.");
        Row("Partner", width);
        if (PartCombo("##bone-align-partner", ref partner)) plugin.PickBoneAlignParts(self, partner);
        Tooltip("Your partner's body part it should meet.");

        var gap = plugin.BoneAlignGap;
        Row("Gap", width);
        if (ImGui.SliderFloat("##bone-align-gap", ref gap, 0f, 0.10f, "%.2fy")) plugin.SetBoneAlignGap(gap, false);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SetBoneAlignGap(gap, true);
        Tooltip("Room to leave between the two parts, so they meet instead of passing through each other.");

        var faceSameWay = plugin.BoneAlignFaceSameWay;
        if (Theme.Toggle("##bone-align-face", "Face partner's way", ref faceSameWay))
            plugin.SetBoneAlignFaceSameWay(faceSameWay);
        Tooltip("Align first turns you to face the way your partner does, since most couple animations are made\n" +
                "that way. Turn it off to keep your own facing.");
    }

    private void DrawTurns(float width)
    {
        Row("Turn", width);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var button = (width - LabelWidth * Theme.Scale - spacing * (Turns.Length - 1)) / Turns.Length;
        Disabled(!plugin.CanBoneAlignTurn, () =>
        {
            for (var i = 0; i < Turns.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                if (Theme.Outline(Turns[i].Label, button)) plugin.BoneAlignTurn(Turns[i].Degrees);
                Tooltip($"Turn around your {BodyParts.Words(plugin.BoneAlignSelf)}.");
            }
        });
    }

    private void DrawButtons(float width, bool blocked)
    {
        var aligning = plugin.IsBoneAligning;
        Disabled(blocked || aligning, () =>
        {
            if (Theme.Primary(aligning ? "Aligning…###bone-align-go" : "Align###bone-align-go", width)) plugin.BoneAlignNow();
        });
        Tooltip("Restarts both animations, watches both parts for a moment, then moves you so they meet.\n" +
                "If an animation from Synastry is playing, the result is remembered for it.");

        var rememberable = plugin.RememberablePlay is not null;
        Disabled(blocked || aligning || !rememberable, () =>
        {
            if (Theme.Outline(plugin.KnownAlignment is null ? "Remember###bone-align-remember" : "Update memory###bone-align-remember"))
                plugin.RememberAlignment();
        });
        Tooltip(rememberable
            ? "Remembers how you are placed right now for this animation, without moving you.\n" +
              "Use it after adjusting yourself in Simple Heels."
            : "Play an animation from Synastry to remember it.");
        ImGui.SameLine();
        Disabled(!plugin.CanBoneAlignReset, () =>
        {
            if (Theme.Text("Reset")) plugin.ResetBoneAlign();
        });
        Tooltip("Back to how you were before bone align. What is remembered stays.");
    }

    /// <summary>A label, with the next item lined up beside it and filling the rest of the row.</summary>
    private static void Row(string label, float width)
    {
        var left = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        Theme.Label(label);
        ImGui.SameLine(left + LabelWidth * Theme.Scale);
        ImGui.SetNextItemWidth(width - LabelWidth * Theme.Scale);
    }

    private static bool PartCombo(string id, ref BodyPart value)
    {
        var changed = false;
        if (!ImGui.BeginCombo(id, BodyParts.DisplayName(value))) return false;
        foreach (var part in BodyParts.All)
        {
            if (!ImGui.Selectable(BodyParts.DisplayName(part), part == value)) continue;
            value = part;
            changed = true;
        }
        ImGui.EndCombo();
        return changed;
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }

    private static void Disabled(bool disabled, Action draw)
    {
        if (disabled) ImGui.BeginDisabled();
        draw();
        if (disabled) ImGui.EndDisabled();
    }
}

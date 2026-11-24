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
    private readonly BoneAlignPanel panel;
    private Vector2? anchor;
    private Vector2 pivot;

    public BoneAlignWindow(Plugin plugin) : base("Bone align###SynastryBoneAlign")
    {
        panel = new BoneAlignPanel(plugin);
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
        var width = Width * Theme.Scale;
        ImGui.Dummy(new Vector2(width, 0f));
        panel.Draw(width);
    }
}

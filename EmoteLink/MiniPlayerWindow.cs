using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

/// <summary>
/// Synastry shrunk down: what's playing, the room at a glance, and the moment-to-moment buttons.
/// Everything else, prompts included, lives in the full window.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    private const float Width = 340f;
    private readonly Plugin plugin;

    public MiniPlayerWindow(Plugin plugin) : base("Synastry mini player###SynastryMiniPlayer")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize;
    }

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 10f) * Theme.Scale);
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
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        // Header: name on the left, expand and close on the right.
        Theme.Label("Synastry");
        var button = 24f * s;
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - button * 2 - 4f * s, start.Y - 3f * s));
        if (IconButton("##expand", button, DrawExpandIcon)) plugin.ShowFullWindow();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open the full window");
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - button, start.Y - 3f * s));
        if (IconButton("##close", button, DrawCloseIcon)) IsOpen = false;
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + button));

        // What's playing.
        if (plugin.NowPlaying is { } playing)
        {
            var lineStart = ImGui.GetCursorScreenPos();
            Theme.Dot(draw, lineStart + new Vector2(4f * s, ImGui.GetTextLineHeight() * 0.5f + 3f * s), Theme.Rose, true);
            ImGui.SetCursorScreenPos(lineStart + new Vector2(16f * s, 0));
            using (Theme.HeadingFont())
                ImGui.TextUnformatted(Theme.Truncate(playing.Animation.Length > 0 ? playing.Animation : playing.ModName,
                    width - 16f * s));
            ImGui.SetCursorScreenPos(new Vector2(lineStart.X + 16f * s, ImGui.GetCursorScreenPos().Y));
            if (playing.Animation.Length > 0) Theme.Label(Theme.Truncate(playing.ModName, width - 16f * s));
        }
        else
        {
            Theme.Quiet("Nothing playing");
        }

        // The room at a glance.
        if (plugin.Sync.Room is { } room)
        {
            var ready = room.Members.Count(member => member.Ready);
            ImGui.TextColored(Theme.AzureText, room.RoomCode);
            ImGui.SameLine(0, 8f * s);
            Theme.Label($"{ready} of {room.Members.Count} ready");
        }
        else
        {
            Theme.Quiet("Not in a room");
        }

        ImGui.Dummy(new Vector2(width, 2f * s));
        if (!plugin.Sync.IsInRoom) ImGui.BeginDisabled();
        if (Theme.Text("Emote sync")) plugin.SyncLobbyEmotes();
        if (!plugin.Sync.IsInRoom) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.Sync.IsInRoom
                ? "Restart everyone in the room's animation at the same moment."
                : "Join a room to sync emotes with it.");

        ImGui.SameLine(0, 2f * s);
        var heels = plugin.SimpleHeelsAvailable;
        if (!heels) ImGui.BeginDisabled();
        if (Theme.Text(plugin.IsLiningUp ? "Lining up…" : "Line up")) plugin.LineUpNow();
        if (!heels) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(heels ? "Line up a couple animation now." : "Line up needs Simple Heels.");

        ImGui.SameLine(0, 2f * s);
        if (Theme.Text(plugin.IsAligning ? "Cancel alignment" : "Align to target")) plugin.ToggleAlignment();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Walk onto your target's spot and facing.");
    }

    private static bool IconButton(string id, float size, Action<ImDrawListPtr, Vector2, uint> icon)
    {
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered) draw.AddRectFilled(start, start + new Vector2(size, size), ImGui.GetColorU32(Theme.RowHover), 3f * Theme.Scale);
        icon(draw, start + new Vector2(size, size) * 0.5f, ImGui.GetColorU32(hovered ? Theme.Bone : Theme.Soft));
        return clicked;
    }

    private static void DrawExpandIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var r = 5f * Theme.Scale;
        draw.AddRect(center - new Vector2(r, r), center + new Vector2(r, r), color, 1f * Theme.Scale, ImDrawFlags.None, 1.4f * Theme.Scale);
        draw.AddLine(center - new Vector2(r, r - 3f * Theme.Scale), center + new Vector2(r, -r + 3f * Theme.Scale), color, 1.4f * Theme.Scale);
    }

    private static void DrawCloseIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var r = 4.5f * Theme.Scale;
        draw.AddLine(center - new Vector2(r, r), center + new Vector2(r, r), color, 1.5f * Theme.Scale);
        draw.AddLine(center + new Vector2(-r, r), center + new Vector2(r, -r), color, 1.5f * Theme.Scale);
    }
}

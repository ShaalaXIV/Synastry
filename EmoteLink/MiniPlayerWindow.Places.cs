using Dalamud.Bindings.ImGui;
using EmoteLink.Places;

namespace EmoteLink;

/// <summary>The mini player's "Save place" button.</summary>
public sealed partial class MiniPlayerWindow
{
    partial void DrawPlacesButton()
    {
        ImGui.SameLine(0, 2f * Theme.Scale);
        var ready = plugin.CanSavePlace;
        if (!ready) ImGui.BeginDisabled();
        if (Theme.Text("Save place")) SavePlacePopup.Open(plugin);
        if (!ready) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ready
                ? "Save this animation and where you are, to play it here again."
                : "Play a looping animation with Synastry first.");
        SavePlacePopup.Draw(plugin);
    }
}

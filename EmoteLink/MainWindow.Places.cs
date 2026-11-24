using System.Numerics;
using Dalamud.Bindings.ImGui;
using EmoteLink.Places;

namespace EmoteLink;

/// <summary>Saved places in the full window: their sidebar row, their list and the "Save place" button.</summary>
public sealed partial class MainWindow
{
    private static readonly (Vector4 Color, string Label)[] PlacesLegend =
    [
        (Theme.Everyone, "In reach"),
        (Theme.Some, "Too far"),
        (Theme.Dormant, "Elsewhere"),
    ];

    private string placeRenameId = "";
    private string placeRename = "";

    partial void DrawPlacesSidebarRow()
    {
        var s = Theme.Scale;
        var lineStart = ImGui.GetCursorScreenPos();
        Theme.HorizontalLine(ImGui.GetWindowDrawList(), lineStart + new Vector2(10f * s, 6f * s),
            ImGui.GetContentRegionAvail().X - 20f * s);
        ImGui.Dummy(new Vector2(0, 12f * s));
        if (FolderRow("places", "Saved places", plugin.SavedPlaces.Count,
                libraryScope == LibraryScope.Places && search.Length == 0, 0, null, out _))
            SelectScope(LibraryScope.Places, null);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Animations saved with where you were when they played.");
    }

    partial void DrawPlacesFooterButton()
    {
        ImGui.SameLine(0, 2f * Theme.Scale);
        var ready = plugin.CanSavePlace;
        Disabled(!ready, () =>
        {
            if (Theme.Text("Save place")) SavePlacePopup.Open(plugin);
        });
        Tooltip(ready
            ? "Save this animation and where you are, to play it here again."
            : "Play a looping animation with Synastry first.");
        SavePlacePopup.Draw(plugin);
    }

    partial void DrawPlacesScope(ref bool drawn)
    {
        drawn = true;
        var s = Theme.Scale;
        var saved = plugin.SavedPlaces;
        var headerStart = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorScreenPos(headerStart + new Vector2(24f * s, 14f * s));
        Theme.Heading("Saved places");
        ImGui.SameLine(0, 10f * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, headerStart.Y + 18f * s));
        Theme.Number(saved.Count.ToString("N0"));
        if (saved.Count > 0 && Theme.LegendWidth(PlacesLegend) < width - 22f * s - ImGui.CalcTextSize("Saved places").X - 80f * s)
            Theme.Legend(ImGui.GetWindowDrawList(),
                new Vector2(headerStart.X + width - 22f * s - Theme.LegendWidth(PlacesLegend), headerStart.Y + 18f * s), PlacesLegend);
        ImGui.SetCursorScreenPos(new Vector2(headerStart.X, headerStart.Y + 50f * s));
        ImGui.Dummy(Vector2.Zero);

        var status = CurrentStatus();
        var statusHeight = status.Length > 0 ? 30f * s : 0f;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 0f) * s);
        ImGui.BeginChild("place-rows", new Vector2(0, ImGui.GetContentRegionAvail().Y - statusHeight),
            false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.None);
        ImGui.PopStyleVar();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 2f) * s);
        if (!plugin.PlacesCanMoveYou) PlacesNote(PlaceService.NeedsSimpleHeels);
        if (saved.Count == 0)
            PlacesNote("No saved places yet. Play an animation, get where you want to be, then choose Save place below.");
        foreach (var group in saved.GroupBy(place => place.ModName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ImGui.Dummy(new Vector2(0, 4f * s));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12f * s);
            Theme.Label(group.Key);
            foreach (var place in group.OrderBy(place => place.Name, StringComparer.OrdinalIgnoreCase)) DrawPlaceRow(place);
        }
        ImGui.PopStyleVar();
        ImGui.Dummy(new Vector2(0, 12f * s));
        ImGui.EndChild();

        if (status.Length == 0) return;
        var statusStart = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        Theme.HorizontalLine(draw, statusStart, width);
        draw.AddText(statusStart + new Vector2(24f * s, (statusHeight - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(Theme.Ash), Theme.Truncate(status, width - 48f * s));
        if (ImGui.IsMouseHoveringRect(statusStart, statusStart + new Vector2(width, statusHeight)))
            ImGui.SetTooltip(status);
        ImGui.Dummy(new Vector2(width, statusHeight));
    }

    private static void PlacesNote(string text)
    {
        var s = Theme.Scale;
        ImGui.Dummy(new Vector2(0, 4f * s));
        ImGui.Indent(12f * s);
        Theme.Wrapped(text, Theme.Faint);
        ImGui.Unindent(12f * s);
    }

    private void DrawPlaceRow(SavedPlace place)
    {
        var s = Theme.Scale;
        ImGui.PushID(place.Id);
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = RowHeight * s;
        var playable = plugin.IsPlacePlayable(place);
        var reach = plugin.ReachOf(place);
        var zone = plugin.ZoneName(place.TerritoryType);
        var inRoom = plugin.Sync.IsInRoom;
        var playLabel = inRoom ? "Ready" : "Play";
        var playWidth = ImGui.CalcTextSize(playLabel).X + 20f * s;
        var soloWidth = inRoom ? ImGui.CalcTextSize("Solo").X + 12f * s + 4f * s : 0f;
        var buttons = playWidth + soloWidth + 18f * s;

        // The row stops short of its buttons, so it can't take their clicks.
        ImGui.InvisibleButton("##place", new Vector2(width - buttons, height));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            var missing = playable ? PlaceDescriptions.Reach(place, reach) : "This animation isn't in your library any more.";
            ImGui.SetTooltip($"{place.Animation}\n{PlaceDescriptions.Detail(place, zone)}\n{missing}\nRight-click to rename, update or delete.");
        }
        DrawPlaceMenu(place);

        Vector4? color = reach switch
        {
            PlaceReach.Here => Theme.Everyone,
            PlaceReach.TooFar => Theme.Some,
            _ => null
        };
        if (color is { } wash)
        {
            draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(Theme.WithAlpha(wash, 0.07f)), 3f * s);
            draw.AddRectFilled(start, start + new Vector2(2f * s, height), ImGui.GetColorU32(wash), 1f * s);
        }
        else if (hovered)
        {
            draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(Theme.RowHover), 3f * s);
        }
        Theme.Dot(draw, new Vector2(start.X + 16f * s, start.Y + height * 0.5f), color ?? Theme.Dormant, true);
        var textY = start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;
        var textRight = start.X + width - buttons;
        var line = Theme.Truncate(PlaceDescriptions.Line(place, zone), (textRight - start.X - 36f * s) * 0.5f);
        var lineWidth = ImGui.CalcTextSize(line).X;
        draw.AddText(new Vector2(start.X + 36f * s, textY), ImGui.GetColorU32(color ?? Theme.Ash),
            Theme.Truncate(place.Name, textRight - start.X - 36f * s - lineWidth - 24f * s));
        draw.AddText(new Vector2(textRight - lineWidth - 4f * s, textY), ImGui.GetColorU32(Theme.Faint), line);

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 3f) * s);
        ImGui.SetCursorScreenPos(new Vector2(textRight + 8f * s, start.Y + (height - ImGui.GetFrameHeight()) * 0.5f));
        Disabled(!playable, () =>
        {
            if (Theme.Outline(playLabel, playWidth)) PlayPlace(place, false);
        });
        ImGui.PopStyleVar();
        Tooltip(inRoom ? "Get ready with this animation and land here when the room starts." : "Play this animation and land here.");
        if (inRoom)
        {
            ImGui.SetCursorScreenPos(new Vector2(textRight + 8f * s + playWidth + 4f * s,
                start.Y + (height - ImGui.GetTextLineHeight() - 8f * s) * 0.5f));
            Disabled(!playable, () =>
            {
                if (Theme.Text("Solo", Theme.Ash)) PlayPlace(place, true);
            });
            Tooltip("Play this animation and land here just for you, without the room.");
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(Vector2.Zero);
        ImGui.PopID();
    }

    private void PlayPlace(SavedPlace place, bool solo)
    {
        plugin.PlayPlace(place, solo);
        // The role chip in the library shows you as ready with it too.
        var split = place.Trigger.IndexOf(':');
        if (!solo && split > 0)
            RememberReady(place.ModDirectory, $"$detected-{place.Trigger[..split]}", place.Trigger[(split + 1)..]);
    }

    private void DrawPlaceMenu(SavedPlace place)
    {
        var s = Theme.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * s);
        var open = ImGui.BeginPopupContextItem("placeMenu");
        ImGui.PopStyleVar();
        if (!open) return;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f) * s);
        if (ImGui.IsWindowAppearing() || placeRenameId != place.Id)
        {
            placeRenameId = place.Id;
            placeRename = place.Name;
        }
        Theme.Label("Rename");
        ImGui.SetNextItemWidth(220f * s);
        var submit = ImGui.InputText("##placeRename", ref placeRename, 96, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((Theme.Outline("Save##place-rename") || submit) && !string.IsNullOrWhiteSpace(placeRename))
        {
            plugin.RenamePlace(place, placeRename);
            ImGui.CloseCurrentPopup();
        }
        ImGui.Separator();
        var update = plugin.CanUpdatePlace(place);
        Disabled(!update, () =>
        {
            if (ImGui.MenuItem("Update to here")) plugin.UpdatePlace(place);
        });
        Tooltip(update
            ? "Keep the place, but land where you are now."
            : "Play this animation first, then move to where you want to land.");
        if (ImGui.MenuItem("Delete")) plugin.DeletePlace(place);
        ImGui.PopStyleVar();
        ImGui.EndPopup();
    }
}

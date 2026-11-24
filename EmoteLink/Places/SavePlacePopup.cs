using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace EmoteLink.Places;

/// <summary>
/// The "Save place" popup, shared by the full window and the mini player: a name, and what the place
/// is held to. Open and Draw have to be called from the same window.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal static class SavePlacePopup
{
    private const string Id = "Save place###SynastrySavePlace";
    private const float Width = 320f;
    private const long ScanIntervalMs = 500;

    private static string name = "";
    private static string suggested = "";
    private static PlaceKind kind;
    private static IReadOnlyList<NearbyFurniture> furnitureNearby = [];
    private static IReadOnlyList<PartnerChoice> partnersNearby = [];
    private static NearbyFurniture? furniture;
    private static PartnerChoice? partner;
    private static long scannedAt;

    public static void Open(Plugin plugin)
    {
        name = suggested = "";
        kind = PlaceKind.Spot;
        furniture = null;
        partner = null;
        Scan(plugin);
        ImGui.OpenPopup(Id);
    }

    public static void Draw(Plugin plugin)
    {
        var s = Theme.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f, 16f) * s);
        var open = ImGui.BeginPopup(Id, ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.PopStyleVar();
        if (!open) return;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f) * s);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Width * s);
        if (Environment.TickCount64 - scannedAt >= ScanIntervalMs) Scan(plugin);

        Theme.Heading("Save place");
        DrawName(plugin);
        ImGui.Dummy(new Vector2(0, 2f * s));
        KindChip("This spot", PlaceKind.Spot);
        ImGui.SameLine();
        KindChip("Furniture", PlaceKind.Furniture);
        ImGui.SameLine();
        KindChip("Partner", PlaceKind.Partner);
        DrawChoice(plugin);
        if (!plugin.PlacesCanMoveYou) Theme.Quiet(PlaceService.NeedsSimpleHeels);

        ImGui.Dummy(new Vector2(0, 4f * s));
        var ready = plugin.CanSavePlace && name.Trim().Length > 0 && kind switch
        {
            PlaceKind.Furniture => furniture is not null,
            PlaceKind.Partner => partner is not null,
            _ => true
        };
        if (!ready) ImGui.BeginDisabled();
        if (Theme.Primary("Save") && plugin.SavePlace(name, kind, furniture, partner)) ImGui.CloseCurrentPopup();
        if (!ready) ImGui.EndDisabled();
        ImGui.SameLine();
        if (Theme.Text("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.PopTextWrapPos();
        ImGui.PopStyleVar();
        ImGui.EndPopup();
    }

    // The name follows what's picked until the player types their own.
    private static void DrawName(Plugin plugin)
    {
        var next = PlaceDescriptions.SuggestedName(plugin.PlayingForPlace, kind, plugin.CurrentZoneName,
            furniture?.Name, partner?.Name);
        if (plugin.CanSavePlace && name == suggested) name = suggested = next;
        ImGui.SetNextItemWidth(Width * Theme.Scale);
        ImGui.InputTextWithHint("##place-name", "Name", ref name, 96);
    }

    private static void KindChip(string label, PlaceKind value)
    {
        if (Theme.Chip(label, kind == value ? ChipState.Mine : ChipState.Open)) kind = value;
    }

    private static void DrawChoice(Plugin plugin)
    {
        switch (kind)
        {
            case PlaceKind.Furniture when furnitureNearby.Count == 0:
                Theme.Quiet("There's no furniture within 5 yalms.");
                break;
            case PlaceKind.Furniture:
                ImGui.SetNextItemWidth(Width * Theme.Scale);
                if (ImGui.BeginCombo("##place-furniture", furniture is { } chosen ? Label(plugin, chosen) : "Choose a piece of furniture"))
                {
                    for (var i = 0; i < furnitureNearby.Count; i++)
                    {
                        ImGui.PushID(i);
                        if (ImGui.Selectable(Label(plugin, furnitureNearby[i]), IsChosen(furnitureNearby[i])))
                            furniture = furnitureNearby[i];
                        ImGui.PopID();
                    }
                    ImGui.EndCombo();
                }
                Theme.Quiet("Works with any copy of it, wherever it's placed.");
                break;
            case PlaceKind.Partner when partnersNearby.Count == 0:
                Theme.Quiet("There's nobody within 15 yalms.");
                break;
            case PlaceKind.Partner:
                ImGui.SetNextItemWidth(Width * Theme.Scale);
                if (ImGui.BeginCombo("##place-partner", partner?.Name ?? "Choose who you're with"))
                {
                    for (var i = 0; i < partnersNearby.Count; i++)
                    {
                        ImGui.PushID(i);
                        var choice = partnersNearby[i];
                        if (ImGui.Selectable($"{choice.Name}  {choice.Distance:F1} y", Same(choice, partner))) partner = choice;
                        ImGui.PopID();
                    }
                    ImGui.EndCombo();
                }
                Theme.Quiet("Works wherever you both are. They stay put; only you move.");
                break;
            default:
                Theme.Quiet($"Where you are now in {plugin.CurrentZoneName}.");
                break;
        }
    }

    // The choice itself is kept, not its place in the list, which changes as people move.
    private static void Scan(Plugin plugin)
    {
        scannedAt = Environment.TickCount64;
        furnitureNearby = plugin.FurnitureForPlace();
        partnersNearby = plugin.PartnersForPlace();
        if (furniture is null || !furnitureNearby.Any(IsChosen))
            furniture = furnitureNearby.Count > 0 ? furnitureNearby[0] : null;
        if (partner is null || !partnersNearby.Any(choice => Same(choice, partner)))
            partner = plugin.SuggestedPartner(partnersNearby);
    }

    private static bool IsChosen(NearbyFurniture item) =>
        furniture is { } chosen && chosen.EntryId == item.EntryId && Vector3.Distance(chosen.Position, item.Position) < 0.01f;

    private static bool Same(PartnerChoice choice, PartnerChoice? other) =>
        other is not null && choice.Name == other.Name && choice.WorldId == other.WorldId;

    private static string Label(Plugin plugin, NearbyFurniture item) => $"{item.Name}  {plugin.DistanceTo(item.Position):F1} y";
}

using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SheetHousingFurniture = Lumina.Excel.Sheets.HousingFurniture;
using SheetHousingYardObject = Lumina.Excel.Sheets.HousingYardObject;

namespace EmoteLink.Places;

/// <summary>A placed piece of furniture. EntryId is its sheet row.</summary>
public readonly record struct NearbyFurniture(uint EntryId, string Name, Vector3 Position, float Rotation);

/// <summary>
/// Finds placed housing furniture. It isn't in Dalamud's object table; it lives in the housing
/// furniture manager's own object array. Reads only. Framework thread only.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal sealed unsafe class FurnitureScanner(IDataManager data)
{
    private readonly Dictionary<uint, string> names = [];

    /// <summary>The house or room you're inside, or 0 when you aren't in one.</summary>
    public static ulong CurrentHouseId()
    {
        var housing = HousingManager.Instance();
        if (housing is null || housing->IndoorTerritory is null) return 0;
        var id = housing->GetCurrentIndoorHouseId().Id;
        return id == ulong.MaxValue ? 0 : id;
    }

    /// <summary>Furniture around a point, nearest first.</summary>
    public List<NearbyFurniture> Scan(Vector3 around, float radius)
    {
        var found = new List<NearbyFurniture>();
        var housing = HousingManager.Instance();
        if (housing is null) return found;
        if (housing->IndoorTerritory is not null)
            Collect(&housing->IndoorTerritory->FurnitureManager, around, radius, found);
        else if (housing->OutdoorTerritory is not null)
            Collect(&housing->OutdoorTerritory->FurnitureManager, around, radius, found);
        found.Sort((left, right) => Vector3.DistanceSquared(left.Position, around)
            .CompareTo(Vector3.DistanceSquared(right.Position, around)));
        return found;
    }

    private void Collect(HousingFurnitureManager* manager, Vector3 around, float radius, List<NearbyFurniture> found)
    {
        var array = &manager->ObjectManager.ObjectArray;
        var objects = array->Objects;
        for (var i = 0; i < array->ObjectCount && i < objects.Length; i++)
        {
            var item = (HousingEventObject*)objects[i].Value;
            if (item is null) continue;
            Vector3 position = item->Position;
            if (Vector3.Distance(position, around) > radius) continue;
            // A sheet row is the type above the entry.
            var type = item->HousingObjectId.Type;
            var row = ((uint)type << 16) | item->HousingObjectId.EntryId;
            var name = NameOf(row, type);
            if (name.Length > 0) found.Add(new NearbyFurniture(row, name, position, item->Rotation));
        }
    }

    private string NameOf(uint row, HousingObjectType type)
    {
        if (names.TryGetValue(row, out var known)) return known;
        var item = type switch
        {
            HousingObjectType.Furniture => data.GetExcelSheet<SheetHousingFurniture>().GetRowOrDefault(row)?.Item,
            HousingObjectType.YardObject => data.GetExcelSheet<SheetHousingYardObject>().GetRowOrDefault(row)?.Item,
            _ => null
        };
        return names[row] = item?.ValueNullable?.Name.ExtractText() ?? "";
    }
}

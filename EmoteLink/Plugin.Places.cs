using Dalamud.Game.ClientState.Objects.SubKinds;
using EmoteLink.Places;

namespace EmoteLink;

/// <summary>
/// Saved places: keeping where a Synastry animation landed, and playing it there again. Places are
/// kept per character in the settings file and never shared with anyone.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
public sealed unsafe partial class Plugin
{
    private const float FurniturePickRadius = 5f;
    private const long PlaceReachIntervalMs = 500;

    private FurnitureScanner? furnitureScanner;
    private PlaceService? places;
    private readonly Dictionary<string, PlaceReach> placeReach = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> zoneNames = [];
    private long placeReachAt;

    partial void InitializePlaces()
    {
        configuration.SavedPlaces ??= [];
        furnitureScanner = new FurnitureScanner(DataManager);
        places = new PlaceService(Objects, heels, furnitureScanner, () => ClientState.TerritoryType,
            () => cyclingPose is not null, () => IsBoneAligning, message => Status = message);
    }

    partial void TickPlaces()
    {
        places?.Tick();
        UpdatePlaceFlags();
    }

    partial void OnPlayStarted()
    {
        places?.Started();
        UpdatePlaceFlags();
    }

    partial void OnPlayCleared()
    {
        places?.Forget();
        UpdatePlaceFlags();
    }

    partial void DisposePlaces() => places?.Forget();

    private void UpdatePlaceFlags()
    {
        IsPlacing = places?.IsPlacing == true;
        HasPartnerPlace = places?.HoldsPartner == true;
    }

    // ---- The list -------------------------------------------------------------------------------

    /// <summary>This character's places.</summary>
    internal IReadOnlyList<SavedPlace> SavedPlaces =>
        PlacesKey() is { } key && configuration.SavedPlaces.TryGetValue(key, out var saved) ? saved : [];

    /// <summary>Simple Heels is loaded, so a place can move where you're drawn.</summary>
    internal bool PlacesCanMoveYou => heels.Available;

    internal bool IsPlacePlayable(SavedPlace place) => modsByDirectory.ContainsKey(place.ModDirectory);

    /// <summary>Whether a place can be reached from here, checked twice a second while something asks.</summary>
    internal PlaceReach ReachOf(SavedPlace place)
    {
        var now = Environment.TickCount64;
        if (now - placeReachAt >= PlaceReachIntervalMs)
        {
            placeReachAt = now;
            placeReach.Clear();
            var saved = SavedPlaces;
            var placed = saved.Any(candidate => candidate.Kind == PlaceKind.Furniture) &&
                         Objects.LocalPlayer is { } local && furnitureScanner is not null
                ? furnitureScanner.Scan(local.Position, float.MaxValue)
                : [];
            foreach (var candidate in saved)
                placeReach[candidate.Id] = places is not null && IsPlacePlayable(candidate)
                    ? places.ReachOf(candidate, placed)
                    : PlaceReach.Elsewhere;
        }
        return placeReach.GetValueOrDefault(place.Id, PlaceReach.Elsewhere);
    }

    internal string CurrentZoneName => ZoneName(ClientState.TerritoryType);

    internal string ZoneName(uint territory)
    {
        if (zoneNames.TryGetValue(territory, out var known)) return known;
        var name = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText();
        return zoneNames[territory] = string.IsNullOrWhiteSpace(name) ? "an unknown place" : name;
    }

    private static string? PlacesKey() =>
        Objects.LocalPlayer is { } player ? $"{player.Name.TextValue}@{player.HomeWorld.RowId}" : null;

    // ---- Saving ---------------------------------------------------------------------------------

    /// <summary>A Synastry animation is looping and nothing is still moving you.</summary>
    internal bool CanSavePlace =>
        CurrentPlay is { Trigger.Length: > 0 } && !IsPlacing && cyclingPose is null && IsSynastryAnimationPlaying();

    /// <summary>The place is for the animation you're playing, so it can be updated to where you are.</summary>
    internal bool CanUpdatePlace(SavedPlace place) =>
        CanSavePlace && CurrentPlay is { } play &&
        play.Directory.Equals(place.ModDirectory, StringComparison.OrdinalIgnoreCase) &&
        play.Trigger.Equals(place.Trigger, StringComparison.OrdinalIgnoreCase);

    /// <summary>What you're playing, as a place would be named after it.</summary>
    internal string PlayingForPlace =>
        CurrentPlay is { } play ? play.Animation.Length > 0 ? play.Animation : play.ModName : "";

    /// <summary>Furniture close enough to hold a place to, nearest first.</summary>
    internal IReadOnlyList<NearbyFurniture> FurnitureForPlace() =>
        Objects.LocalPlayer is { } local && furnitureScanner is not null
            ? furnitureScanner.Scan(local.Position, FurniturePickRadius)
            : [];

    internal float DistanceTo(System.Numerics.Vector3 position) =>
        Objects.LocalPlayer is { } local ? System.Numerics.Vector3.Distance(local.Position, position) : 0f;

    /// <summary>Players close enough to hold a place to, nearest first.</summary>
    internal IReadOnlyList<PartnerChoice> PartnersForPlace()
    {
        if (Objects.LocalPlayer is not { } local) return [];
        return Objects.OfType<IPlayerCharacter>()
            .Where(player => player.Address != local.Address)
            .Select(player => new PartnerChoice(player.Name.TextValue, player.HomeWorld.RowId,
                System.Numerics.Vector3.Distance(player.Position, local.Position), IsRoomMemberNamed(player.Name.TextValue)))
            .Where(choice => choice.Distance <= PlaceService.MaximumReach)
            .OrderBy(choice => choice.Distance)
            .ToList();
    }

    /// <summary>Your target, else the nearest room member. A stranger is never picked for you.</summary>
    internal PartnerChoice? SuggestedPartner(IReadOnlyList<PartnerChoice> choices)
    {
        var target = Targets.Target as IPlayerCharacter;
        return choices.FirstOrDefault(choice => target is not null && choice.Name == target.Name.TextValue &&
                                                choice.WorldId == target.HomeWorld.RowId) ??
               choices.FirstOrDefault(choice => choice.InRoom);
    }

    internal bool SavePlace(string name, PlaceKind kind, NearbyFurniture? furniture, PartnerChoice? partner)
    {
        if (!CanSavePlace || CurrentPlay is not { } play || places is null || PlacesKey() is not { } key) return false;
        var place = new SavedPlace
        {
            Name = name.Trim(),
            ModDirectory = play.Directory,
            ModName = play.ModName,
            Animation = PlayingForPlace,
            Trigger = play.Trigger,
            Selections = play.Selections.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase)
        };
        var character = partner is null ? null : PartnerTracker.Find(Objects, partner.Name, partner.WorldId);
        if (!places.Capture(place, kind, furniture, character))
        {
            Status = "That place couldn't be saved. Try again in a moment.";
            return false;
        }
        if (!configuration.SavedPlaces.TryGetValue(key, out var saved)) configuration.SavedPlaces[key] = saved = [];
        saved.Add(place);
        KeepPlaces();
        Status = $"Saved {place.Name}.";
        return true;
    }

    internal void RenamePlace(SavedPlace place, string name)
    {
        place.Name = name.Trim();
        KeepPlaces();
    }

    /// <summary>Saves where you are now over the place, still held to the same kind of thing.</summary>
    internal void UpdatePlace(SavedPlace place)
    {
        if (!CanUpdatePlace(place) || places is null || Objects.LocalPlayer is not { } local) return;
        NearbyFurniture? furniture = null;
        IPlayerCharacter? partner = null;
        switch (place.Kind)
        {
            case PlaceKind.Furniture:
                foreach (var item in furnitureScanner?.Scan(local.Position, PlaceService.MaximumReach) ?? [])
                {
                    if (item.EntryId != place.FurnitureId) continue;
                    furniture = item;
                    break;
                }
                break;
            case PlaceKind.Partner:
                partner = PartnerTracker.Find(Objects, place.PartnerName, place.PartnerWorldId);
                break;
        }
        if (!places.Capture(place, place.Kind, furniture, partner))
        {
            Status = PlaceDescriptions.Reach(place, PlaceReach.Elsewhere);
            return;
        }
        KeepPlaces();
        Status = $"Updated {place.Name}.";
    }

    internal void DeletePlace(SavedPlace place)
    {
        if (PlacesKey() is not { } key || !configuration.SavedPlaces.TryGetValue(key, out var saved) ||
            !saved.Remove(place)) return;
        KeepPlaces();
        Status = $"Deleted {place.Name}.";
    }

    private void KeepPlaces()
    {
        configuration.Save(PluginInterface);
        placeReachAt = 0;
    }

    // ---- Playing --------------------------------------------------------------------------------

    /// <summary>Plays a place's animation as usual (readying it in a room) and puts you there once it loops.</summary>
    internal void PlayPlace(SavedPlace place, bool solo)
    {
        if (places is null || !modsByDirectory.TryGetValue(place.ModDirectory, out var mod))
        {
            Status = $"{place.ModName} isn't in your library any more.";
            return;
        }
        if (!TryResolveFreeUseTrigger(mod.Directory, place.Trigger, out var pose, out var command, out _))
        {
            Status = $"{mod.Name} doesn't have {place.Animation} any more.";
            return;
        }
        // The options it was saved with, for this play only; anything the mod added since keeps your choice.
        var selections = GetActivationSelections(mod.Directory).ToDictionary(
            pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (group, options) in place.Selections) selections[group] = options.ToList();

        if (solo) CancelGroupReadinessForSolo();
        else PublishDetectedTriggerSelection(mod.Directory, place.Trigger);
        if (!ActivateInternal(mod.Directory, mod.Name, pose, !solo, command, selections)) return;
        places.Begin(place);   // after activating, which lets go of any earlier place
        UpdatePlaceFlags();
        if (!solo) OfferFreeUsePrompts(mod.Directory, mod.Name, place.Trigger);
    }
}

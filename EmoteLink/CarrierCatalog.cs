namespace EmoteLink;

internal enum CarrierFamily
{
    None,
    LoopingDance,
    StandingLoop,
    PropLoop,
    Dote,
    OneShot,
}

/// <summary>
/// Which unlocked emotes may carry a locked emote's animation, and in what order.
///
/// Order is quality first, then how easy the carrier is to get. A carrier whose vanilla intro
/// (slot 1) has its own PAP would play that intro before a mod that has none, so those come
/// after the intro-less ones. Ownership shares are FFXIV Collect's figures (October 2026) and stand
/// in for how hard an emote is to obtain; default emotes are owned by everyone.
/// </summary>
internal static class CarrierCatalog
{
    internal sealed record Carrier(string Command, string Name, float OwnedPercent, string HowToGet);

    private static readonly Carrier[] Dances =
    [
        new("/mandervilledance", "Manderville Dance", 90.7f, "Hildibrand quest \"The Hammer\""),
        new("/harvestdance", "Harvest Dance", 89.6f, "quest \"Saw That One Coming\""),
        new("/balldance", "Ball Dance", 89.3f, "quest \"Help Me, Lord of the Dance\""),
        new("/stepdance", "Step Dance", 88.2f, "quest \"Good for What Ales You\""),
        new("/mandervillemambo", "Manderville Mambo", 80.1f, "Hildibrand quest \"Don't Do the Dewprism\""),
        new("/beesknees", "Bee's Knees", 78.5f, "Gold Saucer, 80,000 MGP"),
        new("/golddance", "Gold Dance", 73.4f, "Gold Saucer, 80,000 MGP"),
        new("/thavdance", "Thavnairian Dance", 72.9f, "Gold Saucer, 80,000 MGP"),
        new("/yoldance", "Yol Dance", 67.5f, "Namazu allied society"),
        new("/mogdance", "Moogle Dance", 67.0f, "quest \"Piecing Together the Past\""),
        new("/sundance", "Sundrop Dance", 65.4f, "quest \"Sundrop the Beat\""),
        new("/lalihop", "Lali Hop", 61.1f, "Dwarf allied society"),
        new("/lophop", "Lop Hop", 58.1f, "quest \"A Dream Worth Chasing\""),
        new("/moonlift", "Moonlift Dance", 57.0f, "quest \"Eternity, Loyalty, Honesty\""),
        new("/flamedance", "Flame Dance", 50.8f, "Moonfire Faire or the Online Store"),
        new("/bombdance", "Bomb Dance", 37.8f, "Moonfire Faire or the Online Store"),
        new("/easterndance", "Eastern Dance", 26.5f, "Online Store"),
        new("/sidestep", "Side Step", 23.6f, "Online Store"),
        new("/getfantasy", "Get Fantasy", 23.3f, "Online Store"),
        new("/popotostep", "Popoto Step", 22.1f, "Online Store"),
        new("/boxstep", "Box Step", 20.2f, "Online Store"),
        new("/goobbuedo", "Gobbue Do", 18.0f, "Online Store"),
        new("/heeltoe", "Heel Toe", 17.1f, "Online Store"),
    ];

    // Ground loops (push-ups, sit-ups, play dead, slump) are left out: they change the character's stance.
    private static readonly Carrier[] StandingLoops =
    [
        new("/box", "Box", 92.1f, "quest \"Arenvald's Adventure\""),
        new("/hum", "Hum", 85.9f, "quest \"The Fire-bird Down Below\""),
        new("/attention", "Attention", 85.2f, "Grand Company, 40,000 seals"),
        new("/atease", "At Ease", 84.6f, "Grand Company, 40,000 seals"),
        new("/wringhands", "Wring Hands", 77.9f, "Bozja or Occult Crescent coffers"),
        new("/lean", "Lean", 77.9f, "Skybuilders' scrips"),
        new("/malevolence", "Malevolence", 73.8f, "Bozja or Occult Crescent"),
        new("/scheme", "Scheme", 73.7f, "Eureka or Occult Crescent"),
        new("/shiver", "Shiver", 69.2f, "Eureka or Occult Crescent"),
        new("/confirm", "Confirm", 69.2f, "treasure hunt dungeons"),
        new("/sweat", "Sweat", 67.5f, "deep dungeon tokens"),
        new("/guard", "Guard", 67.2f, "Bozja or Occult Crescent"),
        new("/reprimand", "Reprimand", 65.7f, "PvP, 15,000 Wolf Marks"),
        new("/squats", "Squats", 65.2f, "achievement \"Dear Leader I\""),
        new("/breathcontrol", "Breath Control", 65.2f, "achievement \"Dear Leader I\""),
        new("/spirit", "Spirit", 61.6f, "PvP rewards"),
        new("/rage", "Rage", 56.2f, "Cosmic Exploration"),
        new("/attend", "Attend", 52.6f, "PvP Series 7"),
        new("/winded", "Winded", 37.7f, "hunt mark logs"),
    ];

    private static readonly Carrier[] PropLoops =
    [
        new("/water", "Water", 86.1f, "Old Gridania, 2 Achievement Certificates"),
        new("/tomescroll", "Tomescroll", 86.0f, "registering the Companion app"),
        new("/sweep", "Sweep", 73.1f, "Skybuilders' scrips"),
        new("/study", "Study", 71.0f, "Skybuilders' scrips"),
        new("/shakedrink", "Shake Drink", 65.9f, "Gold Saucer, 50,000 MGP"),
    ];

    private static readonly Carrier[] DoteCarriers =
    [
        new("/blowkiss", "Blow Kiss", 100f, "available from the start"),
    ];

    private static readonly Carrier[] OneShots =
    [
        new("/wave", "Wave", 100f, "available from the start"),
        new("/clap", "Clap", 100f, "available from the start"),
        new("/cheer", "Cheer", 100f, "available from the start"),
        new("/bow", "Bow", 100f, "available from the start"),
    ];

    private static readonly Dictionary<string, Carrier> ByCommand =
        Dances.Concat(StandingLoops).Concat(PropLoops).Concat(DoteCarriers).Concat(OneShots)
            .ToDictionary(carrier => carrier.Command, StringComparer.OrdinalIgnoreCase);

    /// <summary>The carriers a family may use, before ranking. A source outside every curated family
    /// (or a locked vanilla emote) gets the generic list: every loop carrier, or every one-shot.</summary>
    public static IReadOnlyList<Carrier> For(CarrierFamily family, bool sourceLoop, bool generic)
    {
        var own = family switch
        {
            CarrierFamily.LoopingDance => Dances,
            CarrierFamily.StandingLoop => StandingLoops,
            CarrierFamily.PropLoop => PropLoops,
            CarrierFamily.Dote => DoteCarriers,
            CarrierFamily.OneShot => OneShots,
            _ => [],
        };
        if (!generic) return own;
        var rest = sourceLoop ? StandingLoops.Concat(Dances).Concat(PropLoops) : OneShots.AsEnumerable();
        return own.Concat(rest).DistinctBy(carrier => carrier.Command).ToList();
    }

    public static Carrier? Find(string command) => ByCommand.GetValueOrDefault(command);

    /// <summary>What to unlock when nothing fits: the three most commonly owned carriers for the family.</summary>
    public static string MissingMessage(CarrierFamily family, bool sourceLoop)
    {
        var options = For(family, sourceLoop, family == CarrierFamily.None)
            .OrderByDescending(carrier => carrier.OwnedPercent)
            .Take(3)
            .Select(carrier => $"{carrier.Name} ({carrier.HowToGet})")
            .ToList();
        var kind = family switch
        {
            CarrierFamily.LoopingDance => "a dance emote",
            CarrierFamily.StandingLoop => "a standing loop emote",
            CarrierFamily.PropLoop => "a looping prop emote",
            CarrierFamily.Dote => "Blow Kiss",
            CarrierFamily.OneShot => "a starter emote",
            _ => sourceLoop ? "a looping emote" : "a starter emote",
        };
        return options.Count == 0
            ? $"This animation needs {kind} you own to play through."
            : $"This animation needs {kind} you own to play through. The easiest to get: {JoinOr(options)}.";
    }

    private static string JoinOr(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, or {items[^1]}",
    };
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace EmoteLink.Ik;

/// <summary>
/// One role's contact map, measured from the animation itself (sex-animation-corpus) or edited by a
/// moderator: which partner roles it expects, which partner surfaces each hand really grips, and
/// which opening the shaft is meant for. Without a map, nothing bends.
/// </summary>
internal sealed class ContactMap
{
    public string Scene { get; set; } = "";
    public string Role { get; set; } = "";
    /// <summary>Partner role → the animation files (SHA-256) that play it.</summary>
    public Dictionary<string, List<string>> Partners { get; set; } = new(StringComparer.Ordinal);
    /// <summary>"l"/"r" → the partner role and the surfaces that hand grips.</summary>
    public Dictionary<string, HandMap?> Hands { get; set; } = new(StringComparer.Ordinal);
    public ShaftMap? Shaft { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ContactMap? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ContactMap>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="hash"/> plays <paramref name="role"/> in this scene.</summary>
    public bool IsPartner(string role, string? hash) =>
        hash is not null && Partners.TryGetValue(role, out var hashes) &&
        hashes.Any(candidate => candidate.Equals(hash, StringComparison.OrdinalIgnoreCase));
}

internal sealed class HandMap
{
    public string Partner { get; set; } = "";
    public List<string> Surfaces { get; set; } = [];
}

internal sealed class ShaftMap
{
    public string Partner { get; set; } = "";
    /// <summary>"vagina", "anus" or "mouth".</summary>
    public string Opening { get; set; } = "";
}

/// <summary>
/// Works out which animation file a character is playing and fetches its contact map. The game says
/// which action timeline is in a character's base slot; its key and the character's race give the
/// game path, Penumbra resolves that to the file this character really loads (their own mods, or a
/// sync plugin's collection for partners), and the file's SHA-256 names the map.
/// </summary>
internal sealed unsafe class ContactMapResolver
{
    private readonly IDataManager data;
    private readonly PenumbraService penumbra;
    private readonly Func<IReadOnlyList<string>, Task<IReadOnlyList<(string Hash, string Json)>>> fetch;
    private readonly IPluginLog log;
    // Per character: the animation last seen and what it resolved to.
    private readonly Dictionary<ulong, (string Key, string? Hash)> playing = [];
    private readonly ConcurrentDictionary<string, string> hashByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ContactMap?> maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);

    public ContactMapResolver(IDataManager data, PenumbraService penumbra,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<(string Hash, string Json)>>> fetch, IPluginLog log)
    {
        this.data = data;
        this.penumbra = penumbra;
        this.fetch = fetch;
        this.log = log;
    }

    /// <summary>The playing animation's file hash and its map, if known yet. Framework thread.</summary>
    public (string? Hash, ContactMap? Map) Resolve(nint address, ulong objectId, ushort objectIndex, bool isLocal)
    {
        var character = (Character*)address;
        if (character is null || character->DrawObject is null ||
            character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return (null, null);
        var characterBase = (CharacterBase*)character->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return (null, null);
        var race = ((Human*)characterBase)->RaceSexId;
        var timeline = character->Timeline.BaseOverride != 0
            ? character->Timeline.BaseOverride
            : character->Timeline.TimelineSequencer.TimelineIds[0];
        if (timeline == 0) return (null, null);

        var key = $"{race}:{timeline}";
        if (!playing.TryGetValue(objectId, out var known) || known.Key != key)
        {
            known = (key, FileHashFor(race, timeline, objectIndex, isLocal));
            playing[objectId] = known;
        }
        var hash = known.Hash ?? (playing[objectId] = (key, FileHashFor(race, timeline, objectIndex, isLocal))).Hash;
        if (hash is null) return (null, null);
        if (maps.TryGetValue(hash, out var map)) return (hash, map);
        Request(hash);
        return (hash, null);
    }

    public void Forget() => maps.Clear();

    /// <summary>The hash of the file this character loads for the timeline, or null while it is
    /// still being hashed in the background or when it is a game file (no mod, so no map).</summary>
    private string? FileHashFor(ushort race, ushort timeline, ushort objectIndex, bool isLocal)
    {
        var row = data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>()?.GetRowOrDefault(timeline);
        var timelineKey = row?.Key.ExtractText();
        if (string.IsNullOrWhiteSpace(timelineKey)) return null;

        // The game falls back to the base race's file when a race has none of its own.
        var female = race / 100 % 2 == 0;
        var races = new List<ushort> { race };
        if (female && race != 201) races.Add(201);
        if (race != 101) races.Add(101);
        foreach (var candidate in races)
        {
            var gamePath = $"chara/human/c{candidate:D4}/animation/a0001/bt_common/{timelineKey}.pap";
            var file = isLocal ? penumbra.ResolvePlayerPath(gamePath) : penumbra.ResolveGameObjectPath(gamePath, objectIndex);
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file)) continue;
            if (hashByFile.TryGetValue(FileKey(file), out var hash)) return hash;
            HashInBackground(file);
            return null;
        }
        return null;
    }

    private void HashInBackground(string file)
    {
        var fileKey = FileKey(file);
        if (!pending.TryAdd("file|" + fileKey, 0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                hashByFile[fileKey] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Couldn't hash animation file {File}", file);
            }
            finally
            {
                pending.TryRemove("file|" + fileKey, out _);
            }
        });
    }

    private void Request(string hash)
    {
        if (!pending.TryAdd("map|" + hash, 0)) return;
        _ = fetch([hash]).ContinueWith(task =>
        {
            pending.TryRemove("map|" + hash, out _);
            // An unreachable relay counts as "no map" until the next room or reconnect clears it.
            if (!task.IsCompletedSuccessfully)
            {
                maps[hash] = null;
                return;
            }
            var found = task.Result.FirstOrDefault(result => result.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            maps[hash] = found.Json is null ? null : ContactMap.FromJson(found.Json);
        }, TaskScheduler.Default);
    }

    private static string FileKey(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return file;
        }
    }
}

using System.Text.Json;

namespace EmoteLink.Relay;

/// <summary>
/// Per-animation contact maps: for one role's animation file (by the file's SHA-256), which partner
/// roles it expects, which partner surfaces each hand grips and which opening the shaft is meant
/// for. Plugins look up only the animations being played. Maps a moderator edited are locked, so a
/// later corpus import never overwrites them.
/// </summary>
public sealed class ContactMapStore(RelayDatabase database)
{
    public const int MaximumMapBytes = 32 * 1024;
    private const int MaximumLookup = 16;
    private readonly object gate = new();

    public IReadOnlyList<ContactMapDto> Get(IReadOnlyList<string> hashes)
    {
        var wanted = hashes.Select(Clean).Where(hash => hash.Length == 64).Distinct().Take(MaximumLookup).ToList();
        if (wanted.Count == 0) return [];
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            var names = wanted.Select((_, index) => "$h" + index).ToArray();
            for (var i = 0; i < wanted.Count; i++) command.Parameters.AddWithValue(names[i], wanted[i]);
            command.CommandText =
                $"SELECT pap_hash, map_json FROM animation_contact_maps WHERE pap_hash IN ({string.Join(',', names)});";
            var result = new List<ContactMapDto>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new ContactMapDto(reader.GetString(0), reader.GetString(1)));
            return result;
        }
    }

    public IReadOnlyList<AdminContactMapDto> List(string? query, int limit)
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pap_hash, scene, role, map_json, source, locked, updated_utc
                FROM animation_contact_maps
                WHERE $query = '' OR scene LIKE '%' || $query || '%' OR pap_hash LIKE $query || '%'
                ORDER BY scene COLLATE NOCASE, role COLLATE NOCASE
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", (query ?? "").Trim());
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            var result = new List<AdminContactMapDto>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(new AdminContactMapDto(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0, reader.GetString(6)));
            return result;
        }
    }

    /// <summary>A moderator's edit: stored and locked against later imports.</summary>
    public AdminContactMapDto? Set(string hash, string mapJson)
    {
        var clean = Clean(hash);
        if (clean.Length != 64) throw new ArgumentException("Give the animation file's 64-character SHA-256.");
        var (scene, role) = Describe(mapJson);
        lock (gate)
        {
            using var connection = database.OpenConnection();
            Upsert(connection, clean, mapJson, scene, role, "admin", locked: true, overwriteLocked: true);
        }
        return List(clean, 1).FirstOrDefault();
    }

    public bool Delete(string hash)
    {
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM animation_contact_maps WHERE pap_hash = $hash;";
            command.Parameters.AddWithValue("$hash", Clean(hash));
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Bulk corpus import. Maps a moderator locked are left alone.</summary>
    public (int Written, int Kept) Import(IReadOnlyDictionary<string, JsonElement> maps)
    {
        var written = 0;
        var kept = 0;
        lock (gate)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var (hash, map) in maps)
            {
                var clean = Clean(hash);
                var json = map.GetRawText();
                if (clean.Length != 64 || json.Length > MaximumMapBytes) continue;
                var (scene, role) = Describe(json);
                if (Upsert(connection, clean, json, scene, role, "corpus", locked: false, overwriteLocked: false, transaction)) written++;
                else kept++;
            }
            transaction.Commit();
        }
        return (written, kept);
    }

    private static bool Upsert(Microsoft.Data.Sqlite.SqliteConnection connection, string hash, string json, string scene,
        string role, string source, bool locked, bool overwriteLocked, Microsoft.Data.Sqlite.SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO animation_contact_maps (pap_hash, map_json, scene, role, source, locked, updated_utc)
            VALUES ($hash, $json, $scene, $role, $source, $locked, $now)
            ON CONFLICT(pap_hash) DO UPDATE SET
                map_json = excluded.map_json, scene = excluded.scene, role = excluded.role,
                source = excluded.source, locked = excluded.locked, updated_utc = excluded.updated_utc
            {(overwriteLocked ? "" : "WHERE animation_contact_maps.locked = 0")};
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$scene", scene);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$locked", locked ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    private static (string Scene, string Role) Describe(string json)
    {
        if (json.Length > MaximumMapBytes) throw new ArgumentException("This contact map is too large.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var scene = root.TryGetProperty("scene", out var s) ? s.GetString() ?? "" : "";
            var role = root.TryGetProperty("role", out var r) ? r.ToString() : "";
            return (scene[..Math.Min(200, scene.Length)], role[..Math.Min(40, role.Length)]);
        }
        catch (JsonException)
        {
            throw new ArgumentException("This contact map is not valid JSON.");
        }
    }

    private static string Clean(string hash) =>
        new string((hash ?? "").Trim().Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
}

public sealed record ContactMapDto(string Hash, string MapJson);
public sealed record AdminContactMapDto(string Hash, string Scene, string Role, string MapJson, string Source,
    bool Locked, string UpdatedUtc);

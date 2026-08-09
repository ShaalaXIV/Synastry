using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmoteLink.Relay;

public sealed class CommunityRoleLabelStore
{
    public const int InitialAcceptanceThreshold = 1;
    public const int CorrectionAcceptanceThreshold = 5;
    private const int MaximumRecords = 100_000;
    private const int MaximumVotesPerRecord = 1_000;
    private readonly object gate = new();
    private readonly string path;
    private Dictionary<string, StoredRoleLabel> records = new(StringComparer.OrdinalIgnoreCase);

    public CommunityRoleLabelStore()
    {
        var root = Environment.GetEnvironmentVariable("EMOTELINK_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmoteLink.Relay");
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "community-role-labels.json");
        Load();
    }

    public IReadOnlyList<CommunityRoleLabelDto> Get(IReadOnlyCollection<string> fingerprints)
    {
        var requested = fingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (gate)
            return records.Values
                .Where(record => requested.Contains(record.Fingerprint) && record.AcceptedLabel.Length > 0)
                .Select(ToDto)
                .ToList();
    }

    public IReadOnlyList<AdminRoleLabelDto> GetAll()
    {
        lock (gate)
            return records.Select(pair => ToAdminDto(pair.Key, pair.Value)).ToList();
    }

    public AdminRoleLabelDto? ApproveLeadingVote(string key)
    {
        lock (gate)
        {
            if (!records.TryGetValue(key, out var record) || record.Votes.Count == 0) return null;
            record.AcceptedLabel = record.Votes.Values
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(grouping => grouping.Count())
                .ThenBy(grouping => grouping.Key, StringComparer.OrdinalIgnoreCase)
                .First().First();
            record.Votes.Clear();
            Save();
            return ToAdminDto(key, record);
        }
    }

    public AdminRoleLabelDto? SetAcceptedLabel(string key, string label)
    {
        lock (gate)
        {
            if (!records.TryGetValue(key, out var record)) return null;
            record.AcceptedLabel = label.Trim();
            record.Votes.Clear();
            Save();
            return ToAdminDto(key, record);
        }
    }

    public bool ClearVotes(string key)
    {
        lock (gate)
        {
            if (!records.TryGetValue(key, out var record)) return false;
            record.Votes.Clear();
            Save();
            return true;
        }
    }

    public bool Delete(string key)
    {
        lock (gate)
        {
            if (!records.Remove(key)) return false;
            Save();
            return true;
        }
    }

    public bool RegisterMetadata(
        string fingerprint, string group, string option, string modName, string animationName)
    {
        var key = fingerprint + "\n" + group + "\n" + option;
        lock (gate)
        {
            if (!records.TryGetValue(key, out var record)) return false;
            if (UpdateMetadata(record, modName, animationName)) Save();
            return true;
        }
    }

    public (CommunityRoleLabelDto? Accepted, bool Changed) Submit(
        string fingerprint, string group, string option, string label, string reporterId,
        string modName = "", string animationName = "")
    {
        var key = fingerprint + "\n" + group + "\n" + option;
        lock (gate)
        {
            if (!records.TryGetValue(key, out var record))
            {
                if (records.Count >= MaximumRecords) throw new InvalidOperationException("The community label database is full.");
                records[key] = record = new StoredRoleLabel
                {
                    Fingerprint = fingerprint,
                    Group = group,
                    Option = option
                };
            }
            UpdateMetadata(record, modName, animationName);
            var reporterHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reporterId)));
            if (label.Equals(record.AcceptedLabel, StringComparison.OrdinalIgnoreCase))
            {
                record.Votes.Remove(reporterHash);
                Save();
                return (ToDto(record), false);
            }
            if (!record.Votes.ContainsKey(reporterHash) && record.Votes.Count >= MaximumVotesPerRecord)
                throw new InvalidOperationException("This role label has too many reports.");
            record.Votes[reporterHash] = label;

            var winner = record.Votes.Values
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(grouping => new { Label = grouping.First(), Count = grouping.Count() })
                .OrderByDescending(candidate => candidate.Count)
                .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
                .First();
            var requiredVotes = record.AcceptedLabel.Length == 0
                ? InitialAcceptanceThreshold
                : CorrectionAcceptanceThreshold;
            var changed = winner.Count >= requiredVotes &&
                !winner.Label.Equals(record.AcceptedLabel, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                record.AcceptedLabel = winner.Label;
                // A newly accepted value starts a fresh correction round. This lets
                // five matching corrections replace an old label regardless of how
                // many confirmations the old value accumulated previously.
                record.Votes.Clear();
            }
            Save();
            return (record.AcceptedLabel.Length == 0 ? null : ToDto(record), changed);
        }
    }

    private static CommunityRoleLabelDto ToDto(StoredRoleLabel record) =>
        new(record.Fingerprint, record.Group, record.Option, record.AcceptedLabel);

    private static bool UpdateMetadata(StoredRoleLabel record, string modName, string animationName)
    {
        var cleanModName = modName.Trim()[..Math.Min(160, modName.Trim().Length)];
        var cleanAnimationName = animationName.Trim()[..Math.Min(120, animationName.Trim().Length)];
        var changed = false;
        if (cleanModName.Length > 0 && !record.ModName.Equals(cleanModName, StringComparison.Ordinal))
        {
            record.ModName = cleanModName;
            changed = true;
        }
        if (cleanAnimationName.Length > 0 &&
            !record.AnimationName.Equals(cleanAnimationName, StringComparison.Ordinal))
        {
            record.AnimationName = cleanAnimationName;
            changed = true;
        }
        return changed;
    }

    private static AdminRoleLabelDto ToAdminDto(string key, StoredRoleLabel record)
    {
        var votes = record.Votes.Values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(grouping => new AdminVoteDto(grouping.First(), grouping.Count()))
            .OrderByDescending(vote => vote.Count)
            .ThenBy(vote => vote.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(key, record.Fingerprint, record.ModName, record.AnimationName, record.Group, record.Option,
            record.AcceptedLabel, votes);
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, StoredRoleLabel>>(File.ReadAllText(path));
            if (loaded is not null) records = new Dictionary<string, StoredRoleLabel>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch { records = new Dictionary<string, StoredRoleLabel>(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(records));
        File.Move(temporary, path, true);
    }

    public sealed class StoredRoleLabel
    {
        public string Fingerprint { get; set; } = "";
        public string ModName { get; set; } = "";
        public string AnimationName { get; set; } = "";
        public string Group { get; set; } = "";
        public string Option { get; set; } = "";
        public string AcceptedLabel { get; set; } = "";
        public Dictionary<string, string> Votes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record CommunityRoleLabelDto(string Fingerprint, string Group, string Option, string Label);
public sealed record AdminVoteDto(string Label, int Count);
public sealed record AdminRoleLabelDto(string Key, string Fingerprint, string ModName, string AnimationName,
    string Group, string Option, string AcceptedLabel, IReadOnlyList<AdminVoteDto> Votes);

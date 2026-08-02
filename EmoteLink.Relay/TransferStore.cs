using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace EmoteLink.Relay;

public sealed class TransferStore : BackgroundService
{
    public const long MaximumBytes = 75L * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Transfer> transfers = new();
    private readonly object reservationGate = new();
    private readonly string root = Path.Combine(Path.GetTempPath(), "emotelink-transfers");

    public TransferStore()
    {
        Directory.CreateDirectory(root);
        foreach (var orphan in Directory.EnumerateFiles(root, "*.pmp"))
            try { File.Delete(orphan); } catch { }
    }

    public TransferUploadDto Begin(string roomCode, string senderId, string senderName, string modName,
        long size, string sha256, IReadOnlyList<string> recipients)
    {
        if (size <= 0 || size > MaximumBytes) throw new InvalidOperationException("Mod must be 75 MB or smaller.");
        var hash = CleanHash(sha256);
        if (hash.Length != 64) throw new InvalidOperationException("A valid SHA-256 checksum is required.");
        if (recipients.Count == 0) throw new InvalidOperationException("There is nobody else in the room.");

        lock (reservationGate)
        {
            if (transfers.Values.Count(value => value.RoomCode.Equals(roomCode, StringComparison.OrdinalIgnoreCase)) >= 16)
                throw new InvalidOperationException("This room already has too many active transfers.");
            if (transfers.Values.Sum(value => value.Size) + size > 1024L * 1024 * 1024)
                throw new InvalidOperationException("The relay transfer storage is currently full.");
            var id = RandomToken(18);
            var uploadToken = RandomToken(32);
            var recipientTokens = recipients.Distinct().ToDictionary(value => value, _ => RandomToken(32));
            var transfer = new Transfer(id, CleanName(modName), roomCode, senderId, senderName, size, hash,
                DateTimeOffset.UtcNow.Add(Lifetime), uploadToken, Path.Combine(root, id + ".pmp"), recipientTokens);
            if (!transfers.TryAdd(id, transfer)) throw new InvalidOperationException("Could not create the transfer.");
            return new TransferUploadDto(id, uploadToken);
        }
    }

    public Transfer? GetUpload(string id, string token) =>
        transfers.TryGetValue(id, out var transfer) && FixedEquals(transfer.UploadToken, token) && !transfer.Uploaded
            ? transfer : null;

    public IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> FinishUpload(Transfer transfer)
    {
        lock (transfer.Gate)
        {
            transfer.Uploaded = true;
            return transfer.RecipientTokens.Select(pair => (pair.Key,
                new ModTransferOfferDto(transfer.Id, transfer.ModName, transfer.SenderName, transfer.Size,
                    transfer.Sha256, pair.Value, transfer.ExpiresAt))).ToList();
        }
    }

    public Transfer? GetDownload(string id, string token)
    {
        if (!transfers.TryGetValue(id, out var transfer) || !transfer.Uploaded || !File.Exists(transfer.Path)) return null;
        return transfer.RecipientTokens.Values.Any(value => FixedEquals(value, token)) ? transfer : null;
    }

    public void MarkDownloaded(string id, string connectionId)
    {
        if (!transfers.TryGetValue(id, out var transfer)) return;
        lock (transfer.Gate)
        {
            if (!transfer.RecipientTokens.ContainsKey(connectionId)) return;
            transfer.Downloaded.Add(connectionId);
            if (transfer.Downloaded.Count < transfer.RecipientTokens.Count) return;
        }
        Remove(id);
    }

    public void Decline(string id, string connectionId)
    {
        if (transfers.TryGetValue(id, out var transfer) && transfer.RecipientTokens.ContainsKey(connectionId))
            transfer.Declined.TryAdd(connectionId, 0);
    }

    public void RemoveForRoom(string roomCode)
    {
        foreach (var transfer in transfers.Values.Where(value => value.RoomCode.Equals(roomCode, StringComparison.OrdinalIgnoreCase)))
            Remove(transfer.Id);
    }

    private void Remove(string id)
    {
        if (!transfers.TryRemove(id, out var transfer)) return;
        try { File.Delete(transfer.Path); } catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
            foreach (var transfer in transfers.Values.Where(value => value.ExpiresAt <= DateTimeOffset.UtcNow))
                Remove(transfer.Id);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var transfer in transfers.Values) Remove(transfer.Id);
        await base.StopAsync(cancellationToken);
    }

    private static string CleanHash(string value) => new(value.Where(Uri.IsHexDigit).Take(64).Select(char.ToUpperInvariant).ToArray());
    private static string CleanName(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();
        return clean.Length == 0 ? "Animation mod" : clean;
    }
    private static string RandomToken(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    public sealed class Transfer(string id, string modName, string roomCode, string senderId, string senderName,
        long size, string sha256, DateTimeOffset expiresAt, string uploadToken, string path,
        Dictionary<string, string> recipientTokens)
    {
        public object Gate { get; } = new();
        public string Id { get; } = id;
        public string ModName { get; } = modName;
        public string RoomCode { get; } = roomCode;
        public string SenderId { get; } = senderId;
        public string SenderName { get; } = senderName;
        public long Size { get; } = size;
        public string Sha256 { get; } = sha256;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string UploadToken { get; } = uploadToken;
        public string Path { get; } = path;
        public Dictionary<string, string> RecipientTokens { get; } = recipientTokens;
        public HashSet<string> Downloaded { get; } = [];
        public ConcurrentDictionary<string, byte> Declined { get; } = new();
        public bool Uploaded { get; set; }
    }
}

public sealed record TransferUploadDto(string TransferId, string UploadToken);
public sealed record ModTransferOfferDto(string TransferId, string ModName, string SenderName, long Size,
    string Sha256, string DownloadToken, DateTimeOffset ExpiresAt);

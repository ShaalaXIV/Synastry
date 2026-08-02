using EmoteLink.Relay;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = TransferStore.MaximumBytes);
builder.Services.AddSingleton<TransferStore>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TransferStore>());
builder.Services.AddSignalR(options =>
{
    // A full 1,000-entry fingerprint catalog is roughly 70 KB as JSON. The old
    // 16 KB ceiling disconnected players with larger Penumbra libraries while
    // SetCatalog was being sent, which appeared in the UI as a reconnect loop.
    options.MaximumReceiveMessageSize = 128 * 1024;
    options.EnableDetailedErrors = false;
});

var app = builder.Build();
app.MapGet("/health", () => Results.Text("emotelink-relay:ok", "text/plain"));
app.MapPut("/transfers/{id}", async (string id, string token, HttpRequest request, TransferStore store,
    IHubContext<AnimationHub> hub, CancellationToken cancellationToken) =>
{
    var transfer = store.GetUpload(id, token);
    if (transfer is null) return Results.NotFound();
    if (request.ContentLength is null or <= 0 || request.ContentLength > TransferStore.MaximumBytes ||
        request.ContentLength != transfer.Size) return Results.BadRequest("Invalid transfer size.");

    try
    {
        long written = 0;
        string actualHash;
        await using (var output = new FileStream(transfer.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                written += read;
                if (written > TransferStore.MaximumBytes) throw new InvalidDataException("Transfer exceeds 75 MB.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            actualHash = Convert.ToHexString(hash.GetHashAndReset());
        }
        if (written != transfer.Size || !actualHash.Equals(transfer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Transfer checksum did not match.");

        foreach (var (connectionId, offer) in store.FinishUpload(transfer))
            await hub.Clients.Client(connectionId).SendAsync("ModTransferOffered", offer, cancellationToken);
        return Results.Ok();
    }
    catch
    {
        try { File.Delete(transfer.Path); } catch { }
        throw;
    }
}).DisableAntiforgery();
app.MapGet("/transfers/{id}", (string id, string token, TransferStore store) =>
{
    var transfer = store.GetDownload(id, token);
    return transfer is null
        ? Results.NotFound()
        : Results.File(transfer.Path, "application/octet-stream", transfer.ModName + ".pmp");
});
app.MapHub<AnimationHub>("/animation");
app.Run();

using EmoteLink.Relay;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var adminPort = ReadPort("EMOTELINK_ADMIN_PORT", 25081);
var publicUrls = (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:25080")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (publicUrls.Any(url => ConfiguredPort(url) == adminPort))
    throw new InvalidOperationException(
        $"The admin port {adminPort} must not also be configured as a public ASPNETCORE_URLS endpoint.");
// The administration listener is deliberately loopback-only. Remote moderation
// connects through an SSH tunnel and the public reverse proxy never sees this port.
builder.WebHost.UseUrls(publicUrls.Append($"http://127.0.0.1:{adminPort}").ToArray());
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = TransferStore.MaximumBytes);
builder.Services.AddSingleton<RelayDatabase>();
builder.Services.AddSingleton<ITransferModerationRepository, SqliteTransferModerationRepository>();
builder.Services.AddSingleton<AnimationCatalogStore>();
builder.Services.AddSingleton<CatalogSearchService>();
builder.Services.AddSingleton<AdminTransferEventBroker>();
builder.Services.AddSingleton<TransferStore>();
builder.Services.AddSingleton<CommunityRoleLabelStore>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TransferStore>());
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Only a same-host reverse proxy may replace the peer address used by the
    // catalog creation limiter. Direct clients cannot forge X-Forwarded-For.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddSignalR(options =>
{
    // A full 1,000-entry fingerprint catalog is roughly 70 KB as JSON. The old
    // 16 KB ceiling disconnected players with larger Penumbra libraries while
    // SetCatalog was being sent, which appeared in the UI as a reconnect loop.
    options.MaximumReceiveMessageSize = AnimationCatalogStore.MaximumSignalRMessageBytes;
    options.EnableDetailedErrors = false;
    // Idle rooms only need a lightweight liveness frame every 30 seconds. Any room
    // action already produces traffic and resets SignalR's idle keepalive timer.
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(90);
});

var app = builder.Build();
// Force durable storage initialization before Kestrel starts accepting clients. In particular,
// a legacy JSON migration/backup failure must stop startup instead of surfacing on first use.
_ = app.Services.GetRequiredService<RelayDatabase>();
_ = app.Services.GetRequiredService<CommunityRoleLabelStore>();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var isAdminPath = context.Request.Path.StartsWithSegments("/admin");
    if (context.Connection.LocalPort == adminPort)
    {
        if (!isAdminPath && !context.Request.Path.Equals("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    else if (isAdminPath)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.MapGet("/health", () => Results.Text("emotelink-relay:ok", "text/plain"));
var adminRoot = app.MapGroup("/admin");
adminRoot.AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var address = http.Connection.RemoteIpAddress;
    if (http.Connection.LocalPort != adminPort || address is null || !IPAddress.IsLoopback(address))
        return Results.NotFound();
    var expected = Environment.GetEnvironmentVariable("EMOTELINK_ADMIN_TOKEN");
    var supplied = http.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(expected) || !supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied[7..]))))
        return Results.Unauthorized();
    return await next(context);
});
var admin = adminRoot.MapGroup("/community-labels");
admin.MapGet("/", (CommunityRoleLabelStore store) => store.GetAll());
admin.MapPost("/approve", (string key, CommunityRoleLabelStore store) =>
    store.ApproveLeadingVote(key) is { } record ? Results.Ok(record) : Results.NotFound());
admin.MapPut("/accepted", (string key, AdminLabelUpdate update, CommunityRoleLabelStore store) =>
{
    var label = update.Label.Trim();
    if (label.Length is < 1 or > 80) return Results.BadRequest("Label must be 1-80 characters.");
    return store.SetAcceptedLabel(key, label) is { } record ? Results.Ok(record) : Results.NotFound();
});
admin.MapDelete("/votes", (string key, CommunityRoleLabelStore store) =>
    store.ClearVotes(key) ? Results.NoContent() : Results.NotFound());
admin.MapDelete("/record", (string key, CommunityRoleLabelStore store) =>
    store.Delete(key) ? Results.NoContent() : Results.NotFound());

var animationAdmin = adminRoot.MapGroup("/animation-index");
animationAdmin.MapGet("/", (string? query, int? limit, AnimationCatalogStore store) =>
    string.IsNullOrWhiteSpace(query)
        ? store.GetAll(Math.Clamp(limit ?? 1_000, 1, 10_000))
        : store.Search(query, Math.Clamp(limit ?? 100, 1, 500)));
animationAdmin.MapPut("/override", (string signature, AdminArtifactOverrideUpdate update,
    AnimationCatalogStore store) =>
{
    try
    {
        return Results.Ok(store.SetAdminOverride(signature, update.Classification,
            update.SharingPolicy, update.ReasonCode ?? "", update.Note ?? "", "local-admin",
            update.ApprovedPayloadSha256));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});
animationAdmin.MapDelete("/override", (string signature, AnimationCatalogStore store) =>
    store.RevokeAdminOverride(signature) ? Results.NoContent() : Results.NotFound());

var catalogAdmin = adminRoot.MapGroup("/catalog");
catalogAdmin.MapGet("/search", (string query, int? limit, CatalogSearchService search) =>
    search.Search(query, Math.Clamp(limit ?? 100, 1, 500)));

var transferAdmin = adminRoot.MapGroup("/transfers");
transferAdmin.MapGet("/", (string? query, string? status, TransferStore store) =>
    store.GetAdminTransfers(query, status));
transferAdmin.MapGet("/events", async (HttpContext context, AdminTransferEventBroker events,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";
    await context.Response.WriteAsync(": connected\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
    await foreach (var message in events.Subscribe(cancellationToken))
    {
        await context.Response.WriteAsync($"id: {message.Sequence}\n", cancellationToken);
        await context.Response.WriteAsync("event: transfer\n", cancellationToken);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(message)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
});
transferAdmin.MapGet("/{id}", (string id, TransferStore store) =>
    store.GetAdminTransfer(id) is { } transfer ? Results.Ok(transfer) : Results.NotFound());
transferAdmin.MapGet("/{id}/package", (string id, HttpRequest request, HttpResponse response,
    TransferStore store) =>
{
    response.Headers.CacheControl = "no-store";
    var download = store.OpenAdminDownload(id, TransferAdminAudit.Actor(request));
    return download is null
        ? Results.NotFound()
        : Results.File(download.Stream, "application/octet-stream", download.Transfer.ModName + ".pmp");
});
transferAdmin.MapPost("/{id}/block", (string id, TransferBlockRequest update, HttpRequest request,
    TransferStore store) => store.BlockRecipientAccess(id, update.Reason, TransferAdminAudit.Actor(request)) switch
    {
        TransferBlockResult.NotFound => Results.NotFound(),
        _ => Results.NoContent()
    });
transferAdmin.MapGet("/bans", (bool? includeRevoked, string? query, ITransferModerationRepository moderation) =>
    moderation.GetTransferBans(includeRevoked ?? false, query));
transferAdmin.MapPost("/bans", (TransferBanUpdate update, HttpRequest request,
    ITransferModerationRepository moderation, TransferStore store) =>
{
    try
    {
        var actor = update.CreatedBy ?? TransferAdminAudit.Actor(request);
        var ban = moderation.UpsertTransferBan(
            update.Scope, update.Value, update.ReasonCode, update.Note ?? "", actor,
            update.DisplayName ?? "");
        TransferAdminAudit.RecordBanAction(moderation, ban, "admin-ban-upserted", actor);
        return Results.Ok(new TransferBanUpdateResult(ban, store.ApplyCurrentBans()));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});
transferAdmin.MapPost("/{id}/ban", (string id, TransferBanFromTransferRequest update, HttpRequest request,
    ITransferModerationRepository moderation, TransferStore store) =>
{
    var transfer = store.GetAdminTransfer(id);
    if (transfer is null) return Results.NotFound();
    var value = update.Scope switch
    {
        TransferBanScope.ExactPackageSha256 => transfer.Sha256,
        TransferBanScope.AnimationCatalogFingerprint => transfer.CatalogFingerprint,
        TransferBanScope.ModFamilyNameHash => transfer.ModNameHash,
        _ => ""
    };
    if (value.Length != 64) return Results.BadRequest("This transfer has no value for the requested ban scope.");
    try
    {
        var actor = update.CreatedBy ?? TransferAdminAudit.Actor(request);
        var ban = moderation.UpsertTransferBan(
            update.Scope, value, update.ReasonCode, update.Note ?? "", actor,
            transfer.ModName);
        TransferAdminAudit.RecordBanAction(moderation, ban, "admin-ban-upserted", actor);
        return Results.Ok(new TransferBanUpdateResult(ban, store.ApplyCurrentBans()));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});
transferAdmin.MapDelete("/bans/{id:long}", (long id, HttpRequest request,
    ITransferModerationRepository moderation) =>
{
    var ban = moderation.GetTransferBans(true).FirstOrDefault(value => value.Id == id);
    if (ban is null || ban.RevokedUtc is not null) return Results.NotFound();
    var actor = TransferAdminAudit.Actor(request);
    TransferAdminAudit.RecordBanAction(moderation, ban, "admin-ban-revoke-requested", actor);
    if (!moderation.DeleteTransferBan(id)) return Results.NotFound();
    TransferAdminAudit.RecordBanAction(moderation, ban, "admin-ban-revoked", actor);
    return Results.NoContent();
});
transferAdmin.MapGet("/audit", (int? limit, ITransferModerationRepository moderation) =>
    moderation.GetAuditEvents(Math.Clamp(limit ?? 500, 1, 2000)));
transferAdmin.MapDelete("/{id}", (string id, HttpRequest request, TransferStore store) =>
    store.AdminDelete(id, TransferAdminAudit.Actor(request)) switch
    {
        TransferRemovalResult.NotFound => Results.NotFound(),
        TransferRemovalResult.Deferred => Results.Accepted(),
        _ => Results.NoContent()
    });

app.MapPut("/transfers/{id}", async (string id, HttpRequest request, HttpResponse response,
    TransferStore store, AnimationCatalogStore animationCatalog,
    IHubContext<AnimationHub> hub, ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "no-store";
    var token = request.Headers[TransferStore.CapabilityHeaderName].ToString();
    var transfer = store.GetUpload(id, token);
    if (transfer is null) return Results.NotFound();
    if (request.ContentLength is null or <= 0 || request.ContentLength > TransferStore.MaximumBytes ||
        request.ContentLength != transfer.Size)
    {
        store.AbortUpload(transfer, "Invalid transfer size.");
        return Results.BadRequest("Invalid transfer size.");
    }

    IReadOnlyList<(string ConnectionId, ModTransferOfferDto Offer)> offers;
    using var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, store.GetUploadCancellationToken(transfer));
    var uploadToken = uploadCancellation.Token;
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
                var read = await request.Body.ReadAsync(buffer, uploadToken);
                if (read == 0) break;
                written += read;
                if (written > TransferStore.MaximumBytes) throw new InvalidDataException("Transfer exceeds 75 MB.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), uploadToken);
            }
            await output.FlushAsync(uploadToken);
            actualHash = Convert.ToHexString(hash.GetHashAndReset());
        }
        if (written != transfer.Size || !actualHash.Equals(transfer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Transfer checksum did not match.");

        offers = store.FinishUpload(transfer);
        _ = Task.Run(() =>
        {
            try
            {
                animationCatalog.IndexUploadedPackage(transfer.Path, transfer.ModName);
            }
            catch (Exception verificationError)
            {
                // Catalog enrichment is intentionally non-fatal: the transfer was checksum
                // verified and may proceed even when its PMP is nonstandard or not an animation.
                loggerFactory.CreateLogger("EmoteLink.Relay.PackageIndex")
                    .LogWarning(verificationError,
                        "Could not index completed transfer package {TransferId}", transfer.Id);
            }
        }, CancellationToken.None);
    }
    catch (Exception exception)
    {
        store.AbortUpload(transfer, exception.Message);
        if (exception is OperationCanceledException && transfer.ExpiresAt <= DateTimeOffset.UtcNow)
            return Results.StatusCode(StatusCodes.Status410Gone);
        throw;
    }
    await TransferOfferDelivery.DeliverAsync(
        hub, store, offers, loggerFactory.CreateLogger("EmoteLink.Relay.TransferOfferDelivery"));
    return Results.Ok();
}).DisableAntiforgery();
app.MapGet("/transfers/{id}", (string id, HttpRequest request, HttpResponse response, TransferStore store) =>
{
    response.Headers.CacheControl = "no-store";
    var token = request.Headers[TransferStore.CapabilityHeaderName].ToString();
    var download = store.OpenDownload(id, token);
    return download is null
        ? Results.NotFound()
        : Results.File(download.Stream, "application/octet-stream", download.Transfer.ModName + ".pmp");
});
app.MapHub<AnimationHub>("/animation");
app.Run();

static int ReadPort(string name, int fallback)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    if (!int.TryParse(raw, out var value) || value is < 1 or > 65535)
        throw new InvalidOperationException($"{name} must be a TCP port from 1 through 65535.");
    return value;
}

static int ConfiguredPort(string url)
{
    var normalized = url.Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
        .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal);
    if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https"))
        throw new InvalidOperationException($"Invalid ASPNETCORE_URLS endpoint: {url}");
    return uri.Port;
}

public sealed record AdminLabelUpdate(string Label);
public sealed record AdminArtifactOverrideUpdate(
    AnimationArtifactClassification? Classification,
    AnimationSharingPolicy SharingPolicy,
    string? ReasonCode,
    string? Note,
    string? ApprovedPayloadSha256);
public sealed record TransferBanUpdate(
    TransferBanScope Scope,
    string Value,
    TransferBanReasonCode ReasonCode,
    string? Note,
    string? CreatedBy,
    string? DisplayName);
public sealed record TransferBanFromTransferRequest(
    TransferBanScope Scope,
    TransferBanReasonCode ReasonCode,
    string? Note,
    string? CreatedBy);
public sealed record TransferBanUpdateResult(TransferSharingBanDto Ban, int BlockedActiveTransfers);

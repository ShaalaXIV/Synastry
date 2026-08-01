using EmoteLink.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.EnableDetailedErrors = false;
});

var app = builder.Build();
app.MapGet("/health", () => Results.Text("emotelink-relay:ok", "text/plain"));
app.MapHub<AnimationHub>("/animation");
app.Run();

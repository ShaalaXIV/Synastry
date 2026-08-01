# EmoteLink Relay

A minimal, in-memory SignalR relay for synchronized animation starts. It does not transfer mods, files, character data, appearance data, chat, or arbitrary game commands.

## Run directly on Linux

Install the .NET 10 ASP.NET Core Runtime, extract the `linux-x64` bundle, then:

```bash
chmod +x EmoteLink.Relay
ASPNETCORE_URLS=http://0.0.0.0:5080 ./EmoteLink.Relay
```

Health check: `GET /health`. SignalR endpoint: `/animation`.

## Docker

From this directory:

```bash
docker build -t emotelink-relay .
docker run --rm -p 5080:5080 emotelink-relay
```

For internet use, place the relay behind HTTPS (for example Caddy or Cloudflare). Rooms are ephemeral and disappear when their last member leaves or the process restarts.

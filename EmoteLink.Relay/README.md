# EmoteLink Relay

A SignalR relay for synchronized animation starts and explicit room-member animation-mod transfers. Transfers are limited to 75 MB each, expire after 10 minutes, and share a 25 GB storage budget. When storage is needed, the oldest pending transfers are removed first. The relay does not transfer character data, appearance data, chat, or arbitrary game commands.

Room option selections can be offered as animation suggestions. Recipients may explicitly decline a suggestion, and the relay returns the named decline to the suggesting room member.

## Run directly on Linux

Install the .NET 10 ASP.NET Core Runtime, extract the `linux-x64` bundle, then:

```bash
chmod +x EmoteLink.Relay
ASPNETCORE_URLS=http://0.0.0.0:25080 ./EmoteLink.Relay
```

Health check: `GET /health`. SignalR endpoint: `/animation`.

## Docker

From this directory:

```bash
docker build -t emotelink-relay .
docker run --rm -p 25080:25080 emotelink-relay
```

For internet use, place the relay behind HTTPS (for example Caddy or Cloudflare). Rooms are ephemeral and disappear when their last member leaves or the process restarts.

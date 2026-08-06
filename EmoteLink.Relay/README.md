# EmoteLink Relay

A SignalR relay for synchronized animation starts and explicit room-member animation-mod transfers. Transfers are limited to 75 MB each, expire after 10 minutes, and share a 25 GB storage budget. When storage is needed, the oldest pending transfers are removed first. The relay does not transfer character data, appearance data, chat, or arbitrary game commands.

Room option selections can be offered as animation suggestions. Recipients may explicitly decline a suggestion, and the relay returns the named decline to the suggesting room member.

Animation role labels are shared within a room so clients with an empty local label can adopt one from another member. Clients retain their own existing labels, and private mods are not advertised by the plugin.

Accepted community role labels persist across rooms and relay restarts. The first submission accepts an initial label; five matching installation reports accept a later correction. Set `EMOTELINK_DATA_DIR` to place `community-role-labels.json` on durable production storage.

## Community-label administration

Set a long random `EMOTELINK_ADMIN_TOKEN` in the relay service environment and restart it. The administration API only answers requests whose network peer is localhost and whose `Authorization` header contains that bearer token. Access it remotely through an SSH tunnel; do not expose another public listener for it.

On the Windows machine, open the tunnel (replace the host name):

```powershell
ssh -N -L 25081:127.0.0.1:25080 user@relay-host
```

Run `EmoteLink.LabelAdmin`, use `http://127.0.0.1:25081` as the tunnel URL, enter the admin token, and press **Refresh**. The tool can approve the current leading correction immediately, clear pending votes, edit an accepted label, or delete a record. Normal client submissions still require five matching votes for later corrections.

## Run directly on Linux

Install the .NET 10 ASP.NET Core Runtime, extract the `linux-x64` bundle, then:

```bash
chmod +x EmoteLink.Relay
ASPNETCORE_URLS=http://0.0.0.0:25080 ./EmoteLink.Relay
```

Health check: `GET /health`. SignalR endpoint: `/animation`.

Room clients use WebSockets for immediate events. Completely idle connections exchange only a lightweight keepalive every 30 seconds.

## Docker

From this directory:

```bash
docker build -t emotelink-relay .
docker run --rm -p 25080:25080 emotelink-relay
```

For internet use, place the relay behind HTTPS (for example Caddy or Cloudflare). Rooms are ephemeral and disappear when their last member leaves or the process restarts.

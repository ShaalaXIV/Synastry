# EmoteLink Relay

A SignalR relay for synchronized animation starts, explicit room-member animation-mod transfers, and community role labels. Transfers are limited to 75 MB each. Recipients have 10 minutes to accept and download an offer, and the sender has the same 10 minutes to upload. The package itself is kept on disk for 24 hours after it was uploaded, and all stored packages share a 25 GB storage budget. The relay also caps active transfers globally (4,096), per sender (32), and per room (128). A send counts as active only while it waits for its upload or still has a recipient who can accept it; it frees its slot as soon as it finishes. At most 20,000 retained sends are kept, and the oldest finished send is removed to make room. Reserved bytes and counts are maintained atomically; under actual storage pressure, the stored package whose sends end first is tombstoned and removed before a new reservation is accepted. The relay does not transfer character data, appearance data, chat, or arbitrary game commands.

Versioned transfer starts compare the offered animation fingerprint with each recipient's room catalog. Members who already have the animation are treated as received without receiving a notification. Download, decline, room-close, and moderation actions revoke normal access and clear the room queue, but the uploaded package remains available to authorized moderators until its 24-hour retention ends. Recipients still pending when the 10-minute offer window passes are marked expired; the send itself stays retained. Storage pressure, the retained-send limit, or an administrative deletion can remove it sooner. A relay restart does not: retained sends and their packages are restored, but offers still waiting for a recipient are not.

Packages are stored once per SHA-256. When `BeginModTransferV2` names bytes the relay already holds (same SHA-256 and size), the new send is created already uploaded from the stored copy: its reply carries the transfer ID with an empty upload token, which plugins since 1.0.51 treat as "no upload needed", and the relay delivers the offers itself. Each send keeps the package for 24 hours from that send, and the file is deleted when the last send using it ends. The legacy `BeginModTransfer` always uploads, but an upload of bytes that are already stored still keeps only one copy. In practice reuse mostly helps the same sender re-sending, because packages built on different installs rarely match byte for byte.

Upload and download capability tokens travel only in the `X-Synastry-Transfer-Token` request header, never in URLs. Transfer responses are marked `Cache-Control: no-store`. An upload is cancelled when its 10-minute offer window ends, even if its request stalls. Offer notifications are delivered independently of the sender's HTTP cancellation, in parallel, with one recipient failure isolated from the others.

Deletion is a tracked lifecycle, not a best-effort disappearance. An administrative delete during an upload tombstones the transfer, revokes capabilities, cancels the body, and finishes deletion when the file handle closes. An administrative delete withdraws the stored package and ends every send that shares it. Failed deletions remain visible as `deletion-pending` and are retried. At startup the relay restores each retained send whose package file is still present with the right size, deletes the records it cannot use, and then removes every other `.pmp` or `.part` file in the package folder; if the records cannot be read, no package file is removed. Packages left in the old temporary `emotelink-transfers` folder by earlier relays are deleted. Startup logs report every orphan-package sweep and retain failed orphan deletions for background retry.

Room option selections can be offered as animation suggestions. Recipients may explicitly decline a suggestion, and the relay returns the named decline to the suggesting room member.

Animation role labels are shared within a room so clients with an empty local label can adopt one from another member. Clients retain their own existing labels, and private mods are not advertised by the plugin.

Accepted community role labels persist across rooms and relay restarts. The first submission accepts an initial label; five matching installation reports accept a later correction. Updated clients also register the human-readable mod and animation names used by the moderation UI.

Set `EMOTELINK_DATA_DIR` to place `emotelink-relay.db` on durable production storage. Transfer packages are stored beside it in `<EMOTELINK_DATA_DIR>/transfer-packages` (set `EMOTELINK_TRANSFER_DIR` to use another folder), one `.pmp` file per unique package, and survive restarts. Each send keeps its package for `EMOTELINK_TRANSFER_RETENTION_MINUTES` after its upload (default 1440, that is 24 hours; accepted range 1 to 10080), never less than its 10-minute offer window. Packages are removed sooner by storage pressure, the retained-send limit, or administrative deletion. An upload is written to a `.part` file, checked against the announced size and SHA-256, flushed to disk, and only then moved into the store. SQLite uses WAL mode, foreign keys, a five-second busy timeout, and forward-only schema migrations. On first startup, an existing `community-role-labels.json` is transactionally imported and checked for equivalent records and votes before the migration is accepted. The untouched source receives a unique, timestamped, checksum-verified `.pre-sqlite.*.bak` backup. SQLite is authoritative afterward. Set `EMOTELINK_WRITE_LEGACY_LABEL_JSON=true` only during a short rollback window if an additional compatibility mirror is required; it is off by default so label writes remain constant-time.

If an older relay writes to the legacy JSON during a rollback and SQLite is later restored, startup fails closed instead of silently choosing a side. Back up both files, manually reconcile them to the same semantic records and votes, set `EMOTELINK_RECONCILE_LEGACY_LABEL_JSON=accept-reconciled` for one startup, then remove that variable. The switch only acknowledges equality; it never overwrites either source.

Community role labels are stored per animation fingerprint and option, with the mod and animation display names players submit and per-installation votes. A moderator action through the administration listener locks a label and increments its `moderation_revision`; votes on a locked label are kept but never replace it, and removing a label keeps the row as a locked empty label so it cannot be re-voted back. Every action is broadcast to connected clients (`CommunityRoleLabelModerated`), and `GetCommunityRoleLabelsV2` also returns removals, so each client applies a revision once. The relay keeps no catalog of installed mods: schema migration 8 drops the former animation artifact tables, and the old report and lookup hub methods remain only as no-ops for plugins before 1.0.78. FTS5 trigram indexes provide bounded server-side searches across community labels, pending tag votes, and durable transfer-ban names.

Relay enforcement comes only from persistent transfer bans keyed by exact package SHA-256, animation fingerprint, or normalized mod-family-name SHA-256. Bans are checked before every send, including one offered from a stored copy. A stored copy is also not reused while a moderator's access block or delete applies to any send of it.

The relay records only anonymous aggregate statistics: rooms generated, completed animation shares, and animation performances. It never adds a character name, account identifier, installation identifier, room code, animation identifier, or connection identifier to the statistics row. The active-user value is an in-memory connection count and is not persisted. `GET /statistics` exposes these totals for read-only integrations such as a Discord bridge; the plugin requests only the active-user count through SignalR.

While a send is retained, the `retained_transfers` table (schema migration 10) keeps one row for it: mod name, mod-name hash, animation fingerprint, package SHA-256 and file name, size, room code, the sender's display name, timestamps, any moderator access block, and how many recipients downloaded, declined, were closed out, blocked, failed, or expired. It holds no connection ids, capability tokens, or recipient identities, and the row is deleted with the send. This is what lets retained sends and their moderation view survive a restart.

Transfer audit history is capped by event count at the newest 100,000 records; it is not governed by a time-retention window. Each record may contain its transfer ID, event and timestamp, exact package SHA-256, animation catalog fingerprint, normalized mod-name SHA-256, and a bounded moderation note. It excludes package bytes, capability tokens, sender identity/display name, and room code. Administrator labels are SHA-256 hashed before persistence; plaintext administrator labels are never written to the audit table. Package bytes are never copied into SQLite. Audit metadata can outlive the 24-hour package, while the reviewable `.pmp` cannot.

## Administration

Set a long random `EMOTELINK_ADMIN_TOKEN` in the relay service environment and restart it. The administration API listens separately on loopback port `25081` by default (`EMOTELINK_ADMIN_PORT` may change it), requires that local port plus a localhost peer and bearer token, and returns 404 on the public listener even when a valid token is supplied. Only `/admin/*` and `/health` are served on the admin listener. Access it remotely through an SSH tunnel such as `ssh -N -L 25081:127.0.0.1:25081 server`; never proxy or publicly expose the admin port.

The external administration application is intentionally maintained outside this public plugin repository. Its authenticated views cover anonymous relay statistics, community tags (lock, use top suggestion, remove, unlock), live and retained transfers, and persistent sharing bans. Transfer lifecycle updates are available through a protected server-sent-event stream. In transfer listings, `expiresAt` is the end of retention, `offerExpiresAt` is the end of the 10-minute offer window, and `packageSendCount` is how many retained sends share the package. Moderators can obtain a review copy until the package's retention ends (24 hours by default), revoke recipient access, delete evidence (deleting a package ends every send that shares it), and create creator-opt-out bans by exact package hash, animation fingerprint, or normalized mod-family hash. Review downloads, access blocks, delete requests, and ban upserts/revocations are recorded in the hashed audit trail.

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

The standard container command intentionally publishes only the public listener. The loopback-only admin listener is not reachable through Docker port publishing; keep it unavailable until a separately reviewed private administration transport is configured.

The database and stored transfer packages live under `EMOTELINK_DATA_DIR`; to keep them across container restarts, point it at a mounted volume (for example `-v emotelink-data:/data -e EMOTELINK_DATA_DIR=/data`).

For internet use, place the relay behind HTTPS (for example Caddy or Cloudflare). Rooms are ephemeral and disappear when their last member leaves or the process restarts; stored transfer packages are kept until their retention ends.

When the HTTPS reverse proxy connects from a non-loopback address, set
`EMOTELINK_TRUSTED_PROXY_IPS` to that proxy's exact peer IP (or a comma-separated
list of exact proxy IPs). This allows ASP.NET Core to accept `X-Forwarded-For`
only from those explicitly trusted peers, so per-client limits are not shared by
every user behind the proxy. Never set it
to an address that ordinary clients can originate from.

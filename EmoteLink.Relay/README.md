# EmoteLink Relay

A SignalR relay for synchronized animation starts, explicit room-member animation-mod transfers, community role labels, and a content-only animation manifest catalog. Transfers are limited to 75 MB each, expire after 10 minutes, and share a 25 GB storage budget. The relay also caps active reservations globally (4,096), per sender (32), and per room (128). Reserved bytes and counts are maintained atomically; under actual storage pressure, an eligible oldest transfer is tombstoned and removed before a new reservation is accepted. The relay does not transfer character data, appearance data, chat, or arbitrary game commands.

Versioned transfer starts compare the offered animation fingerprint with each recipient's room catalog. Members who already have the animation are treated as received without receiving a notification. Download, decline, room-close, and moderation actions revoke normal access and clear the room queue, but the uploaded package remains available to authorized moderators until its original ten-minute expiry. Storage pressure, relay shutdown/restart, or an administrative deletion can remove it sooner.

Upload and download capability tokens travel only in the `X-Synastry-Transfer-Token` request header, never in URLs. Transfer responses are marked `Cache-Control: no-store`. An upload is cancelled at its original expiry even if its request stalls. Offer notifications are delivered independently of the sender's HTTP cancellation, in parallel, with one recipient failure isolated from the others.

Deletion is a tracked lifecycle, not a best-effort disappearance. An administrative delete during an upload tombstones the transfer, revokes capabilities, cancels the body, and finishes deletion when the file handle closes. Failed deletions remain visible as `deletion-pending` and are retried. Startup logs report every orphan-package sweep and retain failed orphan deletions for background retry.

Room option selections can be offered as animation suggestions. Recipients may explicitly decline a suggestion, and the relay returns the named decline to the suggesting room member.

Animation role labels are shared within a room so clients with an empty local label can adopt one from another member. Clients retain their own existing labels, and private mods are not advertised by the plugin.

Accepted community role labels persist across rooms and relay restarts. The first submission accepts an initial label; five matching installation reports accept a later correction. Updated clients also register the human-readable mod and animation names used by the moderation UI.

Set `EMOTELINK_DATA_DIR` to place `emotelink-relay.db` on durable production storage. It does **not** relocate transfer packages: `.pmp` files remain in the operating system's temporary `emotelink-transfers` directory and are intentionally ephemeral. They expire after ten minutes and are removed sooner by storage pressure, administrative deletion, shutdown, or the next startup sweep. SQLite uses WAL mode, foreign keys, a five-second busy timeout, and forward-only schema migrations. On first startup, an existing `community-role-labels.json` is transactionally imported and checked for equivalent records and votes before the migration is accepted. The untouched source receives a unique, timestamped, checksum-verified `.pre-sqlite.*.bak` backup. SQLite is authoritative afterward. Set `EMOTELINK_WRITE_LEGACY_LABEL_JSON=true` only during a short rollback window if an additional compatibility mirror is required; it is off by default so label writes remain constant-time.

If an older relay writes to the legacy JSON during a rollback and SQLite is later restored, startup fails closed instead of silently choosing a side. Back up both files, manually reconcile them to the same semantic records and votes, set `EMOTELINK_RECONCILE_LEGACY_LABEL_JSON=accept-reconciled` for one startup, then remove that variable. The switch only acknowledges equality; it never overwrites either source.

The animation artifact catalog stores exact, versioned manifest signatures; bounded positive/negative reports; searchable display-name aliases; consensus and moderation overrides; and an optional versioned portable extraction payload. It does not store an owner, room, local directory, or private/public flag. Reporter deduplication hashes are derived independently for each artifact so they cannot be used to reconstruct one installation's mod inventory. Completed PMP uploads are safely inspected without extraction and may add a relay-verified candidate, but this never approves that content automatically. The private administration tool must explicitly choose and pin the exact payload SHA-256 before normal clients can hydrate it. A positive and negative report for the same exact signature is treated as a conflict until moderation resolves it. Non-animation classification requires three uncontested reports, remains untrusted community evidence, and is returned to normal clients as unknown until a moderator explicitly verifies it. Ordinary lookups also omit display-name aliases and private moderation notes. FTS5 trigram indexes provide bounded server-side searches across artifact aliases, community labels and pending tag votes, and durable transfer-ban names. Hard artifact and payload-byte budgets reject new catalog entries at capacity; no automatic evidence eviction occurs.

The catalog's `CatalogOnlyBlocked` policy is deliberately **non-enforcing**: it marks an artifact for catalog moderation but cannot block a package transfer because catalog artifact keys and transfer identifiers are different namespaces. Actual relay enforcement comes only from persistent transfer bans keyed by exact package SHA-256, animation catalog fingerprint, or normalized mod-family-name SHA-256.

Transfer audit history is capped by event count at the newest 100,000 records; it is not governed by a time-retention window. Each record may contain its transfer ID, event and timestamp, exact package SHA-256, animation catalog fingerprint, normalized mod-name SHA-256, and a bounded moderation note. It excludes package bytes, capability tokens, sender identity/display name, and room code. Administrator labels are SHA-256 hashed before persistence; plaintext administrator labels are never written to the audit table. Active package bytes are never copied into SQLite. Audit metadata can outlive the ten-minute package, while the reviewable `.pmp` cannot.

## Administration

Set a long random `EMOTELINK_ADMIN_TOKEN` in the relay service environment and restart it. The administration API listens separately on loopback port `25081` by default (`EMOTELINK_ADMIN_PORT` may change it), requires that local port plus a localhost peer and bearer token, and returns 404 on the public listener even when a valid token is supplied. Only `/admin/*` and `/health` are served on the admin listener. Access it remotely through an SSH tunnel such as `ssh -N -L 25081:127.0.0.1:25081 server`; never proxy or publicly expose the admin port.

The external administration application is intentionally maintained outside this public plugin repository. Its authenticated views cover community tags, catalog classifications/conflicts, live and retained transfers, and persistent sharing bans. Transfer lifecycle updates are available through a protected server-sent-event stream. Moderators can obtain a review copy during the retention window, revoke recipient access, delete evidence, resolve catalog conflicts, and create creator-opt-out bans by exact package hash, animation fingerprint, or normalized mod-family hash. Review downloads, access blocks, delete requests, and ban upserts/revocations are recorded in the hashed audit trail.

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

For internet use, place the relay behind HTTPS (for example Caddy or Cloudflare). Rooms are ephemeral and disappear when their last member leaves or the process restarts.

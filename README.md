# Synastry

![Synastry logo](images/icon.png)

Synastry is a Dalamud plugin for finding, organizing, sharing, and playing Penumbra animation mods.

It turns large animation collections into a practical launcher: browse mods, organize them into folders, collapse large option groups, label multi-actor roles, and activate animations without manually changing Penumbra settings.

## Community role labels

Multi-actor animation packs are often difficult to understand when their options are only named `Ground Sit 1`, `Ground Sit 2`, and so on. Synastry lets those activation buttons carry useful role names such as `Driver`, `Passenger`, `Top`, or `Bottom`.

Role labels are community-driven:

- Right-click an activation button to create or edit its label.
- Existing local labels are always respected and never overwritten.
- Non-private labels can be shared with other members of a room.
- Accepted community labels are available to anyone connected to Synastry.
- If a shared label is wrong, right-click it and submit a correction.
- Your correction applies locally immediately.
- The first submitted label becomes the community default immediately.
- Five matching installation reports replace an existing community label.
- Private mods never submit labels to the community database.

The goal is simple: once the community identifies the roles in a complicated animation pack, everyone else can use it without solving the same puzzle again.

## Mod sharing

If another room member does not have an animation, Synastry can offer the mod directly through the room. The recipient explicitly accepts or declines the transfer, and accepted packages are handed to Penumbra for installation.

Incoming offers stay in a retrieval queue even if its notification window is closed. The room panel shows **No Animations in the Cloud** when the queue is empty and a rainbow **You have Animations pending retrieval** button when one or more offers are waiting. Members whose room catalog already contains the animation are acknowledged automatically, do not see a notification, and do not keep an unnecessary relay transfer alive.

This makes coordinated animation sessions much easier: find an animation, share it with the people who need it, assign the roles, and start together from the same room.

When Penumbra finishes an accepted install, Synastry indexes only that mod and updates its room fingerprint. The row changes to orange or green as appropriate without a full library refresh.

- Transfers are limited to 75 MB.
- Transfer packages remain reviewable on the relay until their original 10-minute expiry, even after every recipient downloads or declines them. Recipient access is revoked as soon as an item leaves their queue.
- Authorized relay moderators may inspect a retained package during that window to investigate safety, policy, or creator opt-out reports. Packages can disappear sooner after a relay restart, storage pressure, or an administrative deletion.
- One-time upload and download capabilities are sent in a dedicated HTTP header, never placed in URLs, and transfer responses are marked not to be cached.
- Private mods cannot be advertised or sent.
- Mods are never transferred without the recipient accepting the offer.

Synastry is not intended for exchanging paid or otherwise restricted animations. Please support community creators and only share files you are permitted to share. A creator can ask the relay moderation team to block an exact package, animation fingerprint, or mod family from future sharing.

## Other features

- Automatically lists Penumbra mods containing animation files.
- Collapsible option groups support large packs with hundreds of choices.
- Create folders and move mods between them individually or in Shift-selected groups. Folders keep their open/closed state after a move, and mods sort alphabetically within each availability-color tier.
- Mark mods private from their right-click menu.
- Temporarily activate animations without disturbing unrelated Penumbra settings.
- Ready a room for synchronized playback or play an animation locally with **Solo**. Each member keeps their own prepared actor role, even when the roles come from different mod entries. Group playback sends the normal animation trigger once. **Auto EmoteSync** can be disabled in the room controls; when enabled, Synastry runs its lobby-only EmoteSync six seconds later.
- Use the footer to run lobby-only **EmoteSync** manually. When Simple Heels is loaded, **Temp Offset** opens `/heels temp` and **Livepose** opens `/heels livepose`.
- Right-click another player to send a room invitation they can accept or decline immediately.
- Align your character with a nearby target before playback.
- Optionally enable **Sit/doze anywhere** for furniture-free chair-sit and doze animations.
- Green, orange, purple, cyan, and white indicators show room availability, suggestions, and privacy state.

## Install

Add this URL under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```text
https://plugins.aethercast.org/
```

This combined repository contains **AetherPress** and **Synastry**. Save the
settings, open the Plugin Installer, and search for **Synastry**.

Use `/synastry` to open the plugin. To join a room directly, use:

```text
/synastry join ROOMCODE
```

## Privacy

Room matching uses animation fingerprints instead of local file paths. Community labels are keyed by opaque mod fingerprints and animation trigger IDs. Public label submissions include the mod and animation display names so the community database can be moderated; they do not include character names or local file paths.

Private mods are excluded from room catalogs, transfers, and community-label submissions.

To accelerate library refreshes, connected clients may contribute a versioned fingerprint of a mod's top-level Penumbra manifests, its display name, whether those manifests contain animation mappings, and a portable extraction result. The catalog does not store a character, room, local directory, private/public flag, or reusable cross-mod reporter identity. Private mods can contribute the same content-only index record, but they remain excluded from room advertising, transfers, and community-label submissions.

Completed transfer packages may also be inspected in place by the relay to create an exact catalog candidate; the package is never extracted into a mod directory. Candidate evidence is not automatically trusted. A moderator must approve the classification and pin the exact payload SHA-256 before another client can use that payload to accelerate its local index.

Transfer moderation is separate from the catalog. While a package is retained for up to 10 minutes, authorized moderators can see its transient sender/room metadata and obtain a review copy. Persistent sharing bans retain content hashes, a searchable display name, and the moderation reason; they do not retain the package. A catalog-only blocked marker is a moderation label and does not itself prevent transfers; enforced blocks are separate transfer-ban records keyed by an exact package SHA-256, animation fingerprint, or normalized mod-family-name SHA-256.

The moderation audit is bounded to the newest 100,000 events by count, not by age. An event may retain its transfer ID, event name and timestamp, exact package SHA-256, animation catalog fingerprint, normalized mod-name SHA-256, and a bounded moderation note. It excludes package bytes, transfer capability tokens, sender identity/display name, and room code. Administrator labels are hashed before storage. Audit metadata may therefore outlive the ten-minute review package.

## Build

Building the plugin requires the Dalamud development environment:

```powershell
dotnet build EmoteLink.slnx -c Release
```

The relay source and deployment notes are in `EmoteLink.Relay`.

Synastry is licensed under AGPL-3.0-or-later.

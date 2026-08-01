# EmoteLink

A deliberately small Dalamud plugin for browsing Penumbra mods, temporarily activating one animation mod at a time, and aligning your character to a nearby target.

## Install

Add this URL under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/ShaalaXIV/EmoteLink/main/repo.json
```

Save the settings, open the Plugin Installer, search for **EmoteLink**, and install it.

## Behavior

- `/emotelink` opens a plain window containing the Penumbra mod list.
- Only mods containing at least one physical `.pap` animation file are listed.
- Mods with Penumbra option groups can be expanded; selected options are saved and applied during temporary activation.
- Every expanded option has its own **Activate** button. It selects that option, reapplies the temporary assignment, and starts its detected animation or pose.
- Pose options are detected from their PAP paths. Clicking an idle, chair-sit, ground-sit, or doze option immediately applies it, enters the correct state, and cycles to the matching pose index.
- Each option shows its detected pose beside it. Click that pose label to manually choose Idle, Sit, GroundSit, Doze, and the pose index when a mod's metadata is unusual.
- Create and delete folders, reorder folders, and drag mods between or within folders. Organization is saved automatically. Deleting a folder moves its mods to **Uncategorized**.
- **Activate** clears this plugin's previous temporary assignments, enables the chosen mod temporarily with its existing options and at least priority 100, detects its affected emote through Penumbra, and starts that emote automatically.
- The temporary assignment remains active through the animation and is removed only after several consecutive frames of actual character translation. Turning in place, redraws, and pose-entry snaps do not clear it.
- **Clear temporary animations** immediately removes every assignment created and tracked by this plugin.
- **Align / teleport to target** walks to a target within 2 yalms, then snaps to its exact position and rotation. Player input cancels the walk.

The plugin intentionally does not remove temporary settings created by other plugins. Doing so would unexpectedly break unrelated tools.

## Group play

The built-in Linux relay provides Snowcloak-style persistent client/server callbacks, limited to animation synchronization. The client uses the official EmoteLink relay; users cannot select an alternate relay. Connect both plugins, create or join a short-code room, then select local options from the same mod. Options may differ between participants. Once at least two room members are ready with the same mod fingerprint, the relay sends every member the same 1.5-second relative countdown. Relative timing prevents differences between players' system clocks from causing multi-second playback skew.

No mod files, character appearance, chat, or arbitrary commands are sent. The fingerprint contains only the normalized mod name and a hash of its redirected PAP game paths.

## Build

Requires the Dalamud development environment, then:

```powershell
dotnet build EmoteLink.slnx -c Release
```

The alignment implementation is adapted from [IcarusXIV/Encore](https://github.com/IcarusXIV/Encore). This project is therefore licensed under AGPL-3.0-or-later.

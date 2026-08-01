# Radar Missile Control

A BepInEx 5/Harmony mod for Nuclear Option 0.34.1 that lets the host configure which semi-active and active radar-homing missiles are weakened or disabled, who launched them, who they target, and how strong each nerf is.

## Exact scope

The mod recognizes missiles by Nuclear Option's `SARHSeeker` and `ARHSeeker` components, not by weapon names. The host can independently select SARH and ARH, then select any combination of:

- AI launcher -> Player target
- AI launcher -> AI target
- Player launcher -> AI target
- Player launcher -> Player target

The safe default remains SARH + ARH and AI -> Player only, so human-fired missiles remain vanilla unless the host explicitly enables a player-launch route. ARAD/ARM, IR, laser, optical, inertial, and ballistic missile types are never changed.

## Modes

- `Vanilla`: makes no gameplay changes while leaving the plugin loaded.
- `Nerfed` (default): uses separate host-adjustable percentages for radar tracking strength, lock persistence, jam tolerance, motor thrust, and top-speed limits. Added moving aim error is adjustable from 0-10% of remaining range and is clamped to 25-400 meters. Defaults are 50% for the five strength values and 4% aim error. Warhead damage remains completely vanilla.
- `Disabled`: prevents new selected launches on enabled engagement routes. Matching missiles already in flight are disarmed, have zero blast and penetration damage, and stop receiving seeker guidance.

Only the authoritative host/server controls gameplay. Press `F7` to open the host settings panel and stage changes, then use `Apply & Save`. Press `F8` to cycle `OFF (VANILLA)` -> `NERFED` -> `DISABLED` -> `OFF (VANILLA)` without opening the panel. The off state restores original values and saved targets for affected airborne missiles. Client control attempts are rejected with an on-screen notice. All settings and shortcuts are stored in:

```text
BepInEx\config\blacknight2u.sarhcontrol.cfg
```

## NOMM and local installation

The package contains rich `meta.json` metadata for NOMM: display name, gameplay summary, tags, author, version, and package type. Extract the package into:

```text
Nuclear Option\BepInEx\plugins\blacknight2u.sarhcontrol\
```

Or close Nuclear Option and run:

```powershell
.\scripts\Install-Local.ps1
```

NOMM can enable or disable the whole plugin. Changes made through NOMM take effect the next time the game starts; the host's `F7` panel and `F8` mode toggle work live after the plugin has loaded.

NOMM gets the human-readable Library title, author, description, tags, and release details from the online NOMNOM registry. `Build-Package.ps1` generates the ready-to-submit registry file at:

```text
artifacts\blacknight2u.sarhcontrol.json
```

The registry entry targets this open-source repository and its GitHub release assets. Until the NOMNOM registry submission is merged and deployed, friends can install the release ZIP manually using the extraction path above; NOMM will still detect and manage the installed plugin by its ID.

## Toggle text

A small upper-right notice appears for about 3.5 seconds only when the mode or settings are changed:

- Host example: `RADAR MISSILES: NERFED | SARH+ARH | AI>PLAYER`
- Client after the host changes mode: the same notice followed by `(HOST)`
- Client pressing `F7` or `F8`: a short notice that only the host/server can control the settings

There is no persistent status HUD and no notice during ordinary gameplay or initial synchronization. The full panel is visible only while the host has it open.

## Multiplayer

Only the host or dedicated server applies gameplay changes. Clients announce the plugin to the server, and the server sends its current mode, missile selection, engagement routes, and nerf values only to those modded clients. This keeps control host-only while allowing every modded client to see actual host changes.

For synchronized notices, the host/server and participating clients should all run the same plugin version. Human-fired SARH and ARH missiles, including hostile-player missiles in PvP, remain unchanged under the default settings but can be selected explicitly by the host.

## Build

Prerequisites:

- Nuclear Option installed through Steam
- BepInEx 5 installed in the game folder
- .NET SDK 9 or newer

Run:

```powershell
.\scripts\Build-Package.ps1
```

The NOMM-ready archive is written to `artifacts/SARHControl-v0.5.0.zip`.

Game updates can change internal type or field names. Rebuild and retest against the current `Assembly-CSharp.dll` after a Nuclear Option update.

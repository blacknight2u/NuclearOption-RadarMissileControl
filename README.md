# Radar Missile Control

A BepInEx 5/Harmony mod for Nuclear Option 0.33.4 that weakens or disables AI-launched semi-active and active radar-homing missiles aimed at human players.

## Exact scope

The mod recognizes missiles by Nuclear Option's `SARHSeeker` and `ARHSeeker` components, not by weapon names. A missile is affected only when both conditions are true:

1. The launching unit is not a human-controlled aircraft.
2. The target is a human-controlled aircraft.

Human player-launched SARH and ARH missiles remain completely vanilla. ARAD/ARM, IR, laser, optical, inertial, and ballistic missile types are not changed.

## Modes

- `Vanilla`: makes no gameplay changes while leaving the plugin loaded.
- `Nerf50` (default): reduces affected SARH and ARH missiles to 50% radar tracking strength, 50% lock persistence, 50% motor thrust, and 50% of each motor's top-speed limit. It also halves jam tolerance and adds a moving aim error equal to 4% of remaining range, clamped to 25-400 meters. Warhead damage remains completely vanilla.
- `Disabled`: prevents new affected launches. Affected SARH and ARH missiles already in flight are disarmed, have zero blast and penetration damage, and stop receiving seeker guidance.

Only the authoritative host/server controls the mode. The host can press `F8` in game to cycle `OFF (VANILLA)` -> `NERFED (50%)` -> `DISABLED` -> `OFF (VANILLA)`. The off state restores normal game behavior. Client toggle attempts are rejected with an on-screen notice. The host mode and shortcut are also stored in:

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

NOMM can enable or disable the whole plugin. Changes made through NOMM take effect the next time the game starts; the host's `F8` mode toggle works live after the plugin has loaded.

NOMM gets the human-readable Library title, author, description, tags, and release details from the online NOMNOM registry. `Build-Package.ps1` generates the ready-to-submit registry file at:

```text
artifacts\blacknight2u.sarhcontrol.json
```

The registry entry targets this open-source repository and its GitHub release assets. Until the NOMNOM registry submission is merged and deployed, friends can install the release ZIP manually using the extraction path above; NOMM will still detect and manage the installed plugin by its ID.

## Toggle text

A small upper-right notice appears for about 3.5 seconds only when the mode is toggled:

- Host: `RADAR MISSILES: OFF (VANILLA)`, `RADAR MISSILES: NERFED (50%)`, or `RADAR MISSILES: DISABLED`
- Client after the host changes mode: the same notice followed by `(HOST)`
- Client pressing `F8`: a short notice that only the host/server can change the mode

There is no persistent status HUD and no notice during ordinary gameplay or initial synchronization.

## Multiplayer

Only the host or dedicated server applies gameplay changes. Clients announce the plugin to the server, and the server sends its current mode only to those modded clients. This keeps control host-only while allowing every modded client to see actual host toggle changes.

For synchronized toggle notices, the host/server and participating clients should all run the same plugin version. Human-fired SARH and ARH missiles, including hostile-player missiles in PvP, remain unchanged.

## Build

Prerequisites:

- Nuclear Option installed through Steam
- BepInEx 5 installed in the game folder
- .NET SDK 9 or newer

Run:

```powershell
.\scripts\Build-Package.ps1
```

The NOMM-ready archive is written to `artifacts/SARHControl-v0.4.1.zip`.

Game updates can change internal type or field names. Rebuild and retest against the current `Assembly-CSharp.dll` after a Nuclear Option update.

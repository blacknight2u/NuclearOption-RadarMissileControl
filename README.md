# Radar Missile Control

A BepInEx 5/Harmony mod for Nuclear Option 0.34.1 that lets the host configure two radar-missile presets: what they affect and the gameplay values each preset applies.

## Exact scope

The mod recognizes missiles by Nuclear Option's `SARHSeeker` and `ARHSeeker` components, not by weapon names. The host can independently select SARH and ARH, then select any combination of:

- AI launcher -> Player target
- AI launcher -> AI target
- Player launcher -> AI target
- Player launcher -> Player target

The safe default remains SARH + ARH and AI -> Player only, so human-fired missiles remain vanilla unless the host explicitly enables a player-launch route. ARAD/ARM, IR, laser, optical, inertial, and ballistic missile types are never changed.

## Presets

- `Vanilla`: makes no gameplay changes while leaving the plugin loaded.
- `Preset 1`: defaults to 50% values for SARH+ARH missiles on AI-to-player engagements.
- `Preset 2`: defaults to zero on every slider (off) and uses the same controls.

Both presets independently store the same missile types, engagement routes, and tuning values. Neither preset has a hidden effect or special behavior. F8 only selects `VANILLA`, `PRESET 1`, or `PRESET 2`. Warhead damage remains vanilla.

Only the authoritative host/server controls gameplay. Press `F7` to edit Preset 1 or Preset 2, then use `Apply & Save`. Press `F8` to cycle `VANILLA` -> `PRESET 1` -> `PRESET 2` -> `VANILLA`. Client control attempts are rejected with an on-screen notice. Existing v0.5.0 settings migrate into Preset 1 automatically; Preset 2 starts with zeroed slider defaults and the same controls. All settings and shortcuts are stored in:

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

NOMM can enable or disable the whole plugin. Changes made through NOMM take effect the next time the game starts; the host's `F7` panel and `F8` preset cycle work live after the plugin has loaded.

NOMM gets the human-readable Library title, author, description, tags, and release details from the online NOMNOM registry. `Build-Package.ps1` generates the ready-to-submit registry file at:

```text
artifacts\blacknight2u.sarhcontrol.json
```

The registry entry targets this open-source repository and its GitHub release assets. Until a release and its exact artifact hash are added to the NOMNOM registry, a manually installed development build may appear under the fallback DLL name `SARHControl`. Once the matching registry update is deployed, NOMM displays `Radar Missile Control` and the full store description.

## Toggle text

A small centered line appears for about 3.5 seconds when F8 changes the active preset:

- F8 shows only `RADAR CONTROL: VANILLA`, `RADAR CONTROL: PRESET 1`, or `RADAR CONTROL: PRESET 2` as brief plain text.
- Modded clients see the same text when the host changes the active preset.
- Client pressing `F7` or `F8`: a short notice that only the host/server can control the settings

There is no persistent status HUD and no preset notice for slider edits, ordinary gameplay, or initial synchronization. The full panel is visible only while the host has it open.

## Multiplayer

Only the host or dedicated server applies gameplay changes. Clients announce the plugin to the server, and the server sends the active F8 preset only to those modded clients for the transient status line. Missile selection, engagement routes, and tuning values remain authoritative host-side state.

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

The NOMM-ready archive is written to `artifacts/SARHControl-v0.5.1.zip`.

Game updates can change internal type or field names. Rebuild and retest against the current `Assembly-CSharp.dll` after a Nuclear Option update.

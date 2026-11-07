# Investigation notes

## Nuclear Option 0.34.6 compatibility (2026-11-07)

- Steam build 26327221; `Assembly-CSharp.dll` SHA-256
  `babdd626bbab5f01e6c244150c1e77c57c131ee86967214e2e5ced6d813c2bce`.
- v0.5.3 updates version and compatibility metadata. Gameplay code, configuration
  migration, authority rules and protocol version 6 are unchanged from v0.5.2.
- Release build with warnings treated as errors: zero warnings and errors.
- `scripts/Test-GameCompatibility.ps1` checks all seven Harmony targets and hook
  arguments, all ten reflected fields, and 28 compiled game/network references.
  All checks pass. The detailed report is generated under `artifacts/compatibility`.
- `Build-Package.ps1` runs the audit before packaging and refuses to overwrite an
  existing release archive.
- These are build/static checks. No 0.34.6 flight or multiplayer session was run.

## Earlier verification

Environment verified on 2026-08-30:

- Nuclear Option 0.34.2 (Steam build 24724372)
- Unity 2022.3.62f2, Mono scripting backend
- BepInEx 5.4.23.4
- Harmony 2.9.0

0.34.2 compatibility audit:

- Reverified against the currently installed Steam build 24724372. `Assembly-CSharp.dll` SHA-256 is `eb3b93bdaec37dd7b3bab72f801a2c84e5be2ae3c559f39251e2320ae6b11ccc`; the build and every reflected field/Harmony target below remain valid.
- v0.5.2 changes version and compatibility metadata only; gameplay behavior and multiplayer protocol remain unchanged from v0.5.1.
- v0.5.1 loaded on 0.34.2 without plugin, Harmony, missing-method, or type-load errors.
- A clean warnings-as-errors build against the updated game assemblies completed with zero warnings and zero errors.
- All Harmony targets retain their expected signatures: `SARHSeeker.Initialize`, `SARHSeeker.Seek`, `SARHSeeker.GetTrackingStrength`, `ARHSeeker.Initialize`, `ARHSeeker.Seek`, `ARHSeeker.GetRadarReturn`, and `Missile.SetAimpoint`.
- All reflected fields retain their expected declaring types and value types: seeker missile/target references; SARH/ARH persistence, jamming, and scan-time fields; the missile motor array; and motor thrust/top-speed fields.
- The published v0.5.1 DLL and a fresh 0.34.2 build have identical normalized IL and assembly-reference identities. Their file hashes differ only because rebuild-specific module/debug metadata changed, so no replacement DLL is required.

Relevant game flow:

1. SARH missiles use `SARHSeeker` and active-radar missiles use `ARHSeeker`; their seeker type strings are `SARH` and `ARH`.
2. Both seeker `Initialize(Unit target, GlobalPosition aimpoint)` methods receive the intended target after `Missile.owner` is resolved.
3. A human-controlled aircraft is identified by `Aircraft.Player != null`.
4. `SARHSeeker.GetTrackingStrength(Unit)` calculates radar tracking strength; `lockPersistence` and `jamTolerance` store seeker persistence and resistance to jamming.
5. `ARHSeeker.GetRadarReturn()` performs a fresh onboard-radar scan every 0.5 seconds and otherwise returns its cached strength. Its corresponding fields are `lockPerseverance` and `jamTolerance`.
6. `Missile` stores its private motor array in `motors`; each motor exposes `thrust` and `topSpeed` fields used by the authoritative flight simulation.
7. Both seekers supply guidance through `Missile.SetAimpoint(GlobalPosition, Vector3)`, which provides a narrow point for adding aim error only to registered affected missiles.
8. Both seeker `Seek()` methods are called from the authoritative missile simulation and are narrow hooks for applying or restoring the selected preset.

Chosen behavior:

- The host selects SARH and/or ARH plus any combination of AI/Player launcher-to-target routes independently for Preset 1 and Preset 2. Scope defaults remain SARH + ARH and AI -> Player only.
- Both presets use the same code and independently scale tracking strength, lock persistence, jam tolerance, motor thrust, and motor top-speed limits from 0-100% of vanilla.
- Both presets add configurable moving aim error from 0-10% of remaining range, clamped to 25-400 meters.
- Preset 1 defaults to 50% for the five remaining-strength values and 4% aim error. Preset 2 defaults to zero for every slider, providing the visible user-configured off preset without hidden behavior.
- The host `F7` panel stages and applies settings; `F8` cycles `VANILLA` -> `PRESET 1` -> `PRESET 2` -> `VANILLA`. Leaving an active preset or deselecting a route restores original seeker and motor values for an affected missile already airborne.
- Player-launched SARH/ARH missiles remain unchanged by default but can be selected explicitly. Every other seeker type is unchanged.
- `NetworkManagerNuclearOption.i.Server.Active` is the authority check; client gameplay patches are no-ops.
- Mirage hello/status messages use manually registered compact serializers and synchronize only the active preset to modded clients for F8 notices.
- The client hello is an opt-in handshake, so the server sends status only to clients known to have the plugin.
- ARH return strength is scaled only on fresh 0.5-second scans so the cached signal is not repeatedly scaled every frame.
- The host-only IMGUI panel uses staged Apply/Revert/Defaults behavior. F8 does not discard staged edits. Preset status text remains transient and appears only for actual F8 changes, not slider edits or initial synchronization.

The project references the locally installed game and BepInEx assemblies but does not redistribute them.

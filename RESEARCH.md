# Investigation notes

Environment inspected on 2026-07-26:

- Nuclear Option 0.33.4, Steam build 23670157
- Unity 2022.3.62f2, Mono scripting backend
- BepInEx 5.4.23.4
- Harmony 2.9.0

Relevant game flow:

1. SARH missiles use `SARHSeeker` and active-radar missiles use `ARHSeeker`; their seeker type strings are `SARH` and `ARH`.
2. Both seeker `Initialize(Unit target, GlobalPosition aimpoint)` methods receive the intended target after `Missile.owner` is resolved.
3. A human-controlled aircraft is identified by `Aircraft.Player != null`.
4. `MissileLauncher.Fire(Unit owner, Unit target, ...)` is the earliest narrow hook that can prevent selected SARH/ARH launches before ammunition is consumed.
5. `SARHSeeker.GetTrackingStrength(Unit)` calculates radar tracking strength; `lockPersistence` and `jamTolerance` store seeker persistence and resistance to jamming.
6. `ARHSeeker.GetRadarReturn()` performs a fresh onboard-radar scan every 0.5 seconds and otherwise returns its cached strength. Its corresponding fields are `lockPerseverance` and `jamTolerance`.
7. `Missile` stores its private motor array in `motors`; each motor exposes `thrust` and `topSpeed` fields used by the authoritative flight simulation.
8. Both seekers supply guidance through `Missile.SetAimpoint(GlobalPosition, Vector3)`, which provides a narrow point for adding aim error only to registered affected missiles.
9. `Missile` stores warhead strength in the private `blastYield` and `pierceDamage` instance fields. These remain unchanged in `Nerfed` and are touched only to neutralize airborne missiles in `Disabled` mode.
10. Both seeker `Seek()` methods are called from the authoritative missile simulation and are narrow hooks for changing missiles already airborne.

Chosen behavior:

- The host selects SARH and/or ARH plus any combination of AI/Player launcher-to-target routes. Defaults remain SARH + ARH and AI -> Player only.
- `Nerfed` preserves full warhead damage while independently scaling tracking strength, lock persistence, jam tolerance, motor thrust, and motor top-speed limits from 0-100% of vanilla. Each defaults to 50%.
- `Nerfed` adds configurable moving aim error from 0-10% of remaining range, clamped to 25-400 meters and defaulting to 4%.
- Disabled mode blocks future launches and neutralizes affected airborne missiles.
- The host `F7` panel stages and applies settings; `F8` cycles `OFF (VANILLA)` -> `NERFED` -> `DISABLED` -> `OFF (VANILLA)`. Leaving Disabled or deselecting a route restores the saved target and original values for an affected missile already airborne.
- Player-launched SARH/ARH missiles remain unchanged by default but can be selected explicitly. Every other seeker type is unchanged.
- `NetworkManagerNuclearOption.i.Server.Active` is the authority check; client gameplay patches are no-ops.
- Mirage hello/status messages use manually registered compact serializers and synchronize the mode, seeker mask, engagement mask, and six nerf values to modded clients for notices.
- The client hello is an opt-in handshake, so the server sends status only to clients known to have the plugin.
- ARH return strength is scaled only on fresh 0.5-second scans so the cached signal is not repeatedly scaled every frame.
- The host-only IMGUI panel uses staged Apply/Revert/Defaults behavior. Status text remains transient and appears only for applied changes or actual synchronized host changes.

The project references the locally installed game and BepInEx assemblies but does not redistribute them.

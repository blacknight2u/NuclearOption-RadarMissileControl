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
4. `MissileLauncher.Fire(Unit owner, Unit target, ...)` is the earliest narrow hook that can prevent only AI-to-player SARH/ARH launches.
5. `SARHSeeker.GetTrackingStrength(Unit)` calculates radar tracking strength; `lockPersistence` and `jamTolerance` store seeker persistence and resistance to jamming.
6. `ARHSeeker.GetRadarReturn()` performs a fresh onboard-radar scan every 0.5 seconds and otherwise returns its cached strength. Its corresponding fields are `lockPerseverance` and `jamTolerance`.
7. `Missile` stores its private motor array in `motors`; each motor exposes `thrust` and `topSpeed` fields used by the authoritative flight simulation.
8. Both seekers supply guidance through `Missile.SetAimpoint(GlobalPosition, Vector3)`, which provides a narrow point for adding aim error only to registered affected missiles.
9. `Missile` stores warhead strength in the private `blastYield` and `pierceDamage` instance fields. These remain unchanged in `Nerf50` and are touched only to neutralize airborne missiles in `Disabled` mode.
10. Both seeker `Seek()` methods are called from the authoritative missile simulation and are narrow hooks for changing missiles already airborne.

Chosen behavior:

- Scope changes to AI-launched SARH and ARH missiles whose target is a human-controlled aircraft.
- `Nerf50` preserves full warhead damage while applying 50% tracking strength, lock persistence, jam tolerance, motor thrust, and motor top-speed limits.
- `Nerf50` adds a moving aim error equal to 4% of remaining range, clamped to 25-400 meters.
- Disabled mode blocks future launches and neutralizes affected airborne missiles.
- The host `F8` cycle is `OFF (VANILLA)` -> `NERFED (50%)` -> `DISABLED` -> `OFF (VANILLA)`; leaving Disabled restores the saved target for an affected missile already airborne.
- Player-launched SARH/ARH missiles and every other seeker type are unchanged.
- `NetworkManagerNuclearOption.i.Server.Active` is the authority check; client gameplay patches are no-ops.
- Mirage hello/status messages use manually registered compact serializers.
- The client hello is an opt-in handshake, so the server sends status only to clients known to have the plugin.
- ARH return strength is halved only on fresh 0.5-second scans so the cached signal is not repeatedly halved every frame.
- IMGUI text is transient and appears only for toggle actions or actual synchronized host mode changes.

The project references the locally installed game and BepInEx assemblies but does not redistribute them.

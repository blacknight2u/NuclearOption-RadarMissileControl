# Radar Missile Control maintenance

This repository contains the complete plugin source. No Blender, Unity authoring
project, external model, or past Codex session is needed.

- Read README.md and RESEARCH.md before editing.
- Build against the actual local Nuclear Option and BepInEx 5 assemblies using
  `scripts/Build-Package.ps1 -GameDir <game directory>`.
- Keep the version in SARHControl.csproj, src/Plugin.cs and README.md aligned.
  Update the supported game version in scripts/Build-Package.ps1 and README.md.
- The package script checks Harmony hooks, reflected fields and compiled game
  references before packaging. Generated files belong under artifacts/.
- Never commit game assemblies, installed plugins or user config/log files.
- Preserve host authority, protocol compatibility, presets and configuration
  migration unless the task explicitly changes those behaviors.
- Never replace an already published release ZIP. NOMNOM imports new releases;
  supported game-version tags use its dedicated update issue template.
- Record exactly which build/static/runtime/flight/multiplayer checks were run.

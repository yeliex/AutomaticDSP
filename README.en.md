# AutomaticDSP

[简体中文](README.md) | English

AutomaticDSP is an automation mod for *Dyson Sphere Program*. It enables external AI agents to query game state through a local API and submit in-game tasks for sequential execution.

This repository currently includes design documents, a BepInEx project scaffold, and the M1 implementation for read-only game state observation:

- Requirements: [docs/requirements.md](docs/requirements.md)
- Technical design: [docs/technical-design.md](docs/technical-design.md)
- Development plan: [docs/development-plan.md](docs/development-plan.md)
- API documentation: [docs/api.md](docs/api.md)
- Query syntax: [docs/query-syntax.md](docs/query-syntax.md)
- Getting started with an AI agent: [docs/ai-agent-getting-started.md](docs/ai-agent-getting-started.md)
- AI agent skill: [AutomaticDSP](skills/automatic-dsp/SKILL.md), covering gameplay, planning guidance, and separately maintained API references
- Starter production line verification: [docs/verification-startline.md](docs/verification-startline.md)
- BepInEx project scaffold: [src/AutomaticDSP](src/AutomaticDSP)

The linked documentation and skill are currently written in Simplified Chinese.

## Install the AI agent skill

Install the skill on the machine running your agent and the mod on the Windows machine running the game. These may be the same machine.

For Codex, copy the entire `skills/automatic-dsp` directory from this repository to `$CODEX_HOME/skills/automatic-dsp`, or `~/.codex/skills/automatic-dsp` if `CODEX_HOME` is unset. Keep `SKILL.md`, `references/`, `scripts/`, and the other skill files together. Invoke `$automatic-dsp` in your next conversation turn. For other agents, use their supported skill installation directory.

Alternatively, send this prompt to Codex with this repository open:

```text
Install skills/automatic-dsp from this repository into my Codex user skill directory.
Use CODEX_HOME if set, otherwise ~/.codex, and copy the entire skill directory.
If a skill with the same name already exists, compare the differences and preserve local changes.
Confirm the installation path and explain how to invoke automatic-dsp.
```

## AI prompt for mod installation and initialization

Send the following prompt to an agent with access to the game host's filesystem, terminal, and game UI. Replace the bracketed fields. Building requires the game and BepInEx assemblies, plus a build environment that supports targeting .NET Framework 4.7.2.

```text
Install and initialize AutomaticDSP so an AI agent can query and control Dyson Sphere Program.

Project: the current AutomaticDSP repository; if unavailable, obtain it from https://github.com/yeliex/AutomaticDSP.
Game host: [local Windows machine / remote Windows host and configured access method]
Game directory: [discover automatically / absolute path]
Session: [continue the current game / load a named save / new game with seed, star count,
          resource multiplier, peace or combat mode, and whether to skip the prologue]
Agent and game on the same machine: [yes / no, provide the game host address reachable by the agent]

Read the README, src/AutomaticDSP/AutomaticDSP.csproj, and the skill's
references/mod-usage.md, references/guides/initialization.md, and references/interface/game.md to confirm installation and API requirements.

1. Check the game directory, BepInEx, and .NET build environment. If BepInEx is missing,
   install a compatible version from an official release or a trusted DSP distribution,
   preserving existing mods and configuration.
2. Build using the actual game directory:
   dotnet build ./src/AutomaticDSP/AutomaticDSP.csproj -p:DSPGameDir="actual game directory"
3. With the game closed, deploy the mod DLL, runtime dependencies, and native dependency
   subdirectories from src/AutomaticDSP/bin/Debug/net472 into BepInEx/plugins/AutomaticDSP
   under the game directory. Preserve the dependency directory structure.
4. Launch the game and check the BepInEx logs to confirm AutomaticDSP loaded successfully.
   Confirm the connection address using the Mod usage guide. For remote access, configure
   the required listener and access scope.
5. Request GET /game from the agent's machine to verify connectivity. Continue, load, or
   create the session specified above; ask if the session requirements are unclear.
   Wait for ready to become true, then verify the save and runtime state.
6. Use the query documentation to read game, player, and inventory state.
   Confirm game time advances when tasks need to progress.

Report the deployment path, connection address, current session, and initialization results.
If anything fails, report the specific cause and the steps still incomplete.
```

## Mod usage

See the [Mod usage guide](skills/automatic-dsp/references/mod-usage.md) (Simplified Chinese) for installation, building, the default service address, configuration, remote access, and runtime data. The guide links to three API references with request parameters and examples.

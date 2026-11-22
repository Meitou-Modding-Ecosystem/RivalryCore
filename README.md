# Meitou mods

Rivalry Core (`rivalry.core`), a framework for [MeitouClient](https://github.com/Meitou-Modding-Ecosystem/MeitouClient) code mods:
[docs/rivalry-core.md](docs/rivalry-core.md). GPL-3.0-or-later.

Mods that lived here now have their own repos: [AshenHand](https://github.com/Meitou-Modding-Ecosystem/AshenHand),
[Husbandry](https://github.com/Meitou-Modding-Ecosystem/Husbandry), [SquadArena](https://github.com/Meitou-Modding-Ecosystem/SquadArena)
(squad rating in Dust-bandit equivalents) and [LogisticsCore](https://github.com/Meitou-Modding-Ecosystem/LogisticsCore) (vanilla node seeding). New mods start from [MeitouModTemplate](https://github.com/Meitou-Modding-Ecosystem/MeitouModTemplate).

Building a mod on Rivalry Core: [docs/depending-on-rivalry-core.md](docs/depending-on-rivalry-core.md) (agent skill:
`.claude/skills/rivalry-core-dependent`). Planned work: [docs/roadmap.md](docs/roadmap.md). Factions and where their squads come from: [docs/factions.md](docs/factions.md).

Works alongside: [The Wasteland Remembers](docs/wasteland-remembers.md) (a separate mod on the Steam Workshop; town heat, bounty hunters, rivals,
outlaws and town talk, with a public API: [docs/wasteland-remembers-api.md](docs/wasteland-remembers-api.md)).

## Layout

- `engine/`: MeitouClient as a git submodule, pinned to the commit the mods build against: the head of the fork's `fork` branch (upstream
  plus fork-only CI and mod API).
- `<Mod>/`: the mod's project and `meitou.json`. `<Mod>.Tests/`: its tests (synthetic worlds, no game install needed).
- `Directory.Build.props` imports the engine's, so mods build with the game's settings (warnings are errors).

## Build

```
git clone --recurse-submodules <this repo>
dotnet build
dotnet test
```

After a pull that moves the engine pin: `git submodule update --init`.

One click from Visual Studio: open `RivalryCore.slnx` and press F5 on the `Play` project; it builds the engine and mods, copies the mods
into the game and runs it ([docs/play.md](docs/play.md)).

A clone that won't build (empty `engine/`, wrong branch):

```
git fetch origin
git checkout beta
git pull
git submodule update --init --recursive
```

Pulling upstream MeitouClient in: `pwsh tools/Sync-Upstream.ps1` (upstream -> engine `master` -> `fork` -> `beta` -> `main`, one PR per step;
run it again after each merge, `-Abort` drops an unfinished merge; details at the top of the script).

## Branches and releases

How to contribute, add a mod and who owns what: [CONTRIBUTING.md](CONTRIBUTING.md).

- Work goes in PRs to `beta`; CI (build and tests) must pass. `beta` is merged into `main` once it has been tested in play. Production is promoted through a PR to `release`, with two approvals including a code owner.
- A mod is released by pushing a tag `<mod-slug>-v<version>` on `release` (e.g. `rivalry-core-v0.2.0`; slugs in
  `.github/workflows/release.yml`). CI builds that mod and publishes `<Mod>-<version>.zip`, holding the folder to drop into `Kenshi/mods/`.
  The version must match the mod's `meitou.json`.
- Rivalry Core is also published from `beta` as prereleases (`rivalry-core-beta` and one `rivalry-core-<version>-beta.<commit>` per change)
  for mods in other repos to test against: [docs/depending-on-rivalry-core.md](docs/depending-on-rivalry-core.md#1-get-it-and-reference-it-at-build-time-never-ship-it).
- A mod folder also needs a `<Mod>.mod` file. The packaging workflow (GutenStructure `package-meitou-mod.yml`) adds an empty one to a
  mod without data (`empty-data-mod`) and refuses to pack a mod whose `.mod` is missing otherwise.
  `.mod` files are git-ignored: one built from the installed game is generated on the player's machine.

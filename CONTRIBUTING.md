# Contributing

Code mods for MeitouClient, built by several authors in one repo. Each mod has an owner (see [.github/CODEOWNERS](.github/CODEOWNERS)).

## Where a change goes

- A mod's code, data generator, tests and docs: this repo, in the mod's folders.
- An engine hook a mod needs (a new event, a saved field, a command): a PR to the `fork` branch of
  [MeitouClient](https://github.com/Meitou-Modding-Ecosystem/MeitouClient), then a PR here that moves the `engine/` pin.
  Never commit inside `engine/`. Hooks follow that repo's `docs/modding.md` contract.
- A question or idea: open an issue first when it touches another author's mod or the engine.

## Workflow

1. Branch from `beta` (`git checkout beta && git pull && git submodule update --init --recursive`).
2. One piece of work per branch and PR, into `beta`. Fill in the PR template.
3. CI (build and tests) must pass, and a code owner of every folder you touch must approve (a maintainer reviews an owner's own PR).
   Then the author merges.
4. Maintainers merge `beta` into `main` once it has been tested in play, then tag releases (see [README](README.md#branches-and-releases)).

Don't push to `beta` or `main` directly, rewrite their history, or merge another author's open PR.

## Adding a mod

- `<Mod>/` with `<Mod>.csproj` and `meitou.json` (id `<author-or-mod>.<name>`, SemVer `version`, `apiVersion` 2), `<Mod>.Tests/` for
  its tests, `docs/<mod-slug>.md` for its design and API.
- The version lives only in `meitou.json`: `Directory.Build.props` gives the assembly that version, so never set `<Version>` in a `.csproj`.
- Register it: a row in the README table, a folder in `RivalryCore.slnx`, a slug line in `.github/workflows/release.yml`, and an owner line
  in `.github/CODEOWNERS`.
- Depend on another mod only through its public API and a `dependencies` range in `meitou.json` (e.g. Rivalry Core's `IRivalryApi`, see
  [docs/depending-on-rivalry-core.md](docs/depending-on-rivalry-core.md)), never its internals. A breaking API change bumps that mod's minor version (major after 1.0) and updates its dependents in the same PR.

## Rules

- Code follows the engine's style: its `.editorconfig` and the "Code style" section of its `CLAUDE.md`; warnings are errors.
- Mods must stay deterministic: RNG through the engine's hashed RNG, no wall-clock time, no `World` access outside the tick
  (engine `docs/modding.md`).
- Never commit original game files or data dumps. `.mod` files are git-ignored; a mod built from game data generates its `.mod` on the
  player's machine (see `AshenHand.Data` in the [AshenHand](https://github.com/Meitou-Modding-Ecosystem/AshenHand) repo).
- Tests use synthetic worlds; a test that needs the game install skips when it is missing.
- Contributions are licensed GPL-3.0-or-later, like the repo.

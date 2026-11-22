# Depending on Rivalry Core

How a code mod builds on Rivalry Core (`rivalry.core`): reference it, declare it, register rules, and send parties within its caps. Contract
and details: [rivalry-core.md](rivalry-core.md); loader rules: engine
[modding-dependencies.md](https://github.com/Meitou-Modding-Ecosystem/MeitouClient/blob/master/docs/modding-dependencies.md). An agent skill
walks through the same steps: [.claude/skills/rivalry-core-dependent](../.claude/skills/rivalry-core-dependent/SKILL.md).

## 1. Get it and reference it at build time, never ship it

| Source | Use it for | Where |
| --- | --- | --- |
| Framework lock (recommended) | a mod repo of the org: builds and tests against a release, moved automatically on new ones | `meitou.lock.json` naming a release or beta zip; see below |
| Commit-pinned source | building against current `beta` before any release | this repo as a git submodule of yours (`deps/RivalryCore`), pinned to a commit |
| Beta zip | testers' installs; a DLL to compile against without a submodule | prereleases `rivalry-core-<version>-beta.<commit>` (one per beta commit that changes the framework, never moved) and `rivalry-core-beta` (always the newest) |
| Release zip | published mods | releases `rivalry-core-v<version>`, tagged on `release` |

In every case:

- Reference Rivalry Core with `Private="false"`, and never list `RivalryCore.dll` in your own `assemblies`: the loader refuses the mod. A
  stray copy in your folder is ignored; the loaded `rivalry.core` serves the types, so every dependent shares one framework, one grudge
  ledger and one set of party caps.
- Build against the same engine commit Rivalry Core was built against: the `engine/` pin of the RivalryCore commit, which every zip's
  release notes name.
- The version lives only in `RivalryCore/meitou.json`; the DLL takes it at build time. A beta zip carries the manifest's version, so your
  `dependencies` range accepts it, but later beta builds can carry the same version: pin the tag, not the version.

### Framework lock

A repo made from [MeitouModTemplate](https://github.com/Meitou-Modding-Ecosystem/MeitouModTemplate) names Rivalry Core's zip in
`meitou.lock.json` (tag, asset, SHA-256 and engine commit from the release notes); the build downloads, checks and references it, Play
deploys it, and a scheduled workflow moves the lock to new releases. Setup: GutenStructure
[framework lock](https://github.com/TheIvoryDisaster/GutenStructure/blob/beta/docs/framework-lock.md) and
[framework update](https://github.com/TheIvoryDisaster/GutenStructure/blob/beta/docs/framework-update.md). Working example:
[AshenHand](https://github.com/Meitou-Modding-Ecosystem/AshenHand).

### Commit-pinned source

**In your mod repo's root:**

```sh
git submodule add https://github.com/Meitou-Modding-Ecosystem/RivalryCore.git deps/RivalryCore
git -C deps/RivalryCore checkout --detach <commit on beta>
git submodule update --init --recursive
git add .gitmodules deps/RivalryCore
git commit -m "Pin Rivalry Core to <commit on beta>"
```

In your mod's `.csproj` (one folder below your repo's root), reference the engine inside the submodule, so your mod and Rivalry Core build
against one engine:

```xml
<ItemGroup>
  <ProjectReference Include="../deps/RivalryCore/engine/src/Meitou.Core/Meitou.Core.csproj" Private="false" />
  <ProjectReference Include="../deps/RivalryCore/engine/src/Meitou.Data/Meitou.Data.csproj" Private="false" />
  <ProjectReference Include="../deps/RivalryCore/engine/src/Meitou.Simulation/Meitou.Simulation.csproj" Private="false" />
  <ProjectReference Include="../deps/RivalryCore/engine/src/Meitou.Game/Meitou.Game.csproj" Private="false" />
  <ProjectReference Include="../deps/RivalryCore/RivalryCore/RivalryCore.csproj" Private="false" />
</ItemGroup>
```

Import `deps/RivalryCore/engine/Directory.Build.props` from your own `Directory.Build.props`, so your mod builds with the engine's settings.

Stamp the engine as the oldest game release your mod supports, or the released game refuses your DLL (the submodule builds the engine as
`1.0.0.0`, and .NET never binds to an older assembly): a `Directory.Build.rsp` in your repo's root holding `-p:AssemblyVersion=0.22.0.0`,
and the same range in your `gameVersion`. Copy Rivalry Core's `Directory.Build.props` lines if your own assembly should keep its manifest
version.

To install the pinned Rivalry Core for play, download the beta zip of that commit if it has one; otherwise, **in your mod repo's root**:

```sh
dotnet publish deps/RivalryCore/RivalryCore -c Release -o "<Kenshi>/mods/RivalryCore"
dotnet run --project deps/RivalryCore/tools/EmptyMod -c Release -- "<Kenshi>/mods/RivalryCore/RivalryCore.mod"
```

To move the pin, **in your mod repo's root**:

```sh
git -C deps/RivalryCore fetch origin beta
git -C deps/RivalryCore checkout --detach <new commit>
git submodule update --init --recursive
git commit -am "Move Rivalry Core to <new commit>"
```

### Zip

Versioned releases hold the zip, its `.sha256` file, and notes naming the RivalryCore commit, the engine commit and the zip's SHA-256. The zip holds
the `RivalryCore/` folder (`RivalryCore.dll`, `RivalryCore.pdb`, `meitou.json` and an empty `RivalryCore.mod`).

| Zip | Download |
| --- | --- |
| Newest beta | `https://github.com/Meitou-Modding-Ecosystem/RivalryCore/releases/download/rivalry-core-beta/RivalryCore-beta.zip` |
| One beta build | `https://github.com/Meitou-Modding-Ecosystem/RivalryCore/releases/download/rivalry-core-<version>-beta.<commit>/RivalryCore-<version>-beta.<commit>.zip` |
| Release | `https://github.com/Meitou-Modding-Ecosystem/RivalryCore/releases/download/rivalry-core-v<version>/RivalryCore-<version>.zip` |

- To play: extract the zip into `Kenshi/mods/`, so the files sit in `Kenshi/mods/RivalryCore/`, and enable Rivalry Core above your mod.
- To compile against it: copy `RivalryCore.dll` from the zip to `lib/RivalryCore.dll` in your mod repo, and in your mod's `.csproj`
  (one folder below your repo's root) add `<Reference Include="RivalryCore" HintPath="../lib/RivalryCore.dll" Private="false" />` beside
  your engine references.
- To fetch it in CI: pin the tag and the SHA-256 from the release notes in your workflow, never `rivalry-core-beta`, and check the bytes:

```sh
gh release download "$RIVALRY_TAG" --repo Meitou-Modding-Ecosystem/RivalryCore --pattern 'RivalryCore-*.zip' --dir deps
echo "$RIVALRY_SHA256  $(ls deps/RivalryCore-*.zip)" | sha256sum -c -
```

### Optional dependency

A mod that works without Rivalry Core declares it with `"optional": true` and reaches it only after `c.HasDependency(RivalryMod.Id)`
(engine [modding-dependencies.md](https://github.com/Meitou-Modding-Ecosystem/MeitouClient/blob/master/docs/modding-dependencies.md)).
It still compiles against Rivalry Core through one of the sources above, so the compiler catches API changes, and the launcher still loads
Rivalry Core first when it is enabled.
Compiling proves only the typed calls: test the mod both with Rivalry Core loaded and without it, since the absent path runs code the
compiler never ties to Rivalry Core.

## 2. Declare it in `meitou.json`

```json
"apiVersion": 2,
"dependencies": [{ "id": "rivalry.core", "version": ">=0.6.0 <0.7.0" }]
```

- Before 1.0 a minor version may break the API, so cap the range at the next minor. 0.6.0 is the first version with party caps; 0.6.1 adds
  the rest between hunts (`HostileRestUntil`); 0.6.2 who goes next (`Propose`, `Rule.Grudge`, `Assessment.Propose`); 0.6.4 builds against
  Meitou 0.22 and needs it (`gameVersion` `>=0.22.0 <0.22.2`); 0.6.5 renames `MaxHuntsPerAssessment` to
  `GrudgeCandidatesPerAssessment` (the old name still works, marked obsolete); 0.6.6 offers a winning candidate its claim for one
  assessment, confirmed by the next `Propose` (an offer that ends unconfirmed starts no rest).
- Rivalry Core must sit above your mod in `data/mods.cfg`; the launcher and loader say "move X above it" otherwise.

## 3. Two ways players get it

Write the mod so both work; the code is the same.

| Delivery | What the player has | What you do |
| --- | --- | --- |
| Separate release, loaded first | the `RivalryCore/` folder from its release zip in `Kenshi/mods/`, enabled above your mod | link the release in your mod's page; declare the range |
| Baked into your download | a pipeline (planned) copies the unchanged `RivalryCore/` folder of a release zip next to your mod folder | still declare the dependency; never rename its id, change its manifest, or merge its DLL into yours |

- The engine loads one mod per id: when two downloads both bring `rivalry.core`, the first enabled copy loads and later copies are refused
  ("an id another mod took"). So a baked copy must stay `rivalry.core`: a renamed or merged copy would run a second framework with its own
  caps, and the one-hostile-party rule would break.
- The copy that loads must satisfy every dependent's range; with an older copy first, dependents needing newer are refused. Keep ranges as
  wide as your code allows.

## 4. Register in `Initialize`

```csharp
public void Initialize(ModContext c)
{
    var rivalry = c.Dependency<IRivalryApi>(RivalryMod.Id) ?? throw new InvalidOperationException("Needs Rivalry Core.");
    var mine = rivalry.For(c.Id);                     // every name starts with "<your id>."
    mine.AddRule(new Rule
    {
        Name = $"{c.Id}.collectors",
        Target = BuiltIn.CharacterTarget,
        When = (a, t) => a.Notoriety.Grudge(a.Data.FactionIndex("1234-mymod.mod"), t.Character) >= a.Options.HuntThreshold,
        Behaviour = BuiltIn.Pursue,                    // hostile: counted under the cap, waits its turn
        CooldownHours = _ => 48,
        Faction = (a, _) => a.Data.FactionIndex("1234-mymod.mod"),
        Args = (_, _) => new BehaviourArgs().Set("template", "5678-mymod.mod"),
    });
}
```

- Registrations only in `Initialize`; later ones throw. Duplicate or unprefixed names throw.
- Rivalry Core drops everything your mod registered if your `Initialize` throws after registering (`IModDependents`).
- Code in rules, conditions, targets and directors reads only; randomness from `Assessment.Roll(key)`; state only in cooldowns
  (`Decisions.TryClaim`) and runs. Behaviours keep state only in their `BehaviourRun`.
- Use `rivalry.Options` only for the code-only values it documents (`Homes`, `OwnHitSquads`, packages). The party caps are not options.

## 5. Parties and the caps

At most one hostile party is out against the player at a time, from all mods together; friendly parties up to the player's setting (1 to 10).
Details: [rivalry-core.md "Party caps"](rivalry-core.md#party-caps).

- **Using a built-in behaviour or decision**: nothing to do. `pursue`, `burgle`, `raid`, `hunt` are hostile; `visit`, `reinforce` friendly.
- **Your own behaviour**: declare `public PartyKind Party => PartyKind.Hostile;` (or `Friendly`). Override `Underway(run)` when a run stops
  counting before it ends (as a hit squad does once beaten). A behaviour that sends people and declares `None` slips past the cap: don't.
- **Who goes first**: hostile candidates from every mod are ranked by grudge − 2 × travel hours + 1 × hours waited
  ([rivalry-core.md "Who goes next"](rivalry-core.md#who-goes-next)). A hostile rule is a candidate by itself; set `Rule.Grudge` when the
  faction's grudge against the target is not what should rank it.
- **Your own director**: propose hostile parties instead of deciding them, and claim the cooldown in the dispatch; for friendly ones check
  `assessment.MayStart(PartyKind.Friendly, Name)` before `TryClaim`.

```csharp
var at = a.Report.Find(who)!.Position;
var candidate = new HostileCandidate(Name, key, faction, grudge, a.TravelHours(faction, at));
a.Propose(candidate, () => { if (d.TryClaim(key, 48)) d.Start(BuiltIn.Pursue, target, args); });
```

- **A party your mod runs itself** (its own pursuit code, not a Rivalry behaviour): propose it, send it once it holds the slot, renew the
  claim while out, release it after. A win is an offer that lasts one assessment: propose again soon (the `true` confirms it), and release
  it if you won't send the party after all (it then starts no rest).

```csharp
var state = rivalry.In(world);                        // in your system's serial step
var key = characterName;
var travel = state?.TravelHours(faction, characterId) ?? 0;
var candidate = new HostileCandidate($"{c.Id}.bounty", key, faction, grudge, travel, ClaimHours: 6);
if (state?.Propose(candidate) != true) return;        // waiting its turn (or resting): propose again next assessment
// ... send the crew; while it is out, call ClaimParty(new PartyClaim(candidate.Source, PartyKind.Hostile, key, 6, faction)) within 6 h:
state.ReleaseParty(candidate.Source, key);            // when it ends
```

- `ClaimParty` alone still works (it takes a free slot at once, skipping the queue); use it only for a party that cannot wait, such as one
  already under way when your mod loads. It skips the queue but not the rest between hunts: a new claim is refused while the rest runs
  (`HostileRestUntil`), a renewal of a claim already held always holds, and a claim that ends (released or lapsed) starts a rest like any
  hostile party.

- **Going over the cap**: only on purpose, in `Initialize`: `mine.ExceedHostileCap($"{c.Id}.siege", 1, "the siege brings its own scouts")`.
  The source may send one more than the cap; its parties still count for everyone else. `Rivalry: parties` (F10) lists every override.
- **Seeing it**: `IRivalryState.Parties`, `PartiesOut(kind)`, `PartyCap(kind)`, `HostileRestUntil`, `Waiting`; F10 `Rivalry: parties` and
  `Rivalry: log` (`held: ...`).


### Packaging publication checks

The hardened packager rechecks production tags against the current protected release head immediately before publishing, in addition to the caller's central release-gate prerequisite. Beta snapshots require beta history, and an older build/rerun does not move the rolling beta pointer backward. Existing versioned releases are verified against source/channel/digest; if rebuilt ZIP bytes differ (including timestamps), the rerun fails clearly instead of reporting a new hash for the old artifact. Use the existing canonical snapshot/hash, and consume outputs only from a successful workflow. Both rolling ZIP names receive sidecars after this update.

The write-token job executes only the pinned central publisher helper, never caller code. These guards are covered by GutenStructure publication tests; this does not prove a consumer's game integration.

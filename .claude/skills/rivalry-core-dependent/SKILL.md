---
name: rivalry-core-dependent
description: Use when making a Meitou code mod depend on Rivalry Core, or adding Rivalry rules, behaviours, directors or parties (hunters, raiders, visitors, reinforcements) to a mod.
---

# Build on Rivalry Core

Rivalry Core (`rivalry.core`) is the shared framework for factions acting on the player. Your mod adds rules and behaviours to it; it never
copies or replaces it. Read `docs/depending-on-rivalry-core.md` first, and `docs/rivalry-core.md` for the section you touch.

## Hard rules

- One framework: reference `RivalryCore` with `Private="false"`; never list `RivalryCore.dll` in your `assemblies`, never rename
  `rivalry.core`, never merge or fork its code into your mod. A baked-in copy ships its folder unchanged.
- One hostile party at a time, from every mod together. Never work around it: no behaviour that sends people with `Party` left at `None`,
  no `Custom`/`Direct` decision that spawns or sends squads, no own pursuit code without `ClaimParty`.
- Going over the cap needs `ExceedHostileCap(source, extra, reason)` in `Initialize`, and the user's explicit agreement. Ask before adding it.
- Determinism: read only in rules, conditions, targets and directors; randomness from `Assessment.Roll` / `BehaviourContext.Roll`; state only in
  cooldowns and `BehaviourRun`; serial steps only for `IRivalryState`.
- Ask the user before adding tests (repo rule), saying what they would catch.

## Steps

1. **Manifest**: `apiVersion` 2 and `"dependencies": [{ "id": "rivalry.core", "version": ">=0.6.0 <0.7.0" }]` (match the current
   `RivalryCore/meitou.json` minor). Tell the user Rivalry Core must load above the mod in `mods.cfg`.
2. **Project**: in a repo made from MeitouModTemplate, add a `rivalry.core` entry to `meitou.lock.json` from the release notes (as in the
   AshenHand repo); the build references it. Elsewhere pin this repo as a submodule
   or reference a zip's `RivalryCore.dll`, with `Private="false"` ([docs/depending-on-rivalry-core.md](../../../docs/depending-on-rivalry-core.md) section 1).
3. **Entry**: in `Initialize`, `var rivalry = c.Dependency<IRivalryApi>(RivalryMod.Id)`, then `var mine = rivalry.For(c.Id)`. Every name
   starts with `c.Id + "."`. Register nothing outside `Initialize`.
4. **Pick the shape** of the new feature:

   | Want | Use |
   | --- | --- |
   | When X holds for a town or character, do Y, with chance and cooldown | a `Rule` (`AddRule`) |
   | A reusable test | `AddCondition`, then name it in `Rule.Conditions` |
   | A number per town or character in the report | `AddTownProbe` / `AddCharacterProbe` |
   | Something new to aim at (a building, a caravan) | `AddTarget` with a stable `Key` |
   | A squad that acts over time | an `IBehaviour` with runs (`context.Begin`, `Step`), or a built-in (`rivalry.pursue`, `rivalry.burgle`, `rivalry.visit`) with args |
   | Logic across many targets at once | an `IDirector`; rules first if they fit |
   | A party your mod already drives itself | `IRivalryState.Propose`, then `ClaimParty` to renew and `ReleaseParty` |

5. **Rule**: `Name`, `Target` (`BuiltIn.TownTarget` or `BuiltIn.CharacterTarget`, matching what the behaviour needs: `pursue` takes a
   character), `Conditions`/`When`, `Behaviour`, `ChancePerDay` (null: always), `CooldownHours`, `Faction` (-1 skips), `Args`, `Notice`,
   `Enabled` (tie it to a mod setting). Look at `AshenHand/AshenHandMod.cs` in the AshenHand repo and `RivalryCore/BuiltIn.cs` for working rules.
6. **Behaviour** (only if no built-in fits): set `Party` to `Hostile` or `Friendly` for anything that sends people; override
   `Underway(run)` if a run stops counting before it ends; keep all state in the run's `Args`, `Phase`, `Until`, `Platoon`.
7. **Director**: a hostile party is `a.Propose(new HostileCandidate(Name, key, faction, grudge, a.TravelHours(faction, at)), dispatch)`
   with `TryClaim` and the start inside `dispatch` (the queue picks who goes: grudge − 2 × travel h + 1 × h waited); a friendly one checks
   `a.MayStart(PartyKind.Friendly, Name)` before `TryClaim`. Hostile rules queue by themselves; set `Rule.Grudge` if the faction's grudge
   against the target should not rank it.
8. **Own pursuit code** (e.g. TWR's crews): each assessment, `Propose(new HostileCandidate(source, key, faction, grudge,
   state.TravelHours(faction, who), ClaimHours: hours))`; false means it waits, send nothing. True: it holds a claim; send, renew with
   `ClaimParty` within `hours` while out, `ReleaseParty` when done. Key: stable across loads (a character's name, `town{n}`).
9. **Docs**: describe the mod's rules, args and settings in `docs/<mod>.md`; a change to Rivalry Core's API updates `docs/rivalry-core.md`
   and `docs/depending-on-rivalry-core.md`, bumps its minor version and its dependents' ranges in the same PR.
10. **Check**: `dotnet build` (warnings are errors), `dotnet test`; in game, F10 `Rivalry: runs and rules`, `Rivalry: parties`,
    `Rivalry: fire rule (your rule, target)` (skips the cap), `Rivalry: log` for `held: ...` lines.

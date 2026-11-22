# Rivalry Core (framework mod)

**Status: proposed, mod version 0.6.6, code-mod API 2. Needs Meitou 0.22.0 or 0.22.1** (`gameVersion` `>=0.22.0 <0.22.2`, "Building"); Meitou
0.21 and earlier refuse it (0.6.0 named no game version). A code mod in `RivalryCore` (id `rivalry.core`) that lets the world's factions
inspect the player's setup and act on it: hunt a character, raid a weak rich town, reinforce a town that is losing a fight. Not rival-only: the
same pipeline sends allies to help and friendly visitors. Built on [dependencies](https://github.com/Meitou-Modding-Ecosystem/MeitouClient/blob/master/docs/modding-dependencies.md); other mods add probes, conditions,
target kinds, behaviours, rules and directors (see "Extension API"; how-to: [depending on Rivalry Core](depending-on-rivalry-core.md)). Needs the engine hooks of [modding](https://github.com/Meitou-Modding-Ecosystem/MeitouClient/blob/master/docs/modding.md) "World hooks".

## Pipeline

At each assessment (every `AssessEveryHours` game hours, default 2, in the system's `SlowWorld` at `SystemAnchor.End`), all serial:

1. **Inspect** (`Inspector`) the player faction into an immutable `PlayerReport`; probes add their numbers.
2. **Decide**: the rules are evaluated in the order added (`RuleEngine`), then each `IDirector` reads the `Assessment`; both record `Decision`s.
3. **Act**: decisions are carried out in order. One that would send a party over its cap is held (see "Party caps"). A `StartDecision`
   starts a behaviour; the rest go through `Actions`. Each yields a log line (`History`, the game log).

Between assessments, every slow step: behaviours under way are stepped (`BehaviourRuns`), then the debug rows' asks run.

A throwing rule or director is logged and off for the rest of that world; a failing decision or run is logged and dropped, the rest go on.

## Inspection

| Report | Contents |
| --- | --- |
| `CharacterReport` | each living player-faction character: id, name, squad, position, town (nearest player town whose radius holds it), down, in combat, strength, gear value (equipment sections), carried value (the rest, bags included); `Value(probe)` |
| `TownReport` | each player town: finished buildings, turrets (function 12), gates (21), highest research `tech level` (3), value in player-owned containers, building value (finished buildings and furniture at their construction materials' base price), defenders (player characters in it), defenders down, defence, hostiles standing in it, threat; `UnderAttack`, `Balance` = defence / threat; `Public`, `Beds` (the player's bed fixtures); `Value(probe)` |
| `CampaignReport` | running campaigns aimed at a player town: faction, FACTION_CAMPAIGN id, town, phase, hostile |
| `PlayerReport` | the above, money, `Carried` (worn and carried), `Stored` (town containers), `Built` (building value), `Wealth` (all four), `Strongest` |

Engine choices (framework's own, not the original's): strength = mean of melee attack, melee defence, dexterity, toughness (10 without stats, 0
while down); defence = defenders' strength + 15 per turret; hostile = faction relation ≤ -30 with the player, or a squad whose campaign war
targets include the player. Item value = the game's base price (`Market.BaseValue`: quality, material, manufacturer) × quantity; **Observed** (2026-11-13, a new game
without a base): the record's `value` field alone gave wealth 0. `rivalry.tempting` counts only what can be stolen (stored, carried), not buildings. Function numbers: [campaigns](https://github.com/Meitou-Modding-Ecosystem/MeitouClient/blob/master/docs/game/factions-squads-towns.md#campaigns).

## Notoriety

`NotorietyLedger`: grudge per (faction, character). Fed by `ICombatHitObserver`: a player-faction attacker landing a hit on another faction's
member adds `HitGrudge` (1), plus `DownedHitGrudge` (3) if the victim was already down; the victim dying within `KillWindowHours` (1) adds
`KillGrudge` (25). At each assessment every grudge cools by `GrudgeCoolPerHour` (1) per game hour since the last; below 0.5 or once the
character leaves the table it is dropped. Directors and dialogue add to it too. A character's notoriety is the sum over factions.

**Held floor**: when a hit squad (`rivalry.pursue`) starts for a grudge at or above `HuntThreshold`, the threshold becomes that grudge's floor:
cooling and negative adds stop there. It is let go when the squad is beaten (every member down or dead) while a standing player character is
within `SeenRange` (1500, the outdoor sight range) of a member; a leader handed to the law clears the faction's grudges and floors outright
(see "Rival leaders"). So a grudge that sent a squad keeps sending one, paced by the sender's rules, until the player beats it in view.

## Built-ins

Registered under the prefix `rivalry.`; constants in `BuiltIn`. Each rule and the director has a settings switch; cooldowns are per rule or
director and key.

| Kind | Name | Meaning |
| --- | --- | --- |
| target | `rivalry.town` | each player town; key `town{number}` |
| target | `rivalry.character` | each standing player character; key: its name |
| condition | `rivalry.under-attack`, `rivalry.calm` | hostiles are standing in the town, or not |
| condition | `rivalry.losing` | under attack and `Balance` < `ReinforceBelowBalance` (0.75) or half its defenders down |
| condition | `rivalry.tempting` | not under attack, holds ≥ `RaidWealthMin` (20000) and > 400 per point of defence |
| condition | `rivalry.town-public` | the town is open to the public |
| behaviour | `rivalry.raid`, `rivalry.reinforce`, `rivalry.hunt` | the `Raid`, `Reinforce`, `Hunt` actions below; args `campaign`, `delay`, `nearby`, `package`, `squads`, `hours`. Hostile, friendly, hostile |
| behaviour | `rivalry.visit` | see "Visit". Friendly |
| behaviour | `rivalry.burgle`, `rivalry.pursue` | see "Burgle" and "Pursue": squads the mod walks itself. Hostile |
| rule | `rivalry.reinforce` | town `rivalry.losing`: `rivalry.reinforce` by the closest ally (relation ≥ 50; allies with a DEFEND_TOWN campaign first); once per town per 12 h |
| rule | `rivalry.opportunist` | town `rivalry.tempting`, chance `RaidChancePerDay`: `rivalry.raid` by the most hostile faction that has an assault campaign; once per town per 96 h |
| rule | `rivalry.hit-squad` | a standing character against whom a faction's grudge is ≥ `HuntThreshold`: `rivalry.pursue` with that faction's own squad (see "Hit squads of any faction"); once per character per 48 h |
| director | `rivalry.grudge` | a non-allied faction's grudge on a standing character ≥ `HuntThreshold` (60) and a squad of it within `HuntRadius`: `Hunt` with `HuntSquads` squads (none near: nothing, no cooldown); the largest `GrudgeCandidatesPerAssessment` (8; it sets how many are proposed, not how many go) are hostile candidates with no travel (see "Who goes next"); once per faction and character name per 48 h |

## Rules

A `Rule`: `Name`, `Target` kind (default `rivalry.town`), named `Conditions` and an optional `When` test, `Behaviour`, `ChancePerDay` (null:
always; spread over the assessments), `CooldownHours`, `Faction` picker (-1 skips the target), `Args`, `Notice`, `Enabled`. Per target, in the
kind's order: not cooling, conditions hold, roll (`Assessment.Roll("{rule}/{key}")`) passes, a faction is found. A hostile start then becomes
a candidate (see "Who goes next", scored on `Grudge`, default `Assessment.GrudgeAt`); any other needs the party cap to admit one more of the
behaviour's kind. Then the cooldown `{rule}/{key}` is claimed and the start recorded. A rule held by its cap, or still waiting its turn, keeps
its cooldown free. Rules are code only; no data file.

## Behaviours

`IBehaviour.Start(context, request)` returns a log line; one that lasts calls `context.Begin(this, request, until)` for a `BehaviourRun`
(id, behaviour, source, key, faction, town, platoon, phase, started, until, args) and is stepped by `Step(context, run)` every slow step until
it returns false. The run is the behaviour's only state. `BehaviourContext`: world, data, report, options, population, grudges (`Notoriety`),
`Roll`, `Apply(decision)`, `ValueOf(item)` (the report's prices), `System<T>()`. `Party` (default none) is what a start sends at the player
and `Underway(run)` (default every run) whether a run still counts as one (see "Party caps").

### Visit

`rivalry.visit` (args: text `template`, a SQUAD_TEMPLATE whose package is a wandering trader's; numbers `hours` 48, `minRadius` 600,
`maxRadius` 1200, `money`): phase 0 `SpawnPlatoon` in the ring round the town, homed at the nearest town site, tried each step for 24 h when no
spot is walkable; phase 1 waits up to 24 h for the platoon to load, then sends `VisitTown` at the next tick; phase 2 ends when the stay + 24 h
has passed or the platoon is gone. **Unknown**: everything in play (see "Evidence"); a town that is not public sends the trader away at once.

### Driving a squad

`SquadDriver`, for a behaviour that walks its squad itself instead of giving it a package: `Hold` (`PopulationSystem.HoldPlatoon` and
`CharacterCold.Engaged` on each member, optionally the Sneak order), `Keep` (holds again; a hold is not saved, so driven runs call it every
step), `GiveBack`, `Able` (members that can act), `FirstLiving`, `WalkTo` (true within 20; a walk under way is left alone unless `redirect`
and its goal is more than `RedirectSlack` (10) from the new point, since each new walk asks for a fresh path) and `RollSkills`:
a number argument named `<skill>Min` (and `<skill>Max`), the skill a `StatsEnumerated` name in any case (`stealthMin`, `martialArtsMax`),
gives every member that skill uniformly in the range when the squad loads. A run that ends, or whose squad unloads, gives the platoon back.

### Burgle

`rivalry.burgle` (args: text `template`; numbers `goal` 1000 cats, `hours` 12, `minRadius` 600, `maxRadius` 1200, `salePrice` 1000, skill
ranges). The run keeps `loot`, its spawn point and the buyer's site in its args.

| Phase | What happens | Next |
| --- | --- | --- |
| 0 spawning, 1 loading | as Visit; the platoon is held, and once loaded its members sneak and get their skills | 2 |
| 2 stealing | each thief walks to one of the player's containers in the town, the one with the most valuable stack first; a locked one is picked with `LockSystem.Picking.Step` (the game's time and its Burglary for witnesses); it takes the most valuable stack that fits (a Theft). No container with goods left: it pickpockets the player character in the town carrying the most (`CrimeSystem.Pickpocket`, the game's roll) | seen, or caught pickpocketing: 3; `loot` ≥ `goal` or `hours` up: 5 |
| 3 fighting | sneaking ends; the thieves are ordered onto the nearest standing player character and left to the fight. After it, one lifts the nearest downed player character a buyer exists for | carrying: 4; nobody to take or `hours` up: 5 |
| 4 abducting | the squad walks to the buyer's town; the captive's hunger is held at 1.5 of 3 (`CharacterCold.FedTo`). Within 400 of the town centre the nearest able member of the buyer's faction takes the captive as a slave (`ArrestSystem.TakeOver`) and the squad's purse gets `salePrice` | sold, body lost or 72 h up: 5 |
| 5 leaving | the squad runs back to its spawn point; there, or after 6 h, it is given back to the game | ends |

Seen: a thief is in combat, has a pending crime that was witnessed (`Pending.Timer` ≥ `CrimeSystem.CaughtTimer`), or
`AggroSystem.Spotted` says a standing player character has it. Buyer (`SlaveMarket`): the nearest town site whose faction is not hostile to the
thieves and keeps slaves of the captive's race, that is one of its squad templates lists a `slaves` character of that race; so the game's own
data decides who enslaves whom. **Unknown**: everything in play, above all NPC sneaking against player characters, a walk through zones that
unload (the carrier and captive then travel as a stand-in or not at all), and what a released squad without a package does.

### Pursue

`rivalry.pursue` (target: a character; args: text `template`, `home`, `leaderNames`, lines (see "Hunter barks"); numbers `hours` 24,
`minRadius`/`maxRadius` (`HuntSpawnMinRadius` 2200, `HuntSpawnMaxRadius` 3400), skill ranges). Phases:

1. **Travelling** (5): on the road for the distance from the faction's nearest base to the quarry over `HuntTravelUnitsPerHour` (2727: the
   original's off-screen travel speed 25 per second over its 1200/11 s game hour), clamped to `HuntTravelMinHours` (1) and the setting
   `HuntTravelMaxHours` (48; 0 skips the road). Base: the faction's nearest base ([factions](factions.md#bases): homes named in `Options.Homes`
   or `home`, else owned towns, else towns that spawn its squads, else regions that do), else `HuntTravelFallbackHours` (12). The squad is homed
   at that base's town (else the town nearest the quarry).
2. **Spawning** (0): `SpawnPlatoon` in the ring round the quarry, beyond sight (1500 × vision) and inside the loaded 3 × 3 zones (4608 each),
   tried for 24 h; then **loading** (1): once in the world the squad is held, its skills rolled, its leader named (see "Rival leaders"), its
   evidence planted, and the quarry marked TEMP_ENEMY.
3. **Closing / attacking** (2, 3): runs to the quarry's current position at every step; within 150 its idle members are ordered to attack.
4. Settled: quarry down or gone (won), every hunter down (beaten) or `hours` up (broke off). The squad is given back, the faction's last hit
   squad counts as finished now (`RivalRoster.Ended`), the leader is credited. Beaten in view releases the held floor; a beaten squad's leader
   is then watched (**aftermath**, 4) for its fate.

The run keeps `quarryName` (a loaded run finds the quarry by it, else the standing character with the largest grudge), `travelHours`,
`ended`, `leaderName`, `leaderSerial`. A run counts as a hostile party until its aftermath (`PursueBehaviour.HitSquadUnderway(run)`);
`Assessment.HitSquadUnderway(faction)` and `HoursSinceHitSquad(faction)` give a rule a rest per faction on top of the cap. **Unknown** in play.

### Rival leaders

`RivalRoster` (`IRivalryState.Rivals`, `BehaviourContext.Rivals`, `Assessment.Rivals`; saved, hashed; factions by string id):

- **Naming**: when a pursue squad loads, its first living member takes a leader's name: the faction's free, idle leader that fought most
  recently and whose last body is not standing in the world, else a new name from `leaderNames` (picked by roll; the next unused; all used:
  numbered, e.g. "Old Sorrow II"). The leader's portrait serial ties the roster entry to the body.
- **Record** (`RivalLeader`): dispatches, wins, losses, withdrawals, left for dead, captures, escapes, comrades lost (squad members dead when a
  fight is settled; total and last), last outcome, quarry and hour, fate (free, captive, dead, jailed, recruited). Only a free leader is sent.
- **Aftermath** (a beaten squad's leader, looked at every 30 ticks): dead: killed. In a cage of a faction neither the player's nor lawless, or
  a bounty on it claimed, alive: **handed to the law**: the faction forgets every grudge and floor (`NotorietyLedger.ClearFaction`) and a
  `Reckoning` (faction, leader, quarry, hour, grudge cleared) is kept. Carried by a player character or in the player's cage: captive, watched
  while held; free again: escaped. Gone from the world (its platoon unloaded) or still free after `AftermathHours` (12): left for dead.
  Joined the player's faction: recruited.
- Personality on redispatch and recruitment are planned on top of this record ([roadmap](roadmap.md)).

### Evidence

Burgle and Pursue plant evidence when their squad loads (`Evidence.Plant`): with a text arg `evidence` (an ITEM record), the squad's first
living member gets one in its main grid, its `ModTag` `rivalry.evidence|<portrait serial>|<bounty>|<crime>`. Bounty: `evidenceFactor` (2) ×
`goal`, at least `evidenceMin` (3000); crime Burglary for Burgle, Assault for Pursue. A dependent registers `c.AddItemUse(record, Evidence.Use)`
(engine "Item use"): a right click on a tagged note in a player character's own inventory adds the bounty to the named character's entry with
every booking faction (each faction's `BookingOf`, once), raised now and claimable again, posts a line and uses the note up. A character not
in the world (dead, or its platoon unloaded) leaves the note and posts that its owner is nowhere to be found; an untagged item of the record
moves as usual. The portrait serial is saved with the character and the tag with the item, so a note survives a load. A bounty under 4000
draws no bounty hunters and lasts 0.04 h per cat (3000: 120 h) from use. **Unknown** in play, including whether police arrest such a
character on sight.

## Actions

| Decision | Carried out by |
| --- | --- |
| `Raid(faction, town, campaign?)` | `PopulationSystem.RequestCampaign` with an ASSAULT_TOWN campaign: the given one, else the faction's first hostile one (its `campaigns` list, then records whose `faction override` names it). The game's request rules then apply: world states and leader only, no chance, range or cooldown ([campaigns](https://github.com/Meitou-Modding-Ecosystem/MeitouClient/blob/master/docs/game/factions-squads-towns.md#campaigns)) |
| `Reinforce(faction, town, campaign?, nearby)` | the same with a DEFEND_TOWN campaign (the "Ally ... (trigger)" records carry a `faction override`), and with `nearby` each of the faction's candidate squads within `ReinforceRadius` (8000) marks up to 16 of the town's hostiles TEMP_ENEMY and runs the defend package (`DefendPackage`, else the first AI_PACKAGE by id with signal 15, `Contract_DefendPlayerBase`) for `ReinforceHours` |
| `Hunt(faction, target, squads, package?)` | the nearest candidate squads within `HuntRadius` (20000) mark the target TEMP_ENEMY (squad memory, so the aggro fights it on sight) and, given a package (`HuntPackage`), run it as a contract aimed at the target |
| `Direct(squad, package, hours, target?)` | `StartContract` (dialogue's `AI contract`), deferred to the next tick |
| `ShiftRelation`, `Grudge`, `Notice`, `Custom` | `PlayerRelations.Add`; the ledger; `World.Post`; the director's own serial code |

Candidate squad: of the faction, not the player's, not dead, leader alive, in no campaign, not a town's own squad unless `UseTownSquads`; nearest
leader first, ties by id.

**Unknown**: which vanilla AI_PACKAGE makes a squad travel to a contract's mission target and attack it; so `HuntPackage` has no default and a
hunt without one only marks the target. A dependent (e.g. a rival faction mod) ships that package as data. Whether `Contract_DefendPlayerBase`
walks a squad into the town from 8000 away is **Unknown** until tried in play.

## Party caps

At most `Parties.HostileCap` (1) hostile party is out against the player at once, whichever mod sends it; friendly parties are capped by
the setting "Friendly parties at once" (`FriendlyPartiesAtOnce`, 1 to 10, default 3). Code: `Parties`.

| Kind | Counted while | Sources |
| --- | --- | --- |
| Hostile | a run of a hostile behaviour is `Underway`; a `Hunt` that marked squads, for its `hours`; a queued `Raid`, for its delay + 24 h and while a campaign of that faction at that town runs; a claim, until it lapses | `rivalry.pursue`, `rivalry.burgle`, `rivalry.raid`, `rivalry.hunt`, `Decisions.Hunt`, `Decisions.Raid`, any behaviour declaring `Party => PartyKind.Hostile` |
| Friendly | the same for friendly runs; a `Reinforce` that queued a campaign or sent squads, as a raid | `rivalry.visit`, `rivalry.reinforce`, `Decisions.Reinforce` |

- One gate: every decision is checked when it is carried out, after the decisions before it in the same assessment; one over its cap is
  logged `held: <what> would be hostile party 2 of 1` and dropped. Hostile starts of rules and `rivalry.grudge` go through "Who goes next";
  friendly ones ask first (`Assessment.MayStart(kind, source)`, which counts the decisions recorded so far), so a held start spends no
  cooldown. A dependent's director proposes hostile parties (`Assessment.Propose`) and asks `MayStart` for friendly ones.
- The cap is a constant, not an option: a dependent cannot raise it by setting a value. The only way over it is
  `IRivalryRegistry.ExceedHostileCap(source, extra, reason)` in `Initialize`: starts by that source (a rule, director or claim source of the
  registering mod) may go `extra` over; they still count for everyone else. Overrides show in `Rivalry: parties`.
- **Rest between hunts**: when the last hostile party ends (its run stops being `Underway`, its ledger entry expires, its claim is released or
  lapses; an offer never confirmed does not count, see "Who goes next"), no hostile party starts or is claimed for a whole number of game hours
  rolled uniformly between the settings "Rest between hunts, at least" (`HuntRestMinHours`, default 12) and "at most" (`HuntRestMaxHours`,
  default 12), each 0 to 168; a ceiling under the floor counts as the floor. Checked every slow step; the roll is the world's hashed RNG.
  Friendly parties don't rest; overrides don't skip it. A held start logs `held: resting after the last hostile party until <hour> h`;
  `IRivalryState.HostileRestUntil` gives the hour.
- Debug rows (`fire rule`, `visit town`) skip the caps and the rest.
- A mod running a party itself (not as a Rivalry behaviour) counts it with `IRivalryState.ClaimParty(new PartyClaim(source, kind, key, hours,
  faction))`: true when it holds, false when the cap is full or (hostile) the rest runs (send nothing; ask again later). Renew with the
  same source and key while the party is out (a renewal always holds); release with `ReleaseParty(source, key)`; an unrenewed claim lapses
  after `hours`, so a removed mod frees its slot. A hostile claim that ends starts the rest.
- `IRivalryState.Parties` (a copy), `PartiesOut(kind)` and `PartyCap(kind)` show what is out. `Custom` and `Direct` decisions are not counted:
  send parties through a behaviour, a hunt or raid decision, or a claim.

## Who goes next

When several hostile parties could go, each is a `HostileCandidate(Source, Key, Faction, Grudge, TravelHours, ClaimHours = 6)` and scores

`score = grudge − HuntTravelWeight × travel hours + HuntWaitWeight × hours waited`

with the settings "Hunt points lost per travel hour" (`HuntTravelWeight`, default 2) and "Hunt points per hour waited" (`HuntWaitWeight`,
default 1), each 0 to 10. Code: `HuntQueue`.

- Grudge: a rule's `Grudge`, else `Assessment.GrudgeAt(faction, target)` (the faction's grudge against the character, or the largest against
  the player characters in the town); `rivalry.grudge` uses the grudge it acts on.
- Travel: `Assessment.TravelHours(faction, position, home)`, the same rule as a hit squad's road time (`Travel`, "Pursue"): from the faction's
  base nearest the target, at most `HuntTravelMaxHours`; no base: `HuntTravelFallbackHours`. A hunt by squads already near (`rivalry.grudge`)
  and a rule with no faction travel 0.
- Waited: game hours since the candidate (`Source|Key`) was first proposed. It keeps its place while proposed again within
  `HuntQueue.ForgetHours` (24 h), so a failed chance roll does not reset it; one that goes starts afresh. Saved and hashed.
- After the rules and directors, the candidates are ranked by score, then grudge, then the longer wait, then source and key (ordinal); each
  in turn is sent when `MayStart(Hostile, source)` admits it (one while the cap is one and no override applies), and the rest wait. Sending
  runs the proposer's dispatch: a rule claims its cooldown and records its start, `rivalry.grudge` claims its cooldown and marks squads.
- Example: a grudge of 60 two hours away scores 56 and goes before a grudge of 80 twenty hours away (40); the far one, still proposed, scores
  70 thirty hours later and goes next.
- Another mod's party (e.g. TWR's crews) joins with `IRivalryState.Propose(candidate)`, with `TravelHours(faction, character)` for the
  travel: false while it waits; when it wins at an assessment it is offered a hostile claim (source and key as the candidate's) for one
  assessment (`AssessEveryHours`), and the next `Propose` returns true and confirms it for `ClaimHours` (a `ClaimParty` renewal confirms it
  too). An offer fills the cap like any claim, but one released or lapsed before it is confirmed starts no rest: the mod sent nothing.
  Propose at least every assessment while it wants to go (unproposed for 2 × `AssessEveryHours` it is dropped); renew with `ClaimParty`
  while it is out, release with `ReleaseParty`, also when it won but will not send the party after all.
- `IRivalryState.Waiting` and `Rivalry: parties` show the candidates still waiting, best first, with score and hours waited.

## Extension API

```csharp
public void Initialize(ModContext c)
{
    var rivalry = c.Dependency<IRivalryApi>("rivalry.core")!;
    var mine = rivalry.For(c.Id);                                   // names must start with "<my id>."
    mine.AddTownProbe("inn.rating", (world, town) => Rating(world, town));
    mine.AddCondition("inn.open", (a, t) => a.Report.TownOf(t.Town)?.Value("inn.rating") > 0);
    mine.AddRule(new Rule
    {
        Name = "inn.travellers",
        Conditions = [BuiltIn.TownPublic, BuiltIn.Calm, "inn.open"],
        Behaviour = BuiltIn.Visit,
        ChancePerDay = _ => 0.5f,
        CooldownHours = _ => 24,
        Faction = (a, _) => a.Data.FactionIndex("1234-inn.mod"),
        Args = (_, _) => new BehaviourArgs().Set("template", "5678-inn.mod").Set("hours", 48),
    });
}
```

| Call | Adds |
| --- | --- |
| `AddTownProbe`, `AddCharacterProbe` | a number per town or character in every report (`Value(name)`; 0 when it throws) |
| `AddCondition` | a named test over the assessment and a target |
| `AddTarget` | a target kind: lists `RuleTarget`s (stable `Key`, town, character, site) in a fixed order |
| `AddBehaviour` | an `IBehaviour` |
| `AddRule` | a `Rule`, evaluated after those added before |
| `AddDirector` | an `IDirector` (`Assess(assessment, decisions)`), asked after the rules; `decisions.Start(behaviour, target, args)` starts a behaviour; a hostile party is proposed with `assessment.Propose(candidate, dispatch)` |
| `ExceedHostileCap` | leave for one source to go over the hostile cap (see "Party caps") |

- Registrations only before the first world (in `Initialize`); a duplicate or unprefixed name throws. Rivalry Core implements
  `IModDependents`: a dependent that fails to load has its pieces dropped.
- Read only in probes, conditions, targets and directors; randomness from `Assessment.Roll(key)` / `BehaviourContext.Roll(key)`; no state
  outside cooldowns (`TryClaim`/`Cooling`) and runs, which are hashed and saved.
- `IRivalryApi.In(world)` gives the report, ledger, history, runs, parties and claims to a dependent's own systems (serial steps only).

## Hunter barks

`HunterBarks` watches the squads of pursue runs that are closing in or attacking, every 15 ticks, reading each member's combat target
(`CombatSystem.TargetOf`) while the member is in combat and the target is standing:

- A member that turns on someone not of the player's faction says a **distracted** line, at most one per squad per `BarkCooldownSeconds`
  (30): the squad is held up by another fight.
- The squad's first blow against one of the player's characters: its leader (if it can stand) opens with a line from its record: back after
  a fight, the last outcome's list; new while its faction has a reckoning, the reckoning list; else its first words. A member speaks the
  engage list when the leader cannot. Once per run.

Lines come from the run's text args (`HunterLines`: `say.distracted`, `say.engage`, `say.first`, `say.reckoning`, `say.won`, `say.withdrew`,
`say.beaten`, `say.leftForDead`, `say.escaped`; entries `|`-separated, one picked per bark), with generic defaults (`OffTargetLine` for
distracted). Placeholders: `{target}` the quarry, `{name}` the speaker, `{leader}` the faction's last leader handed to the law, `{fallen}` the
squad members dead after the leader's last fight. Switch: "Hunters speak". Display only: not hashed or saved. Squads the `rivalry.grudge`
director marks (no run) do not speak.

## Dialogue codes

| Code | Kind | Meaning |
| --- | --- | --- |
| 1600 | condition | grudge of the speaker's faction against the subject, compared with the value |
| 1601 | condition | the subject's notoriety, compared with the value |
| 1600 | effect | the speaker's faction's grudge against the target changes by the value |

## Options

The switches, assessment period, grudge cooling per hour, hunt threshold, hit squad travel at most, reinforce balance, friendly parties at
once, rest between hunts (floor and ceiling), the hunt points per travel hour and per hour waited, raid chance and
`Announce` (a message per decision) are on the Mod settings page. Everything else in `RivalryOptions` is for dependents. The system copies the options in `Inputs` each tick.

## Save data

Sidecar system `rivalry`, version 7 (1 to 6 still load): next/last assessment hours, grudges by faction string id and character name + record
id (slots change on load; restored to the first living match in slot order at the first step after the load, unmatched ones dropped),
cooldowns; since version 2 the next run id and each run (faction by string id, args, and its platoon as template + position: platoon ids change
on load, so a loaded run takes the nearest platoon of that template and faction no other run holds); since version 3 each grudge's held floor
and the rival roster (its own layout version 1: leaders, reckonings, each faction's last hit squad hour); since version 4 the party ledger
(kind, source, what, key, faction by string id, town, campaign flag, until); since version 5 the rest between hunts (whether a hostile party
was out, the hour the rest ends); since version 6 the hostile candidates' waits (id, first proposed hour, last proposed hour); since version 7,
last, which ledger claims are unconfirmed offers (count, then a flag per ledger entry). Other mods' candidates are not saved: they propose
again. Kill watches are not saved. A renamed character loses its grudges across a save made before the rename.

## Debug rows (F10)

A row with a field takes its values separated by commas, in the order below; spaces round a value are dropped, so a name cannot hold a comma.
`[x]` may be left out. A row that changes something only queues it: it happens at the next tick, so **unpause**, then press `Rivalry: log` or
`Rivalry: character`. A row refuses at once, in its status line, what cannot work (unknown rule or faction, a target of the wrong kind) and
lists what would. The reading rows show a copy of the state made once a game second; their answer also goes to the game log, in full.

### Hit squads of any faction

`rivalry.hit-squad` (`HitSquads`; settings switch "Any faction sends hit squads", on): the faction that acts is the one with the largest
grudge ≥ `HuntThreshold` against the character that is not the player's, not an ally, not in `RivalryOptions.OwnHitSquads` (factions whose
mod sends theirs by its own rule: The Ashen Hand), has no hit squad out, whose last one finished ≥ `HitSquadRestHours` (72) ago, and has a
squad to send: of the SQUAD_TEMPLATEs in its `squads` list, else its `squad default`, that are not traders, the one with the largest weight
(ties by id). `Rivalry: faction bases` names that squad, or says the faction has none. The leader keeps the name it was made with. The grudge
director still marks the faction's squads already near. **Unknown** in play: which squad each vanilla faction ends up sending, and how a
vanilla patrol squad of full size behaves when driven as a hit squad.

### Two ways a character gets hunted

| | Grudge hunt | Hit squad |
| --- | --- | --- |
| Who | any faction that is not an ally | any faction that is not an ally and has a roaming squad of its own (`rivalry.hit-squad`); The Ashen Hand by its own rule (`ashen.hand.hunters`) |
| Started by | the director `rivalry.grudge` at an assessment, when the faction's grudge is ≥ 60 and no other hostile party is out | the rule at an assessment (grudge ≥ 60, no other hostile party out, rested 72 h), or `fire rule` |
| What happens | the faction's squads **already in the world** within 20000 of the character hold them as an enemy and attack on sight. Nothing is spawned, nothing travels | a new squad travels from the faction's base, appears out of sight, closes in and attacks |
| A run? | no: `runs and rules` stays empty | yes: one `rivalry.pursue` run |
| How to see it | `Rivalry: character`: "N of its squads in the world hold them as an enemy", "marks again in X h"; `Rivalry: log`: `rivalry.grudge: hunt: N Reavers squad(s) already near Zimm now hold them as an enemy`; with none near, nothing is logged and the director looks again at each assessment | `Rivalry: character`: "hit squads: The Ashen Hand run 3: travelling from its base, arrives in 5.2 h" |

Both are hostile parties, so a grudge from Reavers starts whichever comes first; the other waits for it to end (see "Party caps"). `rivalry.grudge` cannot be fired: it is a director, not a rule. `rivalry.opportunist` and
`rivalry.reinforce` aim at a town (a raid on it, allies coming to it), never at a character, and make no run either: they queue a campaign.

### Rows

| Row | Field | Does |
| --- | --- | --- |
| `Rivalry: report` | none | characters, wealth (money, carried, stored, built), grudge count, each town's buildings, stored value, defence and threat, the latest log line |
| `Rivalry: log` | none | the six latest decisions and debug results, newest first |
| `Rivalry: character` | `[name]` | for the named character (any case), else the selected ones, else everyone with a grudge or a hit squad: notoriety, each faction's grudge, the squads hunting them, the rules resting on them |
| `Rivalry: assess now` | none | runs the inspection, rules and directors at the next tick instead of waiting for the period (2 h) |
| `Rivalry: runs and rules` | none | the runs under way in words, the parties against their caps, and every rule with what it aims at |
| `Rivalry: parties` | none | hostile and friendly parties out against their caps (`hostile parties 1/1 (rivalry.hit-squad run 3 -> Zimm)`), the hostile candidates waiting (`waiting: rivalry.hit-squad -> Zimm 31 (waited 4 h)`), and the overrides |
| `Rivalry: rival leaders` | none | each named leader's record and fate, and the leaders handed to the law |
| `Rivalry: faction bases` | `faction` | how the faction's bases were found, their count and the five nearest the first player character |
| `Rivalry: grudge on selected` | `faction[, amount]` | adds `amount` to the faction's grudge against each selected character. Nothing else happens until the next assessment |
| `Rivalry: fire rule` | `rule[, target]` | starts the rule's behaviour on one target, skipping its switch, conditions, chance, cooldown and party cap |
| `Rivalry: visit town` | `template, faction[, hours[, town]]` | starts `rivalry.visit` (see "Visit") |

| Value | Accepts | Left out |
| --- | --- | --- |
| `rule` | a rule's full name, exact case, as `runs and rules` lists it with its target kind: `rivalry.reinforce [a town]`, `rivalry.opportunist [a town]`, `rivalry.hit-squad [a character]`, `ashen.hand.thieves [a town]`, `ashen.hand.hunters [a character]` | required |
| `target` | for a town rule: the player town's name (any case), its number, or `town{number}`. For a character rule: a standing player character's name, exact case | the rule's first target: the first player town, or the first standing character |
| `name` | a player character's name, any case | the selected characters; none selected: everyone with a grudge or a hit squad |
| `faction` | a faction's name as the game shows it (any case) or its record id | required |
| `amount` | a number, `.` as decimal point; negative eases the grudge | 100 |
| `template` | a SQUAD_TEMPLATE's record id (not its name) whose package is a wandering trader's | required |
| `hours` | the stay in game hours | 48 |
| `town` | as a town `target` | the first player town |

### What `Rivalry: character` says

`Zimm: notoriety 175, hunts start at 60 | grudges: Reavers 100, 1 of its squads in the world hold them as an enemy, marks again in 46.5 h;
The Ashen Hand 75, held at 60 until its hit squad is beaten | hit squads: The Ashen Hand run 3: travelling from its base, arrives in 5.2 h
(leader Varn) | rules resting: ashen.hand.hunters 47.1 h`

| Part | Meaning |
| --- | --- |
| `notoriety` | every faction's grudge summed |
| `(below the threshold)` | that grudge is under `HuntThreshold`: it starts nothing |
| `N of its squads ... hold them as an enemy` | grudge hunt under way: that many of the faction's squads in the world have the character marked now (the mark fades by the game's own rule) |
| `marks again in X h` | the grudge director's rest for that faction and character (48 h) |
| `held at 60 until its hit squad is beaten` | the grudge cannot cool below this while a hit squad is owed a fight |
| `its last hit squad finished X h ago` | the faction's rest between hit squads counts from here (The Ashen Hand: 72 h) |
| `no hit squad after them` | no `rivalry.pursue` run aims at the character |
| `rules resting` | cooldowns that name the character: the rule cannot fire on them by itself for that long (`fire rule` ignores it) |
| `(down: not hunted while down)` | hunts start only against standing characters |

A hit squad's states: `travelling from its base, arrives in X h` (nothing in the world yet); `arrived, no free spot to appear at yet`;
`spawned out of sight as platoon N, not in the loaded world yet` (it exists but no player character is near enough to load it); `spawned and
closing in, gives up in X h`; `engaged, gives up in X h`; `beaten or called off; its leader is watched`. After the last one ends the run goes.

### Recipes

| To see | Do | Expect |
| --- | --- | --- |
| a grudge hunt | select Zimm; `grudge on selected`: `Reavers, 100`; unpause; `assess now`; unpause; `character` | `grudges: Reavers 100, N of its squads ...`; N is 0 when no Reaver squad is within 20000, and then nothing comes |
| any faction's hit squad | select Zimm; `grudge on selected`: `Reavers, 100`; unpause; `fire rule`: `rivalry.hit-squad, Zimm` (or `assess now`); unpause; `character` | `hit squads: Reavers run 1: travelling from its base, arrives in ...`; `no faction for Zimm`: no faction with a grudge ≥ 60 has a squad to send (`faction bases`: `Reavers`) or it is resting |
| The Ashen Hand's hit squad now | `fire rule`: `ashen.hand.hunters, Zimm`; unpause; `character` | `hit squads: The Ashen Hand run 1: travelling ...` |
| a hit squad by its rule | select Zimm; `grudge on selected`: `The Ashen Hand, 100`; unpause; `assess now`; unpause; `character` | the same, plus `rules resting: ashen.hand.hunters 48 h` |
| thieves now | `fire rule`: `ashen.hand.thieves` (or `ashen.hand.thieves, Home`) | `runs and rules`: `run 2 rivalry.burgle: The Ashen Hand -> town 1: ...` |
| a raid on a town | `fire rule`: `rivalry.opportunist, Home` | `log`: `rivalry.opportunist: raid: <faction> queued <campaign> against town 1`; no run |
| allies coming | `fire rule`: `rivalry.reinforce` | `log`: `rivalry.reinforce: reinforce: <faction> to town 1: ...`; no run |
| a trader visit | `visit town`: `<trader squad id>, Tech Hunters, 24, Home` | `log`: `debug: visit: run 3, ...` |

What a `fire rule` result in the log means: `debug: rule X fired on K`, then the behaviour's own line; `rule X: no faction for K` (the
rule's faction pick found none: reinforce needs an ally at relation ≥ 50, opportunist a hostile faction with an assault campaign, The Ashen
Hand its `AshenHand.mod`); `rule X threw: ...`. A rule fired this way does not start its cooldown. Behaviour refusals: `no population`,
`no template`, `no town`, `quarry gone`; `waiting for a spot`: no walkable point found yet in the ring (tried again for 24 h). Record ids in
a visit are the game's own, read in FCS.

## Building

Built against the released game players run: the `engine` submodule is pinned to Meitou's release tag (`v0.22.1`), and
`Directory.Build.rsp` stamps the engine assemblies as `0.22.0.0`, the oldest release the mod supports (`gameVersion` `>=0.22.0 <0.22.2`
in `meitou.json`). Without the stamp the submodule builds them as `1.0.0.0`; .NET never binds a reference to an older assembly, so the
released game refuses the mod ("entry type ... is not in its assemblies"). The mod's own assembly keeps its manifest version
(`Directory.Build.props`). A newer game release that changes an engine call the mod makes needs a new pin, stamp and range together.

`dotnet build RivalryCore` writes `RivalryCore.dll` and `meitou.json`; the game assemblies are referenced with `Private="false"`. Copy both
into `Kenshi/mods/RivalryCore/` beside an empty `RivalryCore.mod` made in FCS and enable it in `mods.cfg` above its dependents.
Tests: `dotnet test RivalryCore.Tests` (synthetic worlds, no game needed).

## Evidence

**Verified** by `RivalryCore.Tests` (synthetic worlds without a population system): the reinforce rule and its nearby squad, a grudge's
hunt, grudges and cooldowns across a save, a dependent's probe/condition/rule firing once per cooldown, a run across a save, a version 1 save
loading, name prefixes and dropping a mod's pieces, the world hash on 1 and 4 threads, which races a faction buys (`SlaveMarket`), the debug rows' character report for a grudge and their refusal of a rule aimed at the wrong kind of target, the hit squad rule firing for a faction with a roaming squad and not for one without. Burgle and
Pursue have no test of their steps; nor do grudge cooling and floors, the rival roster, leader fates or the barks. **Unknown**: everything in a real game (campaign starts
from `RequestCampaign`, contract packages, the Visit behaviour end to end, a run's platoon found again after a real load), and the
`meitou-tools mod-check` result.

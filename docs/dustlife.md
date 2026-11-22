# Dustlife (war, territory and economy)

**Status: proposed, not yet in this repo.** A code mod (planned id `pza.dustlife`, folder `Dustlife`) that keeps the whole map at war while
the player is elsewhere: factions hold towns, raise garrisons from what their towns produce, send their surplus to take more, and lose what
they cannot feed or defend. Its rules run today as a C++ simulation (66 offline checks, a fuzz suite and a balance runner on the real map);
this document is the design the C# port follows. Author: PZA.

Part of the group split: **Dustlife owns war, territory and economy.** The Wasteland Remembers keeps crime, heat and the memory layer
(talk, gossip, the Atlas, Recap); Rivalry Core keeps the factions' reactions to the player (hunts, raids on the player's towns, reinforcing
them). Dustlife reads the others through their public APIs and never sends hunters while another mod's hunt is under way.

## Scope of the first slice

- **At war:** the Holy Nation, the United Cities and the Shek Kingdom. In the game's data the Holy Nation is hostile to both others,
  and the UC and the Shek are not hostile to each other.
- **The UC's economy:** the Slave Traders (slave farms, ports, stone camps, the Slave Markets) and the Traders Guild (Trader's Edge) are
  the slave and trade side of the UC's economy: their places produce for it and their caravans carry its goods. They hold and defend their places but do
  not launch invasions of their own. Who buys their slaves (the UC's farms and mines; whether the Holy Nation trades for them too) is
  checked against the game data before it becomes a rule.
- **Everyone else** keeps what it holds and does not fight Dustlife's war. Outlaws and bandits stay with The Wasteland Remembers and
  the game; in a later phase bandits may raid caravans without holding land, agreed with The Wasteland Remembers.
- **Map:** the base game's own (a fresh Meitou game): 290 places, 36 bases. The faction list is a setting, so more factions (the
  Hives, the Tech Hunters, the Nomads) are a configuration change later.

## In play

- Factions take and lose towns, camps, outposts and ruins across the map, with or without the player.
- Each town's garrison grows from its surplus; a town that cannot feed its garrison loses men; a besieged town produces nothing.
- When a base has more men than it needs, it sends the surplus to take the best target nearby: sieges, a town that falls, a new garrison.
- Caravans carry stock between a faction's places; raiding them starves the town at the end of the road (planned, section "Economy").
- Every band has a named leader with a record (battles, kills, places taken, battle scars, an epithet). Each faction's war host follows its
  most renowned living captain, its warlord.
- The news reaches the player through The Wasteland Remembers (town talk, gossip, Atlas, Recap), told once.

## World model

| Game place | Dustlife kind | Garrison cap | Resource points |
| --- | --- | --- | --- |
| Town | base | 4 | 3 |
| Military base | base | 4 | 2 |
| Village, slave camp | resource point | 2 | 2 |
| Prison, outpost | resource point | 2 | 1 |
| Ruins | territory | 1 | 1 |
| Point of interest | territory | 1 | 0 |
| Nest | lair (animals); a people's camp counts as territory | 2 | 0 |

A place held by a faction the simulation does not run (a boss hideout) is locked: never a target. On the base game plus several large world
mods this makes 420 places (38 bases, 119 resource points, 263 territories, 145 locked) and 64 factions.

## The place graph (nodes and edges)

Dustlife doesn't build a graph of its own: it reads Logistics Core's (#59). Today a place in the simulation is only a point: bands walk to a
target in a straight line, and a base scores targets by priority over straight-line distance squared, so a town across a mountain range
counts as near. Logistics Core fixes that for every mod at once:
- **Nodes:** every placed town, `town:<InstanceId>`, with its owner and the owners its town versions allow (vanilla node seeding,
  Meitou-Modding-Ecosystem/LogisticsCore `docs/vanilla-nodes.md`).
- **Edges:** which places the game's own roads join, and the length walked (vanilla edge seeding, Meitou-Modding-Ecosystem/LogisticsCore#5,
  `docs/vanilla-edges.md`). A route through a third town is two edges, places within the game's straight-walk distance are near edges, and
  islands no road reaches join the rest overland. On the base game: 226 ownable places, 618 edges, one connected graph.

Dustlife's part is what the edges **mean** for the war, as rules it registers on top of that graph:

| Use | Rule |
| --- | --- |
| Targets | A base scores targets by **road distance** (the sum of edge costs), not straight-line distance. |
| Fronts | The front is the set of edges whose two ends are held by factions at war. The Bast Front is then computed, not drawn: the contested edges round Okran's Shield and Stack. |
| Supply | Caravans travel along edges between a faction's places. An edge held by an enemy band, or a caravan raided on it, is **cut**: stock stops reaching the place at the far end (the economy's "cut road", #46). |
| Sieges | A place is **cut off** when every edge to a friendly place is cut. A cut-off place gets no supply and no relief bands. |
| Chokepoints | A place with few edges that joins two parts of a faction's graph is a natural strongpoint: targets can weigh it higher. The Hub is one: the only node between Stack and Squin (#90). |
| News | "The road from X to Y is cut" and "Z is cut off" are events for The Wasteland Remembers to tell. |

Anything that changes what an edge costs (danger, tolls, a hostile player base on the road, #90) goes into Logistics Core's edge costs
as a policy, so caravans of every mod see the same roads.

How it is checked (each rule logs when it fires and is counted, as every Dustlife rule):
- **Behaviour, balance runner:** with road distance, invasions follow roads and no band targets a place across impassable ground; a cut
  edge lowers the far place's stock within the next supply step. Falsified if the front and capture counts don't change between straight
  and road distance (then the graph adds nothing), or if cut places never starve.
- **The graph itself** is Logistics Core's to check (its tests: a route through a third town is two edges, islands join the rest, the
  result doesn't depend on input order).

## War rules (today)

- **Seeding:** 2 garrison bands per base, 1 per resource point.
- **Refill:** a base adds a garrison band on a timer until it holds its cap; the richer the faction (its share of all resource points), the
  shorter the timer. A hostile base never spawns close to the player.
- **Surplus becomes an army:** each base scores two targets (priority over distance squared, shared between allies) and, once it holds its
  invasion size plus half its cap, sends the surplus.
- **Ownership by presence:** a band never settles on an enemy-held place; it besieges the edge. Sieges wear morale. A town's own residents
  hold it until it has been besieged with no defenders for 12 game hours.
- **Patrols** take empty territory; a faction with no base left gets a comeback wave, never in front of the player.
- **Combat** between bands: rounds with morale breaks; a leader may fall, or come out scarred and tougher.

## What the balance runner found

**The three majors on the base map** (5 seeds x 90 days, under 1 s each): something happens every day (about 3 invasions and 6 routs a
day), but the war still does not move: the same 109 bands from day 2 to day 90, none raised and none killed, and only 15 to 18 places
change hands. Invasions arrive, besiege, lose heart and go home without a battle. The UC's apparent early loss (27 places to 14 on day
1) is a rule, not a defeat: its 15 ruins and points of interest start with no garrison, and an unguarded place falls to nobody on the
first tick. The first economy slice changes that rule: a claim holds until another faction settles there.

**The whole map with every faction:** 
the runner builds the map as the game does and runs it hour by hour (90 game days in about 2.6 s). Five seeds: about 115 of 420 places
change hands in the first five days, 19 factions lose every place, and **from day 7 nothing happens**: no band spawns, none dies. The seeded
garrisons already exceed the war's band budget, so no base refills or has a surplus, and settled bands never die. A larger cap only moves
the stall. The war has no engine: spawning is a capped timer and nothing costs anything. The economy is the fix.

## Economy (planned, first iteration)

Each rule ships with a falsifier the runner checks over 5 seeds x 90 days.

1. **Food per place:** towns and villages produce by kind and population; every band eats. Falsifier: with production off, every garrison
   shrinks within N days.
2. **Garrisons from surplus, not a timer:** a base adds a band only when its stock covers that band's upkeep for a set number of days.
   Falsifier: events every day through day 90 with the same caps (the stall is gone).
3. **Hunger:** a place short of food loses bands; a besieged place produces nothing. Falsifier: a siege longer than the stock ends in
   surrender or a sally.
4. **Caravans:** bands carrying stock between a faction's places along the roads; a raid on one is a war target. Falsifier: cutting a town's
   only route causes a shortage there within days.
5. **Growth stages** (later): a lasting surplus grows a town a stage, a lasting shortage shrinks it.

The original game has no economy to read: its faction wealth function is a stub that returns 1, prosperity is hardly read, and towns never
send squads out by themselves (MeitouClient `docs/game/economy.md`, `factions-squads-towns.md`). Dustlife models its own.

## On Meitou

- **Tick:** a system (`AddSystem`, `SlowWorld`) steps the simulation in game hours; state in the save through `IModSaveData`; settings
  through `AddSettings`.
- **Bands as real platoons:** the engine's world hooks fit directly. `PopulationSystem.SpawnPlatoon` makes a band an unloaded platoon of a
  SQUAD_TEMPLATE, kept off screen and saved; `Platoon.ModTag` (`pza.dustlife:<band id>`) finds it again after a load;
  `HoldPlatoon` + `SendPlatoon` move it off screen toward a target by the roads. Dustlife then decides where bands go and what happens at a
  siege; the engine walks them, and they are real squads when the player meets them.
- **Randomness:** the engine's hashed RNG (`Rng.Hash`). The C++ reference switches to the same scheme first, so both draw identical numbers.
- **Map:** the engine's own towns and their owners (`PopulationData`), relations from the game.
- **Tests:** synthetic worlds (`Dustlife.Tests`), plus fixtures from the C++ balance runner: the same map and seed must give the same numbers.

## With the other mods

- **News to The Wasteland Remembers** (issue #23): Dustlife reports, at the moment it happens: `town-fell`, `siege-began`, `siege-broken`,
  `raid-sent` (surplus sets out), `garrison-grew`, `town-short` (shortage), `caravan-lost`, `warlord-fell`, `warlord-rose`. Place by town
  name, faction by name, a weight from 0 to 100.
- **Named leaders** (issue #25): Dustlife marks its leaders as "leave alone" when it names them.
- **One hunter at a time** (issue #24): before sending a band after the player, Dustlife checks The Wasteland Remembers' `HuntUnderway()` and
  Rivalry Core's hunt state, and publishes its own.
- **Rivalry Core:** its hunts, raids and reinforcements answer the player's setup; Dustlife's war is between factions. Open: which mod owns a
  rival leader who could be both, and whether Dustlife's raids on player towns should go through Rivalry Core's rules instead of its own.

## Fronts and world states (the Bast Front)

The long-term goal is a world that "moves on with or without you": towns that change hands, grow and fall, and a first front in Bast.
It has three layers, built bottom-up:

1. **World facts the data can use.** Kenshi's world states only test unique characters (dead, alive, jailed) and the player's relations;
   nothing in the data can say "the UC is at war with the Holy Nation", "the Shek hold this town" or "day 50 has passed". On Meitou the
   fix is an engine hook: a mod answers tagged world states (each WORLD_EVENT_STATE has a free `notes` field to tag). Then any data mod
   can gate town versions, squads and campaigns on facts a code mod keeps, exactly as it gates them on unique characters today.
2. **The Bast Front (content).** Towns in Bast that can change hands, one override per holder, squads and raids gated on the front's
   states. Reactive World's Deadcat (Cannibal Capital, changed in stages by switches) is the pattern. This is FCS data, so it works on
   both runtimes; it needs town versions and building sets, which is where models come in.
3. **Dustlife moves the facts.** A Warfare instance scoped to the Bast region runs the front; its captures, shortages and growth set the
   facts of layer 1, and the map redraws as each area loads (a town re-picks its version only when its area loads, forward along its
   current version's list).

How the group fits: Dustlife keeps the facts; the engine hook lets data read them; content mods draw the versions; The Wasteland
Remembers tells the news and its Atlas shows who holds each town; Rivalry Core's reactions to the player can read the same facts (a
faction at war at the front has fewer men to send after the player).

Data findings that carry over unchanged (they come from the game's records): 292 world states in a large load order, 130 towns that can
change, 513 reachable town versions; the Holy Nation, Shek and UC leaders tie most of them together, which makes Bast the natural first
front. A town re-picks its version only when its area loads, the override with the most conditions wins, and equal ones keep list order
(MeitouClient implements the same rule).

## Port plan

One PR each, into `beta`:
1. This document, a README row and a CODEOWNERS line; the mod skeleton (`meitou.json`, empty system).
2. The core world (places, factions, bands, time, combat) and Warfare's garrisons and resources, on synthetic-world tests.
3. The balance runner and its fixtures from the C++ reference.
4. On Logistics Core's graph: targets by road distance, fronts and cut roads.
5. Bands as real platoons through the world hooks.
6. The economy rules, one at a time, each with its falsifier.

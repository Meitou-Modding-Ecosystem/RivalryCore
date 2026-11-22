using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;

namespace Rivalry;

/// <summary>
/// A small squad sneaks into a player town to steal (docs/rivalry-core.md "Burgle"). Texts: <c>template</c> (a SQUAD_TEMPLATE). Numbers:
/// <c>goal</c> in cats (1000), <c>hours</c> before it gives up (12), <c>minRadius</c> (600), <c>maxRadius</c> (1200), and skill ranges
/// such as <c>stealthMin</c>/<c>stealthMax</c> (<see cref="SquadDriver.RollSkills"/>),
/// <c>salePrice</c> for a captive (1000). The run keeps <c>loot</c>, <c>last</c> (the hour of its last step) and its spawn point.
/// </summary>
public sealed class BurgleBehaviour : IBehaviour
{
    /// <summary>
    /// The phases: 0 spawning, 1 waiting for the platoon to load, 2 stealing from containers, then from pockets, 3 fighting once seen, 4 carrying
    /// a downed character to a buyer, 5 leaving.
    /// </summary>
    public const int Spawning = 0, Loading = 1, Stealing = 2, Fighting = 3, Abducting = 4, Leaving = 5;

    /// <summary>
    /// How long a spawn is tried, a platoon may take to load, and a squad may take to walk off, in game hours.
    /// </summary>
    public const double SpawnWindowHours = 24, LoadWindowHours = 24, LeaveWindowHours = 6, DeliverWindowHours = 72;

    /// <summary>
    /// The hunger level a carried captive is kept at (of 3), and how near a buyer's town centre the sale is made.
    /// </summary>
    public const float CaptiveFedTo = 1.5f, SaleRadius = 400;

    public string Name => BuiltIn.Burgle;

    public PartyKind Party => PartyKind.Hostile;

    public string Start(BehaviourContext context, BehaviourRequest request)
    {
        if (context.Population is null) return "burgle: no population";
        if (request.Args.Text("template") is null) return "burgle: no template";
        if (context.Report.TownOf(request.Target.Town) is null) return $"burgle: no town {request.Target.Town}";

        var run = context.Begin(this, request, context.World.GameHours + SpawnWindowHours);
        Step(context, run);
        return $"burgle: run {run.Id} against town {request.Target.Town}" + (run.Phase == Spawning ? ", waiting for a spot" : $", platoon {run.Platoon}");
    }

    public bool Step(BehaviourContext context, BehaviourRun run)
    {
        double now = context.World.GameHours;
        float seconds = (float)Math.Max(0, (now - run.Args.Number("last", now)) * World.SecondsPerGameHour);
        run.Args.Set("last", now);
        if (run.Phase >= Stealing) SquadDriver.Keep(context.World, run.Platoon);

        return run.Phase switch
        {
            Spawning => Spawn(context, run),
            Loading => Load(context, run),
            Stealing => Steal(context, run, seconds),
            Fighting => Fight(context, run),
            Abducting => Deliver(context, run),
            _ => Leave(context, run),
        };
    }

    #region Arriving

    /// <summary>
    /// Phase 0: spawns the platoon in the ring round the town and holds it; no spot yet: tried again until the window closes.
    /// </summary>
    static bool Spawn(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (context.Population is not { } population || context.Report.TownOf(run.Town) is not { } town) return false;

        var at = town.Position.Flat();
        int site = VisitBehaviour.NearestSite(context.Data, at);
        int platoon = site < 0 ? -1 : population.SpawnPlatoon(world, run.Faction, run.Args.Text("template")!, site, at,
            (float)run.Args.Number("minRadius", 600), (float)run.Args.Number("maxRadius", 1200));
        if (platoon < 0) return site >= 0 && world.GameHours < run.Until;

        PopulationSystem.HoldPlatoon(world, platoon, true);
        var spot = world.Platoons.Find(platoon)!.Position;
        run.Args.Set("homeX", spot.X).Set("homeZ", spot.Y);
        run.Platoon = platoon;
        run.Phase = Loading;
        run.Until = world.GameHours + LoadWindowHours;
        return true;
    }

    /// <summary>
    /// Phase 1: once the squad is in the world its members are taken from the AI, set sneaking and given their rolled skills; the leader gets the
    /// run's evidence (<see cref="Evidence.Plant"/>).
    /// </summary>
    static bool Load(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (world.Platoons.Find(run.Platoon) is null) return false;
        if (SquadDriver.SquadOf(world, run.Platoon) is not { } squad) return world.GameHours < run.Until || SquadDriver.GiveBack(world, run.Platoon);

        SquadDriver.Hold(world, run.Platoon, squad, held: true, sneak: true);
        SquadDriver.RollSkills(context, run, squad);
        Evidence.Plant(context, run, squad, CrimeCode.Burglary);
        run.Phase = Stealing;
        run.Until = world.GameHours + run.Args.Number("hours", 12);
        return true;
    }

    #endregion

    #region Stealing

    /// <summary>
    /// Phase 2: each thief goes for the most valuable thing left in the town's containers (a locked one is picked first), then for the pockets
    /// of the people in it; seen, the squad fights; the goal reached, or nothing left and time up, it leaves.
    /// </summary>
    static bool Steal(BehaviourContext context, BehaviourRun run, float seconds)
    {
        var world = context.World;
        if (SquadDriver.SquadOf(world, run.Platoon) is not { } squad) return SquadDriver.GiveBack(world, run.Platoon);

        var thieves = SquadDriver.Able(world, squad);
        if (thieves.Count == 0) return Release(context, run, squad);
        if (Seen(context, squad, thieves)) return StartFight(context, run, squad, thieves);
        if (run.Args.Number("loot") >= run.Args.Number("goal", 1000) || world.GameHours >= run.Until) return StartLeaving(context, run, squad);

        var chests = Chests(context, run.Town);
        for (int k = 0; k < thieves.Count; k++)
        {
            if (chests.Count > 0) Rob(context, run, thieves[k], chests[k % chests.Count], seconds);
            else if (!Pickpocket(context, run, thieves[k], k)) return StartFight(context, run, squad, thieves);
        }

        return true;
    }

    /// <summary>
    /// Whether a thief has been spotted by one of the player's characters, is in a fight, or was caught at a crime.
    /// </summary>
    static bool Seen(BehaviourContext context, Squad squad, List<int> thieves)
    {
        var table = context.World.Characters;
        var aggro = context.System<AggroSystem>();
        foreach (int thief in thieves)
        {
            var cold = table.Cold(thief)!;
            if (cold.InCombat || cold.Pending is { Timer: >= CrimeSystem.CaughtTimer }) return true;
            if (aggro is null) continue;

            var id = table.IdOf(thief);
            foreach (var watcher in context.Report.Characters)
            {
                if (!watcher.Down && table.TryResolveNext(watcher.Id, out int w) && aggro.Spotted(w, id)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The player's containers in the town that hold something, the one with the most valuable stack first (ties by id).
    /// </summary>
    static List<Fixture> Chests(BehaviourContext context, int town)
    {
        var world = context.World;
        int player = world.Player.Faction;
        var towns = world.Buildings.Towns.Where(t => t.Owner == player).ToList();
        return [.. world.Fixtures.All
            .Where(f => f.Owner == player && f.Contents is { } box && box.All().Any() && Inspector.TownAt(towns, f.Position) == town)
            .Select(f => (Fixture: f, Best: f.Contents!.All().Max(context.ValueOf)))
            .OrderByDescending(x => x.Best)
            .ThenBy(x => x.Fixture.Id, StringComparer.Ordinal)
            .Select(x => x.Fixture)];
    }

    /// <summary>
    /// One thief at one container: walks to it, picks its lock (a Burglary a witness answers, slow as any lockpicking), then takes its most
    /// valuable stack that fits.
    /// </summary>
    static void Rob(BehaviourContext context, BehaviourRun run, int thief, Fixture chest, float seconds)
    {
        var world = context.World;
        if (!SquadDriver.WalkTo(world, thief, chest.Position)) return;

        var crimes = context.System<CrimeSystem>();
        if (chest.Lock is { EffectivelyLocked: true })
        {
            context.System<LockSystem>()?.Picking.Step(world, thief, chest, seconds, crimes);
            return;
        }

        if (world.Characters.Cold(thief)?.Inventory is not { } bag) return;

        foreach (var item in chest.Contents!.All().OrderByDescending(context.ValueOf).ThenBy(i => i.Record, StringComparer.Ordinal).ToList())
        {
            if (!Move(chest.Contents, bag, item)) continue;

            crimes?.SetPending(world, thief, CrimeCode.Theft, chest.Owner, CharacterId.None, CrimeSystem.MinTimer);
            run.Args.Set("loot", run.Args.Number("loot") + context.ValueOf(item));
            return;
        }
    }

    /// <summary>
    /// Moves a stack between inventories; put back when it does not fit.
    /// </summary>
    static bool Move(Inventory from, Inventory to, ItemInstance item)
    {
        if (!from.Remove(item)) return false;
        if (to.Add(item)) return true;

        from.Add(item);
        return false;
    }

    /// <summary>
    /// With no container left: thief <paramref name="k"/> goes for the pockets of a player character in the town (the game's pickpocket roll).
    /// False when it was caught.
    /// </summary>
    static bool Pickpocket(BehaviourContext context, BehaviourRun run, int thief, int k)
    {
        var world = context.World;
        var table = world.Characters;
        var marks = context.Report.Characters
            .Where(c => c.Town == run.Town && table.TryResolveNext(c.Id, out int s) && table.Cold(s)?.Inventory?.All().Any() == true)
            .OrderByDescending(c => c.CarriedValue)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        if (marks.Count == 0 || context.System<CrimeSystem>() is not { } crimes) return true;

        var mark = marks[k % marks.Count];
        int owner = mark.Id.Slot;
        if (!SquadDriver.WalkTo(world, thief, table.Next[owner].Position, redirect: true)) return true;
        if (table.Cold(thief)?.Inventory is not { } bag || table.Cold(owner)?.Inventory is not { } pockets) return true;

        var item = pockets.All()
            .Where(i => !InventoryCommands.IsEquipmentSection(i.Section))
            .OrderByDescending(context.ValueOf)
            .ThenBy(i => i.Record, StringComparer.Ordinal)
            .FirstOrDefault();
        if (item is null) return true;
        if (crimes.Pickpocket(world, thief, owner, item, CrimeSystem.TakeKind.RightClick)) return false;

        if (Move(pockets, bag, item)) run.Args.Set("loot", run.Args.Number("loot") + context.ValueOf(item));
        return true;
    }

    #endregion

    #region Fighting and abducting

    /// <summary>
    /// Seen: the thieves stop sneaking and turn on the nearest standing player character in the town.
    /// </summary>
    static bool StartFight(BehaviourContext context, BehaviourRun run, Squad squad, List<int> thieves)
    {
        var world = context.World;
        var table = world.Characters;
        SquadDriver.Hold(world, run.Platoon, squad, held: true, sneak: false);
        var from = table.Next[thieves[0]].Position;
        var foe = context.Report.Characters
            .Where(c => c.Town == run.Town && !c.Down && table.TryResolveNext(c.Id, out _))
            .OrderBy(c => Vector3.DistanceSquared(c.Position, from))
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (foe is not null)
        {
            world.Defer(new AttackOrder([.. thieves.Select(table.IdOf)], foe.Id) { Tick = world.Tick + 1, Source = TaskSource.Npc });
        }

        run.Phase = Fighting;
        run.Until = world.GameHours + run.Args.Number("hours", 12);
        return true;
    }

    /// <summary>
    /// Phase 3: while the fight lasts nothing is ordered. After it a free thief lifts a downed player character, and the squad carries it off; with
    /// nobody to take, or time up, it leaves.
    /// </summary>
    static bool Fight(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        var table = world.Characters;
        if (SquadDriver.SquadOf(world, run.Platoon) is not { } squad) return SquadDriver.GiveBack(world, run.Platoon);

        var thieves = SquadDriver.Able(world, squad);
        if (thieves.Count == 0) return Release(context, run, squad);
        if (thieves.Any(t => table.Cold(t)!.InCombat)) return true;
        if (thieves.Any(t => !table.Cold(t)!.Carrying.IsNone)) return StartDelivery(context, run, squad, thieves);
        if (world.GameHours >= run.Until || !Lift(context, run, thieves)) return StartLeaving(context, run, squad);

        return true;
    }

    /// <summary>
    /// Sends the first thief to lift the nearest downed player character in the town that a buyer would take; false when there is none.
    /// </summary>
    static bool Lift(BehaviourContext context, BehaviourRun run, List<int> thieves)
    {
        var world = context.World;
        var table = world.Characters;
        if (context.System<CarrySystem>() is not { } carry) return false;

        var from = table.Next[thieves[0]].Position;
        var market = SlaveMarket.For(context.Data);
        var body = context.Report.Characters
            .Where(c => c.Town == run.Town && c.Down && table.TryResolveNext(c.Id, out int s) && table.Cold(s) is { Race: { } race } cold
                && cold.CarriedBy.IsNone && market.NearestBuyer(c.Position, race.StringId, run.Faction) >= 0)
            .OrderBy(c => Vector3.DistanceSquared(c.Position, from))
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return body is not null && (table.Next[thieves[0]].Task == (byte)CharacterTask.PickUp || carry.BeginPickUp(world, thieves[0], body.Id));
    }

    /// <summary>
    /// A thief carries someone: the buyer is chosen (the nearest town whose faction keeps slaves of the captive's race) and the squad sets off.
    /// </summary>
    static bool StartDelivery(BehaviourContext context, BehaviourRun run, Squad squad, List<int> thieves)
    {
        var world = context.World;
        var table = world.Characters;
        int carrier = thieves.First(t => !table.Cold(t)!.Carrying.IsNone);
        if (!table.TryResolveNext(table.Cold(carrier)!.Carrying, out int captive) || table.Cold(captive) is not { Race: { } race })
            return StartLeaving(context, run, squad);

        int buyer = SlaveMarket.For(context.Data).NearestBuyer(table.Next[carrier].Position, race.StringId, run.Faction);
        if (buyer < 0) return StartLeaving(context, run, squad);

        run.Args.Set("buyer", buyer);
        run.Phase = Abducting;
        run.Until = world.GameHours + DeliverWindowHours;
        return true;
    }

    /// <summary>
    /// Phase 4: the carrier walks to the buyer's town with the others behind it; the captive does not starve on the way. There a member of the
    /// buyer's faction takes the captive over as its slave (the game's own hand-over) and the squad is paid. The body dropped or lost: it leaves.
    /// </summary>
    static bool Deliver(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        var table = world.Characters;
        if (SquadDriver.SquadOf(world, run.Platoon) is not { } squad) return SquadDriver.GiveBack(world, run.Platoon);

        var thieves = SquadDriver.Able(world, squad);
        int carrier = thieves.FirstOrDefault(t => !table.Cold(t)!.Carrying.IsNone, -1);
        int site = (int)run.Args.Number("buyer", -1);
        if (carrier < 0 || site < 0 || site >= context.Data.Sites.Count || world.GameHours >= run.Until) return StartLeaving(context, run, squad);

        if (table.TryResolveNext(table.Cold(carrier)!.Carrying, out int captive) && table.Cold(captive) is { } held) held.FedTo = CaptiveFedTo;

        var town = context.Data.Sites[site].Position;
        foreach (int thief in thieves) SquadDriver.WalkTo(world, thief, town, run: thief != carrier);
        if (SquadDriver.Distance(world, carrier, town) > SaleRadius) return true;

        return !Sell(context, run, squad, carrier, site) || StartLeaving(context, run, squad);
    }

    /// <summary>
    /// Hands the captive to the nearest able member of the buyer's faction; true when it was taken and the squad paid.
    /// </summary>
    static bool Sell(BehaviourContext context, BehaviourRun run, Squad squad, int carrier, int site)
    {
        var world = context.World;
        var table = world.Characters;
        int owner = context.Data.SiteFactions[site];
        var at = table.Next[carrier].Position;
        if (context.System<ArrestSystem>() is not { } arrests) return false;

        var guards = Enumerable.Range(0, table.HighWater)
            .Where(s => table.Next[s].Alive && table.Cold(s) is { IsPlayer: false, Medical.Incapacitated: false } c && c.Faction == owner
                && c.Carrying.IsNone && Vector3.DistanceSquared(table.Next[s].Position, at) <= SaleRadius * SaleRadius)
            .OrderBy(s => Vector3.DistanceSquared(table.Next[s].Position, at))
            .ThenBy(s => s);
        var captive = table.Cold(carrier)!.Carrying;
        foreach (int guard in guards)
        {
            if (!arrests.TakeOver(world, table.IdOf(guard), table.IdOf(carrier), enslave: true)) continue;

            if (table.TryResolveNext(captive, out int sold) && table.Cold(sold) is { } c) c.FedTo = null;
            squad.Money += (long)run.Args.Number("salePrice", 1000);
            return true;
        }

        return false;
    }

    #endregion

    #region Leaving

    /// <summary>
    /// The squad stops what it does and heads back to where it appeared, running.
    /// </summary>
    static bool StartLeaving(BehaviourContext context, BehaviourRun run, Squad squad)
    {
        SquadDriver.Hold(context.World, run.Platoon, squad, held: true, sneak: false);
        run.Phase = Leaving;
        run.Until = context.World.GameHours + LeaveWindowHours;
        return true;
    }

    /// <summary>
    /// Phase 5: at its spawn point, or with time up, the squad is given back to the game, which unloads or despawns it as any roaming one.
    /// </summary>
    static bool Leave(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (SquadDriver.SquadOf(world, run.Platoon) is not { } squad) return SquadDriver.GiveBack(world, run.Platoon);

        var thieves = SquadDriver.Able(world, squad);
        var home = new Vector3((float)run.Args.Number("homeX"), 0, (float)run.Args.Number("homeZ"));
        bool there = thieves.Count == 0;
        foreach (int thief in thieves) there |= SquadDriver.WalkTo(world, thief, home, run: true);

        return !there && world.GameHours < run.Until || Release(context, run, squad);
    }

    /// <summary>
    /// Gives the squad back to the game and ends the run.
    /// </summary>
    static bool Release(BehaviourContext context, BehaviourRun run, Squad squad)
    {
        SquadDriver.Hold(context.World, run.Platoon, squad, held: false, sneak: false);
        return false;
    }

    #endregion
}

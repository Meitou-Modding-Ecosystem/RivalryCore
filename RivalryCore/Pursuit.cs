using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Crime;

namespace Rivalry;

/// <summary>
/// A hit squad comes for one character (docs/rivalry-core.md "Pursue"): it travels from its faction's nearest base, appears off screen, walks to wherever
/// the quarry is now and attacks; once beaten its named leader is watched for its fate (<see cref="RivalLeaders"/>). Texts: <c>template</c> (a
/// SQUAD_TEMPLATE), <c>home</c> (town or region names separated by <c>|</c>, see <see cref="FactionBases"/>), <c>leaderNames</c>, lines
/// (<see cref="HunterLines"/>). Numbers: <c>hours</c> of the chase (24), <c>minRadius</c>/<c>maxRadius</c> (the options' spawn ring), and skill ranges
/// (<see cref="SquadDriver.RollSkills"/>). The run keeps <c>quarryName</c>, <c>travelHours</c>, <c>ended</c> and the leader's name and serial.
/// </summary>
public sealed class PursueBehaviour : IBehaviour
{
    #region Phases

    /// <summary>
    /// The phases: 0 spawning, 1 waiting for the platoon to load, 2 closing in, 3 attacking, 4 watching the beaten leader, 5 travelling from the base
    /// (numbered after the others so runs saved before it keep their meaning).
    /// </summary>
    public const int Spawning = 0, Loading = 1, Closing = 2, Attacking = 3, Aftermath = 4, Travelling = 5;

    /// <summary>
    /// How long a spawn is tried and a platoon may take to load, in game hours.
    /// </summary>
    public const double SpawnWindowHours = 24, LoadWindowHours = 24;

    /// <summary>
    /// How near the quarry the hunters stop walking and attack.
    /// </summary>
    public const float StrikeRange = 150;

    /// <summary>
    /// Ticks between looks at a beaten leader (one game second at 30 ticks a second; the look walks the character table).
    /// </summary>
    const int WatchEveryTicks = 30;

    /// <summary>
    /// The quarry of each run in this world's process, by run id; after a load found again by <c>quarryName</c>.
    /// </summary>
    readonly Dictionary<(World, int), CharacterId> _quarry = [];

    public string Name => BuiltIn.Pursue;

    public PartyKind Party => PartyKind.Hostile;

    public bool Underway(BehaviourRun run) => run.Phase != Aftermath;

    /// <summary>
    /// Whether the run is a hit squad still on its way or fighting (not yet in its aftermath): what limits a faction to one hit squad at a time.
    /// </summary>
    public static bool HitSquadUnderway(BehaviourRun run) => run.Behaviour == BuiltIn.Pursue && run.Phase != Aftermath;

    public string Start(BehaviourContext context, BehaviourRequest request)
    {
        if (context.Population is null) return "pursue: no population";
        if (request.Args.Text("template") is null) return "pursue: no template";
        if (!context.World.Characters.TryResolveNext(request.Target.Character, out int slot)) return "pursue: quarry gone";

        var run = context.Begin(this, request, context.World.GameHours);
        Aim(context, run, request.Target.Character, slot);
        Step(context, run);
        return $"pursue: run {run.Id} after {run.Args.Text("quarryName")}" + Describe(run);
    }

    /// <summary>
    /// The start's log line tail.
    /// </summary>
    static string Describe(BehaviourRun run) => run.Phase switch
    {
        Travelling => $", on the road {run.Args.Number("travelHours"):0.#} h",
        Spawning => ", waiting for a spot",
        _ => $", platoon {run.Platoon}",
    };

    /// <summary>
    /// Fixes the run's quarry, holds the grudge's floor at the hunt threshold, and sets it on the road for the travel time from its base.
    /// </summary>
    void Aim(BehaviourContext context, BehaviourRun run, CharacterId quarry, int slot)
    {
        _quarry[(context.World, run.Id)] = quarry;
        run.Args.Set("quarryName", context.World.Characters.Cold(slot)?.Name ?? "");
        context.Notoriety.HoldFloor(run.Faction, quarry, context.Options.HuntThreshold);

        var at = context.World.Characters.Next[slot].Position;
        double hours = Travel.Hours(context, run.Faction, at.Flat(), run.Args);
        run.Args.Set("travelHours", hours);
        run.Phase = hours > 0 ? Travelling : Spawning;
        run.Until = context.World.GameHours + (hours > 0 ? hours : SpawnWindowHours);
    }

    public bool Step(BehaviourContext context, BehaviourRun run)
    {
        if (run.Phase is Closing or Attacking) SquadDriver.Keep(context.World, run.Platoon);

        bool goes = run.Phase switch
        {
            Travelling => Arrive(context, run),
            Spawning => Spawn(context, run),
            Loading => Load(context, run),
            Aftermath => context.World.Tick % WatchEveryTicks != 0 || RivalLeaders.Watch(context, run, run.Until),
            _ => Chase(context, run),
        };
        if (!goes) Finish(context, run);
        return goes;
    }

    /// <summary>
    /// The run ends: the faction's hit squad counts as finished now (unless it already did when beaten), and its leader is free to be sent again.
    /// </summary>
    void Finish(BehaviourContext context, BehaviourRun run)
    {
        _quarry.Remove((context.World, run.Id));
        MarkEnded(context, run);
        RivalLeaders.Release(context, run);
    }

    /// <summary>
    /// Notes the faction's hit squad finished, once per run.
    /// </summary>
    static void MarkEnded(BehaviourContext context, BehaviourRun run)
    {
        if (run.Args.Number("ended") != 0 || RivalLeaders.FactionId(context, run.Faction) is not { } faction) return;

        context.Rivals.Ended(faction, context.World.GameHours);
        run.Args.Set("ended", 1);
    }

    #endregion

    #region Arriving

    /// <summary>
    /// Phase 5: on the road until the travel time is up, then spawning.
    /// </summary>
    bool Arrive(BehaviourContext context, BehaviourRun run)
    {
        if (context.World.GameHours < run.Until) return true;

        run.Phase = Spawning;
        run.Until = context.World.GameHours + SpawnWindowHours;
        return Spawn(context, run);
    }

    /// <summary>
    /// The run's quarry while it stands; none when it is down, dead or gone. After a load: the player character named <c>quarryName</c>, else the
    /// standing one its faction holds the largest grudge against.
    /// </summary>
    CharacterId Quarry(BehaviourContext context, BehaviourRun run)
    {
        if (!_quarry.TryGetValue((context.World, run.Id), out var id))
        {
            id = Named(context, run.Args.Text("quarryName")) ?? MostHated(context, run.Faction);
            _quarry[(context.World, run.Id)] = id;
        }

        var table = context.World.Characters;
        return table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Medical.Incapacitated: false } ? id : CharacterId.None;
    }

    /// <summary>
    /// The player character with the name; null when none.
    /// </summary>
    static CharacterId? Named(BehaviourContext context, string? name) =>
        context.Report.Characters.FirstOrDefault(c => c.Name == name) is { } found ? found.Id : null;

    /// <summary>
    /// The standing player character the faction holds the largest grudge against.
    /// </summary>
    static CharacterId MostHated(BehaviourContext context, int faction) => context.Report.Characters
        .Where(c => !c.Down)
        .OrderByDescending(c => context.Notoriety.Grudge(faction, c.Id))
        .ThenBy(c => c.Name, StringComparer.Ordinal)
        .Select(c => c.Id)
        .DefaultIfEmpty(CharacterId.None)
        .First();

    /// <summary>
    /// Phase 0: spawns the platoon in the ring round the quarry (beyond sight, inside the loaded zones), homed at its faction's base, and holds it.
    /// </summary>
    bool Spawn(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (context.Population is not { } population || Quarry(context, run) is not { IsNone: false } quarry) return false;

        var at = world.Characters.Next[quarry.Slot].Position.Flat();
        int site = HomeSite(context, run, at);
        float min = (float)run.Args.Number("minRadius", context.Options.HuntSpawnMinRadius);
        float max = (float)run.Args.Number("maxRadius", context.Options.HuntSpawnMaxRadius);
        int platoon = site < 0 ? -1 : population.SpawnPlatoon(world, run.Faction, run.Args.Text("template")!, site, at, min, max);
        if (platoon < 0) return site >= 0 && world.GameHours < run.Until;

        PopulationSystem.HoldPlatoon(world, platoon, true);
        run.Platoon = platoon;
        run.Phase = Loading;
        run.Until = world.GameHours + LoadWindowHours;
        return true;
    }

    /// <summary>
    /// The town the squad belongs to (where it heads once given back): its faction's nearest base town, else the town nearest the quarry.
    /// </summary>
    static int HomeSite(BehaviourContext context, BehaviourRun run, Vector2 at) =>
        Travel.Base(context, run.Faction, at, run.Args) is { Site: >= 0 } home ? home.Site : VisitBehaviour.NearestSite(context.Data, at);

    /// <summary>
    /// Phase 1: once the squad is in the world it is taken from the AI and remembers its quarry as an enemy; its leader is named from the roster
    /// (<see cref="RivalLeaders.Assign"/>) and gets the run's evidence (<see cref="Evidence.Plant"/>).
    /// </summary>
    bool Load(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (world.Platoons.Find(run.Platoon) is null) return false;
        if (SquadDriver.SquadOf(world, run.Platoon) is not { } squad) return world.GameHours < run.Until || SquadDriver.GiveBack(world, run.Platoon);

        SquadDriver.Hold(world, run.Platoon, squad, held: true);
        SquadDriver.RollSkills(context, run, squad);
        RivalLeaders.Assign(context, run, squad);
        Evidence.Plant(context, run, squad, CrimeCode.Assault);
        if (Quarry(context, run) is { IsNone: false } quarry)
            context.System<DialogueSystem>()?.Memory.Flag(world, squad.Id, quarry, MemoryTag.TempEnemy);

        run.Phase = Closing;
        run.Until = world.GameHours + run.Args.Number("hours", 24);
        return true;
    }

    #endregion

    #region Fighting

    /// <summary>
    /// Phases 2 and 3: the hunters run to where the quarry is now; in range they are ordered to attack and left to the fight. The quarry down or gone
    /// (won), every hunter down (beaten) or time up (broke off): the squad is given back to the game and the fight is settled (<see cref="Settle"/>).
    /// </summary>
    bool Chase(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (SquadDriver.SquadOf(world, run.Platoon) is not { } squad) return SquadDriver.GiveBack(world, run.Platoon);

        var hunters = SquadDriver.Able(world, squad);
        var quarry = Quarry(context, run);
        if (hunters.Count == 0) return Settle(context, run, squad, RivalOutcome.Beaten);
        if (quarry.IsNone) return Settle(context, run, squad, RivalOutcome.Won);
        if (world.GameHours >= run.Until) return Settle(context, run, squad, RivalOutcome.Withdrew);

        var at = world.Characters.Next[quarry.Slot].Position;
        if (!hunters.Any(h => SquadDriver.Distance(world, h, at) <= StrikeRange))
        {
            foreach (int hunter in hunters) SquadDriver.WalkTo(world, hunter, at, run: true, redirect: true);
            run.Phase = Closing;
            return true;
        }

        Strike(world, hunters, quarry);
        run.Phase = Attacking;
        return true;
    }

    /// <summary>
    /// Orders the hunters not yet fighting to attack the quarry, at the next tick.
    /// </summary>
    static void Strike(World world, List<int> hunters, CharacterId quarry)
    {
        var table = world.Characters;
        var idle = hunters.Where(h => !table.Cold(h)!.InCombat).Select(table.IdOf).ToList();
        if (idle.Count > 0) world.Defer(new AttackOrder(idle, quarry) { Tick = world.Tick + 1, Source = TaskSource.Npc });
    }

    /// <summary>
    /// The fight is over: the squad goes back to the game, the faction's hit squad counts as finished, and the leader is credited. Beaten in sight of a
    /// player character, the grudge's floor is let go. A beaten squad's leader is then watched (phase 4); otherwise the run ends.
    /// </summary>
    bool Settle(BehaviourContext context, BehaviourRun run, Squad squad, RivalOutcome outcome)
    {
        var world = context.World;
        string quarry = run.Args.Text("quarryName") ?? "";
        SquadDriver.Hold(world, run.Platoon, squad, held: false);
        MarkEnded(context, run);
        RivalLeaders.Credit(context, run, squad, outcome, quarry);
        if (outcome != RivalOutcome.Beaten) return false;

        if (Seen(context, squad)) ReleaseFloor(context, run, quarry);

        run.Phase = Aftermath;
        run.Until = world.GameHours + context.Options.AftermathHours;
        return RivalLeaders.Of(context, run) is not null;
    }

    /// <summary>
    /// Whether a standing player character is now within <see cref="RivalryOptions.SeenRange"/> of any member of the squad.
    /// </summary>
    static bool Seen(BehaviourContext context, Squad squad)
    {
        var table = context.World.Characters;
        var members = squad.Members.Where(id => table.TryResolveNext(id, out _)).Select(id => FlatAt(table, id)).ToList();
        foreach (var c in context.Report.Characters)
        {
            if (!table.TryResolveNext(c.Id, out int slot) || table.Cold(slot) is not { Medical.Incapacitated: false }) continue;
            if (members.Any(m => Vector2.Distance(m, FlatAt(table, c.Id)) <= context.Options.SeenRange)) return true;
        }

        return false;
    }

    /// <summary>
    /// A character's position now on the ground plane.
    /// </summary>
    static Vector2 FlatAt(CharacterTable table, CharacterId id) => table.Next[id.Slot].Position.Flat();

    /// <summary>
    /// Lets the quarry's grudge cool freely again; with the quarry not found, every floor the faction holds.
    /// </summary>
    static void ReleaseFloor(BehaviourContext context, BehaviourRun run, string quarry)
    {
        if (Named(context, quarry) is { } who) context.Notoriety.ReleaseFloor(run.Faction, who);
        else context.Notoriety.ReleaseFloors(run.Faction);
    }

    #endregion
}

/// <summary>
/// How long a hit squad is on the road (docs/rivalry-core.md "Pursue"): the distance from its faction's nearest base (<see cref="FactionBases"/>) to the
/// quarry over <see cref="RivalryOptions.HuntTravelUnitsPerHour"/>, between the least and most travel time; with no base,
/// <see cref="RivalryOptions.HuntTravelFallbackHours"/>.
/// </summary>
public static class Travel
{
    /// <summary>
    /// The travel time in game hours to a point.
    /// </summary>
    public static double Hours(BehaviourContext context, int faction, Vector2 to, BehaviourArgs args) =>
        Hours(context.Data, context.Options, faction, to, args.Text("home"));

    /// <summary>
    /// The travel time in game hours from the faction's base nearest a point (with <paramref name="home"/>'s names, <c>|</c>-separated) to it.
    /// </summary>
    public static double Hours(PopulationData data, RivalryOptions options, int faction, Vector2 to, string? home = null)
    {
        var names = FactionBases.NamesFor(data, faction, options, RivalLeaders.Split(home));
        if (FactionBases.Nearest(data, faction, names, to) is not { } from) return Math.Min(options.HuntTravelFallbackHours, options.HuntTravelMaxHours);

        double hours = Vector2.Distance(from.Position, to) / Math.Max(1, options.HuntTravelUnitsPerHour);
        return Math.Clamp(hours, Math.Min(options.HuntTravelMinHours, options.HuntTravelMaxHours), options.HuntTravelMaxHours);
    }

    /// <summary>
    /// The faction's base nearest a point, with the run's <c>home</c> names and <see cref="RivalryOptions.Homes"/>; null when it has none.
    /// </summary>
    public static FactionBase? Base(BehaviourContext context, int faction, Vector2 to, BehaviourArgs args)
    {
        var names = FactionBases.NamesFor(context.Data, faction, context.Options, RivalLeaders.Split(args.Text("home")));
        return FactionBases.Nearest(context.Data, faction, names, to);
    }
}

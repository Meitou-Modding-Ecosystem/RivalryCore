using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Locks;

namespace Rivalry;

/// <summary>
/// A hit squad's named leader through a run (docs/rivalry-core.md "Rival leaders"): named when the squad loads, credited with the fight's outcome, then
/// watched for its fate: killed, captured, handed to the law (which clears its faction's grudge) or left for dead. Run arguments it reads: text
/// <c>leaderNames</c> (names separated by <c>|</c>); it keeps <c>leaderName</c> and <c>leaderSerial</c>. Serial steps only.
/// </summary>
public static class RivalLeaders
{
    #region Naming

    /// <summary>
    /// The separator of a list in a text argument.
    /// </summary>
    public const char ListSeparator = '|';

    /// <summary>
    /// Gives the squad's leader (its first living member) a name from the faction's roster: a returning leader, or a new one from <c>leaderNames</c>
    /// (without that argument, under the name the character was made with).
    /// The character takes the name; the run keeps it and the character's portrait serial. Null when the squad has no leader.
    /// </summary>
    public static RivalLeader? Assign(BehaviourContext context, BehaviourRun run, Squad squad)
    {
        var world = context.World;
        if (FactionId(context, run.Faction) is not { } faction || SquadDriver.FirstLiving(world, squad) is not { PortraitSerial: { } serial } body) return null;

        var names = Split(run.Args.Text("leaderNames"));
        if (names.Count == 0 && body.Name.Length > 0) names = [body.Name];   // no roster of names: the leader keeps the name it was made with
        var leader = context.Rivals.Pick(faction, names, context.Roll($"leader{run.Id}"), s => s != 0 && Find(world, s) is not null);
        body.Name = leader.Name;
        leader.Serial = serial;
        leader.Busy = true;
        leader.Dispatches++;
        run.Args.Set("leaderName", leader.Name);
        run.Args.Set("leaderSerial", serial);
        return leader;
    }

    /// <summary>
    /// A list argument's entries, trimmed, empty ones dropped.
    /// </summary>
    public static IReadOnlyList<string> Split(string? list) =>
        list is null ? [] : list.Split(ListSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The run's leader in the roster; null when it named none.
    /// </summary>
    public static RivalLeader? Of(BehaviourContext context, BehaviourRun run) =>
        run.Args.Text("leaderName") is { } name && FactionId(context, run.Faction) is { } faction ? context.Rivals.Find(faction, name) : null;

    /// <summary>
    /// A faction's string id; null for none.
    /// </summary>
    public static string? FactionId(BehaviourContext context, int faction) =>
        context.Data.FactionAt(faction)?.Id;

    #endregion

    #region Outcome

    /// <summary>
    /// Credits the run's leader with how its fight ended and how many of the squad lie dead; the leader stays busy while its fate is watched.
    /// </summary>
    public static void Credit(BehaviourContext context, BehaviourRun run, Squad squad, RivalOutcome outcome, string quarry)
    {
        if (Of(context, run) is not { } leader) return;

        if (outcome == RivalOutcome.Won) leader.Wins++;
        else if (outcome == RivalOutcome.Beaten) leader.Losses++;
        else if (outcome == RivalOutcome.Withdrew) leader.Withdrawals++;

        leader.LastComradesLost = DeadMembers(context.World, squad);
        leader.ComradesLost += leader.LastComradesLost;
        Note(context, leader, outcome);
        leader.LastQuarry = quarry;
    }

    /// <summary>
    /// The run is over for its leader: it may be sent again.
    /// </summary>
    public static void Release(BehaviourContext context, BehaviourRun run)
    {
        if (Of(context, run) is { } leader) leader.Busy = false;
    }

    /// <summary>
    /// The squad's members that are dead (or gone from the world after dying).
    /// </summary>
    static int DeadMembers(World world, Squad squad)
    {
        var table = world.Characters;
        return squad.Members.Count(id => table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Medical.Dead: true });
    }

    /// <summary>
    /// Records an outcome as the leader's last, now.
    /// </summary>
    static void Note(BehaviourContext context, RivalLeader leader, RivalOutcome outcome)
    {
        leader.LastOutcome = outcome;
        leader.LastHour = context.World.GameHours;
    }

    #endregion

    #region Fate

    /// <summary>
    /// One look at a beaten squad's leader; false when its fate is settled (the run ends). Dead: killed. In a lawful faction's cage, or its bounty
    /// claimed, alive: handed to the law, which clears its faction's grudge (<see cref="TurnIn"/>). Carried or caged by the player: captive, watched
    /// as long as it is held. Free again after that: escaped. Gone from the world, or still free when <paramref name="until"/> passes: left for dead.
    /// </summary>
    public static bool Watch(BehaviourContext context, BehaviourRun run, double until)
    {
        if (Of(context, run) is not { } leader) return false;

        var world = context.World;
        var body = Find(world, leader.Serial);
        if (body is null) return Settle(context, leader, LeftOrEscaped(leader));
        if (body.Medical is { Dead: true }) return Settle(context, leader, RivalOutcome.Killed, RivalFate.Dead);
        if (world.Player.Faction >= 0 && body.Faction == world.Player.Faction) return Settle(context, leader, RivalOutcome.Recruited, RivalFate.Recruited);
        if (HandedToLaw(world, body, leader.Serial))
        {
            TurnIn(context, run, leader);
            return false;
        }
        if (HeldByPlayer(world, body)) return Hold(context, leader);
        if (leader.Fate == RivalFate.Captive) return Settle(context, leader, RivalOutcome.Escaped);

        return world.GameHours < until || Settle(context, leader, RivalOutcome.LeftForDead);
    }

    /// <summary>
    /// What a leader that vanished from the world counts as: escaped when it was held, else left for dead (its platoon unloaded with it lying there).
    /// </summary>
    static RivalOutcome LeftOrEscaped(RivalLeader leader) => leader.Fate == RivalFate.Captive ? RivalOutcome.Escaped : RivalOutcome.LeftForDead;

    /// <summary>
    /// Settles the leader's fate (free unless given) and counts it; false (the watch ends).
    /// </summary>
    static bool Settle(BehaviourContext context, RivalLeader leader, RivalOutcome outcome, RivalFate fate = RivalFate.Free)
    {
        if (outcome == RivalOutcome.LeftForDead) leader.LeftForDead++;
        else if (outcome == RivalOutcome.Escaped) leader.Escapes++;

        Note(context, leader, outcome);
        leader.Fate = fate;
        leader.Busy = false;
        return false;
    }

    /// <summary>
    /// The leader is in the player's hands: counted once per capture, watched on.
    /// </summary>
    static bool Hold(BehaviourContext context, RivalLeader leader)
    {
        if (leader.Fate == RivalFate.Captive) return true;

        leader.Captures++;
        leader.Fate = RivalFate.Captive;
        Note(context, leader, RivalOutcome.Captured);
        return true;
    }

    /// <summary>
    /// The player handed the leader to the law: its faction forgets every grudge (floors too), and the reckoning is kept for later.
    /// </summary>
    static void TurnIn(BehaviourContext context, BehaviourRun run, RivalLeader leader)
    {
        float cleared = context.Notoriety.ClearFaction(run.Faction);
        context.Rivals.Reckon(new Reckoning(leader.Faction, leader.Name, run.Args.Text("quarryName") ?? "", context.World.GameHours, cleared));
        Note(context, leader, RivalOutcome.TurnedIn);
        leader.Fate = RivalFate.Jailed;
        leader.Busy = false;
    }

    /// <summary>
    /// Whether the living leader is with the law: in a cage of a faction that is neither the player's nor lawless, or a bounty on it was claimed (the
    /// bounty desk pays only for a body the player carries in).
    /// </summary>
    static bool HandedToLaw(World world, CharacterCold body, uint serial)
    {
        if (body.Bounties?.Entries.Any(e => e.Claimed) == true) return true;
        if (!body.IsImprisoned || CageOf(world, serial) is not { } cage) return false;

        var crimes = world.Systems.OfType<CrimeSystem>().FirstOrDefault();
        return cage.Owner >= 0 && cage.Owner != world.Player.Faction && crimes?.Factions.Lawless(cage.Owner) == false;
    }

    /// <summary>
    /// Whether one of the player's characters carries the leader, or it sits in the player's own cage.
    /// </summary>
    static bool HeldByPlayer(World world, CharacterCold body)
    {
        var table = world.Characters;
        if (!body.CarriedBy.IsNone && table.TryResolveNext(body.CarriedBy, out int carrier) && table.Cold(carrier) is { } c)
            return c.IsPlayer || c.Faction == world.Player.Faction;

        return body.IsImprisoned && CageOf(world, body.PortraitSerial ?? 0) is { } cage && cage.Owner == world.Player.Faction;
    }

    /// <summary>
    /// The cage whose occupant has the portrait serial; null when none.
    /// </summary>
    static Fixture? CageOf(World world, uint serial)
    {
        var table = world.Characters;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Occupant.IsNone || !table.TryResolveNext(f.Occupant, out int slot)) continue;
            if (table.Cold(slot)?.PortraitSerial == serial) return f;
        }

        return null;
    }

    #endregion

    #region Finding

    /// <summary>
    /// The character in the world, alive or a corpse, with the portrait serial (not the player's); null when none (a removed slot has no record).
    /// </summary>
    public static CharacterCold? Find(World world, uint serial)
    {
        if (serial == 0) return null;

        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Cold(i) is { IsPlayer: false } c && c.PortraitSerial == serial) return c;
        }

        return null;
    }


    #endregion
}

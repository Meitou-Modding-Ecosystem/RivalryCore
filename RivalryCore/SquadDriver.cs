using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Rivalry;

/// <summary>
/// What a behaviour that walks a squad itself needs (docs/rivalry-core.md "Driving a squad"): the squad of a run's platoon, its members that can
/// act, and the engine calls that hold, release and move them. Serial steps only.
/// </summary>
public static class SquadDriver
{
    /// <summary>
    /// How close (ground plane) counts as there, for a walk and for reaching a container or a body.
    /// </summary>
    public const float Reach = 20;

    /// <summary>
    /// A redirected walk keeps its path while its goal is within this of the new point: each new walk asks for a fresh path, so following a
    /// moving quarry every tick would ask for one per hunter per tick.
    /// </summary>
    public const float RedirectSlack = Reach / 2;

    /// <summary>
    /// The loaded squad of a platoon; null while the platoon is gone or not in the world.
    /// </summary>
    public static Squad? SquadOf(World world, int platoon) =>
        world.Platoons.Find(platoon) is { SquadId: >= 0 } p ? world.Squads.Find(p.SquadId) : null;

    /// <summary>
    /// The squad's first living member's record; null when none is alive.
    /// </summary>
    public static CharacterCold? FirstLiving(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var id in squad.Members)
            if (table.TryResolveNext(id, out int slot) && table.Next[slot].Alive && table.Cold(slot) is { } c) return c;

        return null;
    }

    /// <summary>
    /// The squad's members that can act (alive, not down, not carried, not caged), as slots in member order.
    /// </summary>
    public static List<int> Able(World world, Squad squad)
    {
        var table = world.Characters;
        var slots = new List<int>();
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int slot) || !table.Next[slot].Alive) continue;
            if (table.Cold(slot) is { Medical.Incapacitated: false, IsImprisoned: false } c && c.CarriedBy.IsNone) slots.Add(slot);
        }

        return slots;
    }

    /// <summary>
    /// Takes the platoon's movement from the population and the members from the AI (<paramref name="held"/>), or gives both back. Sneaking is
    /// set or cleared with it when <paramref name="sneak"/> says so.
    /// </summary>
    public static void Hold(World world, int platoon, Squad squad, bool held, bool? sneak = null)
    {
        PopulationSystem.HoldPlatoon(world, platoon, held);
        var table = world.Characters;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } c) continue;

            c.Engaged = held;
            if (sneak == true) c.Orders |= CharacterOrders.Sneak;
            else if (sneak == false) c.Orders &= ~CharacterOrders.Sneak;
        }
    }

    /// <summary>
    /// Gives each member the skills the run's arguments set a range for: a number named after a skill (<see cref="StatsEnumerated"/>, any
    /// case) with <c>Min</c>, and optionally <c>Max</c>, e.g. <c>stealthMin</c> 40 and <c>stealthMax</c> 60. Uniform per member; a skill without
    /// a range keeps the template's.
    /// </summary>
    public static void RollSkills(BehaviourContext context, BehaviourRun run, Squad squad)
    {
        var table = context.World.Characters;
        foreach (var (name, min) in run.Args.Numbers)
        {
            if (SkillOf(name) is not { } stat) continue;

            string stem = name[..^3];
            double max = Math.Max(min, run.Args.Number(stem + "Max", min));
            for (int k = 0; k < squad.Members.Count; k++)
            {
                if (!table.TryResolveNext(squad.Members[k], out int slot) || table.Cold(slot)?.Stats is not { } stats) continue;

                stats[stat] = (float)(min + (max - min) * context.Roll($"skills{run.Id}/{stem}/{k}"));
            }
        }
    }

    /// <summary>
    /// The skill an argument named <c>&lt;skill&gt;Min</c> sets; null for any other name.
    /// </summary>
    static StatsEnumerated? SkillOf(string name)
    {
        if (!name.EndsWith("Min", StringComparison.Ordinal) || name.Length == 3 || !char.IsLetter(name[0])) return null;

        bool known = Enum.TryParse(name[..^3], ignoreCase: true, out StatsEnumerated stat) && Enum.IsDefined(stat) && stat != StatsEnumerated.None;
        return known ? stat : null;
    }

    /// <summary>
    /// Holds a loaded platoon again: a hold is not saved, so a behaviour calls this at each step of a run that drives its squad.
    /// </summary>
    public static void Keep(World world, int platoon)
    {
        if (SquadOf(world, platoon) is { } squad) Hold(world, platoon, squad, held: true);
    }

    /// <summary>
    /// Gives a platoon whose squad is not in the world back to the population, and returns false (the run ends).
    /// </summary>
    public static bool GiveBack(World world, int platoon)
    {
        PopulationSystem.HoldPlatoon(world, platoon, false);
        return false;
    }

    /// <summary>
    /// The ground-plane distance from a character to a point.
    /// </summary>
    public static float Distance(World world, int slot, Vector3 to)
    {
        return Ground.FlatDistance(world.Characters.Next[slot].Position, to);
    }

    /// <summary>
    /// Walks the character to a point: true when it is there. A walk under way is left alone unless <paramref name="redirect"/> and its goal is
    /// more than <see cref="RedirectSlack"/> from the point (the goal moved).
    /// </summary>
    public static bool WalkTo(World world, int slot, Vector3 to, bool run = false, bool redirect = false)
    {
        if (Distance(world, slot, to) <= Reach) return true;

        ref readonly var hot = ref world.Characters.Next[slot];
        bool walking = hot.Task == (byte)CharacterTask.GoTo;
        if (walking && (!redirect || Vector2.Distance(hot.Goal, to.Flat()) <= RedirectSlack)) return false;

        TaskStart.Walk(world, slot, to, run ? SpeedMode.Run : SpeedMode.Walk, TaskType.MoveOnFreeWill, TaskSource.Npc);
        return false;
    }
}

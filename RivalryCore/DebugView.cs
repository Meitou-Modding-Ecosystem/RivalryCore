using System.Globalization;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// One grudge as the debug rows show it: who holds it, against whom, its size, the floor held under it, how many of the faction's squads in the
/// world hold the character as an enemy now, and the hours before the grudge director may mark the character for that faction again (0: free).
/// </summary>
public sealed record GrudgeView(string Faction, CharacterId Who, string Name, float Value, float Floor, int MarkedSquads, double MarkAgainHours);

/// <summary>
/// One run as the debug rows show it: its behaviour, faction, what it aims at (a character's name or a town), its phase and the hours its phase
/// has left, the platoon it drives (-1: none yet) and, for a hit squad, its leader.
/// </summary>
public sealed record RunView(int Id, string Behaviour, string Faction, string Aim, int Phase, double HoursLeft, int Platoon, string? Leader)
{
    /// <summary>
    /// What the run is doing, in words.
    /// </summary>
    public string State => Behaviour switch
    {
        BuiltIn.Pursue => DebugView.PursuePhase(this),
        BuiltIn.Burgle => DebugView.BurglePhase(this),
        BuiltIn.Visit => DebugView.VisitPhase(this),
        _ => $"phase {Phase}, {DebugView.Hours(HoursLeft)} left",
    };

    /// <summary>
    /// The run as one line.
    /// </summary>
    public override string ToString() => $"run {Id} {Behaviour}: {Faction} -> {Aim}: {State}";
}

/// <summary>
/// One rule as the debug rows show it: its name, what kind of target it takes and the behaviour it starts.
/// </summary>
public sealed record RuleView(string Name, string Target, string Behaviour)
{
    /// <summary>
    /// What to type as the rule's target, in words.
    /// </summary>
    public string Takes => Target switch
    {
        BuiltIn.TownTarget => "a town",
        BuiltIn.CharacterTarget => "a character",
        _ => Target,
    };
}

/// <summary>
/// What the F10 rows read (docs/rivalry-core.md "Debug rows"): a copy of the framework's state made in a serial step, so a row on the host's
/// thread never touches the world. Published about once a game second and after every debug command.
/// </summary>
public sealed record DebugView(
    double GameHours,
    float HuntThreshold,
    IReadOnlyList<(CharacterId Id, string Name, bool Down)> Characters,
    IReadOnlyList<(int Number, string Name)> Towns,
    IReadOnlyList<GrudgeView> Grudges,
    IReadOnlyList<RunView> Runs,
    IReadOnlyList<RuleView> Rules,
    IReadOnlyList<(string Key, double HoursLeft)> Cooldowns,
    IReadOnlyList<(string Faction, double HoursAgo)> LastHitSquads,
    IReadOnlyList<PartyView> Parties,
    int FriendlyCap,
    IReadOnlyList<CapOverride> Overrides,
    double RestUntil,
    IReadOnlyList<WaitingView> Waiting)
{
    /// <summary>
    /// Before the first step of a world.
    /// </summary>
    public static readonly DebugView Empty = new(0, 0, [], [], [], [], [], [], [], [], 0, [], 0, []);

    #region Building

    /// <summary>
    /// Copies what the rows show out of the running world. Serial steps only.
    /// </summary>
    internal static DebugView Build(World world, PopulationData data, PlayerReport report, NotorietyLedger ledger, Cooldowns cooldowns,
        IReadOnlyList<BehaviourRun> runs, RivalRoster rivals, IReadOnlyList<RuleView> rules, RivalryOptions options, SquadMemory? memory,
        Parties parties, IReadOnlyList<WaitingView> waiting)
    {
        double now = world.GameHours;
        string FactionName(int f) => data.FactionAt(f)?.Name ?? "no faction";
        var squads = world.Squads.All.Where(s => !s.Dead).ToLookup(s => s.Faction);

        return new DebugView(
            now,
            options.HuntThreshold,
            [.. report.Characters.Select(c => (c.Id, c.Name, c.Down))],
            [.. report.Towns.Select(t => (t.Number, t.Name))],
            [.. ledger.Entries.Select(g => Grudge(world, g.Key, g.Value, ledger, cooldowns, memory, squads, FactionName(g.Key.Faction)))],
            [.. runs.Select(r => Run(r, now, FactionName(r.Faction)))],
            rules,
            [.. cooldowns.Entries.Where(c => c.Value > now).Select(c => (c.Key, c.Value - now))],
            [.. data.Factions.Select(f => (f.Name, rivals.HoursSinceEnded(f.Id, now))).Where(l => double.IsFinite(l.Item2))],
            [.. parties.Out(runs, report, now)],
            Rivalry.Parties.Cap(PartyKind.Friendly, options),
            parties.Overrides,
            parties.RestUntil,
            waiting);
    }

    /// <summary>
    /// One grudge with the faction's living squads (<paramref name="squads"/>) marked to hunt the character and the grudge director's cooldown
    /// (its key: <c>rivalry.grudge/{faction}:{name}</c>).
    /// </summary>
    static GrudgeView Grudge(World world, GrudgeKey key, float value, NotorietyLedger ledger, Cooldowns cooldowns, SquadMemory? memory,
        ILookup<int, Squad> squads, string faction)
    {
        string name = world.Characters.NameOf(key.Who);
        int marked = memory is null ? 0 : squads[key.Faction].Count(s => memory.HasShort(world, s.Id, key.Who, MemoryTag.TempEnemy));
        double again = cooldowns.Until($"rivalry.grudge/{key.Faction}:{name}") - world.GameHours;
        return new GrudgeView(faction, key.Who, name, value, ledger.HeldFloor(key.Faction, key.Who), marked, Math.Max(0, again));
    }

    /// <summary>
    /// One run; a hit squad aims at its quarry's name, any other at its town.
    /// </summary>
    static RunView Run(BehaviourRun run, double now, string faction)
    {
        string aim = run.Args.Text("quarryName") ?? (run.Town > 0 ? $"town {run.Town}" : run.Key);
        return new RunView(run.Id, run.Behaviour, faction, aim, run.Phase, Math.Max(0, run.Until - now), run.Platoon, run.Args.Text("leaderName"));
    }

    #endregion

    #region Phases in words

    /// <summary>
    /// Hours as text.
    /// </summary>
    internal static string Hours(double hours) => hours.ToString("0.#", CultureInfo.InvariantCulture) + " h";

    /// <summary>
    /// A hit squad's phase.
    /// </summary>
    internal static string PursuePhase(RunView r) => r.Phase switch
    {
        PursueBehaviour.Travelling => $"travelling from its base, arrives in {Hours(r.HoursLeft)}",
        PursueBehaviour.Spawning => $"arrived, no free spot to appear at yet (tries {Hours(r.HoursLeft)} more)",
        PursueBehaviour.Loading => $"spawned out of sight as platoon {r.Platoon}, not in the loaded world yet (waits {Hours(r.HoursLeft)} more)",
        PursueBehaviour.Closing => $"spawned and closing in, gives up in {Hours(r.HoursLeft)}",
        PursueBehaviour.Attacking => $"engaged, gives up in {Hours(r.HoursLeft)}",
        PursueBehaviour.Aftermath => "beaten or called off; its leader is watched",
        _ => $"phase {r.Phase}",
    } + (r.Leader is null ? "" : $" (leader {r.Leader})");

    /// <summary>
    /// A thief squad's phase.
    /// </summary>
    internal static string BurglePhase(RunView r) => r.Phase switch
    {
        BurgleBehaviour.Spawning => $"no free spot to appear at yet (tries {Hours(r.HoursLeft)} more)",
        BurgleBehaviour.Loading => $"spawned out of sight as platoon {r.Platoon}, not in the loaded world yet (waits {Hours(r.HoursLeft)} more)",
        BurgleBehaviour.Stealing => $"sneaking and stealing, leaves in {Hours(r.HoursLeft)}",
        BurgleBehaviour.Fighting => "seen: fighting",
        BurgleBehaviour.Abducting => $"carrying a captive to a buyer ({Hours(r.HoursLeft)} left)",
        BurgleBehaviour.Leaving => "leaving",
        _ => $"phase {r.Phase}",
    };

    /// <summary>
    /// A visiting squad's phase.
    /// </summary>
    internal static string VisitPhase(RunView r) => r.Phase switch
    {
        VisitBehaviour.Spawning => $"no free spot to appear at yet (tries {Hours(r.HoursLeft)} more)",
        VisitBehaviour.Loading => $"spawned out of sight as platoon {r.Platoon}, not in the loaded world yet (waits {Hours(r.HoursLeft)} more)",
        _ => $"visiting, gone in {Hours(r.HoursLeft)} at most",
    };

    #endregion

    #region Rows

    /// <summary>
    /// The runs and the rules: what is under way, and what <c>fire rule</c> accepts.
    /// </summary>
    public string DescribeRuns()
    {
        string runs = Runs.Count > 0
            ? string.Join("; ", Runs)
            : "no runs (a run is a squad Rivalry drives itself: a visit, thieves or a hit squad; a raid, a reinforcement and a plain grudge hunt make none)";
        return $"{runs} | {DescribeParties()} | rules: {string.Join(", ", Rules.Select(r => $"{r.Name} [{r.Takes}]"))}";
    }

    /// <summary>
    /// The parties out against each cap, the hostile candidates waiting their turn, and the mods allowed over the hostile cap.
    /// </summary>
    public string DescribeParties()
    {
        string hostile = PartiesOf(PartyKind.Hostile, Rivalry.Parties.HostileCap);
        string friendly = PartiesOf(PartyKind.Friendly, FriendlyCap);
        string overrides = Overrides.Count > 0 ? $"; over the hostile cap: {string.Join(", ", Overrides)}" : "";
        string rest = RestUntil > GameHours ? $" (resting {Hours(RestUntil - GameHours)} more)" : "";
        string waiting = Waiting.Count > 0 ? $"; waiting: {string.Join(", ", Waiting)}" : "";
        return $"hostile parties {hostile}{rest}{waiting}; friendly parties {friendly}{overrides}";
    }

    /// <summary>
    /// The parties of one kind against its cap, as text.
    /// </summary>
    string PartiesOf(PartyKind kind, int cap)
    {
        var list = Parties.Where(p => p.Kind == kind).ToList();
        return list.Count > 0 ? $"{list.Count}/{cap} ({string.Join(", ", list)})" : $"0/{cap}";
    }

    /// <summary>
    /// What a faction holds against the named or selected characters and whether a squad hunts them; with neither, everyone with a grudge or a hunt.
    /// </summary>
    public string DescribeCharacters(string field, IReadOnlyList<CharacterId> selected)
    {
        string typed = field.Trim();
        var names = typed.Length > 0
            ? Characters.Where(c => string.Equals(c.Name, typed, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).ToList()
            : [.. Characters.Where(c => selected.Contains(c.Id)).Select(c => c.Name)];
        if (typed.Length > 0 && names.Count == 0) return $"no player character named '{typed}' (have: {string.Join(", ", Characters.Select(c => c.Name))})";

        if (names.Count == 0) names = [.. Grudges.Select(g => g.Name).Concat(Runs.Where(r => r.Behaviour == BuiltIn.Pursue).Select(r => r.Aim)).Distinct()];
        if (names.Count == 0) return "no grudges and no hit squads against anyone";

        return string.Join(" || ", names.Distinct().Select(DescribeCharacter));
    }

    /// <summary>
    /// One character: notoriety, each faction's grudge with its squads, the hit squads after it and the cooldowns that name it.
    /// </summary>
    string DescribeCharacter(string name)
    {
        var grudges = Grudges.Where(g => g.Name == name).OrderByDescending(g => g.Value).ToList();
        var hunts = Runs.Where(r => r.Behaviour == BuiltIn.Pursue && r.Aim == name).ToList();
        var cooling = Cooldowns.Where(c => c.Key.EndsWith("/" + name, StringComparison.Ordinal)).ToList();
        string down = Characters.Any(c => c.Name == name && c.Down) ? " (down: not hunted while down)" : "";

        var parts = new List<string> { $"{name}{down}: notoriety {Number(grudges.Sum(g => g.Value))}, hunts start at {Number(HuntThreshold)}" };
        parts.Add(grudges.Count > 0 ? "grudges: " + string.Join("; ", grudges.Select(g => DescribeGrudge(g, hunts))) : "no grudges");
        parts.Add(hunts.Count > 0 ? "hit squads: " + string.Join("; ", hunts.Select(h => $"{h.Faction} run {h.Id}: {h.State}")) : "no hit squad after them");
        if (cooling.Count > 0) parts.Add("rules resting: " + string.Join(", ", cooling.Select(c => $"{c.Key[..c.Key.LastIndexOf('/')]} {Hours(c.HoursLeft)}")));

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// One grudge: its size, whether it is enough, the faction's squads that hold the character as an enemy, and its rests.
    /// </summary>
    string DescribeGrudge(GrudgeView g, List<RunView> hunts)
    {
        string text = $"{g.Faction} {Number(g.Value)}" + (g.Value >= HuntThreshold ? "" : " (below the threshold)");
        if (g.Floor > 0) text += $", held at {Number(g.Floor)} until its hit squad is beaten";
        if (g.MarkedSquads > 0) text += $", {g.MarkedSquads} of its squads in the world hold them as an enemy";
        if (g.MarkAgainHours > 0) text += $", marks again in {Hours(g.MarkAgainHours)}";
        if (hunts.All(h => h.Faction != g.Faction) && LastHitSquads.FirstOrDefault(l => l.Faction == g.Faction) is { Faction: not null } last)
            text += $", its last hit squad finished {Hours(last.HoursAgo)} ago";

        return text;
    }

    /// <summary>
    /// Checks a <c>fire rule</c> field before it is queued: null when the rule exists and its target fits; else what is wrong and what would work.
    /// </summary>
    public string? CheckRule(string rule, string? target)
    {
        if (Rules.FirstOrDefault(r => r.Name == rule) is not { } found)
            return $"no rule '{rule}'. Rules: {string.Join(", ", Rules.Select(r => $"{r.Name} [{r.Takes}]"))}";

        if (found.Target == BuiltIn.TownTarget)
        {
            if (Towns.Count == 0) return $"{rule} aims at a town and you have none";
            if (!string.IsNullOrWhiteSpace(target) && !IsTown(target)) return $"{rule} aims at a town, and '{target}' is not one of yours ({TownList()})";
        }

        if (found.Target == BuiltIn.CharacterTarget)
        {
            var standing = Characters.Where(c => !c.Down).Select(c => c.Name).ToList();
            if (standing.Count == 0) return $"{rule} aims at a standing character and you have none";
            if (!string.IsNullOrWhiteSpace(target) && !standing.Contains(target.Trim()))
                return $"{rule} aims at a character, and '{target}' is not a standing one of yours, exact case ({string.Join(", ", standing)})";
        }

        return null;
    }

    /// <summary>
    /// Whether the text names a player town: its name (any case), number or key.
    /// </summary>
    bool IsTown(string text)
    {
        string name = text.Trim();
        string number = name.StartsWith("town", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        return Towns.Any(t => t.Number.ToString(CultureInfo.InvariantCulture) == number || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The player's towns as text.
    /// </summary>
    string TownList() => string.Join(", ", Towns.Select(t => $"{t.Name} = town{t.Number}"));

    /// <summary>
    /// A number as text.
    /// </summary>
    static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    #endregion
}

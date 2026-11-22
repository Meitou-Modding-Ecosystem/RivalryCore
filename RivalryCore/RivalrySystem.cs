using System.Globalization;
using System.Numerics;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Dialogue;

namespace Rivalry;

/// <summary>Assess at the next serial step, whatever the cadence (the debug row's "assess now").</summary>
public sealed record AssessNowCommand : SimCommand;

/// <summary>
/// Fires a rule on the target with <see cref="Key"/> (its first target when null), whatever its conditions, chance and cooldown say (the debug row).
/// </summary>
public sealed record FireRuleCommand(string Rule, string? Key) : SimCommand;

/// <summary>
/// Starts a behaviour for a player town (its name, number or key; null: the first) with a faction (its name or string id) acting (the debug rows).
/// </summary>
public sealed record StartBehaviourCommand(string Behaviour, string? Town, string Faction, BehaviourArgs Args) : SimCommand;

/// <summary>Adds to the grudge a faction (its name or string id) holds against characters (the debug row; dependents use <see cref="Decisions.Grudge"/>).</summary>
public sealed record GrudgeCommand(IReadOnlyList<CharacterId> Characters, string Faction, float Amount) : SimCommand;

/// <summary>
/// Rivalry Core in one world (docs/rivalry-core.md): the grudge ledger fed by hits, the behaviours under way, and at each assessment the
/// inspection, the rules, the directors and their decisions, all in serial steps. Its state goes into the world's hash and the save.
/// </summary>
public sealed partial class RivalrySystem : ICombatHitObserver, IStateHashed, IModSaveData, IRivalryState
{
    public const int HistoryLength = 64;

    /// <summary>
    /// Ticks between copies of the state for the debug rows (one game second at 30 ticks a second); a debug command or an assessment copies at once.
    /// </summary>
    public const int ViewEveryTicks = 30;

    readonly PopulationData _data;
    readonly IReadOnlyList<IDirector> _directors;
    readonly RivalryOptions _settings;
    readonly RngPurpose _purpose;
    readonly Action<string> _log;
    readonly Inspector _inspector;
    readonly Catalogue _catalogue;
    readonly Actions _actions;
    readonly RuleEngine _rules;
    readonly BehaviourRuns _runs;
    readonly Parties _parties;

    /// <summary>
    /// Who goes next among the hostile candidates (docs/rivalry-core.md "Who goes next").
    /// </summary>
    readonly HuntQueue _queue = new();
    readonly PopulationSystem? _population;
    readonly HunterBarks _barks;
    readonly SquadMemory? _memory;
    readonly Cooldowns _cooldowns = new();
    readonly HashSet<string> _faulted = new(StringComparer.Ordinal);
    readonly List<string> _history = [];
    readonly List<SimCommand> _asked = [];
    RivalryOptions _options;
    World? _world;
    double _nextAssess = double.NaN;
    double _lastAssess = double.NaN;
    bool _assessNow;
    bool _viewDue = true;

    /// <summary>
    /// What the runs looked like when <see cref="Runs"/> was last written (<see cref="RunsPrint"/>), so the lines are only rebuilt on a change.
    /// </summary>
    int _runsPrint;

    internal RivalrySystem(SystemBuildContext context, Registry registry, RivalryOptions settings, RngPurpose purpose, Action<string> log)
    {
        _data = context.Data;
        _directors = [.. registry.Directors.Select(e => e.Value)];
        _settings = settings;
        _purpose = purpose;
        _log = log;
        _options = settings.Copy();
        _inspector = new Inspector(_data, [.. registry.TownProbes.Select(e => (e.Name, e.Value))], [.. registry.CharacterProbes.Select(e => (e.Name, e.Value))]);
        _population = context.Find<PopulationSystem>();
        _catalogue = new Catalogue(_data, _population);
        _memory = context.Find<DialogueSystem>()?.Memory;
        var behaviours = registry.Behaviours.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        _parties = new Parties(behaviours, [.. registry.Overrides.Select(e => e.Value)]);
        _actions = new Actions(_data, _population, _memory, Notoriety, _catalogue, _parties);
        _barks = new HunterBarks(context.Find<CombatSystem>());
        _rules = new RuleEngine(registry, log);
        _runs = new BehaviourRuns(_data, behaviours, log);
    }

    /// <summary>
    /// Rivalry Core for one world with its built-ins and whatever <paramref name="register"/> adds under the mod id <paramref name="modId"/>
    /// (tests, tools; the game goes through <see cref="RivalryMod"/>).
    /// </summary>
    public static RivalrySystem Create(SystemBuildContext context, RivalryOptions settings, RngPurpose purpose, Action<string> log,
        string modId = "test", Action<IRivalryRegistry>? register = null)
    {
        var registry = new Registry();
        BuiltIn.Register(registry.For(RivalryMod.Id, "rivalry."));
        register?.Invoke(registry.For(modId, modId + "."));
        return new RivalrySystem(context, registry, settings, purpose, log);
    }

    public PlayerReport Report { get; private set; } = PlayerReport.Empty;
    public NotorietyLedger Notoriety { get; } = new();

    /// <summary>
    /// The named leaders of hit squads, the leaders handed to the law, and each faction's last hit squad.
    /// </summary>
    public RivalRoster Rivals { get; } = new();

    /// <summary>A copy, safe to read from the host's thread.</summary>
    public IReadOnlyList<string> History { get; private set; } = [];

    /// <summary>
    /// The behaviours under way, as lines; a copy, safe to read from the host's thread.
    /// </summary>
    public IReadOnlyList<string> Runs { get; private set; } = [];

    /// <summary>
    /// The parties out, hostile and friendly (docs/rivalry-core.md "Party caps"); a copy, safe to read from the host's thread.
    /// </summary>
    public IReadOnlyList<PartyView> Parties { get; private set; } = [];

    /// <summary>
    /// What the debug rows read: a copy, safe to read from the host's thread.
    /// </summary>
    public DebugView Debug { get; private set; } = DebugView.Empty;

    /// <summary>
    /// The rules' names, in evaluation order.
    /// </summary>
    public IEnumerable<string> RuleNames => _rules.Names;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        _options = _settings.Copy();
        foreach (var command in commands)
        {
            if (command is AssessNowCommand) _assessNow = true;
            else if (command is GrudgeCommand g) AddGrudge(g);
            else if (command is FireRuleCommand or StartBehaviourCommand) _asked.Add(command);
            else continue;

            _viewDue = true;
        }
    }

    public void SlowWorld(World world)
    {
        _world = world;
        ResolveSaved(world);
        CheckKills(world);
        StepRuns(world);
        _barks.Step(world, _runs.All, Rivals, _options);
        RunAsked(world);
        if (double.IsNaN(_nextAssess)) _nextAssess = world.GameHours;
        if (_assessNow || world.GameHours >= _nextAssess)
        {
            _assessNow = false;
            _viewDue = true;
            Assess(world);
        }

        if (_viewDue || world.Tick % ViewEveryTicks == 0) PublishView(world);
    }

    /// <summary>
    /// Copies the state for the debug rows.
    /// </summary>
    void PublishView(World world)
    {
        _viewDue = false;
        Debug = DebugView.Build(world, _data, Report, Notoriety, _cooldowns, _runs.All, Rivals, _rules.Views, _options, _memory, _parties, Waiting);
    }

    void AddGrudge(GrudgeCommand g)
    {
        int faction = FactionByName(g.Faction);
        if (faction < 0) return;
        foreach (var who in g.Characters) Notoriety.Add(faction, who, g.Amount);
    }

    internal int FactionByName(string text)
    {
        if (_data.FactionIndex(text) is >= 0 and var byId) return byId;
        for (int f = 0; f < _data.Factions.Count; f++)
            if (string.Equals(_data.Factions[f].Name, text.Trim(), StringComparison.OrdinalIgnoreCase)) return f;
        return -1;
    }

    /// <summary>
    /// Where a faction (its name or string id) sends squads from, as a line for the debug row: how the bases were found and the nearest few to the
    /// player's first character.
    /// </summary>
    public string DescribeBases(string factionName)
    {
        int faction = FactionByName(factionName);
        if (faction < 0) return $"no faction {factionName}";

        var bases = FactionBases.Of(_data, faction, FactionBases.NamesFor(_data, faction, _settings, []));
        string squad = HitSquads.TemplateFor(_data, faction) is { } template
            ? $"hit squad: {_data.Db.Find(template)?.Name} ({template})"
            : "no squad of its own to send: no hit squads unless a mod gives it one";
        if (bases.Count == 0) return $"{_data.Factions[faction].Name}: {squad}; no base found; hit squads take {_settings.HuntTravelFallbackHours:0.#} h";

        var at = Report.Characters.FirstOrDefault() is { } c ? c.Position.Flat() : default;
        var nearest = bases.OrderBy(b => Vector2.Distance(b.Position, at)).Select(b => b.Label).Distinct().Take(5);
        return $"{_data.Factions[faction].Name}: {squad}; {bases.Count} {bases[0].Source} bases; nearest: {string.Join(", ", nearest)}";
    }

    /// <summary>
    /// Steps the behaviours under way and publishes their lines.
    /// </summary>
    void StepRuns(World world)
    {
        if (_runs.All.Count > 0) _runs.Step(Context(world));
        Rivals.Idle(_runs.All.Where(r => r.Args.Text("leaderName") is not null).Select(LeaderOf));

        var parties = PartiesNow(world);
        LookForRest(world, parties.Count(p => p.Kind == PartyKind.Hostile));
        PublishRuns(parties);
    }

    /// <summary>
    /// Starts the rest between hunts once the last hostile party has ended (<see cref="Rivalry.Parties.Look"/>).
    /// </summary>
    void LookForRest(World world, int hostile)
    {
        float roll = Rng.Float(Rng.Hash(world.Seed, (ulong)world.Tick, _purpose, Rng.StableHash("rivalry.rest")));
        _parties.Look(hostile, world.GameHours, roll, _options);
    }

    /// <summary>
    /// The faction and leader a run names, for <see cref="RivalRoster.Idle"/>.
    /// </summary>
    (string Faction, string Name) LeaderOf(BehaviourRun run)
    {
        string faction = _data.FactionAt(run.Faction)?.Id ?? "";
        return (faction, run.Args.Text("leaderName")!);
    }

    /// <summary>
    /// The parties out now, as a list.
    /// </summary>
    List<PartyView> PartiesNow(World world) => [.. _parties.Out(_runs.All, Report, world.GameHours)];

    /// <summary>
    /// The parties out and the runs as lines, for <see cref="IRivalryState.Parties"/> and the debug row.
    /// </summary>
    void PublishRuns()
    {
        if (_world is { } world) PublishRuns(PartiesNow(world));
    }

    /// <summary>
    /// Publishes <paramref name="parties"/>, and the runs as lines when a run changed since the last time.
    /// </summary>
    void PublishRuns(List<PartyView> parties)
    {
        Parties = parties;
        int print = RunsPrint();
        if (print == _runsPrint) return;

        _runsPrint = print;
        Runs = [.. _runs.All.Select(r => $"run {r.Id} {r.Behaviour} town {r.Town} platoon {r.Platoon} phase {r.Phase} until {r.Until:0.#} h")];
    }

    /// <summary>
    /// A fingerprint of what the run lines show; 0 with no runs.
    /// </summary>
    int RunsPrint()
    {
        if (_runs.All.Count == 0) return 0;

        var print = new HashCode();
        foreach (var r in _runs.All)
        {
            print.Add(r.Id);
            print.Add(r.Behaviour);
            print.Add(r.Town);
            print.Add(r.Platoon);
            print.Add(r.Phase);
            print.Add(r.Until);
        }

        return print.ToHashCode();
    }

    /// <summary>
    /// The debug rows' asks, in the order they came: a rule fired, a behaviour started.
    /// </summary>
    void RunAsked(World world)
    {
        if (_asked.Count == 0) return;

        Report = _inspector.Inspect(world);
        var decided = new List<Decision>();
        foreach (var command in _asked)
        {
            if (command is FireRuleCommand fire) Fire(world, fire, decided);
            else if (command is StartBehaviourCommand start) Start(start, decided);
        }

        _asked.Clear();
        foreach (var decision in decided) Carry(world, decision, capped: false);
    }

    /// <summary>
    /// A debug row's rule fired, whatever its conditions, cooldown and the party caps say.
    /// </summary>
    void Fire(World world, FireRuleCommand fire, List<Decision> decided)
    {
        string line = _rules.Fire(NewAssessment(world, decided, []), fire.Rule, TownKey(fire.Key), decided, _cooldowns);
        Remember($"debug: {line} (party caps skipped)");
    }

    /// <summary>
    /// A debug start as a decision, or a log line when the player has no such town.
    /// </summary>
    void Start(StartBehaviourCommand start, List<Decision> decided)
    {
        if (TownOf(start.Town) is not { } town)
        {
            Remember($"debug: {start.Behaviour}: no player town {start.Town}");
            return;
        }

        var target = new RuleTarget($"town{town}") { Town = town, Faction = FactionByName(start.Faction) };
        decided.Add(new StartDecision("debug", start.Behaviour, target, start.Args));
    }

    /// <summary>
    /// A debug row's target key with a player town's name or number turned into its key (<c>town{number}</c>); any other text as typed.
    /// </summary>
    string? TownKey(string? key) => key is not null && TownOf(key) is { } town ? $"town{town}" : key;

    /// <summary>
    /// The number of the player town a debug row names: by key, number or name (case ignored); empty or null: the first; null when none matches.
    /// </summary>
    int? TownOf(string? text)
    {
        var towns = Report.Towns;
        if (string.IsNullOrWhiteSpace(text)) return towns.Count > 0 ? towns[0].Number : null;

        string name = text.Trim();
        string number = name.StartsWith("town", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        var match = towns.FirstOrDefault(t => t.Number.ToString(CultureInfo.InvariantCulture) == number)
            ?? towns.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        return match?.Number;
    }

    void Assess(World world)
    {
        double hours = double.IsNaN(_lastAssess) ? 0 : world.GameHours - _lastAssess;
        _lastAssess = world.GameHours;
        _nextAssess = world.GameHours + Math.Max(1, _options.AssessEveryHours);
        Notoriety.Cool(world.Characters, hours, _options.GrudgeCoolPerHour);
        _cooldowns.Expire(world.GameHours);
        Report = _inspector.Inspect(world);
        _parties.Expire(Report, world.GameHours);
        if (Report.PlayerFaction < 0) return;

        var decided = Decide(world);
        foreach (var decision in decided) Carry(world, decision, capped: true);
    }

    /// <summary>
    /// What the rules and directors see; <paramref name="pending"/> holds the decisions recorded so far, which count as parties out, and
    /// <paramref name="proposals"/> collects the hostile candidates.
    /// </summary>
    Assessment NewAssessment(World world, IReadOnlyList<Decision> pending, List<Proposal> proposals) =>
        new(world, _data, Report, Notoriety, _options, _purpose, _catalogue, Rivals, _runs.All, _parties, pending, proposals);

    BehaviourContext Context(World world) =>
        new(world, _data, Report, _options, _purpose, _actions, _runs, _population, _inspector, Notoriety, Rivals);

    List<Decision> Decide(World world)
    {
        var decided = new List<Decision>();
        var proposals = new List<Proposal>();
        var assessment = NewAssessment(world, decided, proposals);
        _rules.Evaluate(assessment, decided, _cooldowns);
        foreach (var director in _directors)
        {
            if (_faulted.Contains(director.Name)) continue;
            try
            {
                director.Assess(assessment, new Decisions(director.Name, decided, _cooldowns, world.GameHours));
            }
            catch (Exception e)
            {
                _faulted.Add(director.Name);
                _log($"director {director.Name} threw and is off for this world: {e.Message}");
            }
        }

        SendNext(assessment, proposals);
        return decided;
    }

    /// <summary>
    /// Sends the best hostile candidates the caps admit, best first (one while the cap is one); the others wait their turn. Another mod's
    /// candidate that goes is given a claim.
    /// </summary>
    void SendNext(Assessment a, List<Proposal> proposals)
    {
        double now = a.GameHours;
        var external = _queue.External(now, ExternalKeepHours).Select(c => new Proposal(c, () => Grant(c, now)));
        var ranked = _queue.Rank(proposals.Concat(external), p => p.Candidate, now, _options);
        foreach (var proposal in ranked)
        {
            if (!a.MayStart(PartyKind.Hostile, proposal.Candidate.Source)) continue;
            if (Dispatch(proposal)) _queue.Sent(proposal.Candidate);
        }
        Waiting = _queue.Latest;
    }

    /// <summary>
    /// Runs a candidate's dispatch; a dispatch that throws is logged and counts as not sent.
    /// </summary>
    bool Dispatch(Proposal proposal)
    {
        try
        {
            return proposal.Dispatch();
        }
        catch (Exception e)
        {
            _log($"{proposal.Candidate.Source}: sending {proposal.Candidate.Key} failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gives another mod's winning candidate its claim.
    /// </summary>
    bool Grant(HostileCandidate c, double now)
    {
        bool held = _parties.Claim(Claim(c), PartiesOut(PartyKind.Hostile), _options, now);
        if (held) Remember($"{c.Source}: {c.Key} goes next (claim for {c.ClaimHours:0.#} h)");
        return held;
    }

    /// <summary>
    /// The game hours another mod's candidate stays in the queue after it was last proposed.
    /// </summary>
    double ExternalKeepHours => 2 * Math.Max(1, _options.AssessEveryHours);

    /// <summary>
    /// Carries out a decision and logs it; <paramref name="capped"/>: one that would send a party over its kind's cap is held instead (the debug
    /// rows' asks are not capped).
    /// </summary>
    void Carry(World world, Decision decision, bool capped)
    {
        if (capped && HeldLine(world, decision) is { } held)
        {
            Remember($"{decision.Director}: {held}");
            return;
        }

        string line;
        try
        {
            line = decision is StartDecision s
                ? _runs.Start(Context(world), s.Behaviour, new BehaviourRequest(s.Director, s.Target, s.Args))
                : _actions.Apply(world, decision, _options, Report);
        }
        catch (Exception e)
        {
            line = $"{decision.GetType().Name} failed: {e.Message}";
        }
        Remember($"{decision.Director}: {line}");
        PublishRuns();
    }

    /// <summary>
    /// Why the decision is held by its party cap; null when it may go.
    /// </summary>
    string? HeldLine(World world, Decision decision)
    {
        var kind = _parties.KindOf(decision);
        if (kind == PartyKind.None) return null;

        int count = _parties.Count(kind, _runs.All, Report, world.GameHours);
        if (_parties.Admits(kind, decision.Director, count, _options, world.GameHours)) return null;
        if (_parties.Resting(world.GameHours)) return $"held: resting after the last hostile party until {_parties.RestUntil:0.#} h";

        string what = decision is StartDecision s ? s.Behaviour : KindName(decision);
        return $"held: {what} would be {kind.ToString().ToLowerInvariant()} party {count + 1} of {Rivalry.Parties.Cap(kind, _options)}";
    }

    /// <summary>
    /// A decision's type as a word (<c>HuntDecision</c>: hunt).
    /// </summary>
    static string KindName(Decision decision) => decision.GetType().Name.Replace("Decision", "", StringComparison.Ordinal).ToLowerInvariant();

    void Remember(string line)
    {
        _log(line);
        _history.Add(line);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);
        History = [.. _history];
    }

    /// <summary>
    /// How many parties of the kind are out now. Serial steps only.
    /// </summary>
    public int PartiesOut(PartyKind kind) => _world is { } world ? _parties.Count(kind, _runs.All, Report, world.GameHours) : 0;

    public int PartyCap(PartyKind kind) => Rivalry.Parties.Cap(kind, _options);

    public double HostileRestUntil => _parties.RestUntil;

    public bool ClaimParty(PartyClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (_world is not { } world || claim.Kind == PartyKind.None || claim.Hours <= 0) return false;

        bool held = _parties.Claim(claim, PartiesOut(claim.Kind), _options, world.GameHours);
        PublishRuns();
        return held;
    }

    public bool Propose(HostileCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_world is not { } world) return false;
        if (_parties.Holds(candidate.Source, candidate.Key)) return ClaimParty(Claim(candidate));

        _queue.Propose(candidate, world.GameHours);
        return false;
    }

    /// <summary>
    /// The claim a winning candidate holds.
    /// </summary>
    static PartyClaim Claim(HostileCandidate c) => new(c.Source, PartyKind.Hostile, c.Key, c.ClaimHours, c.Faction);

    public double TravelHours(int faction, CharacterId target) =>
        _world is { } world && world.Characters.TryResolveNext(target, out int slot)
            ? Travel.Hours(_data, _options, faction, world.Characters.Next[slot].Position.Flat())
            : Math.Min(_options.HuntTravelFallbackHours, _options.HuntTravelMaxHours);

    public IReadOnlyList<WaitingView> Waiting { get; private set; } = [];

    public bool ReleaseParty(string source, string key)
    {
        bool released = _parties.Release(source, key);
        PublishRuns();
        return released;
    }

    public void Hash(ref StateHasher h)
    {
        Notoriety.Hash(ref h);
        _cooldowns.Hash(ref h);
        h.Add(_nextAssess);
        h.Add(_faulted.Count + _rules.Faulted);
        HashVictims(ref h);
        _runs.Hash(ref h);
        Rivals.Hash(ref h);
        _parties.Hash(ref h);
        _queue.Hash(ref h);
    }
}

using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// What a director sees at one assessment: the report, the grudges, the world (read only), the data, the tuning and a deterministic random source.
/// </summary>
public sealed class Assessment
{
    readonly RngPurpose _purpose;
    readonly Catalogue _catalogue;

    /// <summary>
    /// The behaviours under way.
    /// </summary>
    readonly IReadOnlyList<BehaviourRun> _runs;

    /// <summary>
    /// The party caps.
    /// </summary>
    readonly Parties _parties;

    /// <summary>
    /// The decisions recorded so far at this assessment, not carried out yet: they count as parties out.
    /// </summary>
    readonly IReadOnlyList<Decision> _pending;

    /// <summary>
    /// The hostile candidates proposed at this assessment; the best one the caps admit is sent after the directors.
    /// </summary>
    readonly List<Proposal> _proposals;

    internal Assessment(World world, PopulationData data, PlayerReport report, NotorietyLedger notoriety, RivalryOptions options, RngPurpose purpose,
        Catalogue catalogue, RivalRoster rivals, IReadOnlyList<BehaviourRun> runs, Parties parties, IReadOnlyList<Decision> pending,
        List<Proposal> proposals)
    {
        _proposals = proposals;
        _catalogue = catalogue;
        _runs = runs;
        _parties = parties;
        _pending = pending;
        Rivals = rivals;
        World = world;
        Data = data;
        Report = report;
        Notoriety = notoriety;
        Options = options;
        _purpose = purpose;
    }

    /// <summary>The running world. Read only: changes go through <see cref="Decisions"/>.</summary>
    public World World { get; }

    public PopulationData Data { get; }
    public PlayerReport Report { get; }
    public NotorietyLedger Notoriety { get; }
    public RivalryOptions Options { get; }

    /// <summary>
    /// The named leaders of hit squads, the leaders handed to the law, and when each faction's last hit squad finished. Read only here.
    /// </summary>
    public RivalRoster Rivals { get; }

    /// <summary>
    /// Whether one of the faction's hit squads (<see cref="BuiltIn.Pursue"/>) is on its way or fighting.
    /// </summary>
    public bool HitSquadUnderway(int faction) => _runs.Any(r => r.Faction == faction && PursueBehaviour.HitSquadUnderway(r));

    /// <summary>
    /// How many parties of the kind are out (docs/rivalry-core.md "Party caps"): runs under way, one-shot decisions and other mods' claims still
    /// counting, and the decisions recorded earlier at this assessment.
    /// </summary>
    public int PartiesOut(PartyKind kind) =>
        _parties.Count(kind, _runs, Report, GameHours) + _pending.Count(d => _parties.KindOf(d) == kind);

    /// <summary>
    /// Whether <paramref name="source"/> (a rule's or director's name) may send one more party of the kind now: under the cap, or under its
    /// override. Check it before claiming a cooldown: a start over the cap is refused when carried out, and the cooldown would be spent.
    /// </summary>
    public bool MayStart(PartyKind kind, string source) => _parties.Admits(kind, source, PartiesOut(kind), Options, GameHours);

    /// <summary>
    /// The party kind of a behaviour by name; none when it is not registered.
    /// </summary>
    public PartyKind KindOf(string behaviour) => _parties.KindOf(behaviour);

    /// <summary>
    /// Proposes a hostile party (docs/rivalry-core.md "Who goes next"): after the rules and directors, the best-scoring candidate the caps admit
    /// has <paramref name="dispatch"/> run (claim the cooldown and record the decisions there); the others wait. A director sending a hostile
    /// party proposes it instead of deciding it.
    /// </summary>
    public void Propose(HostileCandidate candidate, Action dispatch)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(dispatch);
        _proposals.Add(new Proposal(candidate, () => Sends(dispatch)));
    }

    /// <summary>
    /// Runs a candidate's dispatch; whether it recorded a decision.
    /// </summary>
    bool Sends(Action dispatch)
    {
        int before = _pending.Count;
        dispatch();
        return _pending.Count > before;
    }

    /// <summary>
    /// The travel time in game hours from the faction's base nearest a point to it (<see cref="Travel"/>), with <paramref name="home"/>'s names.
    /// </summary>
    public double TravelHours(int faction, Vector3 to, string? home = null) => Travel.Hours(Data, Options, faction, to.Flat(), home);

    /// <summary>
    /// Where a target stands: its character, else its town; null when neither is in the report.
    /// </summary>
    public Vector3? PositionOf(RuleTarget target) => Report.Find(target.Character)?.Position ?? Report.TownOf(target.Town)?.Position;

    /// <summary>
    /// The grudge behind a party against a target: the faction's grudge against its character, or for a town the largest against the player
    /// characters in it; 0 with none.
    /// </summary>
    public float GrudgeAt(int faction, RuleTarget target)
    {
        if (faction < 0) return 0;
        if (!target.Character.IsNone) return Notoriety.Grudge(faction, target.Character);

        if (target.Town <= 0) return 0;

        return Report.Characters
            .Where(c => c.Town == target.Town)
            .Select(c => Notoriety.Grudge(faction, c.Id))
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    /// Game hours since the faction's last hit squad finished; infinity when none has.
    /// </summary>
    public double HoursSinceHitSquad(int faction) =>
        Faction(faction) is { } f ? Rivals.HoursSinceEnded(f.Id, GameHours) : double.PositiveInfinity;

    /// <summary>
    /// How many of the faction's squads in the world a grudge hunt could mark the character for now: within <see cref="RivalryOptions.HuntRadius"/>,
    /// leader alive, in no campaign, not a town's own unless <see cref="RivalryOptions.UseTownSquads"/>.
    /// </summary>
    public int SquadsNear(int faction, CharacterId who) => World.Characters.TryResolveNext(who, out int slot)
        ? Actions.Candidates(World, faction, World.Characters.Next[slot].Position, Options.HuntRadius, Options.UseTownSquads).Count()
        : 0;

    public int PlayerFaction => Report.PlayerFaction;
    public double GameHours => World.GameHours;

    /// <summary>A uniform number in [0, 1), the same for the same world seed, tick and key: never <c>Random</c>, which would break the determinism check.</summary>
    public float Roll(string key) => Rng.Float(Rng.Hash(World.Seed, (ulong)World.Tick, _purpose, Rng.StableHash(key)));

    public float Relation(int faction) => Known(faction) ? Data.Relations.Get(faction, PlayerFaction) : 0;

    public bool IsHostile(int faction) => Known(faction) && Data.Relations.IsHostile(faction, PlayerFaction);

    /// <summary>An ally of the player and not the player's own faction.</summary>
    public bool IsAlly(int faction) => Known(faction) && faction != PlayerFaction && Data.Relations.IsAlly(faction, PlayerFaction);

    /// <summary>Real factions other than the player's (FCS <c>not real</c> ones left out), in data order.</summary>
    public IEnumerable<int> OtherFactions()
    {
        for (int f = 0; f < Data.Factions.Count; f++)
            if (f != PlayerFaction && !Data.Factions[f].NotReal) yield return f;
    }

    /// <summary>The campaign of a kind a raid or reinforcement by the faction would request (<see cref="Catalogue.CampaignFor"/>); null when it has none.</summary>
    public string? CampaignFor(int faction, CampaignKey key) => _catalogue.CampaignFor(faction, key);

    public FactionData? Faction(int faction) => Data.FactionAt(faction);

    bool Known(int faction) => PlayerFaction >= 0 && Data.Relations.Contains(faction) && Data.Relations.Contains(PlayerFaction);
}

/// <summary>
/// A hostile candidate and what sending it does: true when it went.
/// </summary>
internal readonly record struct Proposal(HostileCandidate Candidate, Func<bool> Dispatch);

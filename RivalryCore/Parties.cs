using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// What a party sent at the player is (docs/rivalry-core.md "Party caps"): a behaviour or decision declares it, and the framework caps how many
/// of each kind are out at once.
/// </summary>
public enum PartyKind
{
    /// <summary>
    /// Not a party: no cap.
    /// </summary>
    None,

    /// <summary>
    /// Sent against the player's characters or towns (a hit squad, thieves, a raid, a grudge hunt): at most <see cref="Parties.HostileCap"/> at once.
    /// </summary>
    Hostile,

    /// <summary>
    /// Sent to help or visit the player (allies reinforcing, travellers): at most <see cref="RivalryOptions.FriendlyPartiesAtOnce"/> at once.
    /// </summary>
    Friendly,
}

/// <summary>
/// A mod's leave to send hostile parties over the cap (<see cref="IRivalryRegistry.ExceedHostileCap"/>): starts by <see cref="Source"/> (a rule,
/// director or claim source of that mod) may go <see cref="Extra"/> over it.
/// </summary>
public sealed record CapOverride(string Owner, string Source, int Extra, string Reason)
{
    /// <summary>
    /// The override as the debug rows and the log show it.
    /// </summary>
    public override string ToString() => $"{Source} +{Extra} ({Owner}: {Reason})";
}

/// <summary>
/// One party out, as the state and the debug rows show it: its kind, who sent it (a rule, director or claim source), what it is (a behaviour,
/// a decision or "claim"), what it aims at, the acting faction's index (-1: none) and, for a run, its id (0: not a run).
/// </summary>
public sealed record PartyView(PartyKind Kind, string Source, string What, string Key, int Faction, int Run)
{
    /// <summary>
    /// The party as one line.
    /// </summary>
    public override string ToString() => Run > 0 ? $"{Source} run {Run} -> {Key}" : $"{Source} {What} -> {Key}";
}

/// <summary>
/// The party caps in one world (docs/rivalry-core.md "Party caps"): which kind each behaviour and decision is, how many of each are out (runs under
/// way plus the ledger of one-shot decisions and other mods' claims), and whether one more may start. The ledger is saved and hashed.
/// </summary>
internal sealed class Parties(IReadOnlyDictionary<string, IBehaviour> behaviours, IReadOnlyList<CapOverride> overrides)
{
    #region Caps

    /// <summary>
    /// Hostile parties out at once, whatever the mods: only an override (<see cref="IRivalryRegistry.ExceedHostileCap"/>) goes over it.
    /// </summary>
    public const int HostileCap = 1;

    /// <summary>
    /// The friendly cap's bounds (the setting's slider).
    /// </summary>
    public const int FriendlyMin = 1, FriendlyMax = 10;

    /// <summary>
    /// Game hours a campaign request (a raid, a reinforcement) counts as out after its delay while no campaign of it is seen running.
    /// </summary>
    public const double CampaignWaitHours = 24;

    /// <summary>
    /// The overrides, by source.
    /// </summary>
    readonly Dictionary<string, int> _extra = overrides
        .GroupBy(o => o.Source, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Sum(o => o.Extra), StringComparer.Ordinal);

    /// <summary>
    /// The overrides, as registered.
    /// </summary>
    public IReadOnlyList<CapOverride> Overrides => overrides;

    /// <summary>
    /// How many parties of the kind may be out at once; none: no cap.
    /// </summary>
    public static int Cap(PartyKind kind, RivalryOptions options) => kind switch
    {
        PartyKind.Hostile => HostileCap,
        PartyKind.Friendly => Math.Clamp(options.FriendlyPartiesAtOnce, FriendlyMin, FriendlyMax),
        _ => int.MaxValue,
    };

    /// <summary>
    /// Whether one more party of the kind from <paramref name="source"/> may start at <paramref name="now"/> with <paramref name="out"/>
    /// already out: under its cap and, for a hostile one, past the rest after the last hostile party.
    /// </summary>
    public bool Admits(PartyKind kind, string source, int @out, RivalryOptions options, double now)
    {
        if (kind == PartyKind.None) return true;
        if (kind == PartyKind.Hostile && Resting(now)) return false;

        int extra = kind == PartyKind.Hostile ? _extra.GetValueOrDefault(source) : 0;
        return @out < Cap(kind, options) + extra;
    }

    /// <summary>
    /// The kind of a behaviour by name; an unknown one is none.
    /// </summary>
    public PartyKind KindOf(string behaviour) => behaviours.TryGetValue(behaviour, out var b) ? b.Party : PartyKind.None;

    /// <summary>
    /// The kind of a decision: a start is its behaviour's, a hunt and a raid are hostile, a reinforcement friendly, the rest none.
    /// </summary>
    public PartyKind KindOf(Decision decision) => decision switch
    {
        StartDecision s => KindOf(s.Behaviour),
        HuntDecision or RaidDecision => PartyKind.Hostile,
        ReinforceDecision => PartyKind.Friendly,
        _ => PartyKind.None,
    };

    #endregion

    #region Counting

    /// <summary>
    /// One-shot decisions and claims still out, in the order they began.
    /// </summary>
    readonly List<LedgerEntry> _ledger = [];

    /// <summary>
    /// One ledger entry: until <see cref="Until"/>, or while a campaign of <see cref="Faction"/> at <see cref="Town"/> runs when
    /// <see cref="Campaign"/>.
    /// </summary>
    sealed record LedgerEntry(PartyKind Kind, string Source, string What, string Key, int Faction, int Town, bool Campaign)
    {
        /// <summary>
        /// The game hour it stops counting (a claim's renewal moves it).
        /// </summary>
        public double Until { get; set; }
    }

    /// <summary>
    /// The parties out now: runs under way whose behaviour is a party, then the ledger.
    /// </summary>
    public IEnumerable<PartyView> Out(IReadOnlyList<BehaviourRun> runs, PlayerReport report, double now) =>
        RunsOut(runs).Concat(_ledger.Where(e => Alive(e, report, now)).Select(View));

    /// <summary>
    /// How many parties of the kind are out now.
    /// </summary>
    public int Count(PartyKind kind, IReadOnlyList<BehaviourRun> runs, PlayerReport report, double now) =>
        Out(runs, report, now).Count(p => p.Kind == kind);

    /// <summary>
    /// The runs that are parties and still under way (<see cref="IBehaviour.Underway"/>).
    /// </summary>
    IEnumerable<PartyView> RunsOut(IReadOnlyList<BehaviourRun> runs)
    {
        foreach (var run in runs)
        {
            if (!behaviours.TryGetValue(run.Behaviour, out var b) || b.Party == PartyKind.None || !b.Underway(run)) continue;

            yield return new PartyView(b.Party, run.Source, run.Behaviour, run.Args.Text("quarryName") ?? run.Key, run.Faction, run.Id);
        }
    }

    /// <summary>
    /// Whether a ledger entry still counts.
    /// </summary>
    static bool Alive(LedgerEntry e, PlayerReport report, double now) =>
        now < e.Until || (e.Campaign && report.Campaigns.Any(c => c.Faction == e.Faction && c.Town == e.Town));

    /// <summary>
    /// A ledger entry as a view.
    /// </summary>
    static PartyView View(LedgerEntry e) => new(e.Kind, e.Source, e.What, e.Key, e.Faction, 0);

    /// <summary>
    /// Drops the ledger entries that stopped counting.
    /// </summary>
    public void Expire(PlayerReport report, double now) => _ledger.RemoveAll(e => !Alive(e, report, now));

    #endregion

    #region Recording

    /// <summary>
    /// What a claim's entry is called.
    /// </summary>
    const string ClaimWhat = "claim";

    /// <summary>
    /// Notes a one-shot decision that sent a party (a hunt that marked squads, a campaign request that was queued, squads sent to reinforce); a
    /// hunt is keyed by its quarry's <paramref name="name"/>.
    /// </summary>
    public void Record(Decision decision, double now, string? name)
    {
        var entry = decision switch
        {
            HuntDecision d => new LedgerEntry(PartyKind.Hostile, d.Director, "hunt", name ?? "", d.Faction, 0, false) { Until = now + d.Hours },
            RaidDecision d => Campaign(PartyKind.Hostile, d.Director, "raid", d.Faction, d.Town, now + d.DelayHours),
            ReinforceDecision d => Campaign(PartyKind.Friendly, d.Director, "reinforce", d.Faction, d.Town, now + d.DelayHours),
            _ => null,
        };
        if (entry is not null) _ledger.Add(entry);
    }

    /// <summary>
    /// A campaign request's entry.
    /// </summary>
    static LedgerEntry Campaign(PartyKind kind, string source, string what, int faction, int town, double starts) =>
        new(kind, source, what, $"town{town}", faction, town, true) { Until = starts + CampaignWaitHours };

    /// <summary>
    /// Another mod's own party (<see cref="IRivalryState.ClaimParty"/>): renews the claim of the same source and key, else adds it when the cap
    /// admits it; whether the claim holds.
    /// </summary>
    public bool Claim(PartyClaim claim, int @out, RivalryOptions options, double now)
    {
        if (_ledger.FirstOrDefault(e => e.What == ClaimWhat && e.Source == claim.Source && e.Key == claim.Key) is { } held)
        {
            held.Until = now + claim.Hours;
            return true;
        }
        if (!Admits(claim.Kind, claim.Source, @out, options, now)) return false;

        _ledger.Add(new LedgerEntry(claim.Kind, claim.Source, ClaimWhat, claim.Key, claim.Faction, 0, false) { Until = now + claim.Hours });
        return true;
    }

    /// <summary>
    /// Whether another mod holds a claim of the source and key.
    /// </summary>
    public bool Holds(string source, string key) => _ledger.Any(e => e.What == ClaimWhat && e.Source == source && e.Key == key);

    /// <summary>
    /// Ends another mod's claim; false when it held none.
    /// </summary>
    public bool Release(string source, string key) => _ledger.RemoveAll(e => e.What == ClaimWhat && e.Source == source && e.Key == key) > 0;

    #endregion

    #region Rest

    /// <summary>
    /// The rest settings' bounds (game hours).
    /// </summary>
    public const int RestMax = 168;

    /// <summary>
    /// Whether a hostile party was out at the last look.
    /// </summary>
    bool _hostileOut;

    /// <summary>
    /// The game hour the rest after the last hostile party ends; no hostile party starts before it.
    /// </summary>
    double _restUntil;

    /// <summary>
    /// The game hour the rest ends (in the past when not resting).
    /// </summary>
    public double RestUntil => _restUntil;

    /// <summary>
    /// Whether the rest after the last hostile party holds new ones back at <paramref name="now"/>.
    /// </summary>
    public bool Resting(double now) => now < _restUntil;

    /// <summary>
    /// Looks at the hostile parties out, every slow step: when the last one has ended, the rest starts, its length rolled from
    /// <paramref name="roll"/> (uniform in [0, 1)) between the settings' floor and ceiling, whole hours.
    /// </summary>
    public void Look(int hostileOut, double now, float roll, RivalryOptions options)
    {
        if (hostileOut > 0)
        {
            _hostileOut = true;
            return;
        }
        if (!_hostileOut) return;

        _hostileOut = false;
        _restUntil = now + RestHours(roll, options);
    }

    /// <summary>
    /// A rest's length: a whole number of hours from the floor to the ceiling, both included (a ceiling under the floor counts as the floor).
    /// </summary>
    public static int RestHours(float roll, RivalryOptions options)
    {
        int floor = Math.Clamp(options.HuntRestMinHours, 0, RestMax);
        int ceiling = Math.Clamp(options.HuntRestMaxHours, floor, RestMax);
        return Math.Min(ceiling, floor + (int)(roll * (ceiling - floor + 1)));
    }

    #endregion

    #region Saving

    /// <summary>
    /// The ledger, into the world's hash.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        h.Add(_ledger.Count);
        foreach (var e in _ledger)
        {
            h.Add((int)e.Kind);
            h.Add(Rng.StableHash(e.Source));
            h.Add(Rng.StableHash(e.Key));
            h.Add(e.Faction);
            h.Add(e.Until);
        }

        h.Add(_hostileOut ? 1 : 0);
        h.Add(_restUntil);
    }

    /// <summary>
    /// The ledger, factions by string id (indexes differ between mod lists).
    /// </summary>
    public void Write(BinaryWriter w, PopulationData data)
    {
        w.Write(_ledger.Count);
        foreach (var e in _ledger)
        {
            w.Write((int)e.Kind);
            w.Write(e.Source);
            w.Write(e.What);
            w.Write(e.Key);
            w.Write(data.FactionAt(e.Faction)?.Id ?? "");
            w.Write(e.Town);
            w.Write(e.Campaign);
            w.Write(e.Until);
        }
    }

    /// <summary>
    /// The rest: whether a hostile party was out, and when the rest ends.
    /// </summary>
    public void WriteRest(BinaryWriter w)
    {
        w.Write(_hostileOut);
        w.Write(_restUntil);
    }

    /// <summary>
    /// Reads the rest <see cref="WriteRest"/> wrote.
    /// </summary>
    public void ReadRest(BinaryReader r)
    {
        _hostileOut = r.ReadBoolean();
        _restUntil = r.ReadDouble();
    }

    /// <summary>
    /// Reads the ledger <see cref="Write"/> wrote.
    /// </summary>
    public void Read(BinaryReader r, PopulationData data)
    {
        _ledger.Clear();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var kind = (PartyKind)r.ReadInt32();
            string source = r.ReadString(), what = r.ReadString(), key = r.ReadString();
            int faction = data.FactionIndex(r.ReadString());
            var entry = new LedgerEntry(kind, source, what, key, faction, r.ReadInt32(), r.ReadBoolean()) { Until = r.ReadDouble() };
            _ledger.Add(entry);
        }
    }

    #endregion
}

/// <summary>
/// A party another mod runs itself, counted under Rivalry Core's caps (<see cref="IRivalryState.ClaimParty"/>): <see cref="Source"/> names it
/// (prefixed with the mod's id, as overrides name it), <see cref="Key"/> is stable across loads (a character's name, a town key), the claim
/// lapses <see cref="Hours"/> game hours after it was made or last renewed.
/// </summary>
public sealed record PartyClaim(string Source, PartyKind Kind, string Key, double Hours, int Faction = -1);

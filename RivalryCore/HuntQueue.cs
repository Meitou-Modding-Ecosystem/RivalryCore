using System.Globalization;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// One hostile party that could be sent now (docs/rivalry-core.md "Who goes next"): who proposes it (<see cref="Source"/>, a rule, director or
/// another mod's source), what it aims at (<see cref="Key"/>, stable across loads), the acting faction's index (-1: none), the grudge behind it
/// and its travel time in game hours. A candidate from another mod that wins is given a claim of <see cref="ClaimHours"/>.
/// </summary>
public sealed record HostileCandidate(string Source, string Key, int Faction, float Grudge, double TravelHours, double ClaimHours = 6)
{
    /// <summary>
    /// The candidate's key in the waiting list and the save.
    /// </summary>
    public string Id => $"{Source}|{Key}";
}

/// <summary>
/// A candidate waiting its turn, as the debug rows show it: who, what it aims at, its score now and the hours it has waited.
/// </summary>
public sealed record WaitingView(string Source, string Key, double Score, double Waited)
{
    /// <summary>
    /// The candidate as one line.
    /// </summary>
    public override string ToString()
    {
        string score = Score.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{Source} -> {Key} {score} (waited {DebugView.Hours(Waited)})";
    }
}

/// <summary>
/// Who goes next among the hostile candidates (docs/rivalry-core.md "Who goes next"): each scores its grudge, less
/// <see cref="RivalryOptions.HuntTravelWeight"/> per hour of travel, plus <see cref="RivalryOptions.HuntWaitWeight"/> per hour it has waited
/// since it first became eligible; the best that the caps admit is sent, the rest wait. The hours each has waited are saved and hashed.
/// Other mods' candidates (<see cref="IRivalryState.Propose"/>) wait here between assessments.
/// </summary>
internal sealed class HuntQueue
{
    #region Waiting

    /// <summary>
    /// A candidate not proposed for this many game hours is forgotten and starts waiting afresh (a rule's chance roll that fails now and then
    /// keeps its place).
    /// </summary>
    public const double ForgetHours = 24;

    /// <summary>
    /// The game hour each candidate first became eligible and the hour it was last proposed, by <see cref="HostileCandidate.Id"/>.
    /// </summary>
    readonly SortedDictionary<string, (double Since, double Seen)> _since = new(StringComparer.Ordinal);

    /// <summary>
    /// Other mods' candidates (<see cref="IRivalryState.Propose"/>) by id, with the game hour they were last proposed.
    /// </summary>
    readonly SortedDictionary<string, (HostileCandidate Candidate, double At)> _external = new(StringComparer.Ordinal);

    /// <summary>
    /// The candidates of the latest assessment, best first, for the debug rows.
    /// </summary>
    public IReadOnlyList<WaitingView> Latest { get; private set; } = [];

    /// <summary>
    /// The game hour the candidate first became eligible; <paramref name="now"/> for a new one.
    /// </summary>
    public double Since(HostileCandidate c, double now) => _since.TryGetValue(c.Id, out var e) ? e.Since : now;

    /// <summary>
    /// The candidate's score at <paramref name="now"/>.
    /// </summary>
    public double Score(HostileCandidate c, double now, RivalryOptions options) =>
        c.Grudge - options.HuntTravelWeight * c.TravelHours + options.HuntWaitWeight * (now - Since(c, now));

    /// <summary>
    /// Another mod's candidate, kept while it is proposed again (<see cref="External"/>).
    /// </summary>
    public void Propose(HostileCandidate c, double now) => _external[c.Id] = (c, now);

    /// <summary>
    /// Other mods' candidates still proposed at <paramref name="now"/>; older ones are dropped.
    /// </summary>
    public IEnumerable<HostileCandidate> External(double now, double keepHours)
    {
        foreach (string id in _external.Where(e => now - e.Value.At > keepHours).Select(e => e.Key).ToList()) _external.Remove(id);
        return _external.Values.Select(e => e.Candidate);
    }

    #endregion

    #region Picking

    /// <summary>
    /// Orders the assessment's candidates best first (score, then grudge, then the longer wait, then source and key), notes when new ones
    /// became eligible and forgets those no longer proposed.
    /// </summary>
    public List<T> Rank<T>(IEnumerable<T> candidates, Func<T, HostileCandidate> info, double now, RivalryOptions options)
    {
        var list = candidates.ToList();
        Remember(list.Select(info), now);

        var ranked = list
            .OrderByDescending(c => Score(info(c), now, options))
            .ThenByDescending(c => info(c).Grudge)
            .ThenBy(c => Since(info(c), now))
            .ThenBy(c => info(c).Source, StringComparer.Ordinal)
            .ThenBy(c => info(c).Key, StringComparer.Ordinal)
            .ToList();
        Latest = [.. ranked.Select(c => View(info(c), now, options))];
        return ranked;
    }

    /// <summary>
    /// The candidate sent: it leaves the debug rows' list and starts waiting afresh next time.
    /// </summary>
    public void Sent(HostileCandidate c)
    {
        _since.Remove(c.Id);
        _external.Remove(c.Id);
        Latest = [.. Latest.Where(v => v.Source != c.Source || v.Key != c.Key)];
    }

    /// <summary>
    /// Notes every candidate proposed now (a new one waits from now) and forgets those not proposed for <see cref="ForgetHours"/>.
    /// </summary>
    void Remember(IEnumerable<HostileCandidate> candidates, double now)
    {
        foreach (var c in candidates) _since[c.Id] = (Since(c, now), now);

        var stale = _since.Where(e => now - e.Value.Seen > ForgetHours).Select(e => e.Key).ToList();
        foreach (string id in stale) _since.Remove(id);
    }

    /// <summary>
    /// A candidate as a view.
    /// </summary>
    WaitingView View(HostileCandidate c, double now, RivalryOptions options) =>
        new(c.Source, c.Key, Score(c, now, options), now - Since(c, now));

    #endregion

    #region Saving

    /// <summary>
    /// The first-eligible and last-proposed hours, into the world's hash.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        h.Add(_since.Count);
        foreach (var (id, (since, seen)) in _since)
        {
            h.Add(Rng.StableHash(id));
            h.Add(since);
            h.Add(seen);
        }
    }

    /// <summary>
    /// The first-eligible and last-proposed hours.
    /// </summary>
    public void Write(BinaryWriter w)
    {
        w.Write(_since.Count);
        foreach (var (id, (since, seen)) in _since)
        {
            w.Write(id);
            w.Write(since);
            w.Write(seen);
        }
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote.
    /// </summary>
    public void Read(BinaryReader r)
    {
        Clear();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string id = r.ReadString();
            double since = r.ReadDouble();
            _since[id] = (since, r.ReadDouble());
        }
    }

    /// <summary>
    /// Forgets every wait (a save from before the queue).
    /// </summary>
    public void Clear()
    {
        _since.Clear();
        _external.Clear();
    }

    #endregion
}

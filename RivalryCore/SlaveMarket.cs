using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay;

namespace Rivalry;

/// <summary>
/// Who buys captives (docs/rivalry-core.md "Abduction"): a faction buys a race when the game's own data shows it keeping slaves of that race, that
/// is when one of its squad templates lists a slave character of the race. Built once per data set from the data (<see cref="For"/>).
/// </summary>
public sealed class SlaveMarket(PopulationData data)
{
    /// <summary>
    /// The market of each data set.
    /// </summary>
    static readonly ConditionalWeakTable<PopulationData, SlaveMarket> Markets = [];

    /// <summary>
    /// The data set's market, built on first use and kept with it: reading every faction's squad templates is too slow to repeat per step.
    /// </summary>
    public static SlaveMarket For(PopulationData data) => Markets.GetValue(data, d => new SlaveMarket(d));

    /// <summary>
    /// The races each faction keeps as slaves, by faction index; filled on first use.
    /// </summary>
    Dictionary<int, HashSet<string>>? _races;

    /// <summary>
    /// Whether the faction keeps slaves of the race (its record id) in the game's data.
    /// </summary>
    public bool Buys(int faction, string race) => (_races ??= Build()).TryGetValue(faction, out var races) && races.Contains(race);

    /// <summary>
    /// The town site nearest to <paramref name="from"/> whose faction buys the race and is not hostile to <paramref name="seller"/> (ties by
    /// index); -1: none.
    /// </summary>
    public int NearestBuyer(Vector3 from, string race, int seller)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        foreach (var site in data.Sites)
        {
            int owner = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
            if (owner < 0 || !Buys(owner, race) || data.Relations.Contains(owner) && data.Relations.IsHostile(owner, seller)) continue;

            float d = Vector2.DistanceSquared(site.Position.Flat(), from.Flat());
            if (d < bestDistance) (best, bestDistance) = (site.Index, d);
        }

        return best;
    }

    /// <summary>
    /// Reads every faction's squad templates for slave characters and their races.
    /// </summary>
    Dictionary<int, HashSet<string>> Build()
    {
        var races = new Dictionary<int, HashSet<string>>();
        for (int f = 0; f < data.Factions.Count; f++)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in TemplatesOf(data.Factions[f])) AddSlaveRaces(link.Id, found);
            if (found.Count > 0) races[f] = found;
        }

        return races;
    }

    /// <summary>
    /// Every squad template a faction lists.
    /// </summary>
    static IEnumerable<RecordLink> TemplatesOf(FactionData faction) => faction.Squads
        .Concat(faction.SquadDefault)
        .Concat(faction.SpecialSquads)
        .Concat(faction.Residents)
        .Concat(faction.BarSquads)
        .Concat(faction.DefaultResident);

    /// <summary>
    /// Adds the races of a template's slave characters.
    /// </summary>
    void AddSlaveRaces(string template, HashSet<string> found)
    {
        if (data.Db.Find(template) is not { } record) return;

        foreach (var slave in SquadTemplate.From(record).Slaves)
        {
            if (data.Db.Find(slave.Id)?.GetReferences("race") is [var race, ..]) found.Add(race.TargetStringId);
        }
    }
}

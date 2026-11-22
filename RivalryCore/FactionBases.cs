using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Rivalry;

/// <summary>
/// How a base was found (docs/factions.md "Bases"), first preferred.
/// </summary>
public enum BaseSource
{
    /// <summary>
    /// A town or region named for the faction (<see cref="RivalryOptions.Homes"/>, a run's <c>home</c> argument): set on purpose, so it wins.
    /// </summary>
    Named,

    /// <summary>
    /// A placed town, camp or outpost the faction owns now.
    /// </summary>
    Owned,

    /// <summary>
    /// A placed town whose residents, roaming squads or bar squads include one of the faction's squad templates.
    /// </summary>
    Spawns,

    /// <summary>
    /// A map cell of a region (BIOME_GROUP) whose homeless spawns include one of the faction's squad templates.
    /// </summary>
    Region,
}

/// <summary>
/// One place a faction's squads come from: a point, the town site there (-1 for a region cell), and how it was found.
/// </summary>
public readonly record struct FactionBase(Vector2 Position, int Site, BaseSource Source, string Label);

/// <summary>
/// Where each faction's squads come from (docs/factions.md "Bases"), read from the game data so a modded faction is covered by whatever towns, squad
/// lists and regions its mod places: the towns and regions named for it; else the towns it owns; else towns that spawn its squads; else the regions
/// whose homeless spawns are its squads. Static data is cached per <see cref="PopulationData"/>; owned towns are read live (an override can change a
/// town's owner).
/// </summary>
public static class FactionBases
{
    #region Lookup

    /// <summary>
    /// The static bases of each data set: spawning towns and region cells, by faction index.
    /// </summary>
    static readonly ConditionalWeakTable<PopulationData, Cache> Caches = [];

    /// <summary>
    /// A faction's bases of the best kind it has (every base of that kind); empty when none. <paramref name="named"/>: town or region names for it.
    /// </summary>
    public static IReadOnlyList<FactionBase> Of(PopulationData data, int faction, IReadOnlyList<string> named)
    {
        if (faction < 0 || faction >= data.Factions.Count) return [];

        var cache = Caches.GetValue(data, d => new Cache(d));
        if (Named(data, named) is { Count: > 0 } byName) return byName;
        if (Owned(data, faction) is { Count: > 0 } owned) return owned;

        return cache.Spawning(faction) is { Count: > 0 } spawning ? spawning : cache.Regions(faction);
    }

    /// <summary>
    /// The base nearest a point; null when the faction has none.
    /// </summary>
    public static FactionBase? Nearest(PopulationData data, int faction, IReadOnlyList<string> named, Vector2 to) =>
        Of(data, faction, named)
            .OrderBy(b => Vector2.DistanceSquared(b.Position, to))
            .ThenBy(b => b.Site)
            .Cast<FactionBase?>()
            .FirstOrDefault();

    /// <summary>
    /// The names a faction's bases come from: <see cref="RivalryOptions.Homes"/> by its string id or name, then a run's own list.
    /// </summary>
    public static IReadOnlyList<string> NamesFor(PopulationData data, int faction, RivalryOptions options, IReadOnlyList<string> run)
    {
        if (faction < 0 || faction >= data.Factions.Count) return run;

        var f = data.Factions[faction];
        var homes = options.Homes;
        string? listed = homes.GetValueOrDefault(f.Id) ?? homes.GetValueOrDefault(f.Name);
        return [.. RivalLeaders.Split(listed), .. run];
    }

    #endregion

    #region Kinds

    /// <summary>
    /// The placed towns the faction owns now.
    /// </summary>
    static List<FactionBase> Owned(PopulationData data, int faction) =>
        [.. data.Sites
            .Where(s => s.Index < data.SiteFactions.Count && data.SiteFactions[s.Index] == faction)
            .Select(s => Site(s, BaseSource.Owned))];

    /// <summary>
    /// The placed towns, and the cells of the regions, whose name is one of <paramref name="names"/> (any case).
    /// </summary>
    static List<FactionBase> Named(PopulationData data, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return [];

        bool Listed(string name) => names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        var towns = data.Sites.Where(s => Listed(s.Town.Name)).Select(s => Site(s, BaseSource.Named));
        return [.. towns, .. Cells(data, g => Listed(g.Name), BaseSource.Named)];
    }

    /// <summary>
    /// A site as a base.
    /// </summary>
    static FactionBase Site(TownSite s, BaseSource source) => new(s.Position.Flat(), s.Index, source, s.Town.Name);

    /// <summary>
    /// The centres of the map cells whose region matches.
    /// </summary>
    static IEnumerable<FactionBase> Cells(PopulationData data, Func<BiomeGroupData, bool> match, BaseSource source)
    {
        if (data.Biomes is not { } biomes) yield break;

        for (int cz = 0; cz < WeatherAreas.Cells; cz++)
        {
            for (int cx = 0; cx < WeatherAreas.Cells; cx++)
            {
                if (biomes.GroupOf(cx, cz) is not (>= 0 and var g) || !match(biomes.Groups[g])) continue;

                yield return new FactionBase(CellCentre(cx, cz), -1, source, biomes.Groups[g].Name);
            }
        }
    }

    /// <summary>
    /// A map cell's centre (<see cref="WeatherAreas.CellOf"/> inverted).
    /// </summary>
    static Vector2 CellCentre(int cx, int cz) =>
        new((cx - WeatherAreas.Origin + 0.5f) * WeatherAreas.CellSize, (cz - WeatherAreas.Origin + 0.5f) * WeatherAreas.CellSize);

    #endregion

    #region Cache

    /// <summary>
    /// The bases read once from a data set's records.
    /// </summary>
    sealed class Cache(PopulationData data)
    {
        /// <summary>
        /// Per faction, the towns whose squad lists hold its templates.
        /// </summary>
        readonly Lazy<Dictionary<int, List<FactionBase>>> _spawning = new(() => BuildSpawning(data));

        /// <summary>
        /// Per faction, the cells of regions whose homeless spawns are its templates.
        /// </summary>
        readonly Lazy<Dictionary<int, List<FactionBase>>> _regions = new(() => BuildRegions(data));

        /// <summary>
        /// The towns that spawn the faction's squads.
        /// </summary>
        public IReadOnlyList<FactionBase> Spawning(int faction) => _spawning.Value.GetValueOrDefault(faction) ?? [];

        /// <summary>
        /// The region cells whose homeless spawns are the faction's squads.
        /// </summary>
        public IReadOnlyList<FactionBase> Regions(int faction) => _regions.Value.GetValueOrDefault(faction) ?? [];

        /// <summary>
        /// Each site's residents, roaming and bar squads, by their templates' spawn faction.
        /// </summary>
        static Dictionary<int, List<FactionBase>> BuildSpawning(PopulationData data)
        {
            var map = new Dictionary<int, List<FactionBase>>();
            foreach (var site in data.Sites)
            {
                var links = site.Town.Residents.Concat(site.Town.RoamingSquads).Concat(site.Town.BarSquads);
                foreach (int f in Factions(data, links, site.Index).Distinct()) Add(map, f, Site(site, BaseSource.Spawns));
            }

            return map;
        }

        /// <summary>
        /// Each region's homeless spawns, by their templates' spawn faction, as the region's cells.
        /// </summary>
        static Dictionary<int, List<FactionBase>> BuildRegions(PopulationData data)
        {
            var map = new Dictionary<int, List<FactionBase>>();
            if (data.Biomes is not { } biomes) return map;

            foreach (var group in biomes.Groups)
            {
                var cells = Cells(data, g => g.Id == group.Id, BaseSource.Region).ToList();
                foreach (int f in Factions(data, group.HomelessSpawns, null).Distinct()) Add(map, f).AddRange(cells);
            }

            return map;
        }

        /// <summary>
        /// The spawn factions of the squad templates linked (<see cref="PopulationData.SpawnFactionOf"/>; a town's own when the template names none).
        /// </summary>
        static IEnumerable<int> Factions(PopulationData data, IEnumerable<RecordLink> links, int? site)
        {
            int? townFaction = site is { } s && s < data.SiteFactions.Count ? data.SiteFactions[s] : null;
            foreach (var link in links)
            {
                if (data.Db.Find(link.Id) is not { Type: Meitou.Data.Fcs.FcsRecordType.SquadTemplate } record) continue;

                yield return data.SpawnFactionOf(SquadTemplate.From(record), townFaction);
            }
        }

        /// <summary>
        /// Adds a base to a faction's list.
        /// </summary>
        static void Add(Dictionary<int, List<FactionBase>> map, int faction, FactionBase b) => Add(map, faction).Add(b);

        /// <summary>
        /// A faction's list, made when missing.
        /// </summary>
        static List<FactionBase> Add(Dictionary<int, List<FactionBase>> map, int faction)
        {
            if (!map.TryGetValue(faction, out var list)) map[faction] = list = [];

            return list;
        }
    }

    #endregion
}

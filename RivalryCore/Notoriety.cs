using Meitou.Simulation;

namespace Rivalry;

/// <summary>A faction's grudge against one character.</summary>
public readonly record struct GrudgeKey(int Faction, CharacterId Who) : IComparable<GrudgeKey>
{
    public int CompareTo(GrudgeKey other)
    {
        if (Faction != other.Faction) return Faction.CompareTo(other.Faction);
        if (Who.Slot != other.Who.Slot) return Who.Slot.CompareTo(other.Who.Slot);
        return Who.Generation.CompareTo(other.Who.Generation);
    }
}

/// <summary>
/// The grudges factions hold against characters (docs/rivalry-core.md "Notoriety"): fed by combat hits and by directors, cooling by a fixed amount per game
/// hour but never below a held floor (<see cref="HoldFloor"/>), dropped below <see cref="Floor"/> or when the character is gone. Changed only in serial
/// steps; walked in key order so the hash and saves never depend on insertion.
/// </summary>
public sealed class NotorietyLedger
{
    /// <summary>A grudge below this is forgotten.</summary>
    public const float Floor = 0.5f;

    readonly SortedDictionary<GrudgeKey, float> _grudges = [];

    /// <summary>
    /// The held floors: a grudge that sent a hit squad cools no lower than this until that squad is beaten where the player can see it.
    /// </summary>
    readonly SortedDictionary<GrudgeKey, float> _floors = [];

    public int Count => _grudges.Count;

    /// <summary>
    /// How many grudges have been started (a key that had none), ever: a cheap sign that someone new is held a grudge against.
    /// </summary>
    public int Started { get; private set; }

    /// <summary>Every grudge, in key order.</summary>
    public IEnumerable<KeyValuePair<GrudgeKey, float>> Entries => _grudges;

    public float Grudge(int faction, CharacterId who) => _grudges.GetValueOrDefault(new GrudgeKey(faction, who));

    /// <summary>
    /// The floor held under a grudge; 0: none.
    /// </summary>
    public float HeldFloor(int faction, CharacterId who) => _floors.GetValueOrDefault(new GrudgeKey(faction, who));

    /// <summary>
    /// Every held floor, in key order.
    /// </summary>
    public IEnumerable<KeyValuePair<GrudgeKey, float>> Floors => _floors;

    /// <summary>A character's notoriety: the sum of every faction's grudge against it.</summary>
    public float Notoriety(CharacterId who) => _grudges.Where(g => g.Key.Who == who).Sum(g => g.Value);

    /// <summary>Adds to (or, negative, eases) a grudge, never below its held floor; one that falls below <see cref="Floor"/> is forgotten. Serial steps only.</summary>
    public void Add(int faction, CharacterId who, float delta)
    {
        if (faction < 0 || who.IsNone || !float.IsFinite(delta)) return;
        var key = new GrudgeKey(faction, who);
        float value = Math.Max(_grudges.GetValueOrDefault(key) + delta, _floors.GetValueOrDefault(key));
        if (value < Floor)
        {
            Forget(key);
            return;
        }

        if (!_grudges.ContainsKey(key)) Started++;
        _grudges[key] = value;
    }

    /// <summary>
    /// Every grudge cools by <paramref name="perHour"/> per game hour over <paramref name="hours"/>, never below its held floor; characters no longer in the
    /// table are dropped with their floors.
    /// </summary>
    public void Cool(CharacterTable table, double hours, double perHour)
    {
        float drop = (float)Math.Max(0, hours * perHour);
        foreach (var key in _grudges.Keys.ToList())
        {
            float value = Math.Max(_grudges[key] - drop, _floors.GetValueOrDefault(key));
            if (value < Floor || !table.IsAlive(key.Who)) Forget(key);
            else _grudges[key] = value;
        }
    }

    /// <summary>
    /// Holds <paramref name="floor"/> under a grudge that has reached it (a hit squad was sent); nothing when the grudge is lower.
    /// </summary>
    public void HoldFloor(int faction, CharacterId who, float floor)
    {
        var key = new GrudgeKey(faction, who);
        if (!_grudges.TryGetValue(key, out float value) || value < floor || !float.IsFinite(floor)) return;

        _floors[key] = Math.Max(floor, _floors.GetValueOrDefault(key));
    }

    /// <summary>
    /// Lets a grudge cool freely again (its hit squad was beaten in sight of the player).
    /// </summary>
    public void ReleaseFloor(int faction, CharacterId who) => _floors.Remove(new GrudgeKey(faction, who));

    /// <summary>
    /// Releases every floor a faction holds.
    /// </summary>
    public void ReleaseFloors(int faction)
    {
        foreach (var key in _floors.Keys.Where(k => k.Faction == faction).ToList()) _floors.Remove(key);
    }

    /// <summary>
    /// Forgets every grudge a faction holds, floors included (its leader was handed to the law); the sum forgotten.
    /// </summary>
    public float ClearFaction(int faction)
    {
        var keys = _grudges.Keys.Where(k => k.Faction == faction).ToList();
        float sum = keys.Sum(k => _grudges[k]);
        foreach (var key in keys) Forget(key);

        ReleaseFloors(faction);
        return sum;
    }

    /// <summary>
    /// Drops a grudge and its floor.
    /// </summary>
    void Forget(GrudgeKey key)
    {
        _grudges.Remove(key);
        _floors.Remove(key);
    }

    public void Clear()
    {
        _grudges.Clear();
        _floors.Clear();
    }

    public void Hash(ref StateHasher h)
    {
        h.Add(_grudges.Count);
        foreach (var (key, value) in _grudges)
        {
            h.Add(key.Faction);
            h.Add(key.Who);
            h.Add(value);
        }

        h.Add(_floors.Count);
        foreach (var (key, value) in _floors)
        {
            h.Add(key.Faction);
            h.Add(key.Who);
            h.Add(value);
        }
    }
}

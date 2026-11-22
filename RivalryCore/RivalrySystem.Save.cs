using System.Text;
using Meitou.Simulation;

namespace Rivalry;

public sealed partial class RivalrySystem
{
    /// <summary>The sidecar layout's version (docs/rivalry-core.md "Save data").</summary>
    public const int SaveVersion = 6;

    /// <summary>A saved grudge waiting for its character: faction by string id, character by name and record (slots differ after a load).</summary>
    readonly record struct SavedGrudge(string Faction, string Name, string Record, float Value, float HeldFloor = 0);

    readonly record struct Subject(string Name, string Record);

    readonly Dictionary<CharacterId, Subject> _subjects = [];

    /// <summary>
    /// <see cref="NotorietyLedger.Started"/> when every grudge last had a subject named.
    /// </summary>
    int _namedAt = -1;
    List<SavedGrudge>? _pending;

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(SaveVersion);
        w.Write(_nextAssess);
        w.Write(_lastAssess);
        WriteGrudges(w);
        var keys = _cooldowns.Entries.ToList();
        w.Write(keys.Count);
        foreach (var (key, end) in keys)
        {
            w.Write(key);
            w.Write(end);
        }

        _runs.Write(_world, w);
        Rivals.Write(w);
        _parties.Write(w, _data);
        _parties.WriteRest(w);
        _queue.Write(w);
    }

    void WriteGrudges(BinaryWriter w)
    {
        var saved = Notoriety.Entries
            .Where(g => g.Key.Faction < _data.Factions.Count && _subjects.ContainsKey(g.Key.Who))
            .Select(g => Saved(g.Key, g.Value))
            .Concat(_pending ?? [])
            .ToList();
        w.Write(saved.Count);
        foreach (var g in saved)
        {
            w.Write(g.Faction);
            w.Write(g.Name);
            w.Write(g.Record);
            w.Write(g.Value);
            w.Write(g.HeldFloor);
        }
    }

    /// <summary>
    /// A grudge as saved: its faction by string id, its character by name and record, its value and held floor.
    /// </summary>
    SavedGrudge Saved(GrudgeKey key, float value)
    {
        var subject = _subjects[key.Who];
        return new SavedGrudge(_data.Factions[key.Faction].Id, subject.Name, subject.Record, value, Notoriety.HeldFloor(key.Faction, key.Who));
    }

    public void Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int version = r.ReadInt32();
        if (version is < 1 or > SaveVersion)
        {
            _log($"save data version {version} is not 1 to {SaveVersion}; starting fresh");
            return;
        }
        _nextAssess = r.ReadDouble();
        _lastAssess = r.ReadDouble();
        // Version 3 adds each grudge's held floor and, at the end, the rival roster.
        _pending = ReadGrudges(r, withFloors: version >= 3);
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++) _cooldowns.Set(r.ReadString(), r.ReadDouble());

        // Version 2 adds the behaviours under way.
        if (version >= 2) _runs.Read(r);
        if (version >= 3) Rivals.Read(r);

        // Version 4 adds, last, the party ledger (one-shot parties and other mods' claims).
        if (version >= 4) _parties.Read(r, _data);

        // Version 5 adds, last, the rest between hunts.
        if (version >= 5) _parties.ReadRest(r);

        // Version 6 adds, last, the hours the hostile candidates have waited.
        if (version >= 6) _queue.Read(r);
        else _queue.Clear();
    }

    static List<SavedGrudge> ReadGrudges(BinaryReader r, bool withFloors)
    {
        int count = r.ReadInt32();
        var list = new List<SavedGrudge>(count);
        for (int i = 0; i < count; i++)
        {
            var g = new SavedGrudge(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadSingle());
            list.Add(withFloors ? g with { HeldFloor = r.ReadSingle() } : g);
        }

        return list;
    }

    /// <summary>
    /// Keeps each grudge's character named for the next save, and gives saved grudges back to the characters of the loaded world: by faction id, then the
    /// first living character in slot order with the saved name and record, at the first serial step after the load (the save's characters are in
    /// the table by then). One not found is dropped.
    /// </summary>
    void ResolveSaved(World world)
    {
        NameSubjects(world);
        if (_pending is null) return;
        var table = world.Characters;
        var taken = new HashSet<(int, CharacterId)>();
        foreach (var g in _pending)
            if (_data.FactionIndex(g.Faction) is >= 0 and var f && FindSubject(table, g, f, taken) is { IsNone: false } who) Restore(f, who, g);
        _pending = null;
    }

    /// <summary>
    /// Gives a saved grudge and its held floor back to its character.
    /// </summary>
    void Restore(int faction, CharacterId who, SavedGrudge g)
    {
        Notoriety.Add(faction, who, g.Value);
        if (g.HeldFloor > 0) Notoriety.HoldFloor(faction, who, g.HeldFloor);
    }

    static CharacterId FindSubject(CharacterTable table, SavedGrudge g, int faction, HashSet<(int, CharacterId)> taken)
    {
        var prev = table.Previous;
        for (int slot = 0; slot < prev.Length; slot++)
        {
            if (!prev[slot].Alive || table.Cold(slot) is not { } c || c.Name != g.Name || c.RecordId != g.Record) continue;
            var id = table.IdOf(slot);
            if (taken.Add((faction, id))) return id;
        }
        return CharacterId.None;
    }

    /// <summary>
    /// Notes the name and record of everyone held a grudge against, for the save; only after a new grudge started, until all are named.
    /// </summary>
    void NameSubjects(World world)
    {
        if (Notoriety.Started == _namedAt) return;
        if (!Notoriety.Entries.All(g => _subjects.ContainsKey(g.Key.Who))) Rename(world);
        if (Notoriety.Entries.All(g => _subjects.ContainsKey(g.Key.Who))) _namedAt = Notoriety.Started;
    }

    /// <summary>
    /// Names every character held a grudge against afresh.
    /// </summary>
    void Rename(World world)
    {
        _subjects.Clear();
        foreach (var (key, _) in Notoriety.Entries)
        {
            if (!world.Characters.TryResolveNext(key.Who, out int slot) || world.Characters.Cold(slot) is not { } c) continue;

            _subjects[key.Who] = new Subject(c.Name, c.RecordId);
        }
    }
}

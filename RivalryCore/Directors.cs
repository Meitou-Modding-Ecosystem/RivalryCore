using Meitou.Data.Gameplay;

namespace Rivalry;

/// <summary>
/// A faction whose grudge against a standing player character reaches <see cref="RivalryOptions.HuntThreshold"/> has its squads already near them
/// hold them as an enemy; with none near nothing happens and it looks again at the next assessment. Largest grudge first, at most
/// once per faction and character per cooldown. Each is a hostile candidate with no travel (<see cref="HuntQueue"/>): the largest
/// <see cref="RivalryOptions.GrudgeCandidatesPerAssessment"/> are proposed, and a hunt that goes counts as a hostile party for its hours. Allies
/// never hunt.
/// </summary>
public sealed class GrudgeDirector : IDirector
{
    public string Name => "rivalry.grudge";

    public void Assess(Assessment a, Decisions d)
    {
        if (!a.Options.Hunters) return;
        var grudges = a.Notoriety.Entries
            .Where(g => g.Value >= a.Options.HuntThreshold && !a.IsAlly(g.Key.Faction) && a.Report.Find(g.Key.Who) is { Down: false })
            .OrderByDescending(g => g.Value).ThenBy(g => g.Key)
            .ToList();
        foreach (var (key, value) in grudges.Take(a.Options.GrudgeCandidatesPerAssessment))
        {
            // Keyed by name, not slot: slots change when a save loads, and the cooldown comes back with it.
            // No squad near: nothing to mark, nothing logged, and the cooldown stays free for when one comes by.
            string name = a.Report.Find(key.Who)?.Name ?? "";
            string cooldown = $"{key.Faction}:{name}";
            if (d.Cooling(cooldown) || a.SquadsNear(key.Faction, key.Who) == 0) continue;

            // Its squads are already near: no travel.
            var candidate = new HostileCandidate(Name, cooldown, key.Faction, value, 0);
            a.Propose(candidate, () => Send(a, d, key, cooldown, name));
        }
    }

    /// <summary>
    /// The hunt chosen to go: marks the faction's squads near the character, once per cooldown.
    /// </summary>
    static void Send(Assessment a, Decisions d, GrudgeKey key, string cooldown, string name)
    {
        if (!d.TryClaim(cooldown, a.Options.HuntCooldownHours)) return;

        d.Hunt(key.Faction, key.Who, a.Options.HuntSquads, hours: a.Options.HuntHours);
        if (a.Options.Announce) d.Notice($"{a.Faction(key.Faction)?.Name} wants {name} dead");
    }
}

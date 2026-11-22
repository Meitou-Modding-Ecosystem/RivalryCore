using System.Globalization;
using Meitou.Modding;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// Rivalry Core's entry (docs/rivalry-core.md): registers the framework system, the dialogue codes and the debug rows, and serves dependents as
/// <see cref="IRivalryApi"/>.
/// </summary>
public sealed class RivalryMod : IMod, IRivalryApi, IModDependents
{
    /// <summary>Dialogue condition: the speaker's faction's grudge against the subject (compared with the value).</summary>
    public const int GrudgeCondition = 1600;

    /// <summary>Dialogue condition: the subject's notoriety, every faction's grudge summed (compared with the value).</summary>
    public const int NotorietyCondition = 1601;

    /// <summary>Dialogue effect: the speaker's faction's grudge against the conversation's target grows by the value (negative eases it).</summary>
    public const int GrudgeEffect = 1600;

    /// <summary>
    /// The mod's manifest id.
    /// </summary>
    public const string Id = "rivalry.core";

    readonly Registry _registry = new();
    volatile RivalrySystem? _latest;

    public RivalryMod() => BuiltIn.Register(_registry.For(Id, "rivalry."));

    public RivalryOptions Options { get; } = new();

    public void Initialize(ModContext c)
    {
        c.AddSettings(Options);
        c.AddSystem("rivalry", ctx => Build(ctx, c), SystemAnchor.End);
        c.AddDialogueCondition(GrudgeCondition, check => check.Compare(GrudgeOf(check.World, check.Speaker, check.Subject)));
        c.AddDialogueCondition(NotorietyCondition, check => check.Compare(StateOf(check.World)?.Notoriety.Notoriety(check.Subject) ?? 0));
        c.AddDialogueEffect(GrudgeEffect, act => AddGrudge(act.World, act.Speaker, act.Target, act.Effect.Value));
        AddDebugRows(c);
    }

    public IRivalryRegistry For(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (modId == Id) throw new ArgumentException("Rivalry Core: a dependent registers under its own id.", nameof(modId));
        return _registry.For(modId, modId + ".");
    }

    public void DependentFailed(string modId) => _registry.Drop(modId);

    public IRivalryState? In(World world) => StateOf(world);

    RivalrySystem Build(SystemBuildContext ctx, ModContext c)
    {
        _registry.Frozen = true;
        var system = new RivalrySystem(ctx, _registry, Options, c.RngPurpose, c.Log);
        _latest = system;
        return system;
    }

    static RivalrySystem? StateOf(World world) => world.Systems.OfType<RivalrySystem>().FirstOrDefault();

    static float GrudgeOf(World world, CharacterId speaker, CharacterId subject)
    {
        if (StateOf(world) is not { } state || !world.Characters.TryResolveNext(speaker, out int s) || world.Characters.Cold(s) is not { } c) return 0;
        return state.Notoriety.Grudge(c.Faction, subject);
    }

    static void AddGrudge(World world, CharacterId speaker, CharacterId target, int amount)
    {
        if (StateOf(world) is not { } state || !world.Characters.TryResolveNext(speaker, out int s) || world.Characters.Cold(s) is not { } c) return;
        state.Notoriety.Add(c.Faction, target, amount);
    }

    void AddDebugRows(ModContext c)
    {
        c.AddDebugCommand("Rivalry: report", _ => Describe(_latest));
        c.AddDebugCommand("Rivalry: log (latest results)", _ => Told(c, DescribeLog(_latest)));
        c.AddDebugCommand("Rivalry: character (name; empty: selected, else all)",
            d => Told(c, _latest?.Debug.DescribeCharacters(d.Field, d.Selected) ?? NoWorld), hasField: true);
        c.AddDebugCommand("Rivalry: assess now", d =>
        {
            d.Enqueue(new AssessNowCommand { Tick = d.Tick + 1 });
            return "assessment queued: unpause, then see 'Rivalry: log'";
        });
        c.AddDebugCommand("Rivalry: grudge on selected (faction[, amount])", d => QueueGrudge(d), hasField: true);
        c.AddDebugCommand("Rivalry: runs and rules", _ => Told(c, _latest?.Debug.DescribeRuns() ?? NoWorld));
        c.AddDebugCommand("Rivalry: parties (hostile and friendly caps)", _ => Told(c, _latest?.Debug.DescribeParties() ?? NoWorld));
        c.AddDebugCommand("Rivalry: rival leaders", _ => DescribeRivals(_latest));
        c.AddDebugCommand("Rivalry: faction bases (faction)", d => _latest?.DescribeBases(d.Field) ?? NoWorld, hasField: true);
        c.AddDebugCommand("Rivalry: fire rule (rule[, town or character])", d => QueueRule(d), hasField: true);
        c.AddDebugCommand("Rivalry: visit town (template, faction[, hours[, town]])", d => QueueVisit(d), hasField: true);
    }

    /// <summary>
    /// A row's status before any world runs.
    /// </summary>
    const string NoWorld = "no world yet";

    /// <summary>
    /// How many of the latest log lines the log row shows.
    /// </summary>
    const int LogLines = 6;

    /// <summary>
    /// Writes a row's answer to the game log too (the menu's status line is one line), and returns it.
    /// </summary>
    static string Told(ModContext c, string answer)
    {
        c.Log(answer);
        return answer;
    }

    /// <summary>
    /// The latest decisions and debug results, newest first.
    /// </summary>
    static string DescribeLog(RivalrySystem? system)
    {
        if (system is null) return NoWorld;

        var lines = system.History.Reverse().Take(LogLines).ToList();
        return lines.Count > 0 ? string.Join(" || ", lines) : "nothing decided yet";
    }

    /// <summary>
    /// The named hit squad leaders with their records, and the leaders handed to the law (read between ticks, as the other rows).
    /// </summary>
    static string DescribeRivals(RivalrySystem? system)
    {
        if (system is null) return NoWorld;

        var lines = system.Rivals.Describe().ToList();
        return lines.Count > 0 ? string.Join("; ", lines) : "no rival leaders yet";
    }

    /// <summary>
    /// Queues a rule to fire on a target, whatever its conditions say; a rule or target that cannot work is refused here with what would.
    /// </summary>
    string QueueRule(DebugCommandContext d)
    {
        var parts = d.Field.Split(',', StringSplitOptions.TrimEntries);
        if (_latest is not { } system) return NoWorld;
        if (parts[0].Length == 0) return "type a rule name: " + string.Join(", ", system.Debug.Rules.Select(r => $"{r.Name} [{r.Takes}]"));

        string? target = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        if (system.Debug.CheckRule(parts[0], target) is { } wrong) return wrong;

        d.Enqueue(new FireRuleCommand(parts[0], target) { Tick = d.Tick + 1 });
        return $"{parts[0]} queued on {target ?? "its first target"}: unpause, then see 'Rivalry: log'";
    }

    /// <summary>
    /// Queues a visit of a trader squad template to a player town (the first when none is given).
    /// </summary>
    static string QueueVisit(DebugCommandContext d)
    {
        var parts = d.Field.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length == 0) return "type a squad template id and a faction name";

        double hours = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 48;
        string? town = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
        var args = new BehaviourArgs().Set("template", parts[0]).Set("hours", hours);
        d.Enqueue(new StartBehaviourCommand(BuiltIn.Visit, town, parts[1], args) { Tick = d.Tick + 1 });
        return $"visit of {parts[0]} to {town ?? "the first town"} queued";
    }

    /// <summary>
    /// Queues a grudge of a faction against the selected characters; an unknown faction is refused here.
    /// </summary>
    string QueueGrudge(DebugCommandContext d)
    {
        var parts = d.Field.Split(',', StringSplitOptions.TrimEntries);
        if (_latest is not { } system) return NoWorld;
        if (parts[0].Length == 0 || d.Selected.Count == 0) return "select characters and type a faction name";
        if (system.FactionByName(parts[0]) < 0) return $"no faction '{parts[0]}': type its name as the game shows it, or its record id";

        float amount = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ? a : 100;
        d.Enqueue(new GrudgeCommand(d.Selected, parts[0], amount) { Tick = d.Tick + 1 });
        return $"{parts[0]}: {amount:+0.#;-0.#} grudge on {d.Selected.Count} character(s): unpause, then see 'Rivalry: character'";
    }

    /// <summary>One status line from the latest report (read on the host's thread: the report and history are immutable snapshots).</summary>
    static string Describe(RivalrySystem? system)
    {
        if (system is null) return NoWorld;

        var r = system.Report;
        var towns = string.Join("; ", r.Towns.Select(t => $"{t.Name}: {t.Buildings} bldg worth {t.BuildingValue}, {t.StoredValue} stored, defence {t.Defence:0} vs threat {t.Threat:0}"));
        string last = system.History.Count > 0 ? system.History[^1] : "no decisions yet";
        string wealth = $"wealth {r.Wealth} (money {r.Money}, carried {r.Carried}, stored {r.Stored}, built {r.Built})";
        return $"{r.Characters.Count} chars, {wealth}, {system.Notoriety.Count} grudges | {towns} | {last}";
    }
}

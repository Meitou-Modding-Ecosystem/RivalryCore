using Meitou.Modding;

namespace Rivalry;

/// <summary>
/// Rivalry Core's tuning (docs/rivalry-core.md "Options"). Properties marked <see cref="SettingAttribute"/> are on the Mod settings page; the rest are for
/// dependents (<see cref="IRivalryApi.Options"/>), set in their <c>Initialize</c>. The simulation reads a copy taken in a serial step.
/// </summary>
public sealed class RivalryOptions
{
    [Setting("Inspect every (game hours)", Hint = "How often the world looks at your setup and its directors decide.", Min = 1, Max = 24)]
    public int AssessEveryHours { get; set; } = 2;

    [Setting("Allies reinforce raids", Hint = "An allied faction sends help when hostiles in one of your towns outmatch its defenders.")]
    public bool Reinforcements { get; set; } = true;

    [Setting("Grudges send hunters", Hint = "A faction that holds a large grudge against one of your characters sends squads after them.")]
    public bool Hunters { get; set; } = true;

    [Setting("Any faction sends hit squads", Hint = "A faction with a large grudge sends one of its own squads from its base after the character.")]
    public bool HitSquads { get; set; } = true;

    [Setting("Opportunist raids", Hint = "Hostile factions raid towns that look rich and poorly defended.")]
    public bool OpportunistRaids { get; set; } = true;

    [Setting("Hunters speak", Hint = "A hunter who fights someone on the way to its quarry says so over its head.")]
    public bool HunterBarks { get; set; } = true;

    /// <summary>
    /// How much every grudge cools per game hour; a grudge that sent a hit squad cools no lower than <see cref="HuntThreshold"/> until that squad is beaten
    /// where the player can see it.
    /// </summary>
    [Setting("Grudge cools per game hour", Hint = "A grudge that sent a hit squad stays at the hunt threshold until you beat that squad in view.",
        Min = 0, Max = 10, Step = 0.25)]
    public double GrudgeCoolPerHour { get; set; } = 1;

    [Setting("Grudge that sends hunters", Min = 10, Max = 500, Step = 10)]
    public float HuntThreshold { get; set; } = 60;

    [Setting("Reinforce below balance", Hint = "Defence over threat in a town under attack at which allies come (1 = even).", Min = 0.1, Max = 2, Step = 0.05)]
    public float ReinforceBelowBalance { get; set; } = 0.75f;

    [Setting("Friendly parties at once", Hint = "Allied or visiting parties that may come to you at the same time. Hostile ones come one at a time.",
        Min = Parties.FriendlyMin, Max = Parties.FriendlyMax, Step = 1)]
    public int FriendlyPartiesAtOnce { get; set; } = 3;

    [Setting("Rest between hunts, at least (game hours)", Hint = "After a hostile party ends, none comes for at least this long.",
        Min = 0, Max = Parties.RestMax, Step = 1)]
    public int HuntRestMinHours { get; set; } = 12;

    [Setting("Rest between hunts, at most (game hours)", Hint = "The rest is a random whole number of hours from the floor to this.",
        Min = 0, Max = Parties.RestMax, Step = 1)]
    public int HuntRestMaxHours { get; set; } = 12;

    [Setting("Hunt points lost per travel hour", Hint = "Who goes next: a hunter's grudge counts this much less for each hour it must travel to you.",
        Min = 0, Max = 10, Step = 0.5)]
    public float HuntTravelWeight { get; set; } = 2;

    [Setting("Hunt points per hour waited", Hint = "Who goes next: a hunter held back gains this much for each hour it has waited its turn.",
        Min = 0, Max = 10, Step = 0.5)]
    public float HuntWaitWeight { get; set; } = 1;

    [Setting("Opportunist raid chance per day", Min = 0, Max = 2, Step = 0.05)]
    public float RaidChancePerDay { get; set; } = 0.25f;

    [Setting("Announce decisions", Hint = "A message line for each decision (for testing and for players who like to know).")]
    public bool Announce { get; set; }

    /// <summary>Grudge per landed hit by a player character on a member of another faction.</summary>
    public float HitGrudge { get; set; } = 1;

    /// <summary>Extra grudge for a hit on a victim already down.</summary>
    public float DownedHitGrudge { get; set; } = 3;

    /// <summary>Grudge when a victim a player character hit dies within <see cref="KillWindowHours"/>.</summary>
    public float KillGrudge { get; set; } = 25;

    public double KillWindowHours { get; set; } = 1;

    /// <summary>AI_PACKAGE a hunting squad runs as a contract aimed at its target; null: hunters only mark the target as an enemy (docs/rivalry-core.md "Actions").</summary>
    public string? HuntPackage { get; set; }

    /// <summary>AI_PACKAGE a nearby reinforcing squad runs; null: the first package with signal 15 (Contract_DefendPlayerBase) in the data, by id.</summary>
    public string? DefendPackage { get; set; }

    /// <summary>
    /// The most game hours a hit squad spends on the road from its faction's nearest base before it appears (<see cref="HuntTravelUnitsPerHour"/>).
    /// </summary>
    [Setting("Hit squad travel at most (game hours)", Hint = "A squad sent from a far base takes longer to arrive, up to this.", Min = 0, Max = 168, Step = 1)]
    public double HuntTravelMaxHours { get; set; } = 48;

    /// <summary>
    /// How far a hit squad travels per game hour on its way from its base: the original's off-screen travel speed (25 a second) over its clock (1200/11
    /// seconds per game hour) (engine docs/game/factions-squads-towns.md, game-loop.md).
    /// </summary>
    public double HuntTravelUnitsPerHour { get; set; } = 25 * 1200.0 / 11;

    /// <summary>
    /// The least travel time of a hit squad, and the travel time of one whose faction has no base found (<see cref="FactionBases"/>).
    /// </summary>
    public double HuntTravelMinHours { get; set; } = 1;
    public double HuntTravelFallbackHours { get; set; } = 12;

    /// <summary>
    /// The ring round its quarry a hit squad appears in: beyond sight (1500 × a race's vision multiplier, engine docs/game/perception.md) and inside the
    /// loaded 3 × 3 zones of 4608 round the camera, so it loads and walks in from off screen.
    /// </summary>
    public float HuntSpawnMinRadius { get; set; } = 2200;
    public float HuntSpawnMaxRadius { get; set; } = 3400;

    /// <summary>
    /// How near a player character must stand to a beaten hit squad to have seen it beaten: the original's outdoor sight range (engine
    /// docs/game/perception.md "Sight").
    /// </summary>
    public float SeenRange { get; set; } = 1500;

    /// <summary>
    /// Game hours a beaten squad's leader is watched for its fate (killed, captured, turned in, left for dead); a captive is watched for as long as
    /// it is held.
    /// </summary>
    public double AftermathHours { get; set; } = 12;

    /// <summary>
    /// Town or region (BIOME_GROUP) names a faction's squads set out from, by the faction's string id or name, entries separated by <c>|</c>: for a
    /// faction whose own towns and squad lists say nothing (one that owns no town), or to override them (docs/factions.md "Bases"). Set by
    /// dependents in their <c>Initialize</c>.
    /// </summary>
    public Dictionary<string, string> Homes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Game hours a faction waits after its last hit squad finished before the built-in rule sends another.
    /// </summary>
    public double HitSquadRestHours { get; set; } = 72;

    /// <summary>
    /// Factions (string ids) whose mod sends their hit squads with a rule of its own: the built-in rule leaves them alone. Set by dependents in
    /// their <c>Initialize</c>.
    /// </summary>
    public HashSet<string> OwnHitSquads { get; } = new(StringComparer.Ordinal);

    public int HuntSquads { get; set; } = 1;
    public float HuntRadius { get; set; } = 20000;
    public float HuntHours { get; set; } = 24;
    public double HuntCooldownHours { get; set; } = 48;

    /// <summary>
    /// How many of the largest grudges the grudge director (<see cref="GrudgeDirector"/>) proposes as hostile candidates at each assessment.
    /// It does not decide how many go: the hostile party cap and who goes next do (docs/rivalry-core.md "Who goes next").
    /// </summary>
    public int GrudgeCandidatesPerAssessment { get; set; } = 8;

    /// <summary>
    /// The old name of <see cref="GrudgeCandidatesPerAssessment"/>, as a quarter of it (it used to cap the hunts sent per assessment).
    /// </summary>
    [Obsolete("Use GrudgeCandidatesPerAssessment: this no longer limits the hunts sent, only the grudges proposed (4 per unit).")]
    public int MaxHuntsPerAssessment
    {
        get => GrudgeCandidatesPerAssessment / 4;
        set => GrudgeCandidatesPerAssessment = value * 4;
    }

    /// <summary>
    /// What a hunter says when it starts a fight with anyone but its quarry; <c>{target}</c> is the quarry's name.
    /// </summary>
    public string OffTargetLine { get; set; } = "I'll tear through you like paper if it gets me closer to putting down {target} like a dog!";

    /// <summary>
    /// Game seconds a hunting squad stays quiet after one of its members speaks.
    /// </summary>
    public float BarkCooldownSeconds { get; set; } = 30;

    /// <summary>How far from a town under attack loaded squads of the reinforcing faction are sent in at once.</summary>
    public float ReinforceRadius { get; set; } = 8000;
    public float ReinforceHours { get; set; } = 12;
    public double ReinforceCooldownHours { get; set; } = 12;

    /// <summary>A town's stored and carried value at which it starts to look worth raiding, and the value per point of defence that makes it a target.</summary>
    public long RaidWealthMin { get; set; } = 20000;
    public float RaidValuePerDefence { get; set; } = 400;
    public double RaidCooldownHours { get; set; } = 96;

    /// <summary>Whether hunts and reinforcements may take a town's own squads (guards, residents), not only roaming ones.</summary>
    public bool UseTownSquads { get; set; }

    public RivalryOptions Copy() => (RivalryOptions)MemberwiseClone();
}

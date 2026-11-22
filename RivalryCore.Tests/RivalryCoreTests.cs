using System.Numerics;
using System.Text;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Rivalry.Tests;

/// <summary>
/// Rivalry Core on synthetic worlds: the built-in rules and director, a dependent's pieces, the save and the hash.
/// </summary>
public class RivalryCoreTests
{
    /// <summary>
    /// Where the player's town stands.
    /// </summary>
    static readonly Vector3 TownAt = new(2000, 150, 2000);

    /// <summary>
    /// A new record, as a mod file adds one.
    /// </summary>
    static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };

    /// <summary>
    /// The player's faction, an ally (Friends) and an enemy (Foes).
    /// </summary>
    static PopulationData Data()
    {
        var file = new FcsFile();
        file.Records.AddRange([
            Rec("204-gamedata.base", FcsRecordType.Faction, "Nameless"),
            Rec("60-a", FcsRecordType.Faction, "Friends"),
            Rec("61-a", FcsRecordType.Faction, "Foes")]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var data = PopulationData.Create(db, []);
        int p = data.FactionIndex("204-gamedata.base"), f = data.FactionIndex("60-a"), e = data.FactionIndex("61-a");
        data.Relations.Add(f, p, 90 - data.Relations.Get(f, p));
        data.Relations.Add(e, p, -90 - data.Relations.Get(e, p));
        return data;
    }

    /// <summary>
    /// A world with one player town and Rivalry Core as its only system.
    /// </summary>
    static (SimWorld World, RivalrySystem System, RivalryOptions Options) Make(PopulationData data, int threads = 1,
        Action<IRivalryRegistry>? register = null)
    {
        var options = new RivalryOptions();
        var system = RivalrySystem.Create(new SystemBuildContext(data, []), options, (RngPurpose)0x80001234u, _ => { }, "test", register);
        var world = new SimWorld(new WorldSettings { Seed = 3, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 },
            new OpenGroundWalkability((_, _) => 150), [system]);
        world.Player.Faction = data.FactionIndex("204-gamedata.base");
        world.Buildings.AddTown(TownAt, world.Player.Faction, "Home");
        return (world, system, options);
    }

    static CharacterId Spawn(SimWorld world, string name, int faction, Vector3 at, int squad = -1)
    {
        var hot = new CharacterHot { Alive = true, Position = at };
        var cold = new CharacterCold
        {
            Name = name,
            Faction = faction,
            SquadId = squad,
            IsPlayer = faction == world.Player.Faction,
            RecordId = "r-" + name,
        };
        return world.Characters.Spawn(hot, cold, world.Tick);
    }

    static void AddSquad(SimWorld world, int faction, CharacterId leader)
    {
        var s = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction };
        s.Members.Add(leader);
        s.Leader = leader;
        world.Squads.Add(s);
    }

    static MemoryStream Saved(RivalrySystem system)
    {
        var stream = new MemoryStream();
        system.Write(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// A behaviour that begins a run lasting <c>hours</c> and counts its steps.
    /// </summary>
    sealed class Waiting : IBehaviour
    {
        public int Steps;

        public string Name => "test.wait";

        public string Start(BehaviourContext context, BehaviourRequest request)
        {
            var run = context.Begin(this, request, context.World.GameHours + request.Args.Number("hours"));
            return $"waiting as run {run.Id}";
        }

        public bool Step(BehaviourContext context, BehaviourRun run)
        {
            Steps++;
            return context.World.GameHours < run.Until;
        }
    }

    [Fact]
    public void An_outmatched_town_calls_its_ally_and_the_allys_nearby_squad()
    {
        var data = Data();
        var (world, system, _) = Make(data);
        int foes = data.FactionIndex("61-a"), friends = data.FactionIndex("60-a");
        Spawn(world, "Me", world.Player.Faction, TownAt);
        for (int i = 0; i < 3; i++) Spawn(world, "Foe" + i, foes, TownAt + new Vector3(50 * i, 0, 0));
        AddSquad(world, friends, Spawn(world, "Ally", friends, TownAt + new Vector3(3000, 0, 0)));

        world.RunTicks(2);

        var town = Assert.Single(system.Report.Towns);
        Assert.True(town.UnderAttack);
        Assert.True(town.Balance < 1);
        Assert.Contains(system.History, l => l.StartsWith("rivalry.reinforce: reinforce: Friends to town 1") && l.Contains("1 squad(s) nearby"));
    }

    [Fact]
    public void A_grudge_sends_hunters_and_comes_back_from_a_save()
    {
        var data = Data();
        var (world, system, _) = Make(data);
        int foes = data.FactionIndex("61-a");
        var me = Spawn(world, "Me", world.Player.Faction, TownAt + new Vector3(9000, 0, 0));
        AddSquad(world, foes, Spawn(world, "Foe", foes, TownAt + new Vector3(12000, 0, 0)));
        world.Commands.Enqueue(new GrudgeCommand([me], "Foes", 100) { Tick = 0 });

        world.RunTicks(2);

        Assert.Contains(system.History, l => l.Contains("hunt: 1 Foes squad(s) already near Me now hold them as an enemy"));

        var (world2, system2, _) = Make(data);
        Spawn(world2, "Other", world2.Player.Faction, TownAt);
        var me2 = Spawn(world2, "Me", world2.Player.Faction, TownAt);
        system2.Read(Saved(system));
        world2.RunTicks(1);

        Assert.True(system2.Notoriety.Grudge(foes, me2) > 99);
        Assert.Empty(system2.History);   // the hunt's cooldown came back too
    }

    [Fact]
    public void A_dependents_rule_fires_on_its_probe_once_per_cooldown()
    {
        var data = Data();
        var (world, system, _) = Make(data, register: r =>
        {
            r.AddTownProbe("test.rating", (_, town) => town.Defenders.Count * 2);
            r.AddCondition("test.rated", (a, t) => a.Report.TownOf(t.Town)?.Value("test.rating") >= 2);
            r.AddBehaviour(new Waiting());
            r.AddRule(new Rule
            {
                Name = "test.guests",
                Conditions = ["test.rated"],
                Behaviour = "test.wait",
                CooldownHours = _ => 1000,
                Args = (_, _) => new BehaviourArgs().Set("hours", 500),
            });
        });

        world.RunTicks(2);
        Assert.DoesNotContain(system.History, l => l.StartsWith("test.guests"));   // no defender: the rating is 0

        Spawn(world, "Me", world.Player.Faction, TownAt);
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);

        Assert.Single(system.History, l => l.StartsWith("test.guests: waiting as run 1"));
        Assert.Single(system.Runs);
    }

    [Fact]
    public void A_run_under_way_comes_back_from_a_save_and_goes_on()
    {
        var data = Data();
        var first = new Waiting();
        var (world, system, _) = Make(data, register: r => r.AddBehaviour(first));
        world.Commands.Enqueue(new StartBehaviourCommand("test.wait", null, "Friends", new BehaviourArgs().Set("hours", 500)) { Tick = 0 });
        world.RunTicks(2);
        Assert.Single(system.Runs);

        var second = new Waiting();
        var (world2, system2, _) = Make(data, register: r => r.AddBehaviour(second));
        system2.Read(Saved(system));
        world2.RunTicks(2);

        Assert.Contains("run 1 test.wait town 1", Assert.Single(system2.Runs));
        Assert.True(second.Steps > 0);
    }

    [Fact]
    public void A_version_1_save_still_loads()
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(1);
            w.Write(5.0);
            w.Write(4.0);
            w.Write(0);                          // no grudges
            w.Write(1);                          // one cooldown
            w.Write("rivalry.reinforce/town1");
            w.Write(900.0);
        }

        stream.Position = 0;
        var data = Data();
        var (world, system, _) = Make(data);
        int foes = data.FactionIndex("61-a");
        Spawn(world, "Me", world.Player.Faction, TownAt);
        for (int i = 0; i < 3; i++) Spawn(world, "Foe" + i, foes, TownAt + new Vector3(50 * i, 0, 0));
        AddSquad(world, data.FactionIndex("60-a"), Spawn(world, "Ally", data.FactionIndex("60-a"), TownAt + new Vector3(3000, 0, 0)));
        system.Read(stream);
        world.Commands.Enqueue(new AssessNowCommand { Tick = 0 });

        world.RunTicks(2);

        Assert.True(Assert.Single(system.Report.Towns).UnderAttack);
        Assert.DoesNotContain(system.History, l => l.StartsWith("rivalry.reinforce"));   // the saved cooldown holds the rule back
    }

    [Fact]
    public void Names_carry_the_mods_id_and_a_failed_mods_pieces_are_dropped()
    {
        var registry = new Registry();
        var mine = registry.For("inn", "inn.");
        Assert.Throws<ArgumentException>(() => mine.AddCondition("other.open", (_, _) => true));

        mine.AddCondition("inn.open", (_, _) => true);
        Assert.Throws<ArgumentException>(() => mine.AddCondition("inn.open", (_, _) => true));
        registry.For("bar", "bar.").AddCondition("bar.open", (_, _) => true);

        registry.Drop("inn");

        Assert.Equal("bar.open", Assert.Single(registry.Conditions).Name);
    }

    [Fact]
    public void The_world_hash_is_the_same_on_one_thread_and_four()
    {
        static ulong Run(int threads)
        {
            var data = Data();
            var (world, _, _) = Make(data, threads, r => r.AddBehaviour(new Waiting()));
            int foes = data.FactionIndex("61-a");
            var me = Spawn(world, "Me", world.Player.Faction, TownAt);
            for (int i = 0; i < 70; i++) Spawn(world, "Foe" + i, foes, TownAt + new Vector3(10 * i, 0, 0));
            world.Commands.Enqueue(new GrudgeCommand([me], "Foes", 100) { Tick = 0 });
            world.Commands.Enqueue(new StartBehaviourCommand("test.wait", null, "Foes", new BehaviourArgs().Set("hours", 500)) { Tick = 1 });
            world.RunTicks(5);
            return world.StateHash();
        }

        Assert.Equal(Run(1), Run(4));
    }

    [Fact]
    public void A_faction_buys_captives_only_of_the_races_its_squads_keep_as_slaves()
    {
        var keeper = Rec("70-a", FcsRecordType.Faction, "Keepers");
        keeper.References["squads"] = [new FcsReference("71-a", 1, 0, 0)];
        var squad = Rec("71-a", FcsRecordType.SquadTemplate, "Keepers with slaves");
        squad.References["slaves"] = [new FcsReference("72-a", 2, 0, 0)];
        var slave = Rec("72-a", FcsRecordType.Character, "Slave");
        slave.References["race"] = [new FcsReference("73-a", 100, 0, 0)];

        var file = new FcsFile();
        file.Records.AddRange([keeper, squad, slave, Rec("60-a", FcsRecordType.Faction, "Friends")]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var data = PopulationData.Create(db, []);
        var market = new SlaveMarket(data);

        Assert.True(market.Buys(data.FactionIndex("70-a"), "73-a"));
        Assert.False(market.Buys(data.FactionIndex("70-a"), "74-a"));    // a race it keeps no slaves of
        Assert.False(market.Buys(data.FactionIndex("60-a"), "73-a"));    // a faction without slaves
        Assert.Equal(-1, market.NearestBuyer(TownAt, "73-a", data.FactionIndex("60-a")));   // no town to sell in
    }

    [Fact]
    public void The_debug_rows_tell_what_is_held_against_a_character_and_refuse_a_rule_aimed_at_the_wrong_thing()
    {
        var data = Data();
        var (world, system, options) = Make(data);
        int foes = data.FactionIndex("61-a");
        var me = Spawn(world, "Zimm", world.Player.Faction, TownAt);
        Spawn(world, "Beep", world.Player.Faction, TownAt);
        world.RunTicks(2);

        Assert.Equal("no grudges and no hit squads against anyone", system.Debug.DescribeCharacters("", []));
        Assert.Contains("is not one of yours (Home = town1)", system.Debug.CheckRule("rivalry.opportunist", "Zimm"));
        Assert.Null(system.Debug.CheckRule("rivalry.opportunist", "home"));
        Assert.StartsWith("no rule 'rivalry.grudge'", system.Debug.CheckRule("rivalry.grudge", null));

        world.Commands.Enqueue(new GrudgeCommand([me], "Foes", options.HuntThreshold + 40) { Tick = world.Tick });
        world.RunTicks(2);

        string selected = system.Debug.DescribeCharacters("", [me]);
        Assert.StartsWith("Zimm: notoriety 100, hunts start at 60 | grudges: Foes 100", selected);
        Assert.Contains("no hit squad after them", selected);
        Assert.DoesNotContain("Beep", selected);
        Assert.Equal(selected, system.Debug.DescribeCharacters("zimm", []));
        Assert.StartsWith("no player character named 'Nobody'", system.Debug.DescribeCharacters("Nobody", []));
        Assert.StartsWith("Beep: notoriety 0", system.Debug.DescribeCharacters("Beep", []));
    }

    [Fact]
    public void Any_faction_with_a_squad_of_its_own_sends_a_hit_squad_for_its_grudge()
    {
        var raiders = Rec("80-a", FcsRecordType.Faction, "Raiders");
        raiders.References["squads"] = [new FcsReference("81-a", 10, 0, 0), new FcsReference("82-a", 50, 0, 0), new FcsReference("83-a", 30, 0, 0)];
        var caravan = Rec("82-a", FcsRecordType.SquadTemplate, "Raider caravan");
        caravan.Bools["is trader"] = true;
        var file = new FcsFile();
        file.Records.AddRange([
            Rec("204-gamedata.base", FcsRecordType.Faction, "Nameless"), Rec("61-a", FcsRecordType.Faction, "Foes"), raiders,
            Rec("81-a", FcsRecordType.SquadTemplate, "Raider scouts"), caravan, Rec("83-a", FcsRecordType.SquadTemplate, "Raider warband")]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var data = PopulationData.Create(db, []);
        var (world, system, _) = Make(data);
        int withSquads = data.FactionIndex("80-a"), without = data.FactionIndex("61-a");
        var me = Spawn(world, "Zimm", world.Player.Faction, TownAt);
        world.RunTicks(2);

        Assert.Equal("83-a", HitSquads.TemplateFor(data, withSquads));   // the heaviest that is not a trader
        Assert.Null(HitSquads.TemplateFor(data, without));

        system.Notoriety.Add(without, me, 100);
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        Assert.DoesNotContain(system.History, l => l.StartsWith(BuiltIn.HitSquadRule));   // a grudge, but no squad of its own to send

        system.Notoriety.Add(withSquads, me, 100);
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Contains(system.History, l => l.StartsWith(BuiltIn.HitSquadRule + ": pursue:"));
    }

    [Fact]
    public void A_grudge_with_no_squad_near_logs_nothing_and_hunts_once_one_comes()
    {
        var data = Data();
        var (world, system, _) = Make(data);
        int foes = data.FactionIndex("61-a");
        var me = Spawn(world, "Me", world.Player.Faction, TownAt);
        world.RunTicks(2);

        system.Notoriety.Add(foes, me, 100);
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        Assert.DoesNotContain(system.History, l => l.Contains("hunt:"));

        AddSquad(world, foes, Spawn(world, "Foe", foes, TownAt + new Vector3(500, 0, 0)));
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Contains(system.History, l => l.Contains("hunt: 1 Foes squad(s) already near Me"));   // the miss claimed no cooldown
    }

    #region Party caps

    /// <summary>
    /// A behaviour of a party kind whose run lasts 500 game hours.
    /// </summary>
    sealed class Sending(string name, PartyKind kind) : IBehaviour
    {
        public string Name => name;

        public PartyKind Party => kind;

        public string Start(BehaviourContext context, BehaviourRequest request)
        {
            var run = context.Begin(this, request, context.World.GameHours + 500);
            return $"sent as run {run.Id}";
        }

        public bool Step(BehaviourContext context, BehaviourRun run) => context.World.GameHours < run.Until;
    }

    /// <summary>
    /// A rule on the player's towns that starts <paramref name="behaviour"/> with Foes acting, once per 1000 h, while <paramref name="when"/>
    /// holds (null: always).
    /// </summary>
    static Rule Sends(string name, string behaviour, Func<bool>? when = null) => new()
    {
        Name = name,
        Behaviour = behaviour,
        When = (_, _) => when?.Invoke() != false,
        CooldownHours = _ => 1000,
        Faction = (a, _) => a.Data.FactionIndex("61-a"),
    };

    /// <summary>
    /// A world with a standing player character and Rivalry Core's own rules off, so only the dependent's rules send parties.
    /// </summary>
    static (SimWorld World, RivalrySystem System) MakeSending(Action<IRivalryRegistry> register, Action<RivalryOptions>? tune = null)
    {
        var (world, system, options) = Make(Data(), register: register);
        options.Reinforcements = options.OpportunistRaids = options.Hunters = false;
        tune?.Invoke(options);
        Spawn(world, "Me", world.Player.Faction, TownAt);
        return (world, system);
    }

    [Fact]
    public void Two_mods_hostile_rules_send_one_party_and_the_held_rule_keeps_its_cooldown()
    {
        var (world, system) = MakeSending(r =>
        {
            r.AddBehaviour(new Sending("test.raiders", PartyKind.Hostile));
            r.AddRule(Sends("test.first", "test.raiders"));
            r.AddRule(Sends("test.second", "test.raiders"));
        });

        world.RunTicks(2);

        Assert.Equal("test.first", Assert.Single(system.Parties).Source);
        Assert.DoesNotContain(system.Debug.Cooldowns, c => c.Key.StartsWith("test.second", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_an_override_sends_a_second_hostile_party()
    {
        var (world, system) = MakeSending(r =>
        {
            r.AddBehaviour(new Sending("test.raiders", PartyKind.Hostile));
            r.AddRule(Sends("test.first", "test.raiders"));
            r.AddRule(Sends("test.second", "test.raiders"));
            r.ExceedHostileCap("test.second", 1, "a siege brings its own scouts");
        });

        world.RunTicks(2);

        Assert.Equal(2, system.PartiesOut(PartyKind.Hostile));
        Assert.Contains("test.second +1", system.Debug.DescribeParties());
    }

    [Fact]
    public void Friendly_parties_stop_at_the_setting()
    {
        var (world, system) = MakeSending(r =>
        {
            r.AddBehaviour(new Sending("test.guests", PartyKind.Friendly));
            for (int i = 0; i < 5; i++) r.AddRule(Sends($"test.guests{i}", "test.guests"));
        });

        world.RunTicks(2);

        Assert.Equal(3, system.PartiesOut(PartyKind.Friendly));   // the default of "Friendly parties at once"
        Assert.Equal(0, system.PartiesOut(PartyKind.Hostile));
    }

    [Fact]
    public void Another_mods_claim_takes_the_hostile_slot_until_released()
    {
        bool tempted = false;
        var (world, system) = MakeSending(r =>
        {
            r.AddBehaviour(new Sending("test.raiders", PartyKind.Hostile));
            r.AddRule(Sends("test.raid", "test.raiders", () => tempted));
        }, o => o.HuntRestMinHours = o.HuntRestMaxHours = 0);
        world.RunTicks(1);
        var claim = new PartyClaim("twr.bounty", PartyKind.Hostile, "Me", 100);
        Assert.True(system.ClaimParty(claim));
        Assert.False(system.ClaimParty(claim with { Source = "twr.reprisal" }));   // the cap is full

        tempted = true;
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Equal("twr.bounty", Assert.Single(system.Parties).Source);

        Assert.True(system.ReleaseParty("twr.bounty", "Me"));
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Equal("test.raid", Assert.Single(system.Parties).Source);
    }

    [Fact]
    public void A_claim_comes_back_from_a_save_and_still_holds_the_slot()
    {
        var (world, system) = MakeSending(_ => { });
        world.RunTicks(1);
        Assert.True(system.ClaimParty(new PartyClaim("twr.bounty", PartyKind.Hostile, "Me", 100)));

        var (world2, system2) = MakeSending(_ => { });
        system2.Read(Saved(system));
        world2.RunTicks(1);

        Assert.Equal(1, system2.PartiesOut(PartyKind.Hostile));
        Assert.False(system2.ClaimParty(new PartyClaim("twr.reprisal", PartyKind.Hostile, "Me", 100)));
    }

    [Fact]
    public void After_a_hostile_party_ends_none_starts_until_the_rest_is_over()
    {
        var (world, system) = MakeSending(_ => { });
        world.RunTicks(1);
        Assert.True(system.ClaimParty(new PartyClaim("twr.bounty", PartyKind.Hostile, "Me", 100)));
        world.RunTicks(1);

        Assert.True(system.ReleaseParty("twr.bounty", "Me"));
        world.RunTicks(1);

        Assert.Equal(world.GameHours + 12, system.HostileRestUntil, 0.01);   // the default floor and ceiling are both 12
        Assert.False(system.ClaimParty(new PartyClaim("twr.reprisal", PartyKind.Hostile, "Me", 100)));
        Assert.True(system.ClaimParty(new PartyClaim("test.guests", PartyKind.Friendly, "town1", 100)));   // friendly parties don't rest
    }

    [Theory]
    [InlineData(6, 18, 0f, 6)]
    [InlineData(6, 18, 0.999f, 18)]
    [InlineData(6, 18, 0.5f, 12)]
    [InlineData(12, 4, 0.7f, 12)]
    public void The_rest_is_whole_hours_between_the_floor_and_the_ceiling(int floor, int ceiling, float roll, int hours)
    {
        var options = new RivalryOptions { HuntRestMinHours = floor, HuntRestMaxHours = ceiling };

        Assert.Equal(hours, Parties.RestHours(roll, options));
    }

    #endregion

    #region Who goes next

    /// <summary>
    /// A rule like <see cref="Sends"/> whose hostile start scores <paramref name="grudge"/>.
    /// </summary>
    static Rule Scores(string name, string behaviour, float grudge, Func<bool>? when = null) => new()
    {
        Name = name,
        Behaviour = behaviour,
        When = (_, _) => when?.Invoke() != false,
        CooldownHours = _ => 1000,
        Faction = (a, _) => a.Data.FactionIndex("61-a"),
        Grudge = (_, _) => grudge,
    };

    /// <summary>
    /// Ranks the candidates at <paramref name="now"/> and sends the best, as an assessment does.
    /// </summary>
    static HostileCandidate SendBest(HuntQueue queue, double now, params HostileCandidate[] candidates)
    {
        var best = queue.Rank(candidates, c => c, now, new RivalryOptions())[0];
        queue.Sent(best);
        return best;
    }

    [Fact]
    public void A_near_grudge_goes_before_a_larger_far_one_until_the_far_one_has_waited()
    {
        var queue = new HuntQueue();
        var far = new HostileCandidate("test.far", "Me", 2, 80, 20);    // 80 - 2 x 20 = 40
        var near = new HostileCandidate("test.near", "Me", 1, 60, 2);   // 60 - 2 x 2 = 56

        Assert.Equal(near, SendBest(queue, 0, far, near));

        // Thirty hours later the far grudge scores 40 + 30 = 70 against a fresh near one's 56.
        Assert.Equal(far, SendBest(queue, 30, far, near with { Key = "You" }));
    }

    [Fact]
    public void A_waits_age_survives_a_save_and_is_forgotten_after_a_day_unproposed()
    {
        var queue = new HuntQueue();
        var waiting = new HostileCandidate("test.raid", "town1", 1, 10, 0);
        queue.Rank([waiting], c => c, 5, new RivalryOptions());

        var stream = new MemoryStream();
        queue.Write(new BinaryWriter(stream));
        stream.Position = 0;
        var loaded = new HuntQueue();
        loaded.Read(new BinaryReader(stream));
        Assert.Equal(5, loaded.Since(waiting, 20));

        loaded.Rank<HostileCandidate>([], c => c, 5 + HuntQueue.ForgetHours + 1, new RivalryOptions());
        Assert.Equal(40, loaded.Since(waiting, 40));
    }

    [Fact]
    public void The_larger_grudge_goes_first_whatever_the_rule_order_and_the_other_waits()
    {
        var (world, system) = MakeSending(r =>
        {
            r.AddBehaviour(new Sending("test.raiders", PartyKind.Hostile));
            r.AddRule(Scores("test.small", "test.raiders", 10));
            r.AddRule(Scores("test.big", "test.raiders", 50));
        });

        world.RunTicks(2);

        Assert.Equal("test.big", Assert.Single(system.Parties).Source);
        Assert.Equal("test.small", Assert.Single(system.Waiting).Source);
        Assert.Contains("waiting: test.small -> town1", system.Debug.DescribeParties());
    }

    [Fact]
    public void Another_mods_candidate_that_wins_is_given_a_claim()
    {
        bool tempted = false;
        var (world, system) = MakeSending(r =>
        {
            r.AddBehaviour(new Sending("test.raiders", PartyKind.Hostile));
            r.AddRule(Scores("test.raid", "test.raiders", 10, () => tempted));
        });
        world.RunTicks(1);
        tempted = true;
        var bounty = new HostileCandidate("twr.bounty", "Me", -1, 200, system.TravelHours(-1, CharacterId.None), ClaimHours: 50);

        Assert.False(system.Propose(bounty));   // waits for the next assessment
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);

        Assert.True(system.Propose(bounty));
        Assert.Equal("twr.bounty", Assert.Single(system.Parties).Source);
        Assert.Equal("test.raid", Assert.Single(system.Waiting).Source);
    }

    #endregion

    #region Squad driving

    [Fact]
    public void A_redirected_walk_keeps_its_path_until_the_goal_moves_past_the_slack()
    {
        var (world, _, _) = Make(Data());
        var hunter = Spawn(world, "Hunter", world.Player.Faction + 1, TownAt);
        world.RunTicks(1);
        int slot = hunter.Slot;
        var goal = TownAt + new Vector3(500, 0, 0);

        Assert.False(SquadDriver.WalkTo(world, slot, goal, run: true, redirect: true));
        Assert.Equal((byte)CharacterTask.GoTo, world.Characters.Next[slot].Task);

        // This world has no movement system: stand in for the walk it would have set up, and mark it to see whether a new one starts.
        world.Characters.Next[slot].Goal = new Vector2(goal.X, goal.Z);
        world.Characters.Next[slot].TaskTime = 99;

        SquadDriver.WalkTo(world, slot, goal + new Vector3(SquadDriver.RedirectSlack / 2, 0, 0), run: true, redirect: true);
        Assert.Equal(99, world.Characters.Next[slot].TaskTime);   // moved within the slack: the same walk

        SquadDriver.WalkTo(world, slot, goal + new Vector3(SquadDriver.RedirectSlack * 3, 0, 0), run: true, redirect: true);
        Assert.Equal(0, world.Characters.Next[slot].TaskTime);    // moved past it: a new walk
    }

    #endregion
}

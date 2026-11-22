using System.Globalization;
using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Items;

namespace Rivalry;

/// <summary>
/// Evidence a rival carries (docs/rivalry-core.md "Evidence"): a note planted on a squad's leader when its run loads, tagged with the leader and the
/// bounty it is worth. A player character who right-clicks it in their own inventory hands it to the law: the leader gets that bounty with every
/// faction that books bounties, and the note is used up. Run arguments: text <c>evidence</c> (the note's ITEM record; none: nothing planted),
/// numbers <c>evidenceFactor</c> (2) times <c>goal</c>, at least <c>evidenceMin</c> (3000). Serial steps only.
/// </summary>
public static class Evidence
{
    /// <summary>
    /// The start of a note's <see cref="ItemInstance.ModTag"/>; the tag reads <c>rivalry.evidence|serial|bounty|crime</c>.
    /// </summary>
    public const string TagPrefix = "rivalry.evidence";

    /// <summary>
    /// The default least bounty, and how many times the run's goal the bounty is.
    /// </summary>
    public const double MinBounty = 3000, GoalFactor = 2;

    #region Planting

    /// <summary>
    /// Puts the run's note into its squad leader's inventory (the first member alive), tagged with the leader's portrait serial, the bounty and
    /// <paramref name="crime"/> (the crime the bounty lists). Nothing without an <c>evidence</c> argument, a record the game knows, or room.
    /// </summary>
    public static void Plant(BehaviourContext context, BehaviourRun run, Squad squad, CrimeCode crime)
    {
        if (run.Args.Text("evidence") is not { Length: > 0 } record) return;
        if (SquadDriver.FirstLiving(context.World, squad) is not { PortraitSerial: { } serial, Inventory: { } inventory }) return;
        if (context.Data.Items.Create(record) is not { } note) return;

        note.ModTag = Tag(serial, BountyFor(run.Args), crime);
        inventory.AddTo("main", note);
    }

    /// <summary>
    /// The bounty a run's note is worth: <c>evidenceFactor</c> times <c>goal</c>, at least <c>evidenceMin</c>.
    /// </summary>
    public static int BountyFor(BehaviourArgs args)
    {
        double bounty = args.Number("evidenceFactor", GoalFactor) * args.Number("goal");
        return (int)Math.Min(int.MaxValue, Math.Max(args.Number("evidenceMin", MinBounty), bounty));
    }

    /// <summary>
    /// A note's tag.
    /// </summary>
    public static string Tag(uint serial, int bounty, CrimeCode crime) =>
        string.Join('|', TagPrefix, serial.ToString(CultureInfo.InvariantCulture), bounty.ToString(CultureInfo.InvariantCulture), (int)crime);

    #endregion

    #region Using

    /// <summary>
    /// The item use a dependent registers for its note record (<c>c.AddItemUse(record, Evidence.Use)</c>): an untagged item is moved as usual;
    /// a tagged one puts the bounty on the rival it names and is used up, or stays with a message when the rival is not in the world.
    /// </summary>
    public static bool Use(ItemUse use)
    {
        if (Parse(use.Item.ModTag) is not { } parsed) return false;

        var (serial, bounty, crime) = parsed;
        var world = use.World;
        if (Find(world, serial) is not { } rival)
        {
            world.Post("The ledger's owner is nowhere to be found. Bring them closer before you hand it in.");
            return true;
        }
        if (world.Systems.OfType<CrimeSystem>().FirstOrDefault() is not { } crimes) return true;

        int factions = Charge(world, crimes, rival, bounty, crime);
        use.Inventory.RemoveOne(use.Item);
        world.Post($"The ledger is handed to the law: {rival.Name} is wanted by {factions} factions, bounty c.{bounty}.");
        return true;
    }

    /// <summary>
    /// A note tag's serial, bounty and crime; null for another mod's tag or none.
    /// </summary>
    public static (uint Serial, int Bounty, CrimeCode Crime)? Parse(string tag)
    {
        var parts = tag.Split('|');
        if (parts.Length != 4 || parts[0] != TagPrefix) return null;

        var inv = CultureInfo.InvariantCulture;
        if (!uint.TryParse(parts[1], inv, out uint serial)) return null;
        if (!int.TryParse(parts[2], inv, out int bounty) || bounty <= 0) return null;
        if (!int.TryParse(parts[3], inv, out int crime)) return null;

        return (serial, bounty, (CrimeCode)crime);
    }

    /// <summary>
    /// The living character other than the player's with portrait serial <paramref name="serial"/>; null when none is in the world.
    /// </summary>
    static CharacterCold? Find(World world, uint serial)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: false, Medical.Dead: false } c) continue;
            if (c.PortraitSerial == serial) return c;
        }

        return null;
    }

    /// <summary>
    /// Adds <paramref name="bounty"/> to the rival's entry with each booking faction (each faction's <see cref="CrimeFactions.BookingOf"/>, once),
    /// listing <paramref name="crime"/>, raised now and claimable again; the number of entries charged.
    /// </summary>
    static int Charge(World world, CrimeSystem crimes, CharacterCold rival, int bounty, CrimeCode crime)
    {
        var keys = new SortedSet<int>();
        for (int f = 0; f < crimes.Factions.Relations.Count; f++)
            if (crimes.Factions.BookingOf(f) is >= 0 and var key) keys.Add(key);

        var ledger = rival.Bounties ??= new BountyLedger();
        foreach (int key in keys)
        {
            var entry = ledger.Get(key, crimes.Factions.NameOf(key));
            entry.Amount = (int)Math.Min(int.MaxValue, (long)entry.Amount + bounty);
            entry.Crimes |= 1 << (int)crime;
            entry.RaisedTick = world.Tick;
            entry.Claimed = false;
        }

        return keys.Count;
    }

    #endregion
}

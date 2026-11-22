using System.Numerics;
using Meitou.Data.Gameplay;

namespace Rivalry;

/// <summary>
/// Small lookups used across Rivalry Core: points on the ground plane (X and Z; height left out) and a faction's id or name by index.
/// </summary>
internal static class Ground
{
    /// <summary>
    /// A world point on the ground plane.
    /// </summary>
    public static Vector2 Flat(this Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// The distance between two world points on the ground plane.
    /// </summary>
    public static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(a.Flat(), b.Flat());

    /// <summary>
    /// The faction at an index; null for none (-1, or past the data's factions).
    /// </summary>
    public static FactionData? FactionAt(this PopulationData data, int faction) =>
        faction >= 0 && faction < data.Factions.Count ? data.Factions[faction] : null;
}

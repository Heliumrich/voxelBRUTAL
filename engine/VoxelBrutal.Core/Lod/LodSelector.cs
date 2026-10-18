using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Lod;

public sealed record LodSettings
{
    public int MinimumLod { get; init; }
    public double PixelError { get; init; } = 2;
    /// <summary>Viewport height / (2 * tan(vertical FOV / 2)).</summary>
    public double ProjectionScale { get; init; } = 540;
    public int MaxSelectedChunks { get; init; } = 32768;
    /// <summary>Conservative default for callers without topology-aware boundary meshing and atomic publication.</summary>
    public bool RequireUniformLod { get; init; } = true;

    internal void Validate(int rootLod, int rootCount)
    {
        if (MinimumLod < 0 || MinimumLod > rootLod) throw new ArgumentOutOfRangeException(nameof(MinimumLod));
        if (!(PixelError > 0) || !double.IsFinite(PixelError)) throw new ArgumentOutOfRangeException(nameof(PixelError));
        if (!(ProjectionScale > 0) || !double.IsFinite(ProjectionScale)) throw new ArgumentOutOfRangeException(nameof(ProjectionScale));
        if (MaxSelectedChunks < rootCount) throw new ArgumentOutOfRangeException(nameof(MaxSelectedChunks));
    }
}

public sealed record LodSelection(IReadOnlyList<ChunkKey> Roots, IReadOnlyList<ChunkKey> Desired, bool RequireUniformLod, bool BudgetLimited);

/// <summary>Finite forest of disjoint, equal-level roots. Does not couple selection to mesh availability or the view direction.</summary>
public static class LodSelector
{
    public static LodSelection Select(IReadOnlyList<ChunkKey> roots, WorldPosition camera, LodSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        settings ??= new LodSettings();
        if (roots.Count == 0) return new([], [], settings.RequireUniformLod, false);
        var rootSet = roots.ToHashSet();
        int rootLod = roots[0].Lod;
        if (rootSet.Count != roots.Count || roots.Any(key => key.Lod != rootLod))
            throw new ArgumentException("Roots must be distinct and at the same LOD.", nameof(roots));
        settings.Validate(rootLod, roots.Count);
        return settings.RequireUniformLod
            ? SelectUniform(roots, camera, settings)
            : SelectMixed(roots, camera, settings);
    }

    private static LodSelection SelectUniform(IReadOnlyList<ChunkKey> roots, WorldPosition camera, LodSettings settings)
    {
        int level = roots[0].Lod;
        double distance = Math.Max(1, Math.Sqrt(roots.Min(camera.DistanceSquaredTo)));
        while (level > settings.MinimumLod && (1 << level) * settings.ProjectionScale / distance > settings.PixelError)
            level--;
        int desiredLevel = level;
        long count = roots.Count;
        // Select the finest complete uniform cover that fits, never an incomplete budget-truncated cover.
        level = roots[0].Lod;
        while (level > desiredLevel && count <= settings.MaxSelectedChunks / 8)
        {
            count *= 8;
            level--;
        }
        var leaves = new List<ChunkKey>((int)count);
        foreach (var root in roots) AppendAtLevel(root, level, leaves);
        return new(roots.ToArray(), Sort(leaves), true, level != desiredLevel);
    }

    private static LodSelection SelectMixed(IReadOnlyList<ChunkKey> roots, WorldPosition camera, LodSettings settings)
    {
        var leaves = roots.ToHashSet();
        var queue = new PriorityQueue<ChunkKey, double>();
        foreach (var root in roots) queue.Enqueue(root, -Error(root, camera, settings));
        bool budgetLimited = false;
        while (queue.TryDequeue(out var node, out var negativeError))
        {
            if (!leaves.Contains(node)) continue;
            if (-negativeError <= settings.PixelError) break;
            if (node.Lod <= settings.MinimumLod) continue;
            var splitPlan = new HashSet<ChunkKey>();
            PlanBalancedSplit(node, leaves, roots[0].Lod, splitPlan);
            if (leaves.Count + (long)splitPlan.Count * 7 > settings.MaxSelectedChunks)
            {
                budgetLimited = true;
                continue;
            }
            foreach (var split in splitPlan)
            {
                leaves.Remove(split);
                foreach (var child in Children(split))
                {
                    leaves.Add(child);
                    queue.Enqueue(child, -Error(child, camera, settings));
                }
            }
        }
        return new(roots.ToArray(), Sort(leaves), false, budgetLimited);
    }

    private static void PlanBalancedSplit(ChunkKey node, HashSet<ChunkKey> leaves, int rootLod, HashSet<ChunkKey> splits)
    {
        if (!splits.Add(node)) return;
        foreach (var neighbor in FaceNeighbors(node, leaves, rootLod))
            if (neighbor.Lod > node.Lod) PlanBalancedSplit(neighbor, leaves, rootLod, splits);
    }

    internal static IEnumerable<ChunkKey> FaceNeighbors(ChunkKey node, HashSet<ChunkKey> leaves, int rootLod)
    {
        for (int face = 0; face < 6; face++)
        {
            if (!TryFacePoint(node, face, out long x, out long y, out long z)) continue;
            // A finer neighbour is unnecessary here: its own query discovers the coarser side.
            for (int level = node.Lod; level <= rootLod; level++)
            {
                var candidate = ChunkKey.FromWorld(x, y, z, level);
                if (leaves.Contains(candidate)) { yield return candidate; break; }
            }
        }
    }

    internal static bool TryFacePoint(ChunkKey node, int face, out long x, out long y, out long z)
    {
        Int128 px = (Int128)node.OriginX + node.WorldSize / 2;
        Int128 py = (Int128)node.OriginY + node.WorldSize / 2;
        Int128 pz = (Int128)node.OriginZ + node.WorldSize / 2;
        switch (face)
        {
            case 0: px = (Int128)node.OriginX - 1; break;
            case 1: px = (Int128)node.OriginX + node.WorldSize; break;
            case 2: py = (Int128)node.OriginY - 1; break;
            case 3: py = (Int128)node.OriginY + node.WorldSize; break;
            case 4: pz = (Int128)node.OriginZ - 1; break;
            case 5: pz = (Int128)node.OriginZ + node.WorldSize; break;
            default: throw new ArgumentOutOfRangeException(nameof(face));
        }
        if (px < long.MinValue || px > long.MaxValue || py < long.MinValue || py > long.MaxValue || pz < long.MinValue || pz > long.MaxValue)
        { x = y = z = 0; return false; }
        (x, y, z) = ((long)px, (long)py, (long)pz);
        return true;
    }

    internal static IEnumerable<ChunkKey> Children(ChunkKey node)
    {
        if (node.Lod == 0) throw new InvalidOperationException("LOD zero has no chunk children.");
        for (int z = 0; z < 2; z++)
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
            yield return new(checked(node.X * 2 + x), checked(node.Y * 2 + y), checked(node.Z * 2 + z), node.Lod - 1);
    }

    internal static ChunkKey Parent(ChunkKey node) => ChunkKey.FromWorld(node.OriginX, node.OriginY, node.OriginZ, node.Lod + 1);
    internal static ChunkKey[] Sort(IEnumerable<ChunkKey> keys) => keys.OrderBy(key => key.Lod).ThenBy(key => key.X).ThenBy(key => key.Y).ThenBy(key => key.Z).ToArray();
    private static double Error(ChunkKey node, WorldPosition camera, LodSettings settings) => node.VoxelScale * settings.ProjectionScale / Math.Max(1, Math.Sqrt(camera.DistanceSquaredTo(node)));
    private static void AppendAtLevel(ChunkKey node, int level, List<ChunkKey> leaves)
    {
        if (node.Lod == level) leaves.Add(node);
        else foreach (var child in Children(node)) AppendAtLevel(child, level, leaves);
    }
}

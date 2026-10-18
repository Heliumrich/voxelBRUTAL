using System.Numerics;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.Visibility;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Tests;

public static class LodVisibilityTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("uniform LOD partitions negative coordinates within budget", UniformPartition);
        yield return ("mixed LOD preserves complete 2:1 balanced coverage", MixedPartition);
        yield return ("uniform residency changes levels globally", UniformResidency);
        yield return ("resident ancestor survives incomplete nested replacement", NestedResidency);
        yield return ("mixed boundary metadata identifies fine-side face", Boundaries);
        yield return ("camera-relative subtraction keeps huge-coordinate detail", Precision);
        yield return ("frustum obeys Vulkan depth and conservative boundary tests", FrustumPlanes);
        yield return ("frustum accepts infinite reversed depth", InfiniteReverseDepth);
        yield return ("reverse-Z Hi-Z preserves holes and odd viewport edges", ConservativeOcclusion);
        yield return ("Hi-Z never rejects a rectangle exposed in a full-depth oracle", OcclusionOracle);
    }

    public static void Run() { foreach (var (_, test) in Cases()) test(); }

    private static void UniformPartition()
    {
        ChunkKey[] roots = [new(-1, -1, -1, 3), new(0, -1, -1, 3)];
        var selection = LodSelector.Select(roots, new(-1, -1, -1), new() { MaxSelectedChunks = 128 });
        Assert(selection.RequireUniformLod && selection.BudgetLimited, "The safe default must signal budget coarsening.");
        Assert(selection.Desired.Count == 128 && selection.Desired.All(key => key.Lod == 1), "Uniform cover must fit exactly.");
        AssertPartition(roots, selection.Desired);
        Throws<ArgumentOutOfRangeException>(() => LodSelector.Select(roots, new(), new() { MaxSelectedChunks = 1 }));
        Throws<ArgumentException>(() => LodSelector.Select([roots[0], roots[0]], new()));
        Throws<ArgumentOutOfRangeException>(() => LodSelector.Select(roots, new(), new() { PixelError = double.NaN }));
    }

    private static void MixedPartition()
    {
        ChunkKey[] roots = [new(-1, 0, 0, 4), new(0, 0, 0, 4), new(1, 0, 0, 4)];
        foreach (int budget in new[] { 3, 45, 129, 500 })
        {
            var selection = LodSelector.Select(roots, new(64, 64, 64), new()
            {
                RequireUniformLod = false, PixelError = 4, ProjectionScale = 200, MaxSelectedChunks = budget
            });
            Assert(selection.Desired.Count <= budget, "Balanced split closure exceeded budget.");
            AssertPartition(roots, selection.Desired);
            Assert(LodBoundaryPlan.Build(selection.Desired).All(boundary => boundary.IsTwoToOne), "Neighbor LOD differs by more than one.");
        }
    }

    private static void UniformResidency()
    {
        ChunkKey[] roots = [new(-1, 0, 0, 1), new(0, 0, 0, 1)];
        var selection = LodSelector.Select(roots, new(), new() { MaxSelectedChunks = 16 });
        var resident = roots.Concat(roots[0].Children()).ToHashSet();
        var partial = ResidentLodCover.Resolve(selection, resident.Contains);
        Assert(partial.Renderable.ToHashSet().SetEquals(roots), "Uniform rendering must retain both parents until every root can switch.");
        Assert(partial.MissingRequests.Count == 8 && partial.UncoveredRoots.Count == 0, "Requests should contain missing children.");
        resident.UnionWith(roots[1].Children());
        var complete = ResidentLodCover.Resolve(selection, resident.Contains);
        Assert(complete.Renderable.Count == 16 && complete.Renderable.All(key => key.Lod == 0), "Complete uniform level should replace parents.");
        AssertPartition(roots, complete.Renderable);
    }

    private static void NestedResidency()
    {
        var root = new ChunkKey(-1, 0, 0, 2);
        var children = root.Children().ToArray();
        var grandchildren = children[0].Children().ToArray();
        var desired = children.Skip(1).Concat(grandchildren).ToArray();
        var selection = new LodSelection([root], desired, false, false);
        var resident = new HashSet<ChunkKey> { root };
        resident.UnionWith(children.Skip(1));
        resident.UnionWith(grandchildren.Take(7));
        var incomplete = ResidentLodCover.Resolve(selection, resident.Contains);
        Assert(incomplete.Renderable.SequenceEqual([root]), "Seven grandchildren cannot retire the root without their immediate parent.");
        Assert(incomplete.MissingRequests.Contains(grandchildren[7]) && incomplete.MissingRequests.Contains(children[0]), "Missing coverage ancestors and leaves must be requested.");
        resident.Add(grandchildren[7]);
        var complete = ResidentLodCover.Resolve(selection, resident.Contains);
        Assert(complete.Renderable.Count == 15 && !complete.Renderable.Contains(root), "Eight ready grandchildren plus sibling children replace root.");
        AssertPartition([root], complete.Renderable);
        resident.Clear();
        var empty = ResidentLodCover.Resolve(selection, resident.Contains);
        Assert(empty.Renderable.Count == 0 && empty.UncoveredRoots.SequenceEqual([root]), "Cold startup reports uncovered roots explicitly.");
        Assert(empty.MissingRequests[0] == root, "Coarse coverage requests must sort ahead of detail.");
    }

    private static void Boundaries()
    {
        var fine = new ChunkKey(-1, 0, 0, 0);
        var coarse = new ChunkKey(0, 0, 0, 1);
        var boundary = LodBoundaryPlan.Build([fine, coarse]).Single();
        Assert(boundary.Fine == fine && boundary.Coarse == coarse && boundary.FineFace == ChunkFaces.PositiveX && boundary.IsTwoToOne,
            "Boundary orientation must belong to the finer chunk.");
        var wider = LodBoundaryPlan.Build([fine, new(0, 0, 0, 2)]).Single();
        Assert(!wider.IsTwoToOne, "Resident fallback gaps larger than 2:1 must remain detectable.");
    }

    private static void Precision()
    {
        const long anchor = 1L << 56;
        var key = ChunkKey.FromWorld(anchor, -anchor, 0);
        var camera = new WorldPosition(anchor - 1, -anchor - 2, 0, .25, .5, 0);
        var origin = camera.RelativeOrigin(key);
        Assert(origin == new Vector3(.75f, 1.5f, 0), "Global values must be subtracted before float conversion.");
        Assert(Math.Abs(camera.DistanceSquaredTo(key) - 2.8125) < 1e-9, "Distance error at huge coordinates.");
        var extreme = new WorldPosition(long.MinValue, 0, 0);
        Assert(double.IsFinite(extreme.DistanceSquaredTo(ChunkKey.FromWorld(long.MaxValue - 64, 0, 0))), "Full signed-long subtraction must not overflow.");
        Throws<ArgumentOutOfRangeException>(() => new WorldPosition(0, 0, 0, double.NaN));
    }

    private static void FrustumPlanes()
    {
        var frustum = Frustum.FromViewProjection(Matrix4x4.Identity);
        Assert(frustum.Intersects(new(new(-.5f, -.5f, .2f), new(.5f, .5f, .8f))), "Interior must remain visible.");
        Assert(frustum.Intersects(new(new(1, 0, .2f), new(2, .5f, .8f))), "Plane contact remains visible.");
        Assert(!frustum.Intersects(new(new(1.1f, 0, .2f), new(2, .5f, .8f))), "Right-side box should be culled.");
        Assert(!frustum.Intersects(new(new(-.5f, -.5f, -.8f), new(.5f, .5f, -.2f))), "Vulkan near depth is zero, not minus one.");
        Assert(!frustum.Intersects(new(new(-.5f, -.5f, 1.2f), new(.5f, .5f, 1.8f))), "Far clip must be tested.");
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 1000);
        var perspective = Frustum.FromViewProjection(projection);
        Assert(perspective.Intersects(new(new(-1, -1, -11), new(1, 1, -9))), "Forward negative-Z perspective interior must remain visible.");
        Assert(!perspective.Intersects(new(new(-1, -1, 9), new(1, 1, 11))), "Behind-camera box must be culled.");
    }

    private static void InfiniteReverseDepth()
    {
        // Right-handed, row-vector, Vulkan 0..1 infinite reverse-Z: depth = near / -viewZ.
        var matrix = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, -1, 0, 0, .1f, 0);
        var frustum = Frustum.FromViewProjection(matrix);
        Assert(frustum.Intersects(new(new(-100, -100, -1_000_100), new(100, 100, -1_000_000))), "Infinite reverse-Z must not invent a finite far plane.");
        Assert(!frustum.Intersects(new(new(-.001f, -.001f, -.02f), new(.001f, .001f, -.01f))), "Near clip still applies in reverse-Z.");
    }

    private static void ConservativeOcclusion()
    {
        float[] depth = Enumerable.Repeat(.8f, 35).ToArray();
        var full = ReversedDepthPyramid.Build(depth, 7, 5);
        Assert(full.LevelCount == 4, "Odd dimensions must reduce completely.");
        Assert(full.IsOccluded(new(0, 0, 7, 5, .5f)), "Covered distant rectangle must be hidden.");
        Assert(!full.IsOccluded(new(0, 0, 7, 5, .9f)), "Closer geometry must remain visible.");
        Assert(!full.IsOccluded(new(0, 0, 7, 5, .8f)), "Equal depth must remain conservatively visible.");
        Assert(full.IsOccluded(new(6, 4, 7, 5, .5f)), "Last pixel of odd viewport cannot disappear from mip coverage.");
        depth[34] = 0;
        var hole = ReversedDepthPyramid.Build(depth, 7, 5);
        Assert(!hole.IsOccluded(new(0, 0, 7, 5, .5f)), "One clear-depth hole prevents rejection.");
        Assert(!hole.IsOccluded(new(6, 4, 7, 5, .5f)), "Odd edge hole must remain visible.");
        Assert(!full.IsOccluded(new(0, 0, 7, 5, .5f, true)), "Near-plane crossing bounds must remain visible.");
        Assert(!full.IsOccluded(new(-1, 0, 7, 5, .5f)), "Unreliable offscreen bounds remain visible.");
    }

    private static void OcclusionOracle()
    {
        const int width = 17, height = 13;
        var random = new Random(713);
        float[] depth = Enumerable.Range(0, width * height).Select(_ => random.Next(0, 5) / 4f).ToArray();
        var pyramid = ReversedDepthPyramid.Build(depth, width, height);
        int rejected = 0;
        for (int i = 0; i < 2000; i++)
        {
            int x0 = random.Next(width), y0 = random.Next(height);
            int x1 = random.Next(x0 + 1, width + 1), y1 = random.Next(y0 + 1, height + 1);
            float nearest = random.Next(1, 10) / 10f;
            if (!pyramid.IsOccluded(new(x0, y0, x1, y1, nearest), 0)) continue;
            rejected++;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                Assert(nearest < depth[y * width + x], "Hi-Z produced a false occlusion rejection.");
        }
        Assert(rejected > 0, "The conservative oracle test must exercise actual rejections.");
    }

    private static void AssertPartition(IReadOnlyList<ChunkKey> roots, IReadOnlyList<ChunkKey> leaves)
    {
        // A dense level-zero occupancy oracle is intentionally independent of octree traversal.
        var occupied = new HashSet<(long X, long Y, long Z)>();
        foreach (var leaf in leaves)
        {
            long side = 1L << leaf.Lod;
            for (long z = leaf.Z * side; z < (leaf.Z + 1) * side; z++)
            for (long y = leaf.Y * side; y < (leaf.Y + 1) * side; y++)
            for (long x = leaf.X * side; x < (leaf.X + 1) * side; x++)
                Assert(occupied.Add((x, y, z)), "Leaves overlap.");
        }
        long expected = 0;
        foreach (var root in roots)
        {
            long side = 1L << root.Lod;
            expected += side * side * side;
            for (long z = root.Z * side; z < (root.Z + 1) * side; z++)
            for (long y = root.Y * side; y < (root.Y + 1) * side; y++)
            for (long x = root.X * side; x < (root.X + 1) * side; x++)
                Assert(occupied.Contains((x, y, z)), "LOD cover contains a hole.");
        }
        Assert(occupied.Count == expected, "Leaves extend beyond roots.");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

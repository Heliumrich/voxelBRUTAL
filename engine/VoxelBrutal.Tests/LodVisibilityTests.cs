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
        yield return ("mesh plan captures all four fine neighbors and stable topology versions", MeshPlanTopology);
        yield return ("mesh plan refuses holes overlap and unbalanced boundary partitions", MeshPlanValidation);
        yield return ("LOD publication retains balanced topology until every exact GPU version is ready", AtomicPublication);
        yield return ("cancelled topology builds cannot publish into a newer candidate", SupersededPublication);
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
            var plan = LodMeshPlan.Create(roots, selection.Desired);
            foreach (var key in plan.Leaves)
            for (int face = 0; face < 6; face++)
            foreach (var neighbor in plan.GetFaceNeighbors(key, face))
                Assert(plan.GetFaceNeighbors(neighbor, face ^ 1).Contains(key), "Every selected adjacency must be reciprocal.");
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

    private static void MeshPlanTopology()
    {
        ChunkKey[] roots = [new(-1, 0, 0, 2), new(0, 0, 0, 2)];
        var leaves = roots[0].Children().Append(roots[1]).ToArray();
        var plan = LodMeshPlan.Create(roots, leaves, 3);
        var fine = roots[0].Children().Where(key => key.X == -1).ToArray();
        Assert(plan.GetFaceNeighbors(roots[1], 0).ToHashSet().SetEquals(fine), "Coarse face must see all four finer neighbours.");
        foreach (var key in fine) Assert(plan.GetFaceNeighbors(key, 1).SequenceEqual([roots[1]]), "Fine/coarse adjacency must be reciprocal.");
        Assert(plan.GetFaceNeighbors(roots[1], 1).Count == 0, "Outside partition must be explicit air.");
        Assert(plan.TryFindLeaf(-1, 1, 1, out var found) && found == fine[0], "Negative-coordinate lookup must find exact leaf.");
        Assert(!plan.TryFindLeaf(512, 1, 1, out _), "Lookup outside forest must fail.");
        var reordered = LodMeshPlan.Create(roots.Reverse().ToArray(), leaves.Reverse().ToArray(), 3);
        Assert(plan.HasSameVersions(reordered), "Input enumeration order cannot change mesh versions.");
        var revisions = leaves.ToDictionary(key => key, _ => 3UL);
        revisions[fine[0]] = 4;
        var edited = LodMeshPlan.Create(roots, leaves, key => revisions[key]);
        Assert(plan.GetEntry(roots[1]).Revision == edited.GetEntry(roots[1]).Revision
            && plan.GetEntry(roots[1]).TopologyStamp != edited.GetEntry(roots[1]).TopologyStamp,
            "A neighbour edit must invalidate a shared boundary even if own revision is unchanged.");
        Assert(plan.GetEntry(fine[0]).Revision != edited.GetEntry(fine[0]).Revision, "Own data revision remains part of identity.");
        var farChild = roots[0].Child(0);
        var refinedLeaves = leaves.Where(key => key != farChild).Concat(farChild.Children()).ToArray();
        var refined = LodMeshPlan.Create(roots, refinedLeaves, 3);
        Assert(refined.GetEntry(roots[1]).Version == plan.GetEntry(roots[1]).Version,
            "Unrelated interior refinement must not rebuild a distant unchanged boundary.");
    }

    private static void MeshPlanValidation()
    {
        var root = new ChunkKey(0, 0, 0, 2);
        var children = root.Children().ToArray();
        Throws<ArgumentException>(() => LodMeshPlan.Create([root], children.Take(7).ToArray()));
        Throws<ArgumentException>(() => LodMeshPlan.Create([root], children.Append(root).ToArray()));
        Throws<ArgumentException>(() => LodMeshPlan.Create([root], children.Append(children[0]).ToArray()));
        Throws<ArgumentException>(() => LodMeshPlan.Create([root], [new(1, 0, 0, 2)]));
        ChunkKey[] roots = [new(-1, 0, 0, 2), root];
        var unbalanced = roots[0].Children().SelectMany(child => child.Children()).Append(root).ToArray();
        Throws<ArgumentException>(() => LodMeshPlan.Create(roots, unbalanced));
        Assert(LodMeshPlan.Create([], []).Entries.Count == 0, "Empty forest is a valid empty publication.");
        ChunkKey[] extremes = [ChunkKey.FromWorld(long.MinValue, 0, 0, 3), ChunkKey.FromWorld(long.MaxValue, 0, 0, 3)];
        var extremePlan = LodMeshPlan.Create(extremes, extremes);
        Assert(extremePlan.GetFaceNeighbors(extremes[0], 0).Count == 0 && extremePlan.GetFaceNeighbors(extremes[1], 1).Count == 0,
            "Probing beyond signed world endpoints must not overflow.");
        Assert(extremePlan.TryFindLeaf(long.MinValue, 0, 0, out var minimum) && minimum == extremes[0]
            && extremePlan.TryFindLeaf(long.MaxValue, 0, 0, out var maximum) && maximum == extremes[1],
            "Representable world endpoints remain valid topology addresses.");
    }

    private static void AtomicPublication()
    {
        ChunkKey[] roots = [new(-1, 0, 0, 2), new(0, 0, 0, 2)];
        var coarse = LodMeshPlan.Create(roots, roots, 1);
        var refined = LodMeshPlan.Create(roots, roots[0].Children().Append(roots[1]).ToArray(), 1);
        var publication = new AtomicLodPublication();
        var resident = new HashSet<ChunkMeshId>();
        publication.Stage(coarse);
        Assert(!publication.TryPublish(resident.Contains) && publication.Published is null, "Cold start cannot claim unavailable coverage.");
        resident.UnionWith(coarse.Entries.Select(entry => entry.Version));
        Assert(publication.TryPublish(resident.Contains) && publication.Published == coarse, "Complete coarse plan must bootstrap.");
        publication.Stage(refined);
        Assert(publication.RequiredVersions.IsSupersetOf(coarse.Entries.Select(entry => entry.Version)), "Keep every old version while replacement loads.");
        foreach (var entry in refined.Entries.Take(refined.Entries.Count - 1))
        {
            resident.Add(entry.Version);
            Assert(!publication.TryPublish(resident.Contains) && publication.Published == coarse, "Partial topology publication would create seams.");
            AssertPartition(roots, publication.Published!.Leaves);
            Assert(LodBoundaryPlan.Build(publication.Published.Leaves).All(boundary => boundary.IsTwoToOne), "Loading must preserve balance.");
        }
        var last = refined.Entries[^1].Version;
        Assert(publication.MissingVersions(resident.Contains).SequenceEqual([last]), "Ready data with old topology is insufficient.");
        resident.Add(last);
        Assert(publication.TryPublish(resident.Contains) && publication.Published == refined && publication.Pending is null,
            "Publish all versions in one owner-thread operation.");
        Assert(publication.PublishedGeneration == 2 && publication.RequiredVersions.SetEquals(refined.Entries.Select(entry => entry.Version)),
            "Only replaced versions can retire after publication.");
        AssertPartition(roots, publication.Published!.Leaves);
    }

    private static void SupersededPublication()
    {
        var root = new ChunkKey(0, 0, 0, 1);
        var first = LodMeshPlan.Create([root], [root], 1);
        var stale = LodMeshPlan.Create([root], root.Children().ToArray(), 1);
        var newer = LodMeshPlan.Create([root], root.Children().ToArray(), 2);
        var publication = new AtomicLodPublication();
        var resident = first.Entries.Select(entry => entry.Version).ToHashSet();
        publication.Stage(first);
        Assert(publication.TryPublish(resident.Contains), "Bootstrap failed.");
        publication.Stage(stale);
        publication.Stage(newer);
        resident.UnionWith(stale.Entries.Select(entry => entry.Version));
        Assert(!publication.TryPublish(resident.Contains) && publication.Published == first, "Stale completed versions cannot publish newer plan.");
        Assert(!publication.RequiredVersions.Overlaps(stale.Entries.Select(entry => entry.Version)), "Superseded staging resources can retire.");
        publication.CancelPending();
        Assert(publication.Pending is null && publication.RequiredVersions.SetEquals(first.Entries.Select(entry => entry.Version)), "Cancel must retain only published resources.");
        publication.Stage(first);
        Assert(publication.Pending is null, "Restaging published content cannot force redundant work.");
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

using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Lod;

public sealed record ResidentLodSelection(
    IReadOnlyList<ChunkKey> Renderable,
    IReadOnlyList<ChunkKey> MissingRequests,
    IReadOnlyList<ChunkKey> UncoveredRoots);

/// <summary>
/// Retains a resident ancestor until every desired child subtree has complete resident coverage.
/// Residency includes upload completion, and an empty mesh is a valid resident result.
/// This solves loading holes. Adaptive topology-aware meshes use LodMeshPlan and AtomicLodPublication
/// instead, so neighbour topology and mesh versions change together while preserving 2:1 balance.
/// </summary>
public static class ResidentLodCover
{
    public static ResidentLodSelection Resolve(LodSelection selection, Func<ChunkKey, bool> isResident)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(isResident);
        if (selection.Roots.Count == 0) return new([], [], []);
        if (selection.RequireUniformLod) return ResolveUniform(selection, isResident);

        var desired = selection.Desired.ToHashSet();
        var render = new List<ChunkKey>();
        var requests = new HashSet<ChunkKey>();
        var uncovered = new List<ChunkKey>();
        var ancestors = new HashSet<ChunkKey>();
        int rootLod = selection.Roots[0].Lod;
        foreach (var leaf in desired)
        {
            var node = leaf;
            while (node.Lod < rootLod) { node = LodSelector.Parent(node); ancestors.Add(node); }
        }
        foreach (var root in selection.Roots)
            if (!ResolveNode(root, desired, ancestors, isResident, render, requests)) uncovered.Add(root);
        return new(LodSelector.Sort(render), SortRequests(requests), uncovered.ToArray());
    }

    private static bool ResolveNode(ChunkKey node, HashSet<ChunkKey> desired, HashSet<ChunkKey> ancestors,
        Func<ChunkKey, bool> isResident, List<ChunkKey> render, HashSet<ChunkKey> requests)
    {
        int start = render.Count;
        if (!desired.Contains(node) && ancestors.Contains(node))
        {
            bool complete = true;
            foreach (var child in LodSelector.Children(node))
                complete &= ResolveNode(child, desired, ancestors, isResident, render, requests);
            if (complete) return true;
            render.RemoveRange(start, render.Count - start);
        }
        if (isResident(node)) { render.Add(node); return true; }
        requests.Add(node);
        return false;
    }

    private static ResidentLodSelection ResolveUniform(LodSelection selection, Func<ChunkKey, bool> isResident)
    {
        int desiredLevel = selection.Desired[0].Lod;
        if (selection.Desired.Any(key => key.Lod != desiredLevel))
            throw new ArgumentException("A uniform selection must contain only one desired LOD.", nameof(selection));
        var requests = new HashSet<ChunkKey>();
        var cover = selection.Desired.ToHashSet();
        // Global atomic level changes avoid introducing spatial mixed-LOD boundaries in the safe mode.
        for (int level = desiredLevel; level <= selection.Roots[0].Lod; level++)
        {
            bool complete = true;
            foreach (var node in cover)
                if (!isResident(node)) { requests.Add(node); complete = false; }
            if (complete) return new(LodSelector.Sort(cover), SortRequests(requests), []);
            if (level < selection.Roots[0].Lod) cover = cover.Select(LodSelector.Parent).ToHashSet();
        }
        return new([], SortRequests(requests), selection.Roots.ToArray());
    }

    private static ChunkKey[] SortRequests(IEnumerable<ChunkKey> requests) => requests
        .OrderByDescending(key => key.Lod).ThenBy(key => key.X).ThenBy(key => key.Y).ThenBy(key => key.Z).ToArray();
}

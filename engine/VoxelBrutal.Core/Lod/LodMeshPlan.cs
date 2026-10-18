using System.Collections.ObjectModel;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Lod;

/// <summary>Geometry identity includes both source content and its exact boundary topology.</summary>
public readonly record struct ChunkMeshId(ChunkKey Key, ulong Revision, ulong TopologyStamp);

public readonly record struct LodMeshPlanEntry(ChunkMeshId Version)
{
    public ChunkKey Key => Version.Key;
    public ulong Revision => Version.Revision;
    public ulong TopologyStamp => Version.TopologyStamp;
}

/// <summary>
/// Immutable complete, face-balanced chunk partition and its mesh versions. Neighbor lists belong
/// to this exact partition: boundary meshing and publication must use the same plan.
/// </summary>
public sealed class LodMeshPlan
{
    private readonly HashSet<ChunkKey> _leafSet;
    private readonly Dictionary<ChunkKey, ReadOnlyCollection<ChunkKey>[]> _neighbors = [];
    private readonly Dictionary<ChunkKey, LodMeshPlanEntry> _entries = [];
    private readonly int _minimumLod, _maximumLod;
    public IReadOnlyList<ChunkKey> Roots { get; }
    public IReadOnlyList<ChunkKey> Leaves { get; }
    public IReadOnlyList<LodMeshPlanEntry> Entries { get; }
    public ulong Signature { get; }

    private LodMeshPlan(IReadOnlyList<ChunkKey> roots, IReadOnlyList<ChunkKey> leaves, Func<ChunkKey, ulong> revision)
    {
        var sortedRoots = LodSelector.Sort(roots);
        var sortedLeaves = LodSelector.Sort(leaves);
        Roots = Array.AsReadOnly(sortedRoots);
        Leaves = Array.AsReadOnly(sortedLeaves);
        _leafSet = sortedLeaves.ToHashSet();
        ValidatePartition(sortedRoots, sortedLeaves, _leafSet);
        _minimumLod = sortedLeaves.Length == 0 ? 0 : sortedLeaves[0].Lod;
        _maximumLod = sortedLeaves.Length == 0 ? 0 : sortedLeaves[^1].Lod;
        var revisions = sortedLeaves.ToDictionary(key => key, revision);
        var entries = new LodMeshPlanEntry[sortedLeaves.Length];
        ulong signature = Stamp.Start;
        foreach (var root in sortedRoots) Stamp.Add(ref signature, root);
        for (int i = 0; i < sortedLeaves.Length; i++)
        {
            var key = sortedLeaves[i];
            var faces = new ReadOnlyCollection<ChunkKey>[6];
            ulong topology = Stamp.Start;
            Stamp.Add(ref topology, key);
            for (int face = 0; face < 6; face++)
            {
                var adjacent = FindFaceNeighbors(key, face);
                if (adjacent.Any(neighbor => Math.Abs(neighbor.Lod - key.Lod) > 1))
                    throw new ArgumentException("Mesh plans require a face-balanced 2:1 partition.", nameof(leaves));
                faces[face] = Array.AsReadOnly(adjacent);
                Stamp.Add(ref topology, (ulong)face);
                Stamp.Add(ref topology, (ulong)adjacent.Length);
                foreach (var neighbor in adjacent)
                {
                    Stamp.Add(ref topology, neighbor);
                    // A neighbour edit changes the boundary even if this chunk's own data is unchanged.
                    Stamp.Add(ref topology, revisions[neighbor]);
                }
            }
            _neighbors.Add(key, faces);
            entries[i] = new(new(key, revisions[key], topology));
            _entries.Add(key, entries[i]);
            Stamp.Add(ref signature, key);
            Stamp.Add(ref signature, revisions[key]);
            Stamp.Add(ref signature, topology);
        }
        Entries = Array.AsReadOnly(entries);
        Signature = signature;
    }

    public static LodMeshPlan Create(IReadOnlyList<ChunkKey> roots, IReadOnlyList<ChunkKey> leaves, ulong revision = 0) =>
        Create(roots, leaves, _ => revision);

    public static LodMeshPlan Create(IReadOnlyList<ChunkKey> roots, IReadOnlyList<ChunkKey> leaves, Func<ChunkKey, ulong> revision)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(leaves);
        ArgumentNullException.ThrowIfNull(revision);
        return new(roots, leaves, revision);
    }

    public LodMeshPlanEntry GetEntry(ChunkKey key) => _entries[key];

    /// <summary>Faces use -X,+X,-Y,+Y,-Z,+Z. An empty face list is outside the finite partition.</summary>
    public IReadOnlyList<ChunkKey> GetFaceNeighbors(ChunkKey key, int face)
    {
        if ((uint)face >= 6) throw new ArgumentOutOfRangeException(nameof(face));
        return _neighbors[key][face];
    }

    /// <summary>Integer world-space lookup with no global float conversion and no per-query allocation.</summary>
    public bool TryFindLeaf(long x, long y, long z, out ChunkKey leaf)
    {
        for (int level = _minimumLod; level <= _maximumLod; level++)
        {
            var candidate = ChunkKey.FromWorld(x, y, z, level);
            if (_leafSet.Contains(candidate)) { leaf = candidate; return true; }
        }
        leaf = default;
        return false;
    }

    public bool HasSameVersions(LodMeshPlan other) => Signature == other.Signature
        && Roots.SequenceEqual(other.Roots) && Entries.SequenceEqual(other.Entries);

    private ChunkKey[] FindFaceNeighbors(ChunkKey key, int face)
    {
        var adjacent = new HashSet<ChunkKey>();
        // Four quarter-face probes cover all possible neighbours of a balanced octree face.
        // A single centre probe would miss up to three finer neighbours at the same boundary.
        for (int v = 0; v < 2; v++)
        for (int u = 0; u < 2; u++)
        {
            int axis = face / 2;
            Int128 x = key.OriginX, y = key.OriginY, z = key.OriginZ;
            long normal = (face & 1) == 0 ? -1 : key.WorldSize;
            long du = key.WorldSize * (1 + u * 2) / 4, dv = key.WorldSize * (1 + v * 2) / 4;
            switch (axis)
            {
                case 0: x += normal; y += du; z += dv; break;
                case 1: y += normal; z += du; x += dv; break;
                default: z += normal; x += du; y += dv; break;
            }
            if (x < long.MinValue || x > long.MaxValue || y < long.MinValue || y > long.MaxValue || z < long.MinValue || z > long.MaxValue) continue;
            if (TryFindLeaf((long)x, (long)y, (long)z, out var neighbor)) adjacent.Add(neighbor);
        }
        // Also discover a coarse neighbour independently, so an invalid >2:1 partition is rejected.
        if (LodSelector.TryFacePoint(key, face, out long centerX, out long centerY, out long centerZ)
            && TryFindLeaf(centerX, centerY, centerZ, out var center)) adjacent.Add(center);
        return LodSelector.Sort(adjacent);
    }

    private static void ValidatePartition(ChunkKey[] roots, ChunkKey[] leaves, HashSet<ChunkKey> leafSet)
    {
        if (roots.Length == 0)
        {
            if (leaves.Length != 0) throw new ArgumentException("Leaves require roots.", nameof(leaves));
            return;
        }
        int rootLevel = roots[0].Lod;
        var rootSet = roots.ToHashSet();
        if (rootSet.Count != roots.Length || roots.Any(root => root.Lod != rootLevel))
            throw new ArgumentException("Roots must be distinct and at one LOD.", nameof(roots));
        if (leafSet.Count != leaves.Length) throw new ArgumentException("Duplicate leaf.", nameof(leaves));
        var volume = roots.ToDictionary(key => key, _ => (Int128)0);
        foreach (var leaf in leaves)
        {
            if (leaf.Lod > rootLevel) throw new ArgumentException("A leaf is coarser than its root.", nameof(leaves));
            var root = ChunkKey.FromWorld(leaf.OriginX, leaf.OriginY, leaf.OriginZ, rootLevel);
            if (!rootSet.Contains(root)) throw new ArgumentException("A leaf lies outside the root forest.", nameof(leaves));
            var ancestor = leaf;
            while (ancestor.Lod < rootLevel)
            {
                ancestor = ancestor.Parent;
                if (leafSet.Contains(ancestor)) throw new ArgumentException("Overlapping ancestor and leaf.", nameof(leaves));
            }
            volume[root] += (Int128)1 << (leaf.Lod * 3);
        }
        Int128 expected = (Int128)1 << (rootLevel * 3);
        if (volume.Values.Any(value => value != expected)) throw new ArgumentException("Leaves must cover every root completely.", nameof(leaves));
    }

    // Stable content signature, not an address hash. Version equality still checks the complete plan.
    private static class Stamp
    {
        public const ulong Start = 14695981039346656037UL;
        public static void Add(ref ulong hash, ChunkKey key)
        {
            Add(ref hash, unchecked((ulong)key.X)); Add(ref hash, unchecked((ulong)key.Y));
            Add(ref hash, unchecked((ulong)key.Z)); Add(ref hash, (ulong)key.Lod);
        }
        public static void Add(ref ulong hash, ulong value)
        {
            for (int b = 0; b < 8; b++) { hash ^= (byte)value; hash = unchecked(hash * 1099511628211UL); value >>= 8; }
        }
    }
}

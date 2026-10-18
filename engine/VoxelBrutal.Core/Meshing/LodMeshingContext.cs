using VoxelBrutal.Core.World;
using VoxelBrutal.Core.Lod;

namespace VoxelBrutal.Core.Meshing;

/// <summary>
/// Immutable actual-leaf occupancy on all six chunk boundaries. Coarse faces adjacent to finer
/// leaves use a 64 squared half-voxel grid. Outside the leaf cover is air, so the generated surface
/// is the boundary of the union of the selected piecewise-constant voxel volumes, including holes.
/// Capture this and the chunk snapshot under the same world-revision read transaction.
/// </summary>
public sealed class LodMeshingContext
{
    private readonly ushort[][] neighbors;
    public ChunkKey Key { get; }
    public ulong Revision { get; }
    public ulong TopologyStamp { get; }

    private LodMeshingContext(ChunkKey key, ulong revision, ulong topologyStamp, ushort[][] neighbors)
    {
        Key = key;
        Revision = revision;
        TopologyStamp = topologyStamp;
        this.neighbors = neighbors;
    }

    public int FaceResolution(int face)
    {
        if ((uint)face >= 6) throw new ArgumentOutOfRangeException(nameof(face));
        return neighbors[face].Length == 4096 ? 64 : 32;
    }

    public ushort NeighborMaterial(int face, int u, int v)
    {
        int size = FaceResolution(face);
        if ((uint)u >= size || (uint)v >= size) throw new ArgumentOutOfRangeException(nameof(u));
        return neighbors[face][u + size * v];
    }

    public static LodMeshingContext Capture(ChunkKey key, IReadOnlyList<ChunkKey> leaves, IVoxelSource source,
        ulong revision = 0, ulong topologyStamp = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        return CaptureNeighbors(key, face => leaves.Where(leaf => TouchesFace(key, leaf, face)).ToArray(),
            source, revision, topologyStamp, cancellationToken);
    }

    public static LodMeshingContext Capture(ChunkKey key, LodMeshPlan plan, IVoxelSource source,
        ulong revision = 0, ulong topologyStamp = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return CaptureNeighbors(key, face => plan.GetFaceNeighbors(key, face), source, revision, topologyStamp, cancellationToken);
    }

    private static LodMeshingContext CaptureNeighbors(ChunkKey key, Func<int, IReadOnlyList<ChunkKey>> getNeighbors,
        IVoxelSource source, ulong revision, ulong topologyStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var faces = new ushort[6][];
        for (int face = 0; face < 6; face++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ChunkKey> adjacent = getNeighbors(face);
            foreach (ChunkKey leaf in adjacent)
                if (Math.Abs(leaf.Lod - key.Lod) > 1)
                    throw new ArgumentException("Mixed-LOD meshing requires a 2:1 balanced leaf cover.", nameof(getNeighbors));
            int resolution = adjacent.Any(leaf => leaf.Lod < key.Lod) ? 64 : 32;
            faces[face] = new ushort[resolution * resolution];
            int step = key.VoxelScale / (resolution / 32);
            if (step == 0) throw new ArgumentException("LOD zero cannot have finer neighbors.", nameof(getNeighbors));
            for (int v = 0; v < resolution; v++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int u = 0; u < resolution; u++)
                {
                    (long x, long y, long z) = FacePoint(key, face, u, v, step);
                    foreach (ChunkKey neighbor in adjacent)
                    {
                        if (!Contains(neighbor, x, y, z)) continue;
                        long scale = neighbor.VoxelScale;
                        long sx = checked(neighbor.OriginX + (x - neighbor.OriginX) / scale * scale);
                        long sy = checked(neighbor.OriginY + (y - neighbor.OriginY) / scale * scale);
                        long sz = checked(neighbor.OriginZ + (z - neighbor.OriginZ) / scale * scale);
                        faces[face][u + resolution * v] = source.Sample(sx, sy, sz, neighbor.Lod);
                        break;
                    }
                }
            }
        }
        return new(key, revision, topologyStamp, faces);
    }

    private static bool TouchesFace(ChunkKey a, ChunkKey b, int face)
    {
        int axis = face / 2;
        Int128 plane = Origin(a, axis) + ((face & 1) == 1 ? a.WorldSize : 0);
        Int128 otherPlane = Origin(b, axis) + ((face & 1) == 0 ? b.WorldSize : 0);
        if (plane != otherPlane) return false;
        for (int dimension = 0; dimension < 3; dimension++)
            if (dimension != axis && (Origin(a, dimension) >= Origin(b, dimension) + b.WorldSize ||
                Origin(b, dimension) >= Origin(a, dimension) + a.WorldSize)) return false;
        return true;
    }

    private static Int128 Origin(ChunkKey key, int axis) => axis == 0 ? key.OriginX : axis == 1 ? key.OriginY : key.OriginZ;

    private static bool Contains(ChunkKey key, long x, long y, long z) =>
        (Int128)x >= key.OriginX && (Int128)x < (Int128)key.OriginX + key.WorldSize &&
        (Int128)y >= key.OriginY && (Int128)y < (Int128)key.OriginY + key.WorldSize &&
        (Int128)z >= key.OriginZ && (Int128)z < (Int128)key.OriginZ + key.WorldSize;

    private static (long X, long Y, long Z) FacePoint(ChunkKey key, int face, int u, int v, int step)
    {
        int axis = face / 2;
        long normal = (face & 1) == 0 ? -1 : key.WorldSize;
        long uu = (long)u * step, vv = (long)v * step;
        return axis switch
        {
            0 => (checked(key.OriginX + normal), checked(key.OriginY + uu), checked(key.OriginZ + vv)),
            1 => (checked(key.OriginX + vv), checked(key.OriginY + normal), checked(key.OriginZ + uu)),
            _ => (checked(key.OriginX + uu), checked(key.OriginY + vv), checked(key.OriginZ + normal)),
        };
    }
}

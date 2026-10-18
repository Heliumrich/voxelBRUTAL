using System.Numerics;
using System.Runtime.InteropServices;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.Meshing;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Tests;

public static class MixedLodMeshingTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("mixed LOD meshes equal a closed uniformly expanded voxel-union oracle on every axis", AllAxisUnion);
        yield return ("mixed LOD edge and corner partitions preserve closed union after edits", CornerUnionAndEdits);
        yield return ("half-voxel quad format carries coordinate64 and extent64 without changing eight-byte stride", HalfGridPacking);
        yield return ("mixed LOD context validates topology balance, revisions, and cancellation", ContextValidation);
    }

    private static void AllAxisUnion()
    {
        for (int axis = 0; axis < 3; axis++)
        {
            ChunkKey coarse = new(-1, -1, -1, 1);
            ChunkKey adjacent = axis == 0 ? coarse.Neighbor(1, 0, 0) : axis == 1 ? coarse.Neighbor(0, 1, 0) : coarse.Neighbor(0, 0, 1);
            ChunkKey[] leaves = [coarse, .. adjacent.Children()];
            VerifyUnion(leaves, new PatternSource((ulong)(axis + 73)));
            // Reverse the fine/coarse orientation and translate to coordinates beyond exact float precision.
            long translation = (1L << 35) + 16;
            ChunkKey[] reversed = [new(adjacent.X + translation, adjacent.Y - translation, adjacent.Z, 1),
                .. new ChunkKey(coarse.X + translation, coarse.Y - translation, coarse.Z, 1).Children()];
            VerifyUnion(reversed, new PatternSource((ulong)(axis + 19)));
        }
    }

    private static void CornerUnionAndEdits()
    {
        ChunkKey[] roots = [new(-1, -1, -1, 1), new(0, -1, -1, 1), new(-1, 0, -1, 1), new(0, 0, -1, 1)];
        ChunkKey[] leaves = [roots[0], roots[1], roots[2], .. roots[3].Children()];
        var source = new EditedSource(new PatternSource(991));
        VerifyUnion(leaves, source);
        // All three axes touch a mixed partition here; edits flip both coarse and fine lattice samples.
        source.Edits[(-2, 0, -2)] = 0;
        source.Edits[(0, -2, -2)] = 5;
        source.Edits[(0, 0, -2)] = 6;
        source.Edits[(1, 0, -1)] = 0;
        VerifyUnion(leaves, source, revision: 7);
    }

    private static void HalfGridPacking()
    {
        Assert(Marshal.SizeOf<PackedQuad>() == 8, "Seam geometry changed the GPU stride.");
        foreach (FaceDirection face in Enum.GetValues<FaceDirection>())
        {
            int axis = (int)face / 2;
            var quad = new PackedQuad(axis == 0 ? 64 : 0, axis == 1 ? 64 : 0, axis == 2 ? 64 : 0,
                64, 64, face, 65535, halfVoxelLattice: true);
            Assert(quad.Width == 64 && quad.Height == 64 && quad.Material == 65535 && quad.UsesHalfVoxelLattice,
                "Half-grid extents/material failed to round trip.");
            Assert((axis == 0 ? quad.X : axis == 1 ? quad.Y : quad.Z) == 64, "Plane64 lost its carry bit.");
            Vector3 cross = Vector3.Cross(quad.GetCorner(1) - quad.GetCorner(0), quad.GetCorner(2) - quad.GetCorner(0));
            Vector3 normal = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            if (((int)face & 1) == 0) normal = -normal;
            Assert(Vector3.Dot(cross, normal) > 0, "Half-grid quad winding changed.");
            Assert(quad.GetCorner(2).X <= 32 && quad.GetCorner(2).Y <= 32 && quad.GetCorner(2).Z <= 32,
                "Decoded half-grid corner escaped the local chunk bounds.");
        }
    }

    private static void ContextValidation()
    {
        var key = new ChunkKey(-1, 0, 0, 1);
        var source = new PatternSource(4);
        ChunkKey[] leaves = [key, .. key.Neighbor(1, 0, 0).Children()];
        var context = LodMeshingContext.Capture(key, leaves, source, 2, 19);
        Throws<ArgumentException>(() => BinaryGreedyMesher.Build(ChunkSnapshot.Create(key, source, 3), context));
        ChunkMesh mesh = BinaryGreedyMesher.Build(ChunkSnapshot.Create(key, source, 2), context);
        Assert(mesh.TopologyStamp == 19 && mesh.Version == new ChunkMeshId(key, 2, 19), "Mesh lost its topology version.");
        Assert(mesh.Quads.ToArray().Any(quad => quad.UsesHalfVoxelLattice), "Coarse/fine boundary emitted no half-grid patch.");
        ChunkKey unbalanced = new(0, 0, 0, 2);
        Throws<ArgumentException>(() => LodMeshingContext.Capture(new(-1, 0, 0), [unbalanced], source));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => LodMeshingContext.Capture(key, leaves, source, cancellationToken: cancellation.Token));
    }

    private static void VerifyUnion(ChunkKey[] leaves, IVoxelSource source, ulong revision = 0)
    {
        int rootLod = leaves.Max(key => key.Lod);
        ChunkKey[] roots = leaves.Select(key =>
        {
            while (key.Lod < rootLod) key = key.Parent;
            return key;
        }).Distinct().ToArray();
        var plan = LodMeshPlan.Create(roots, leaves, _ => revision);
        int minimumScale = leaves.Min(key => key.VoxelScale);
        long ox = leaves.Min(key => key.OriginX), oy = leaves.Min(key => key.OriginY), oz = leaves.Min(key => key.OriginZ);
        int nx = checked((int)((leaves.Max(key => key.OriginX + key.WorldSize) - ox) / minimumScale));
        int ny = checked((int)((leaves.Max(key => key.OriginY + key.WorldSize) - oy) / minimumScale));
        int nz = checked((int)((leaves.Max(key => key.OriginZ + key.WorldSize) - oz) / minimumScale));
        var volume = new ushort[checked(nx * ny * nz)];
        var ownership = new bool[volume.Length];
        foreach (ChunkKey key in leaves)
        {
            int sx = checked((int)((key.OriginX - ox) / minimumScale));
            int sy = checked((int)((key.OriginY - oy) / minimumScale));
            int sz = checked((int)((key.OriginZ - oz) / minimumScale));
            int scale = key.VoxelScale / minimumScale;
            for (int z = 0; z < 32; z++)
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        ushort material = source.Sample(key.OriginX + (long)x * key.VoxelScale,
                            key.OriginY + (long)y * key.VoxelScale, key.OriginZ + (long)z * key.VoxelScale, key.Lod);
                        for (int dz = 0; dz < scale; dz++)
                            for (int dy = 0; dy < scale; dy++)
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    int index = sx + x * scale + dx + nx * (sy + y * scale + dy + ny * (sz + z * scale + dz));
                                    Assert(!ownership[index], "Oracle leaf cover overlaps.");
                                    ownership[index] = true;
                                    volume[index] = material;
                                }
                    }
        }
        var expected = new HashSet<UnitFace>();
        (int X, int Y, int Z)[] directions = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    ushort material = volume[x + nx * (y + ny * z)];
                    if (material == 0) continue;
                    for (int face = 0; face < 6; face++)
                    {
                        var d = directions[face];
                        int xx = x + d.X, yy = y + d.Y, zz = z + d.Z;
                        if ((uint)xx < nx && (uint)yy < ny && (uint)zz < nz && volume[xx + nx * (yy + ny * zz)] != 0) continue;
                        expected.Add(new(ox + (long)(x + (d.X == 1 ? 1 : 0)) * minimumScale,
                            oy + (long)(y + (d.Y == 1 ? 1 : 0)) * minimumScale,
                            oz + (long)(z + (d.Z == 1 ? 1 : 0)) * minimumScale, (FaceDirection)face, material));
                    }
                }

        var actual = new HashSet<UnitFace>();
        foreach (ChunkKey key in leaves)
        {
            ChunkSnapshot snapshot = ChunkSnapshot.Create(key, source, revision);
            var context = LodMeshingContext.Capture(key, plan, source, revision, 33);
            var referenceContext = LodMeshingContext.Capture(key, leaves, source, revision, 33);
            for (int face = 0; face < 6; face++)
            {
                int size = context.FaceResolution(face);
                Assert(size == referenceContext.FaceResolution(face), "Cached plan missed a neighboring face patch.");
                for (int v = 0; v < size; v++)
                    for (int u = 0; u < size; u++)
                        Assert(context.NeighborMaterial(face, u, v) == referenceContext.NeighborMaterial(face, u, v),
                            "Cached leaf lookup differs from brute-force face adjacency.");
            }
            ChunkMesh mesh = BinaryGreedyMesher.Build(snapshot, context);
            foreach (PackedQuad quad in mesh.Quads)
            {
                int worldStep = key.VoxelScale / (quad.UsesHalfVoxelLattice ? 2 : 1);
                Assert(worldStep >= minimumScale, "Quad uses a fractional base-world coordinate.");
                long qx = key.OriginX + (long)quad.X * worldStep;
                long qy = key.OriginY + (long)quad.Y * worldStep;
                long qz = key.OriginZ + (long)quad.Z * worldStep;
                int width = quad.Width * worldStep / minimumScale;
                int height = quad.Height * worldStep / minimumScale;
                int axis = (int)quad.Face / 2;
                for (int v = 0; v < height; v++)
                    for (int u = 0; u < width; u++)
                    {
                        var face = axis switch
                        {
                            0 => new UnitFace(qx, qy + (long)u * minimumScale, qz + (long)v * minimumScale, quad.Face, quad.Material),
                            1 => new UnitFace(qx + (long)v * minimumScale, qy, qz + (long)u * minimumScale, quad.Face, quad.Material),
                            _ => new UnitFace(qx + (long)u * minimumScale, qy + (long)v * minimumScale, qz, quad.Face, quad.Material),
                        };
                        Assert(actual.Add(face), "Mixed-LOD meshes emitted overlapping surface faces.");
                    }
            }
        }
        Assert(expected.SetEquals(actual), $"Mixed-LOD union surface mismatch: expected {expected.Count}, actual {actual.Count}.");
    }

    private sealed class PatternSource(ulong seed) : IVoxelSource
    {
        public ushort Sample(long x, long y, long z, int lod)
        {
            ulong hash = unchecked((ulong)ChunkKey.FloorDiv(x, 3) * 0x9E3779B185EBCA87UL ^
                (ulong)ChunkKey.FloorDiv(y, 3) * 0xC2B2AE3D27D4EB4FUL ^
                (ulong)ChunkKey.FloorDiv(z, 3) * 0x165667B19E3779F9UL ^ seed);
            hash ^= hash >> 23;
            return (hash & 3) == 0 ? (ushort)0 : (ushort)(1 + (hash >> 9) % 5);
        }
    }

    private sealed class EditedSource(IVoxelSource original) : IVoxelSource
    {
        public Dictionary<(long, long, long), ushort> Edits { get; } = [];
        public ushort Sample(long x, long y, long z, int lod) => Edits.TryGetValue((x, y, z), out ushort material)
            ? material : original.Sample(x, y, z, lod);
    }

    private readonly record struct UnitFace(long X, long Y, long Z, FaceDirection Face, ushort Material);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

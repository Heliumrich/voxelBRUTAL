using System.Numerics;
using System.Runtime.InteropServices;
using VoxelBrutal.Core.Meshing;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Tests;

public static class WorldMeshingTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("signed chunk coordinates floor correctly and reject overflow", Coordinates);
        yield return ("uniform chunks promote, compact, and revision edits", UniformStorage);
        yield return ("snapshots and meshes own immutable input copies", SnapshotOwnership);
        yield return ("empty, isolated solid, enclosed solid, and halo suppression", SimpleMeshes);
        yield return ("packed quads retain all fields and outward triangle winding", PackingAndWinding);
        yield return ("randomized binary meshing exactly matches scalar exposed-face oracle", RandomizedOracle);
        yield return ("material stripes, checkerboards, and cavities preserve face coverage", StructuredOracle);
        yield return ("terrain batch and scalar samples match across LODs and huge coordinates", TerrainBatch);
        yield return ("neighbor captures agree exactly at shared global samples", SharedSamples);
        yield return ("generation and meshing honor cancellation", Cancellation);
    }

    private static void Coordinates()
    {
        Assert(ChunkKey.FromWorld(-1, -32, -33) == new ChunkKey(-1, -1, -2), "Negative world coordinates truncated.");
        Assert(ChunkKey.FloorDiv(long.MinValue, 32) == long.MinValue / 32, "Minimum integer division failed.");
        Assert(ChunkKey.FloorMod(-1, 32) == 31 && ChunkKey.FloorMod(long.MinValue, 32) == 0, "Floor modulus failed.");
        var key = new ChunkKey(-3, 5, -7, 4);
        Assert(key.OriginX == -1536 && key.WorldSize == 512 && key.VoxelScale == 16, "Scale/origin contract changed.");
        foreach (ChunkKey child in key.Children())
            Assert(child.Parent == key, "Child-parent identity failed at negative coordinates.");
        Assert(key.Children().Distinct().Count() == 8, "Children must cover eight distinct octants.");
        Throws<ArgumentOutOfRangeException>(() => new ChunkKey(0, 0, 0, -1));
        Throws<ArgumentOutOfRangeException>(() => new ChunkKey(0, 0, 0, ChunkConstants.MaxLod + 1));
        Throws<OverflowException>(() => new ChunkKey(long.MaxValue, 0, 0));
        Throws<OverflowException>(() => ChunkSnapshot.Create(ChunkKey.FromWorld(long.MinValue, 0, 0), new ConstantSource(0)));
        Throws<InvalidOperationException>(() => new ChunkKey(0, 0, 0).Child(0));
        Throws<InvalidOperationException>(() => _ = new ChunkKey(0, 0, 0, ChunkConstants.MaxLod).Parent);
    }

    private static void UniformStorage()
    {
        var chunk = new ChunkData(new(-1, 0, 0), 7);
        Assert(chunk.IsUniform && chunk.AllocatedBytes == 0 && chunk.Revision == 0, "Uniform chunk allocated cells.");
        Assert(!chunk.Set(0, 0, 0, 7) && chunk.Revision == 0, "No-op edit changed revision.");
        Assert(chunk.Set(1, 2, 3, 4) && !chunk.IsUniform && chunk.Revision == 1, "Dense promotion failed.");
        Assert(chunk[0, 0, 0] == 7 && chunk[1, 2, 3] == 4, "Dense promotion lost uniform contents.");
        Assert(!chunk.TryCompact(), "Nonuniform chunk compacted.");
        chunk.Set(1, 2, 3, 7);
        Assert(chunk.TryCompact() && chunk.IsUniform && chunk.UniformMaterial == 7 && chunk.Revision == 2,
            "Uniform compaction changed contents or revision.");
        chunk.Fill(0);
        Assert(chunk.IsUniform && chunk.AllocatedBytes == 0 && chunk.Revision == 3, "Fill must release dense storage.");
        chunk.Set(0, 0, 0, 1);
        chunk.Set(0, 0, 0, 0);
        ulong unchangedRevision = chunk.Revision;
        Assert(!chunk.Fill(0) && chunk.Revision == unchangedRevision && chunk.IsUniform,
            "Fill of already matching dense data must only compact, without a content revision.");
        var maximumRevision = new ChunkData(new(), revision: ulong.MaxValue);
        Throws<OverflowException>(() => maximumRevision.Set(0, 0, 0, 1));
        Assert(maximumRevision[0, 0, 0] == 0 && maximumRevision.IsUniform, "Revision overflow partially mutated data.");
    }

    private static void SnapshotOwnership()
    {
        var cells = new ushort[ChunkConstants.HaloVolume];
        cells[ChunkConstants.HaloIndex(1, 2, 3)] = 9;
        ChunkSnapshot snapshot = ChunkSnapshot.FromCells(new(), cells, 12);
        Array.Clear(cells);
        Assert(snapshot[1, 2, 3] == 9 && snapshot.Revision == 12, "Snapshot retained mutable caller storage.");
        var quads = new[] { new PackedQuad(0, 0, 0, 1, 1, FaceDirection.NegativeX, 1) };
        var mesh = new ChunkMesh(new(), 9, quads);
        quads[0] = default;
        Assert(mesh.Quads[0].Material == 1 && mesh.VisibleUnitFaceCount == 1, "Mesh retained mutable caller storage.");
        var chunk = new ChunkData(new(), 3);
        ChunkSnapshot owned = ChunkSnapshot.FromChunk(chunk, new ConstantSource(0));
        chunk.Set(0, 0, 0, 0);
        Assert(owned[0, 0, 0] == 3 && owned[-1, 0, 0] == 0, "Edited capture shared interior storage or lost halo.");
    }

    private static void SimpleMeshes()
    {
        ChunkMesh empty = BinaryGreedyMesher.Build(ChunkSnapshot.Create(new(), new ConstantSource(0)));
        Assert(empty.QuadCount == 0, "Air emitted geometry.");
        ChunkMesh enclosed = BinaryGreedyMesher.Build(ChunkSnapshot.Create(new(), new ConstantSource(2)));
        Assert(enclosed.QuadCount == 0, "Opaque halo must suppress every face.");
        var cells = new ushort[ChunkConstants.HaloVolume];
        for (int z = 0; z < 32; z++)
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    cells[ChunkConstants.HaloIndex(x, y, z)] = 7;
        ChunkMesh solid = CheckOracle(ChunkSnapshot.FromCells(new(-1, -2, -3, 4), cells));
        Assert(solid.QuadCount == 6 && solid.VisibleUnitFaceCount == 6 * 32 * 32, "Solid cube should merge into six quads.");
        for (int z = 0; z < 32; z++)
            for (int y = 0; y < 32; y++)
                cells[ChunkConstants.HaloIndex(32, y, z)] = 2;
        ChunkMesh suppressed = CheckOracle(ChunkSnapshot.FromCells(new(), cells));
        Assert(suppressed.QuadCount == 5 && suppressed.Quads.ToArray().All(q => q.Face != FaceDirection.PositiveX),
            "A different opaque neighbor material must suppress the shared face.");
    }

    private static void PackingAndWinding()
    {
        Assert(Marshal.SizeOf<PackedQuad>() == 8, "GPU quad stride must be eight bytes.");
        foreach (FaceDirection face in Enum.GetValues<FaceDirection>())
        {
            int axis = (int)face / 2;
            var quad = new PackedQuad(axis == 0 ? 32 : 0, axis == 1 ? 32 : 0, axis == 2 ? 32 : 0,
                32, 32, face, ushort.MaxValue);
            Assert(quad.Width == 32 && quad.Height == 32 && quad.Face == face && quad.Material == ushort.MaxValue,
                "Bit packing lost fields.");
            Vector3 normal = Vector3.Cross(quad.GetCorner(1) - quad.GetCorner(0), quad.GetCorner(2) - quad.GetCorner(0));
            float expectedSign = ((int)face & 1) == 0 ? -1 : 1;
            Vector3 expected = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            Assert(Vector3.Dot(normal, expected * expectedSign) > 0, "Quad triangle winding faces inward.");
        }
        Throws<ArgumentOutOfRangeException>(() => new PackedQuad(0, 0, 0, 33, 1, FaceDirection.PositiveX, 1));
        Throws<ArgumentOutOfRangeException>(() => new PackedQuad(0, 31, 0, 2, 1, FaceDirection.PositiveX, 1));
    }

    private static void RandomizedOracle()
    {
        for (int seed = 0; seed < 8; seed++)
        {
            var random = new Random(711 + seed);
            var cells = new ushort[ChunkConstants.HaloVolume];
            int opaquePercent = seed % 2 == 0 ? 25 : 80;
            for (int i = 0; i < cells.Length; i++)
                cells[i] = random.Next(100) < opaquePercent ? (ushort)random.Next(1, 5) : (ushort)0;
            CheckOracle(ChunkSnapshot.FromCells(new(-seed, seed - 4, -7, seed % 4), cells, (ulong)seed));
        }
    }

    private static void StructuredOracle()
    {
        for (int pattern = 0; pattern < 3; pattern++)
        {
            var cells = new ushort[ChunkConstants.HaloVolume];
            for (int z = -1; z <= 32; z++)
                for (int y = -1; y <= 32; y++)
                    for (int x = -1; x <= 32; x++)
                    {
                        bool inside = (uint)x < 32 && (uint)y < 32 && (uint)z < 32;
                        if (!inside) continue;
                        ushort material = pattern switch
                        {
                            0 => (ushort)(x / 4 + 1),
                            1 => (ushort)(((x + y + z) & 1) == 0 ? 3 : 0),
                            _ => (ushort)(x > 7 && x < 24 && y > 7 && y < 24 && z > 7 && z < 24 ? 0 : 5),
                        };
                        cells[ChunkConstants.HaloIndex(x, y, z)] = material;
                    }
            CheckOracle(ChunkSnapshot.FromCells(new(), cells));
        }
    }

    private static void TerrainBatch()
    {
        var terrain = new TerrainGenerator(991);
        var repeated = new TerrainGenerator(991);
        foreach (ChunkKey key in new[] { new ChunkKey(-3, 1, -4), new(2, 0, -1, 3),
                     new(-123456, 0, 987654, 20), new(1L << 48, 1, -(1L << 48)) })
        {
            ChunkSnapshot snapshot = ChunkSnapshot.Create(key, terrain);
            ChunkData data = ChunkData.Generate(key, terrain);
            for (int z = -1; z <= 32; z++)
                for (int y = -1; y <= 32; y++)
                    for (int x = -1; x <= 32; x++)
                    {
                        long wx = key.OriginX + (long)x * key.VoxelScale;
                        long wy = key.OriginY + (long)y * key.VoxelScale;
                        long wz = key.OriginZ + (long)z * key.VoxelScale;
                        Assert(snapshot[x, y, z] == terrain.Sample(wx, wy, wz, key.Lod), "Batch and scalar terrain differ.");
                        if ((uint)x < 32 && (uint)y < 32 && (uint)z < 32)
                            Assert(data[x, y, z] == snapshot[x, y, z], "Dense batch generation and snapshot disagree.");
                    }
            Assert(terrain.HeightAt(key.OriginX, key.OriginZ) == repeated.HeightAt(key.OriginX, key.OriginZ),
                "Terrain depends on instance state.");
        }
        Assert(TerrainGenerator.MaterialAtHeight(long.MinValue, long.MaxValue) == TerrainGenerator.Stone,
            "Extreme height distance overflowed.");
    }

    private static void SharedSamples()
    {
        var terrain = new TerrainGenerator();
        foreach (int lod in new[] { 0, 3, 10 })
        {
            ChunkKey key = new(-1, 0, 2, lod);
            ChunkSnapshot a = ChunkSnapshot.Create(key, terrain);
            ChunkSnapshot b = ChunkSnapshot.Create(key.Neighbor(1, 0, 0), terrain);
            for (int z = -1; z <= 32; z++)
                for (int y = -1; y <= 32; y++)
                {
                    Assert(a[32, y, z] == b[0, y, z], "Positive halo disagrees with neighboring interior.");
                    Assert(a[31, y, z] == b[-1, y, z], "Neighbor halo disagrees with interior.");
                }
        }
    }

    private static void Cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Throws<OperationCanceledException>(() => ChunkSnapshot.Create(new(), new TerrainGenerator(), cancellationToken: source.Token));
        Throws<OperationCanceledException>(() => ChunkData.Generate(new(), new ConstantSource(0), cancellationToken: source.Token));
        ChunkSnapshot snapshot = ChunkSnapshot.Create(new(), new ConstantSource(0));
        Throws<OperationCanceledException>(() => BinaryGreedyMesher.Build(snapshot, source.Token));
    }

    private static ChunkMesh CheckOracle(ChunkSnapshot snapshot)
    {
        var expected = new HashSet<UnitFace>();
        (int X, int Y, int Z)[] directions = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];
        for (int z = 0; z < 32; z++)
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    ushort material = snapshot[x, y, z];
                    if (material == 0) continue;
                    for (int face = 0; face < 6; face++)
                    {
                        var d = directions[face];
                        if (snapshot[x + d.X, y + d.Y, z + d.Z] != 0) continue;
                        expected.Add(new(x + (d.X == 1 ? 1 : 0), y + (d.Y == 1 ? 1 : 0),
                            z + (d.Z == 1 ? 1 : 0), (FaceDirection)face, material));
                    }
                }
        ChunkMesh mesh = BinaryGreedyMesher.Build(snapshot);
        var actual = new HashSet<UnitFace>();
        foreach (PackedQuad quad in mesh.Quads)
        {
            int axis = (int)quad.Face / 2;
            for (int v = 0; v < quad.Height; v++)
                for (int u = 0; u < quad.Width; u++)
                {
                    var face = axis switch
                    {
                        0 => new UnitFace(quad.X, quad.Y + u, quad.Z + v, quad.Face, quad.Material),
                        1 => new UnitFace(quad.X + v, quad.Y, quad.Z + u, quad.Face, quad.Material),
                        _ => new UnitFace(quad.X + u, quad.Y + v, quad.Z, quad.Face, quad.Material),
                    };
                    Assert(actual.Add(face), "Mesher emitted overlapping unit faces.");
                }
        }
        Assert(expected.SetEquals(actual), $"Face coverage mismatch: expected {expected.Count}, actual {actual.Count}.");
        Assert(mesh.Key == snapshot.Key && mesh.Revision == snapshot.Revision, "Mesh lost world address or revision.");
        Assert(mesh.VisibleUnitFaceCount == expected.Count, "Visibility statistics disagree with scalar oracle.");
        return mesh;
    }

    private readonly record struct UnitFace(int X, int Y, int Z, FaceDirection Face, ushort Material);
    private sealed class ConstantSource(ushort material) : IVoxelSource
    {
        public ushort Sample(long x, long y, long z, int lod) => material;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

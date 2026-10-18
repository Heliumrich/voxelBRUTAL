using VoxelBrutal.Core.World;

namespace VoxelBrutal.Tests;

public static class TerrainGenerationTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("terrain settings bound the generated domain and reject invalid ranges", Bounds);
        yield return ("raising terrain to4096 keeps cubic sparse chunk allocation independent of column height", TallWorld);
        yield return ("three-dimensional caves coexist with solid terrain at the same height", Caves);
        yield return ("biome weights stay normalized and continuous across chunk boundaries", Biomes);
        yield return ("cave-free terrain fills every voxel below the height field", NoCaves);
        yield return ("terrain settings reproduce scalar and batched generation deterministically", Determinism);
    }

    private static void Bounds()
    {
        var terrain = new TerrainGenerator();
        Assert(terrain.Settings.MinimumY == -128 && terrain.Settings.MaximumYExclusive == 512, "Default vertical domain changed.");
        foreach (long x in new long[] { -4097, -32, -1, 0, 31, 32, 4097, 1L << 60 })
            foreach (long z in new long[] { -4097, -1, 0, 32, (1L << 60) + 17 })
            {
                long height = terrain.HeightAt(x, z);
                Assert(height >= -104 && height <= 496, "Height exceeded generation caps.");
                Assert(terrain.Sample(x, -129, z, 0) == 0 && terrain.Sample(x, 512, z, 0) == 0,
                    "Generator emitted voxels outside its vertical domain.");
                Assert(terrain.Sample(x, long.MinValue, z, 0) == 0 && terrain.Sample(x, long.MaxValue, z, 0) == 0,
                    "Extreme vertical coordinate overflowed or emitted material.");
            }
        Throws<ArgumentOutOfRangeException>(() => new TerrainGenerator(settings: new() { MaximumYExclusive = -100 }));
        Throws<ArgumentOutOfRangeException>(() => new TerrainGenerator(settings: new() { MaximumYExclusive = 1_048_576 }));
        Throws<ArgumentOutOfRangeException>(() => new TerrainGenerator(settings: new() { BaseHeight = double.NaN }));
        Throws<ArgumentOutOfRangeException>(() => new TerrainGenerator(settings: new() { HillsAmplitude = -1 }));
    }

    private static void TallWorld()
    {
        var settings = new TerrainSettings
        {
            MinimumY = -128, MaximumYExclusive = 4096, BaseHeight = 3000,
            EnableCaves = false, EnableBiomes = false,
        };
        var terrain = new TerrainGenerator(991, settings);
        long height = terrain.HeightAt(-17, 53);
        Assert(height > 512 && height < 4096, "Raising terrain settings failed to generate high terrain.");
        ChunkData lower = ChunkData.Generate(new(-1, 8, 0), terrain);
        ChunkData upper = ChunkData.Generate(new(-1, 127, 0), terrain);
        Assert(lower.IsUniform && lower.UniformMaterial == TerrainGenerator.Stone && lower.AllocatedBytes == 0,
            "Uniform deep terrain allocated a dense vertical column.");
        Assert(upper.IsUniform && upper.UniformMaterial == 0 && upper.AllocatedBytes == 0,
            "Uniform high sky allocated a dense vertical column.");
        ChunkSnapshot snapshot = ChunkSnapshot.Create(new(-1, 93, 0), terrain);
        Assert(snapshot.AllocatedBytes == ChunkConstants.HaloVolume * sizeof(ushort), "Snapshot size depends on world height.");
    }

    private static void Caves()
    {
        var terrain = new TerrainGenerator(7123);
        bool airAtHeight = false, solidAtHeight = false, verticalTransition = false;
        const long height = -64;
        for (long z = -160; z <= 160; z += 8)
            for (long x = -160; x <= 160; x += 8)
            {
                if (terrain.HeightAt(x, z) < height + 32) continue;
                bool solid = terrain.Sample(x, height, z, 0) != 0;
                airAtHeight |= !solid;
                solidAtHeight |= solid;
                // The column stays under its surface; variation in Y is therefore a 3D cave field.
                if (solid != (terrain.Sample(x, height + 8, z, 0) != 0)) verticalTransition = true;
            }
        Assert(airAtHeight && solidAtHeight, "Caves did not coexist with solid terrain on a horizontal slice.");
        Assert(verticalTransition, "The cave field behaves like an extruded 2D hole mask.");
        for (int y = -128; y < -120; y++)
            Assert(terrain.Sample(0, y, 0, 0) != 0, "Caves pierced the protected bottom layers.");
    }

    private static void Biomes()
    {
        var terrain = new TerrainGenerator(411);
        foreach (long offset in new[] { 0L, 1L << 40 })
            for (long z = -1024; z <= 1024; z += 31)
                for (long boundary = -1024; boundary <= 1024; boundary += 32)
                {
                    BiomeWeights a = terrain.BiomesAt(offset + boundary - 1, z);
                    BiomeWeights b = terrain.BiomesAt(offset + boundary, z);
                    ValidateWeights(a); ValidateWeights(b);
                    Assert(Math.Abs(a.Temperate - b.Temperate) <= .1 && Math.Abs(a.Arid - b.Arid) <= .1 &&
                        Math.Abs(a.Alpine - b.Alpine) <= .1, "Biome weights jumped across a chunk boundary.");
                }
        var disabled = new TerrainGenerator(settings: new() { EnableBiomes = false });
        Assert(disabled.BiomesAt(-213, 951) == new BiomeWeights(1, 0, 0), "Disabling biomes did not select temperate weights.");
    }

    private static void NoCaves()
    {
        var terrain = new TerrainGenerator(991, new() { EnableCaves = false });
        for (long z = -64; z <= 64; z += 13)
            for (long x = -64; x <= 64; x += 11)
            {
                long height = terrain.HeightAt(x, z);
                for (long y = terrain.Settings.MinimumY; y <= height; y++)
                    Assert(terrain.Sample(x, y, z, 0) != 0, "Cave-free terrain contains an underground hole.");
                Assert(terrain.Sample(x, height + 1, z, 0) == 0, "Height field has an unexpected solid voxel above its surface.");
            }
    }

    private static void Determinism()
    {
        var settings = new TerrainSettings { MaximumYExclusive = 4096, BaseHeight = 1711, HillsAmplitude = 103 };
        var a = new TerrainGenerator(912345, settings);
        var b = new TerrainGenerator(912345, settings with { });
        foreach (ChunkKey key in new[] { new ChunkKey(-2, 51, 3), new(2, 7, -2, 3), new(1L << 45, 0, -(1L << 45), 4) })
        {
            ChunkSnapshot first = ChunkSnapshot.Create(key, a);
            ChunkSnapshot second = ChunkSnapshot.Create(key, b);
            Assert(first.Cells.SequenceEqual(second.Cells), "Identical seed/settings produced different snapshots.");
            for (int z = -1; z <= 32; z += 3)
                for (int y = -1; y <= 32; y += 3)
                    for (int x = -1; x <= 32; x += 3)
                        Assert(first[x, y, z] == a.Sample(key.OriginX + (long)x * key.VoxelScale,
                            key.OriginY + (long)y * key.VoxelScale, key.OriginZ + (long)z * key.VoxelScale, key.Lod),
                            "Configured batched terrain differs from scalar generation.");
        }
    }

    private static void ValidateWeights(BiomeWeights weights)
    {
        Assert(double.IsFinite(weights.Temperate) && double.IsFinite(weights.Arid) && double.IsFinite(weights.Alpine), "Biome weights are not finite.");
        Assert(weights.Temperate >= -1e-12 && weights.Arid >= 0 && weights.Alpine >= 0 &&
            weights.Temperate <= 1 && weights.Arid <= 1 && weights.Alpine <= 1, "Biome weights are not probabilities.");
        Assert(Math.Abs(weights.Temperate + weights.Arid + weights.Alpine - 1) < 1e-12, "Biome weights do not sum to one.");
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

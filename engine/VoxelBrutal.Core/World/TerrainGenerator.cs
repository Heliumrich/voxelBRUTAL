namespace VoxelBrutal.Core.World;

/// <summary>
/// Globally continuous climate/height and 3D cave fields. Integer lattice noise avoids large-world
/// coordinate cancellation. Coarse cells point-sample the same field; small features can disappear.
/// </summary>
public sealed class TerrainGenerator : IVoxelBatchSource
{
    public const ushort Air = 0, Stone = 1, Dirt = 2, Grass = 3, Sand = 4, Snow = 5, Moss = 6, Limestone = 7;
    public ulong Seed { get; }
    public TerrainSettings Settings { get; }

    public TerrainGenerator(ulong seed = 0x42525554414CUL, TerrainSettings? settings = null)
    {
        Seed = seed;
        Settings = settings ?? new();
        Settings.Validate();
    }

    public ushort Sample(long x, long y, long z, int lod)
    {
        ChunkKey.ValidateLod(lod);
        if (y < Settings.MinimumY || y >= Settings.MaximumYExclusive) return Air;
        Column column = DescribeColumn(x, z);
        var tunnels = new NoiseColumn(this, x, z, 32, 17);
        var chambers = new NoiseColumn(this, x, z, 64, 23);
        return SampleColumn(y, column, ref tunnels, ref chambers);
    }

    public long HeightAt(long x, long z)
    {
        double continental = Noise(x, z, 512, 0), hills = Noise(x, z, 128, 1);
        double detail = Noise(x, z, 32, 2), fine = Noise(x, z, 8, 3);
        return (long)Math.Floor(Math.Clamp(Settings.BaseHeight + continental * Settings.ContinentalAmplitude +
            hills * Settings.HillsAmplitude + detail * 18 + fine * 4, Settings.MinimumY + 24, Settings.MaximumYExclusive - 16));
    }

    public BiomeWeights BiomesAt(long x, long z) => Weights(x, z, HeightAt(x, z));

    private BiomeWeights Weights(long x, long z, long height)
    {
        if (!Settings.EnableBiomes) return new(1, 0, 0);
        double temperature = .5 + .5 * Noise(x, z, 1024, 8), moisture = .5 + .5 * Noise(x, z, 512, 9);
        double alpine = Smooth(210, 340, height);
        double arid = Smooth(.45, .8, temperature) * (1 - moisture) * (1 - alpine);
        return new(1 - arid - alpine, arid, alpine);
    }

    private Column DescribeColumn(long x, long z)
    {
        long height = HeightAt(x, z);
        BiomeWeights weights = Weights(x, z, height);
        // Material mixture spans a broad climate transition, rather than chunk-sized biome walls.
        double choice = .5 + .5 * Hash(x, 0, z, 31);
        ushort surface = choice < weights.Alpine ? Snow : choice < weights.Alpine + weights.Arid ? Sand : Grass;
        double caveWidth = .10 + weights.Temperate * .065 + weights.Arid * .025;
        return new(height, weights, surface, caveWidth, Smooth(.55, .8, Noise(x, z, 128, 29)));
    }

    private ushort SampleColumn(long y, Column column, ref NoiseColumn tunnels, ref NoiseColumn chambers)
    {
        if (y < Settings.MinimumY || y >= Settings.MaximumYExclusive || y > column.Height) return Air;
        long depth = column.Height - y;
        if (Settings.EnableCaves && y >= (long)Settings.MinimumY + 8)
        {
            double roof = Smooth(2, 16, depth);
            roof += column.Entrance * (1 - roof);
            double tunnel = Math.Abs(tunnels.Sample(y)), chamber = chambers.Sample(y);
            if (tunnel < column.CaveWidth * roof || chamber > .56 + .15 * (1 - roof)) return Air;
            if (depth > 5 && tunnel < column.CaveWidth * roof + .025)
                return column.Weights.Arid > .3 ? Limestone : column.Weights.Temperate > .5 ? Moss : Stone;
        }
        if (depth <= 1) return column.Surface;
        if (depth <= 5) return column.Surface == Sand ? Sand : Dirt;
        return column.Weights.Arid > .45 ? Limestone : Stone;
    }

    public static ushort MaterialAtHeight(long y, long surface)
    {
        if (y > surface) return Air;
        ulong depth = unchecked((ulong)surface - (ulong)y);
        if (depth <= 1) return surface > 115 ? Snow : surface < 34 ? Sand : Grass;
        return depth <= 5 ? Dirt : Stone;
    }

    public void FillChunkHalo(ChunkKey key, Span<ushort> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length != ChunkConstants.HaloVolume) throw new ArgumentException("Expected 34 cubed cells.", nameof(destination));
        long ox = key.OriginX, oy = key.OriginY, oz = key.OriginZ;
        int scale = key.VoxelScale;
        cancellationToken.ThrowIfCancellationRequested();
        if (checked(oy + 32L * scale) < Settings.MinimumY || checked(oy - scale) >= Settings.MaximumYExclusive)
        { destination.Clear(); return; }
        for (int z = -1; z <= 32; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long wz = checked(oz + (long)z * scale);
            for (int x = -1; x <= 32; x++)
            {
                long wx = checked(ox + (long)x * scale);
                Column column = DescribeColumn(wx, wz);
                var tunnels = new NoiseColumn(this, wx, wz, 32, 17);
                var chambers = new NoiseColumn(this, wx, wz, 64, 23);
                for (int y = -1; y <= 32; y++)
                    destination[ChunkConstants.HaloIndex(x, y, z)] = SampleColumn(checked(oy + (long)y * scale), column, ref tunnels, ref chambers);
            }
        }
    }

    private readonly record struct Column(long Height, BiomeWeights Weights, ushort Surface, double CaveWidth, double Entrance);

    // Reuse the two Y lattice planes while walking a column. The scalar path uses the same arithmetic.
    private struct NoiseColumn
    {
        private readonly TerrainGenerator owner;
        private readonly long x, z;
        private readonly int period;
        private readonly ulong octave;
        private readonly double tx, tz;
        private long previousY;
        private double lower, upper;
        private bool ready;

        public NoiseColumn(TerrainGenerator owner, long wx, long wz, int period, ulong octave)
        {
            this.owner = owner; this.period = period; this.octave = octave;
            x = ChunkKey.FloorDiv(wx, period); z = ChunkKey.FloorDiv(wz, period);
            tx = Fade(ChunkKey.FloorMod(wx, period) / (double)period);
            tz = Fade(ChunkKey.FloorMod(wz, period) / (double)period);
        }
        public double Sample(long wy)
        {
            long y = ChunkKey.FloorDiv(wy, period);
            if (!ready || y != previousY) { lower = Plane(y); upper = Plane(y + 1); previousY = y; ready = true; }
            return Lerp(lower, upper, Fade(ChunkKey.FloorMod(wy, period) / (double)period));
        }
        private double Plane(long y) => Lerp(Lerp(owner.Hash(x, y, z, octave), owner.Hash(x + 1, y, z, octave), tx),
            Lerp(owner.Hash(x, y, z + 1, octave), owner.Hash(x + 1, y, z + 1, octave), tx), tz);
    }

    private double Noise(long x, long z, int period, ulong octave)
    {
        long lx = ChunkKey.FloorDiv(x, period), lz = ChunkKey.FloorDiv(z, period);
        double tx = Fade(ChunkKey.FloorMod(x, period) / (double)period), tz = Fade(ChunkKey.FloorMod(z, period) / (double)period);
        return Lerp(Lerp(Hash(lx, 0, lz, octave), Hash(lx + 1, 0, lz, octave), tx),
            Lerp(Hash(lx, 0, lz + 1, octave), Hash(lx + 1, 0, lz + 1, octave), tx), tz);
    }
    private double Hash(long x, long y, long z, ulong octave)
    {
        ulong bits = unchecked((ulong)x * 0x9E3779B185EBCA87UL ^ (ulong)z * 0xC2B2AE3D27D4EB4FUL ^
            (ulong)y * 0xD6E8FEB86659FD93UL ^ Seed ^ octave * 0x165667B19E3779F9UL);
        bits ^= bits >> 30; bits = unchecked(bits * 0xBF58476D1CE4E5B9UL);
        bits ^= bits >> 27; bits = unchecked(bits * 0x94D049BB133111EBUL); bits ^= bits >> 31;
        return (bits >> 11) * (2.0 / (1UL << 53)) - 1;
    }
    private static double Fade(double t) => t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Smooth(double lo, double hi, double value) => Fade(Math.Clamp((value - lo) / (hi - lo), 0, 1));
}

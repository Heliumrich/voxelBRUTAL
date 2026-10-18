namespace VoxelBrutal.Core.World;

/// <summary>
/// Stateless integer-addressed height terrain for reproducible streaming. LOD uses samples of the
/// same global height field, rather than independent chunk-local noise. This is an explicit point
/// sampling baseline, not a claim of topology-preserving downsampling or mixed-LOD seam stitching.
/// </summary>
public sealed class TerrainGenerator(ulong seed = 0x42525554414CUL) : IVoxelBatchSource
{
    public const ushort Air = 0;
    public const ushort Stone = 1;
    public const ushort Dirt = 2;
    public const ushort Grass = 3;
    public const ushort Sand = 4;
    public const ushort Snow = 5;
    public ulong Seed { get; } = seed;

    public ushort Sample(long x, long y, long z, int lod)
    {
        ChunkKey.ValidateLod(lod);
        long surface = HeightAt(x, z);
        return MaterialAtHeight(y, surface);
    }

    public static ushort MaterialAtHeight(long y, long surface)
    {
        if (y > surface)
            return Air;
        // Unsigned subtraction represents the nonnegative mathematical distance even across signed zero.
        ulong depth = unchecked((ulong)surface - (ulong)y);
        if (depth <= 1)
            return surface > 115 ? Snow : surface < 34 ? Sand : Grass;
        if (depth <= 5)
            return Dirt;
        return Stone;
    }

    public void FillChunkHalo(ChunkKey key, Span<ushort> destination,
        CancellationToken cancellationToken = default)
    {
        if (destination.Length != ChunkConstants.HaloVolume)
            throw new ArgumentException("Expected exactly 34 cubed cells.", nameof(destination));
        long ox = key.OriginX, oy = key.OriginY, oz = key.OriginZ;
        int scale = key.VoxelScale;
        for (int z = -1; z <= ChunkConstants.Size; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long wz = checked(oz + (long)z * scale);
            for (int x = -1; x <= ChunkConstants.Size; x++)
            {
                long wx = checked(ox + (long)x * scale);
                long surface = HeightAt(wx, wz);
                for (int y = -1; y <= ChunkConstants.Size; y++)
                    destination[ChunkConstants.HaloIndex(x, y, z)] =
                        MaterialAtHeight(checked(oy + (long)y * scale), surface);
            }
        }
    }

    public long HeightAt(long x, long z)
    {
        double continental = Noise(x, z, 512, 0);
        double hills = Noise(x, z, 128, 1);
        double detail = Noise(x, z, 32, 2);
        double fine = Noise(x, z, 8, 3);
        return (long)Math.Floor(58 + continental * 54 + hills * 24 + detail * 8 + fine * 2);
    }

    private double Noise(long x, long z, int period, ulong octave)
    {
        long lx = ChunkKey.FloorDiv(x, period), lz = ChunkKey.FloorDiv(z, period);
        double tx = ChunkKey.FloorMod(x, period) / (double)period;
        double tz = ChunkKey.FloorMod(z, period) / (double)period;
        tx = tx * tx * (3 - 2 * tx);
        tz = tz * tz * (3 - 2 * tz);
        double a = Lerp(Hash(lx, lz, octave), Hash(lx + 1, lz, octave), tx);
        double b = Lerp(Hash(lx, lz + 1, octave), Hash(lx + 1, lz + 1, octave), tx);
        return Lerp(a, b, tz);
    }

    private double Hash(long x, long z, ulong octave)
    {
        ulong bits = unchecked((ulong)x * 0x9E3779B185EBCA87UL ^ (ulong)z * 0xC2B2AE3D27D4EB4FUL ^
            Seed ^ octave * 0x165667B19E3779F9UL);
        bits ^= bits >> 30;
        bits = unchecked(bits * 0xBF58476D1CE4E5B9UL);
        bits ^= bits >> 27;
        bits = unchecked(bits * 0x94D049BB133111EBUL);
        bits ^= bits >> 31;
        return (bits >> 11) * (2.0 / (1UL << 53)) - 1;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

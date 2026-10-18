namespace VoxelBrutal.Core.World;

/// <summary>Signed chunk-grid address at a particular resolution, independent of the render origin.</summary>
public readonly record struct ChunkKey
{
    public long X { get; }
    public long Y { get; }
    public long Z { get; }
    public int Lod { get; }
    public int VoxelScale => 1 << Lod;
    public long WorldSize => (long)ChunkConstants.Size << Lod;
    public long OriginX => checked(X * WorldSize);
    public long OriginY => checked(Y * WorldSize);
    public long OriginZ => checked(Z * WorldSize);

    public ChunkKey(long x, long y, long z, int lod = 0)
    {
        ValidateLod(lod);
        long size = (long)ChunkConstants.Size << lod;
        _ = checked(x * size);
        _ = checked(y * size);
        _ = checked(z * size);
        X = x;
        Y = y;
        Z = z;
        Lod = lod;
    }

    public ChunkKey Parent => Lod == ChunkConstants.MaxLod
        ? throw new InvalidOperationException("The maximum LOD has no representable parent.")
        : new(FloorDiv(X, 2), FloorDiv(Y, 2), FloorDiv(Z, 2), Lod + 1);

    /// <summary>Child bits are X=bit 0, Y=bit 1, Z=bit 2.</summary>
    public ChunkKey Child(int index)
    {
        if (Lod == 0)
            throw new InvalidOperationException("LOD zero has no finer child.");
        if ((uint)index >= 8)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new(checked(X * 2 + (index & 1)), checked(Y * 2 + ((index >> 1) & 1)),
            checked(Z * 2 + ((index >> 2) & 1)), Lod - 1);
    }

    public IEnumerable<ChunkKey> Children()
    {
        for (int i = 0; i < 8; i++)
            yield return Child(i);
    }

    public ChunkKey Neighbor(int dx, int dy, int dz) =>
        new(checked(X + dx), checked(Y + dy), checked(Z + dz), Lod);

    public static ChunkKey FromWorld(long x, long y, long z, int lod = 0)
    {
        ValidateLod(lod);
        long size = (long)ChunkConstants.Size << lod;
        return new(FloorDiv(x, size), FloorDiv(y, size), FloorDiv(z, size), lod);
    }

    /// <summary>Division toward negative infinity, including long.MinValue without negating it.</summary>
    public static long FloorDiv(long value, long positiveDivisor)
    {
        if (positiveDivisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(positiveDivisor));
        long quotient = value / positiveDivisor;
        return value % positiveDivisor < 0 ? quotient - 1 : quotient;
    }

    public static int FloorMod(long value, int positiveDivisor)
    {
        if (positiveDivisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(positiveDivisor));
        long result = value % positiveDivisor;
        return (int)(result < 0 ? result + positiveDivisor : result);
    }

    public static void ValidateLod(int lod)
    {
        if ((uint)lod > ChunkConstants.MaxLod)
            throw new ArgumentOutOfRangeException(nameof(lod));
    }
}

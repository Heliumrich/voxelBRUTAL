namespace VoxelBrutal.Core.World;

/// <summary>Interior cells are 32 cubed; mesh jobs also capture one cell of halo on every side.</summary>
public static class ChunkConstants
{
    public const int SizeLog2 = 5;
    public const int Size = 1 << SizeLog2;
    public const int Volume = Size * Size * Size;
    public const int HaloSize = Size + 2;
    public const int HaloVolume = HaloSize * HaloSize * HaloSize;
    public const int MaxLod = 20;

    public static int Index(int x, int y, int z)
    {
        if ((uint)x >= Size || (uint)y >= Size || (uint)z >= Size)
            throw new ArgumentOutOfRangeException(nameof(x), "Interior coordinates must be in [0, 31].");
        return x + Size * (y + Size * z);
    }

    public static int HaloIndex(int x, int y, int z)
    {
        if ((uint)(x + 1) >= HaloSize || (uint)(y + 1) >= HaloSize || (uint)(z + 1) >= HaloSize)
            throw new ArgumentOutOfRangeException(nameof(x), "Snapshot coordinates must be in [-1, 32].");
        return x + 1 + HaloSize * (y + 1 + HaloSize * (z + 1));
    }
}

using System.Numerics;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Lod;

/// <summary>An integer voxel cell and fractional offset. Subtract integer origins before conversion to floating point.</summary>
public readonly record struct WorldPosition
{
    public long X { get; }
    public long Y { get; }
    public long Z { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double OffsetZ { get; }

    public WorldPosition(long x, long y, long z, double offsetX = 0, double offsetY = 0, double offsetZ = 0)
    {
        if (!(offsetX >= 0 && offsetX < 1) || !(offsetY >= 0 && offsetY < 1) || !(offsetZ >= 0 && offsetZ < 1))
            throw new ArgumentOutOfRangeException(nameof(offsetX), "Fractional offsets must be in [0, 1).");
        (X, Y, Z, OffsetX, OffsetY, OffsetZ) = (x, y, z, offsetX, offsetY, offsetZ);
    }

    public Vector3 RelativeOrigin(ChunkKey key) => new(
        (float)RelativeX(key.OriginX),
        (float)RelativeY(key.OriginY),
        (float)RelativeZ(key.OriginZ));

    public double RelativeX(long x) => Difference(x, X) - OffsetX;
    public double RelativeY(long y) => Difference(y, Y) - OffsetY;
    public double RelativeZ(long z) => Difference(z, Z) - OffsetZ;

    public double DistanceSquaredTo(ChunkKey key)
    {
        double dx = AxisDistance(Difference(key.OriginX, X) - OffsetX, key.WorldSize);
        double dy = AxisDistance(Difference(key.OriginY, Y) - OffsetY, key.WorldSize);
        double dz = AxisDistance(Difference(key.OriginZ, Z) - OffsetZ, key.WorldSize);
        return dx * dx + dy * dy + dz * dz;
    }

    // Int128 handles the complete signed-long range without overflow or cancellation near 2^53.
    private static double Difference(long value, long reference) => (double)((Int128)value - reference);
    private static double AxisDistance(double minimum, long side) => minimum > 0 ? minimum : Math.Max(0, -minimum - side);
}

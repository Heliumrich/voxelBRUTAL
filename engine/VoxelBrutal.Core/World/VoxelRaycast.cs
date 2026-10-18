using System.Numerics;
using VoxelBrutal.Core.Lod;

namespace VoxelBrutal.Core.World;

public readonly record struct VoxelHit(long X, long Y, long Z, long PreviousX, long PreviousY, long PreviousZ,
    ushort Material, double Distance);

/// <summary>Integer-cell DDA; global coordinates never pass through single-precision world space.</summary>
public static class VoxelRaycast
{
    public static VoxelHit? Cast(IVoxelSource source, WorldPosition origin, Vector3 direction, double maximumDistance = 256)
    {
        if (!double.IsFinite(maximumDistance) || maximumDistance < 0 || maximumDistance > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) || direction.LengthSquared() < 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(direction));
        direction = Vector3.Normalize(direction);
        long x = origin.X, y = origin.Y, z = origin.Z, px = x, py = y, pz = z;
        int sx = Math.Sign(direction.X), sy = Math.Sign(direction.Y), sz = Math.Sign(direction.Z);
        double dx = Delta(direction.X), dy = Delta(direction.Y), dz = Delta(direction.Z);
        double tx = First(origin.OffsetX, sx, dx), ty = First(origin.OffsetY, sy, dy), tz = First(origin.OffsetZ, sz, dz);
        double distance = 0;
        while (distance <= maximumDistance)
        {
            ushort material = source.Sample(x, y, z, 0);
            if (material != 0) return new(x, y, z, px, py, pz, material, distance);
            (px, py, pz) = (x, y, z);
            double next = Math.Min(tx, Math.Min(ty, tz));
            if (next > maximumDistance) break;
            // Step tied boundaries together; cells touched only along an edge have no ray volume.
            if (tx <= next) { x = checked(x + sx); tx += dx; }
            if (ty <= next) { y = checked(y + sy); ty += dy; }
            if (tz <= next) { z = checked(z + sz); tz += dz; }
            distance = next;
        }
        return null;
    }
    private static double Delta(float component) => component == 0 ? double.PositiveInfinity : 1 / Math.Abs((double)component);
    private static double First(double fraction, int step, double delta) => step == 0 ? double.PositiveInfinity : (step > 0 ? 1 - fraction : fraction) * delta;
}

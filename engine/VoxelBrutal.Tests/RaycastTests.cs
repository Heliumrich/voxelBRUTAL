using System.Numerics;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Tests;

internal static class RaycastTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("Raycast DDA crosses negative boundaries and respects distance", NegativeRay);
        yield return ("Raycast preserves voxel precision beyond double integer range", DistantRay);
    }
    private static void NegativeRay()
    {
        var source = new SingleVoxel(-2, 0, 0);
        VoxelHit? hit = VoxelRaycast.Cast(source, new WorldPosition(0, 0, 0, .5, .5, .5), -Vector3.UnitX);
        if (hit is not { X: -2, PreviousX: -1 } || Math.Abs(hit.Value.Distance - 1.5) > 1e-6)
            throw new Exception("Negative ray cell or entry distance incorrect");
        if (VoxelRaycast.Cast(source, new(0, 0, 0, .5, .5, .5), -Vector3.UnitX, 1) is not null)
            throw new Exception("Maximum distance ignored");
        if (VoxelRaycast.Cast(source, new(-2, 0, 0), Vector3.UnitY, 0) is not { Distance: 0 })
            throw new Exception("Origin-inside hit missing");
    }
    private static void DistantRay()
    {
        const long far = 1L << 56;
        VoxelHit? hit = VoxelRaycast.Cast(new SingleVoxel(far + 3, 0, 0), new(far, 0, 0, .25, .5, .5), Vector3.UnitX);
        if (hit is null || hit.Value.X != far + 3 || Math.Abs(hit.Value.Distance - 2.75) > 1e-6)
            throw new Exception("Ray lost distant-cell precision");
    }
    private sealed class SingleVoxel(long sx, long sy, long sz) : IVoxelSource
    {
        public ushort Sample(long x, long y, long z, int lod) => x == sx && y == sy && z == sz ? (ushort)1 : (ushort)0;
    }
}

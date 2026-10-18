using System.Numerics;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Visibility;

public readonly record struct RelativeAabb(Vector3 Minimum, Vector3 Maximum)
{
    /// <summary>Relative to the exact same camera used to construct the translation-free view/projection matrix.</summary>
    public static RelativeAabb FromChunk(ChunkKey key, WorldPosition camera)
    {
        double x = camera.RelativeX(key.OriginX);
        double y = camera.RelativeY(key.OriginY);
        double z = camera.RelativeZ(key.OriginZ);
        // Round outwards. Otherwise a small distant box may shrink to a point or cross a clip plane.
        return new(
            new(Down(x), Down(y), Down(z)),
            new(Up(x + key.WorldSize), Up(y + key.WorldSize), Up(z + key.WorldSize)));
    }

    private static float Down(double value) => MathF.BitDecrement((float)value);
    private static float Up(double value) => MathF.BitIncrement((float)value);
}

/// <summary>Conservative AABB culling. System.Numerics row vectors; Vulkan clip depth 0 &lt;= z &lt;= w, including reverse-Z.</summary>
public sealed class Frustum
{
    private readonly Vector4[] _planes;
    private Frustum(Vector4[] planes) => _planes = planes;

    public static Frustum FromViewProjection(Matrix4x4 matrix)
    {
        var c1 = new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        var c2 = new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        var c3 = new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        var c4 = new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
        Vector4[] planes = [c4 + c1, c4 - c1, c4 + c2, c4 - c2, c3, c4 - c3];
        for (int i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite(p.W))
                throw new ArgumentException("View projection matrix must be finite.", nameof(matrix));
            double length = Math.Sqrt((double)p.X * p.X + (double)p.Y * p.Y + (double)p.Z * p.Z);
            if (length == 0)
            {
                // Infinite reverse-Z has a degenerate far plane with a positive constant.
                if (p.W < 0) throw new ArgumentException("Degenerate negative clip plane.", nameof(matrix));
                planes[i] = Vector4.Zero;
            }
            else planes[i] = new((float)(p.X / length), (float)(p.Y / length), (float)(p.Z / length), (float)(p.W / length));
        }
        return new(planes);
    }

    public bool Intersects(RelativeAabb bounds, float tolerance = 0.01f)
    {
        if (!(tolerance >= 0) || !float.IsFinite(tolerance)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (bounds.Minimum.X > bounds.Maximum.X || bounds.Minimum.Y > bounds.Maximum.Y || bounds.Minimum.Z > bounds.Maximum.Z)
            throw new ArgumentException("AABB minimum exceeds maximum.", nameof(bounds));
        foreach (var plane in _planes)
        {
            // Support point farthest along the inward plane normal; touches remain visible.
            var positive = new Vector3(
                plane.X >= 0 ? bounds.Maximum.X : bounds.Minimum.X,
                plane.Y >= 0 ? bounds.Maximum.Y : bounds.Minimum.Y,
                plane.Z >= 0 ? bounds.Maximum.Z : bounds.Minimum.Z);
            double distance = (double)positive.X * plane.X + (double)positive.Y * plane.Y + (double)positive.Z * plane.Z + plane.W;
            if (distance < -tolerance) return false;
        }
        return true;
    }
}

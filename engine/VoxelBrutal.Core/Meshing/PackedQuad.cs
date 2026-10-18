using System.Numerics;
using System.Runtime.InteropServices;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Meshing;

/// <summary>
/// Eight-byte vertex-pulling format, matched by the Vulkan shader. Geometry: X[0:5], Y[6:11],
/// Z[12:17], width-minus-one[18:22], height-minus-one[23:27], face[28:30]. Surface: material[0:15].
/// Position is the minimum corner on the face plane. U/V axes cycle X=>Y/Z, Y=>Z/X, Z=>X/Y.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public readonly struct PackedQuad : IEquatable<PackedQuad>
{
    public readonly uint Geometry;
    public readonly uint Surface;

    public int X => (int)(Geometry & 63);
    public int Y => (int)((Geometry >> 6) & 63);
    public int Z => (int)((Geometry >> 12) & 63);
    public int Width => (int)((Geometry >> 18) & 31) + 1;
    public int Height => (int)((Geometry >> 23) & 31) + 1;
    public FaceDirection Face => (FaceDirection)((Geometry >> 28) & 7);
    public ushort Material => (ushort)(Surface & 65535);
    public int Area => Width * Height;

    public PackedQuad(int x, int y, int z, int width, int height, FaceDirection face, ushort material)
    {
        if ((uint)x > ChunkConstants.Size || (uint)y > ChunkConstants.Size || (uint)z > ChunkConstants.Size)
            throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)(width - 1) >= ChunkConstants.Size || (uint)(height - 1) >= ChunkConstants.Size)
            throw new ArgumentOutOfRangeException(nameof(width));
        if ((uint)face > (uint)FaceDirection.PositiveZ)
            throw new ArgumentOutOfRangeException(nameof(face));
        if (material == 0)
            throw new ArgumentOutOfRangeException(nameof(material), "Air cannot emit a quad.");
        int axis = (int)face / 2;
        int u = axis == 0 ? y : axis == 1 ? z : x;
        int v = axis == 0 ? z : axis == 1 ? x : y;
        if (u + width > ChunkConstants.Size || v + height > ChunkConstants.Size)
            throw new ArgumentOutOfRangeException(nameof(width), "Quad must fit on the chunk face lattice.");
        Geometry = (uint)x | ((uint)y << 6) | ((uint)z << 12) |
            ((uint)(width - 1) << 18) | ((uint)(height - 1) << 23) | ((uint)face << 28);
        Surface = material;
    }

    /// <summary>CCW local-space corners when viewed from outside; useful for tools and correctness checks.</summary>
    public Vector3 GetCorner(int corner)
    {
        if ((uint)corner >= 4)
            throw new ArgumentOutOfRangeException(nameof(corner));
        if (((int)Face & 1) == 0)
            corner = (4 - corner) & 3;
        int u = corner is 1 or 2 ? Width : 0;
        int v = corner is 2 or 3 ? Height : 0;
        return ((int)Face / 2) switch
        {
            0 => new(X, Y + u, Z + v),
            1 => new(X + v, Y, Z + u),
            _ => new(X + u, Y + v, Z),
        };
    }

    public bool Equals(PackedQuad other) => Geometry == other.Geometry && Surface == other.Surface;
    public override bool Equals(object? obj) => obj is PackedQuad other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Geometry, Surface);
    public static bool operator ==(PackedQuad left, PackedQuad right) => left.Equals(right);
    public static bool operator !=(PackedQuad left, PackedQuad right) => !left.Equals(right);
}

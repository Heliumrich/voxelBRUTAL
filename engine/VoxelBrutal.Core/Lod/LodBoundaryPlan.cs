using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Lod;

[Flags]
public enum ChunkFaces : byte
{
    None = 0,
    NegativeX = 1,
    PositiveX = 2,
    NegativeY = 4,
    PositiveY = 8,
    NegativeZ = 16,
    PositiveZ = 32
}

public readonly record struct LodBoundary(ChunkKey Fine, ChunkKey Coarse, ChunkFaces FineFace)
{
    public bool IsTwoToOne => Coarse.Lod == Fine.Lod + 1;
}

/// <summary>Describes every finer-side mixed-resolution face. Metadata for a future seam implementation, not seam geometry.</summary>
public static class LodBoundaryPlan
{
    public static IReadOnlyList<LodBoundary> Build(IReadOnlyList<ChunkKey> leaves)
    {
        if (leaves.Count == 0) return [];
        var set = leaves.ToHashSet();
        int maxLod = leaves.Max(key => key.Lod);
        var result = new List<LodBoundary>();
        foreach (var leaf in leaves)
        for (int face = 0; face < 6; face++)
        {
            if (!LodSelector.TryFacePoint(leaf, face, out long x, out long y, out long z)) continue;
            for (int level = leaf.Lod + 1; level <= maxLod; level++)
            {
                var coarse = ChunkKey.FromWorld(x, y, z, level);
                if (!set.Contains(coarse)) continue;
                result.Add(new(leaf, coarse, (ChunkFaces)(1 << face)));
                break;
            }
        }
        return result;
    }
}

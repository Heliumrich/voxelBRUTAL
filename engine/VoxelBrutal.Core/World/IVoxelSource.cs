namespace VoxelBrutal.Core.World;

/// <summary>
/// A thread-safe, deterministic material sampler. Positions are minimum corners in base-world units;
/// a sample at LOD n represents a cell of side 2^n. Zero is air; every other material is opaque.
/// A source must remain unchanged throughout a snapshot capture, or use its own immutable revision.
/// </summary>
public interface IVoxelSource
{
    ushort Sample(long x, long y, long z, int lod);
}

/// <summary>Optional bulk sampler. Must exactly match scalar sampling in X-fastest 34 cubed halo order.</summary>
public interface IVoxelBatchSource : IVoxelSource
{
    void FillChunkHalo(ChunkKey key, Span<ushort> destination, CancellationToken cancellationToken = default);
}

using System.Threading;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Streaming;

/// <summary>
/// Sparse edit overlay. A read lock covers the entire halo capture, so a mesh never observes
/// half of an edit transaction. The generator and this overlay stay independent of the GPU.
/// </summary>
public sealed class EditableVoxelSource(IVoxelSource generator) : IVoxelBatchSource, IDisposable
{
    private readonly ReaderWriterLockSlim gate = new();
    private readonly Dictionary<(long X, long Y, long Z), ushort> edits = [];
    private ulong revision;

    public ulong Revision => Volatile.Read(ref revision);

    // Called while Capture holds the read lock. External sampling takes its own read lock.
    private ushort SampleLocked(long x, long y, long z, int lod) =>
        edits.TryGetValue((x, y, z), out ushort material) ? material : generator.Sample(x, y, z, lod);

    public ushort Sample(long x, long y, long z, int lod)
    {
        gate.EnterReadLock();
        try { return SampleLocked(x, y, z, lod); }
        finally { gate.ExitReadLock(); }
    }

    public ChunkSnapshot Capture(ChunkKey key, ulong requestedRevision, CancellationToken cancellationToken)
    {
        gate.EnterReadLock();
        try { return ChunkSnapshot.Create(key, new LockedSource(this), requestedRevision, cancellationToken); }
        finally { gate.ExitReadLock(); }
    }

    public void FillChunkHalo(ChunkKey key, Span<ushort> destination, CancellationToken cancellationToken = default)
    {
        gate.EnterReadLock();
        try { FillLocked(key, destination, cancellationToken); }
        finally { gate.ExitReadLock(); }
    }

    private void FillLocked(ChunkKey key, Span<ushort> destination, CancellationToken cancellationToken)
    {
        if (destination.Length != ChunkConstants.HaloVolume) throw new ArgumentException("Expected 34 cubed cells.", nameof(destination));
        long ox = key.OriginX, oy = key.OriginY, oz = key.OriginZ;
        int scale = key.VoxelScale;
        if (generator is IVoxelBatchSource batch)
        {
            batch.FillChunkHalo(key, destination, cancellationToken);
            foreach (var edit in edits)
            {
                Int128 dx = (Int128)edit.Key.X - ox, dy = (Int128)edit.Key.Y - oy, dz = (Int128)edit.Key.Z - oz;
                if (dx < -scale || dy < -scale || dz < -scale || dx > 32L * scale || dy > 32L * scale || dz > 32L * scale ||
                    dx % scale != 0 || dy % scale != 0 || dz % scale != 0) continue;
                destination[ChunkConstants.HaloIndex((int)(dx / scale), (int)(dy / scale), (int)(dz / scale))] = edit.Value;
            }
        }
        else
        {
            for (int z = -1; z <= 32; z++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int y = -1; y <= 32; y++)
                for (int x = -1; x <= 32; x++)
                    destination[ChunkConstants.HaloIndex(x, y, z)] = SampleLocked(checked(ox + (long)x * scale),
                        checked(oy + (long)y * scale), checked(oz + (long)z * scale), key.Lod);
            }
        }
    }

    /// <summary>
    /// Returns conservative invalidations at every supported LOD, including halo neighbors.
    /// Coarse levels use the generator's point sampling policy; edits at unsampled coordinates
    /// are preserved in the overlay for LOD0 but do not promise conservative coarse occupancy.
    /// </summary>
    public IReadOnlyList<ChunkKey> SetVoxel(long x, long y, long z, ushort material, int maximumLod = 0)
    {
        if (maximumLod is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(maximumLod));
        List<ChunkKey> affected = [];
        for (int lod = 0; lod <= maximumLod; lod++)
        {
            long size = 32L << lod;
            long scale = 1L << lod;
            long cx = FloorDiv(x, size), cy = FloorDiv(y, size), cz = FloorDiv(z, size);
            long lx = x - cx * size, ly = y - cy * size, lz = z - cz * size;
            int minX = lx < scale ? -1 : 0, maxX = lx >= size - scale ? 1 : 0;
            int minY = ly < scale ? -1 : 0, maxY = ly >= size - scale ? 1 : 0;
            int minZ = lz < scale ? -1 : 0, maxZ = lz >= size - scale ? 1 : 0;
            for (int dz = minZ; dz <= maxZ; dz++)
            for (int dy = minY; dy <= maxY; dy++)
            for (int dx = minX; dx <= maxX; dx++)
                affected.Add(new ChunkKey(checked(cx + dx), checked(cy + dy), checked(cz + dz), lod));
        }
        // Validate the complete invalidation set before mutation. An unrepresentable neighbor
        // at the signed-coordinate edge must not leave an applied edit with no invalidations.
        gate.EnterWriteLock();
        try
        {
            ulong nextRevision = checked(revision + 1);
            edits[(x, y, z)] = material;
            revision = nextRevision;
        }
        finally { gate.ExitWriteLock(); }
        return affected;
    }

    private static long FloorDiv(long n, long d)
    {
        long q = n / d;
        return n % d < 0 ? q - 1 : q;
    }

    public void Dispose() => gate.Dispose();

    private sealed class LockedSource(EditableVoxelSource owner) : IVoxelBatchSource
    {
        public ushort Sample(long x, long y, long z, int lod) => owner.SampleLocked(x, y, z, lod);
        public void FillChunkHalo(ChunkKey key, Span<ushort> destination, CancellationToken cancellationToken = default) =>
            owner.FillLocked(key, destination, cancellationToken);
    }
}

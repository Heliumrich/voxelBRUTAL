namespace VoxelBrutal.Core.World;

/// <summary>
/// Immutable owned 34 cubed material samples. Interior is [0,31], halo is [-1,32].
/// Halo cells cull neighboring faces but never emit geometry. Revision identifies the captured world state.
/// </summary>
public sealed class ChunkSnapshot
{
    private readonly ushort[] _cells;
    public ChunkKey Key { get; }
    public ulong Revision { get; }
    public int AllocatedBytes => _cells.Length * sizeof(ushort);
    public ReadOnlySpan<ushort> Cells => _cells;
    public ushort this[int x, int y, int z] => _cells[ChunkConstants.HaloIndex(x, y, z)];

    private ChunkSnapshot(ChunkKey key, ulong revision, ushort[] cells)
    {
        Key = key;
        Revision = revision;
        _cells = cells;
    }

    /// <summary>Copies caller-owned halo storage so later edits cannot affect a mesh in flight.</summary>
    public static ChunkSnapshot FromCells(ChunkKey key, ReadOnlySpan<ushort> cells, ulong revision = 0)
    {
        if (cells.Length != ChunkConstants.HaloVolume)
            throw new ArgumentException("Expected exactly 34 cubed cells, X varying fastest.", nameof(cells));
        return new(key, revision, cells.ToArray());
    }

    public static ChunkSnapshot Create(ChunkKey key, IVoxelSource source, ulong revision = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var cells = new ushort[ChunkConstants.HaloVolume];
        Capture(key, source, cells, cancellationToken);
        return new(key, revision, cells);
    }

    /// <summary>Capture edited interior and canonical same-resolution neighbors without sharing mutable data.</summary>
    public static ChunkSnapshot FromChunk(ChunkData chunk, IVoxelSource neighbors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(neighbors);
        var cells = new ushort[ChunkConstants.HaloVolume];
        ChunkKey key = chunk.Key;
        Capture(key, neighbors, cells, cancellationToken);
        for (int z = 0; z < ChunkConstants.Size; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int y = 0; y < ChunkConstants.Size; y++)
                for (int x = 0; x < ChunkConstants.Size; x++)
                    cells[ChunkConstants.HaloIndex(x, y, z)] = chunk[x, y, z];
        }
        return new(key, chunk.Revision, cells);
    }

    private static void Capture(ChunkKey key, IVoxelSource source, ushort[] cells,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is IVoxelBatchSource batch)
        {
            batch.FillChunkHalo(key, cells, cancellationToken);
            return;
        }
        long ox = key.OriginX, oy = key.OriginY, oz = key.OriginZ;
        int scale = key.VoxelScale;
        for (int z = -1; z <= ChunkConstants.Size; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int y = -1; y <= ChunkConstants.Size; y++)
                for (int x = -1; x <= ChunkConstants.Size; x++)
                    cells[ChunkConstants.HaloIndex(x, y, z)] =
                        source.Sample(checked(ox + (long)x * scale), checked(oy + (long)y * scale),
                            checked(oz + (long)z * scale), key.Lod);
        }
    }
}

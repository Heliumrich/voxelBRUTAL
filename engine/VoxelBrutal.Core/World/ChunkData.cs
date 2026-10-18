using System.Buffers;

namespace VoxelBrutal.Core.World;

/// <summary>
/// Single-owner editable chunk. Uniform air/solid chunks need no cell allocation. Mutation and
/// capture must be serialized by the world owner; workers receive immutable ChunkSnapshots.
/// </summary>
public sealed class ChunkData
{
    private ushort _uniformMaterial;
    private ushort[]? _cells;

    public ChunkKey Key { get; }
    public ulong Revision { get; private set; }
    public bool IsUniform => _cells is null;
    public ushort UniformMaterial => IsUniform ? _uniformMaterial
        : throw new InvalidOperationException("The chunk is not uniform.");
    public int AllocatedBytes => _cells is null ? 0 : ChunkConstants.Volume * sizeof(ushort);

    public ChunkData(ChunkKey key, ushort material = 0, ulong revision = 0)
    {
        Key = key;
        _uniformMaterial = material;
        Revision = revision;
    }

    public ushort this[int x, int y, int z]
    {
        get
        {
            int index = ChunkConstants.Index(x, y, z);
            return _cells is null ? _uniformMaterial : _cells[index];
        }
    }

    public bool Set(int x, int y, int z, ushort material)
    {
        int index = ChunkConstants.Index(x, y, z);
        if ((_cells is null ? _uniformMaterial : _cells[index]) == material)
            return false;
        ulong nextRevision = checked(Revision + 1);
        if (_cells is null)
        {
            _cells = new ushort[ChunkConstants.Volume];
            if (_uniformMaterial != 0)
                Array.Fill(_cells, _uniformMaterial);
        }
        _cells[index] = material;
        Revision = nextRevision;
        return true;
    }

    public bool Fill(ushort material)
    {
        if (_cells is null && _uniformMaterial == material)
            return false;
        if (_cells is not null && _cells.AsSpan().IndexOfAnyExcept(material) < 0)
        {
            _cells = null;
            _uniformMaterial = material;
            return false;
        }
        ulong nextRevision = checked(Revision + 1);
        _cells = null;
        _uniformMaterial = material;
        Revision = nextRevision;
        return true;
    }

    /// <summary>Reclaims a dense allocation if all cells match; representation changes do not edit content.</summary>
    public bool TryCompact()
    {
        if (_cells is null)
            return true;
        ushort material = _cells[0];
        for (int i = 1; i < _cells.Length; i++)
            if (_cells[i] != material)
                return false;
        _uniformMaterial = material;
        _cells = null;
        return true;
    }

    public void CopyInteriorTo(Span<ushort> destination)
    {
        if (destination.Length != ChunkConstants.Volume)
            throw new ArgumentException("Destination must contain exactly 32 cubed cells.", nameof(destination));
        if (_cells is null)
            destination.Fill(_uniformMaterial);
        else
            _cells.CopyTo(destination);
    }

    public static ChunkData Generate(ChunkKey key, IVoxelSource source, ulong revision = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new ChunkData(key, revision: revision);
        var cells = new ushort[ChunkConstants.Volume];
        cancellationToken.ThrowIfCancellationRequested();
        if (source is IVoxelBatchSource batch)
        {
            ushort[] halo = ArrayPool<ushort>.Shared.Rent(ChunkConstants.HaloVolume);
            try
            {
                batch.FillChunkHalo(key, halo.AsSpan(0, ChunkConstants.HaloVolume), cancellationToken);
                for (int z = 0; z < ChunkConstants.Size; z++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int y = 0; y < ChunkConstants.Size; y++)
                        halo.AsSpan(ChunkConstants.HaloIndex(0, y, z), ChunkConstants.Size)
                            .CopyTo(cells.AsSpan(ChunkConstants.Size * (y + ChunkConstants.Size * z), ChunkConstants.Size));
                }
            }
            finally { ArrayPool<ushort>.Shared.Return(halo); }
            result._cells = cells;
            result.TryCompact();
            return result;
        }
        long ox = key.OriginX, oy = key.OriginY, oz = key.OriginZ;
        int scale = key.VoxelScale;
        for (int z = 0; z < ChunkConstants.Size; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int y = 0; y < ChunkConstants.Size; y++)
                for (int x = 0; x < ChunkConstants.Size; x++)
                    cells[x + ChunkConstants.Size * (y + ChunkConstants.Size * z)] =
                        source.Sample(checked(ox + (long)x * scale), checked(oy + (long)y * scale),
                            checked(oz + (long)z * scale), key.Lod);
        }
        result._cells = cells;
        result.TryCompact();
        return result;
    }
}

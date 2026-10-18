using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Meshing;

/// <summary>An immutable owned mesh. Input storage is copied; workers never expose writable GPU upload data.</summary>
public sealed class ChunkMesh
{
    private readonly PackedQuad[] _quads;
    public ChunkKey Key { get; }
    public ulong Revision { get; }
    public ReadOnlySpan<PackedQuad> Quads => _quads;
    public int QuadCount => _quads.Length;
    public int TriangleCount => checked(QuadCount * 2);
    public int ByteSize => checked(QuadCount * 8);
    public int VisibleUnitFaceCount { get; }

    public ChunkMesh(ChunkKey key, ulong revision, ReadOnlySpan<PackedQuad> quads, int visibleUnitFaceCount = -1)
    {
        Key = key;
        Revision = revision;
        _quads = quads.ToArray();
        if (visibleUnitFaceCount < 0)
        {
            visibleUnitFaceCount = 0;
            foreach (PackedQuad quad in quads)
                visibleUnitFaceCount = checked(visibleUnitFaceCount + quad.Area);
        }
        VisibleUnitFaceCount = visibleUnitFaceCount;
    }

    internal ChunkMesh(ChunkKey key, ulong revision, PackedQuad[] ownedQuads, int visibleUnitFaceCount, bool takeOwnership)
    {
        Key = key;
        Revision = revision;
        _quads = takeOwnership ? ownedQuads : (PackedQuad[])ownedQuads.Clone();
        VisibleUnitFaceCount = visibleUnitFaceCount;
    }
}

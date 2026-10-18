using System.Buffers;
using System.Numerics;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Meshing;

/// <summary>
/// Bit-parallel opaque face culling followed by deterministic material-aware rectangle merging.
/// One 64-bit row contains all 32 cells and both halo samples. Scratch is pooled per invocation,
/// so instances can mesh independent immutable snapshots concurrently without shared writable state.
/// </summary>
public static class BinaryGreedyMesher
{
    private const int N = ChunkConstants.Size;
    private const int H = ChunkConstants.HaloSize;
    private const int RowsPerFace = N * N;
    private const ulong InteriorBits = ((1UL << N) - 1) << 1;

    public static ChunkMesh Build(ChunkSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ulong[] occupancy = ArrayPool<ulong>.Shared.Rent(H * H);
        ulong[] masks = ArrayPool<ulong>.Shared.Rent(6 * RowsPerFace);
        try
        {
            Array.Clear(masks, 0, 6 * RowsPerFace);
            BuildOccupancy(snapshot.Cells, occupancy, cancellationToken);
            int visibleFaces = BuildFaceMasks(occupancy, masks, cancellationToken);
            if (visibleFaces == 0)
                return new(snapshot.Key, snapshot.Revision, [], 0, takeOwnership: true);
            var quads = new List<PackedQuad>();
            for (int face = 0; face < 6; face++)
            {
                for (int slice = 0; slice < N; slice++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MergeSlice(snapshot, masks, face, slice, quads);
                }
            }
            return new(snapshot.Key, snapshot.Revision, quads.ToArray(), visibleFaces, takeOwnership: true);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(occupancy);
            ArrayPool<ulong>.Shared.Return(masks);
        }
    }

    private static void BuildOccupancy(ReadOnlySpan<ushort> cells, ulong[] occupancy, CancellationToken ct)
    {
        for (int z = 0; z < H; z++)
        {
            ct.ThrowIfCancellationRequested();
            for (int y = 0; y < H; y++)
            {
                ulong row = 0;
                int start = H * (y + H * z);
                for (int x = 0; x < H; x++)
                    if (cells[start + x] != 0)
                        row |= 1UL << x;
                occupancy[y + H * z] = row;
            }
        }
    }

    private static int BuildFaceMasks(ulong[] occupancy, ulong[] masks, CancellationToken ct)
    {
        int visibleFaces = 0;
        for (int z = 1; z <= N; z++)
        {
            ct.ThrowIfCancellationRequested();
            for (int y = 1; y <= N; y++)
            {
                int rowIndex = y + H * z;
                ulong row = occupancy[rowIndex];
                ulong nx = (row & ~(row << 1) & InteriorBits) >> 1;
                ulong px = (row & ~(row >> 1) & InteriorBits) >> 1;
                ulong ny = (row & ~occupancy[rowIndex - 1] & InteriorBits) >> 1;
                ulong py = (row & ~occupancy[rowIndex + 1] & InteriorBits) >> 1;
                ulong nz = (row & ~occupancy[rowIndex - H] & InteriorBits) >> 1;
                ulong pz = (row & ~occupancy[rowIndex + H] & InteriorBits) >> 1;
                visibleFaces += BitOperations.PopCount(nx) + BitOperations.PopCount(px) +
                    BitOperations.PopCount(ny) + BitOperations.PopCount(py) +
                    BitOperations.PopCount(nz) + BitOperations.PopCount(pz);
                // X faces use Y as U and Z as V; Y faces use Z as U and X as V.
                ScatterX(nx, 0, y - 1, z - 1, masks);
                ScatterX(px, 1, y - 1, z - 1, masks);
                ScatterY(ny, 2, y - 1, z - 1, masks);
                ScatterY(py, 3, y - 1, z - 1, masks);
                // Z faces already have the desired contiguous X-bit orientation.
                masks[4 * RowsPerFace + (z - 1) * N + y - 1] = nz;
                masks[5 * RowsPerFace + (z - 1) * N + y - 1] = pz;
            }
        }
        return visibleFaces;
    }

    private static void ScatterX(ulong visible, int face, int y, int z, ulong[] masks)
    {
        while (visible != 0)
        {
            int x = BitOperations.TrailingZeroCount(visible);
            masks[face * RowsPerFace + x * N + z] |= 1UL << y;
            visible &= visible - 1;
        }
    }

    private static void ScatterY(ulong visible, int face, int y, int z, ulong[] masks)
    {
        while (visible != 0)
        {
            int x = BitOperations.TrailingZeroCount(visible);
            masks[face * RowsPerFace + y * N + x] |= 1UL << z;
            visible &= visible - 1;
        }
    }

    private static void MergeSlice(ChunkSnapshot snapshot, ulong[] masks, int face, int slice,
        List<PackedQuad> quads)
    {
        int axis = face / 2;
        int start = face * RowsPerFace + slice * N;
        for (int v = 0; v < N; v++)
        {
            while (masks[start + v] != 0)
            {
                ulong row = masks[start + v];
                int u = BitOperations.TrailingZeroCount(row);
                ushort material = MaterialAt(snapshot, axis, slice, u, v);
                int width = 1;
                while (u + width < N && (row & (1UL << (u + width))) != 0 &&
                    MaterialAt(snapshot, axis, slice, u + width, v) == material)
                    width++;
                ulong rectangle = ((1UL << width) - 1) << u;
                int height = 1;
                while (v + height < N && (masks[start + v + height] & rectangle) == rectangle &&
                    SameMaterial(snapshot, axis, slice, u, v + height, width, material))
                    height++;
                for (int dv = 0; dv < height; dv++)
                    masks[start + v + dv] &= ~rectangle;

                int plane = slice + (face & 1);
                int x = axis == 0 ? plane : axis == 1 ? v : u;
                int y = axis == 0 ? u : axis == 1 ? plane : v;
                int z = axis == 0 ? v : axis == 1 ? u : plane;
                quads.Add(new(x, y, z, width, height, (FaceDirection)face, material));
            }
        }
    }

    private static bool SameMaterial(ChunkSnapshot snapshot, int axis, int slice, int u, int v,
        int width, ushort material)
    {
        for (int du = 0; du < width; du++)
            if (MaterialAt(snapshot, axis, slice, u + du, v) != material)
                return false;
        return true;
    }

    private static ushort MaterialAt(ChunkSnapshot snapshot, int axis, int slice, int u, int v) => axis switch
    {
        0 => snapshot[slice, u, v],
        1 => snapshot[v, slice, u],
        _ => snapshot[u, v, slice],
    };
}

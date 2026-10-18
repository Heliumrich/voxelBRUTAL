namespace VoxelBrutal.Core.Visibility;

/// <summary>Conservative projected AABB in pixels, half-open rectangle, with its nearest reverse-Z depth.</summary>
public readonly record struct ProjectedOcclusionBounds(
    int MinimumX, int MinimumY, int MaximumX, int MaximumY, float NearestDepth, bool CrossesNearPlane = false);

/// <summary>
/// CPU correctness reference for a future GPU Hi-Z pass. Minimum reduction stores the farthest covered
/// depth; zero clear values prevent a hole in an occluder from hiding geometry. Depth must come from
/// current-frame opaque geometry with the same camera and viewport as the query. This is not a renderer.
/// </summary>
public sealed class ReversedDepthPyramid
{
    private readonly List<Mip> _mips;
    public int Width => _mips[0].Width;
    public int Height => _mips[0].Height;
    public int LevelCount => _mips.Count;
    private sealed record Mip(int Width, int Height, float[] Depth);

    private ReversedDepthPyramid(List<Mip> mips) => _mips = mips;

    public static ReversedDepthPyramid Build(ReadOnlySpan<float> depth, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (depth.Length != checked(width * height)) throw new ArgumentException("Depth size must match viewport dimensions.", nameof(depth));
        foreach (float value in depth)
            if (!(value >= 0 && value <= 1)) throw new ArgumentException("Reverse-Z depth must be finite and in [0, 1].", nameof(depth));
        var current = new Mip(width, height, depth.ToArray());
        var mips = new List<Mip> { current };
        while (current.Width > 1 || current.Height > 1)
        {
            int nextWidth = current.Width / 2 + current.Width % 2;
            int nextHeight = current.Height / 2 + current.Height % 2;
            var next = new Mip(nextWidth, nextHeight, new float[checked(nextWidth * nextHeight)]);
            for (int y = 0; y < next.Height; y++)
            for (int x = 0; x < next.Width; x++)
            {
                float farthest = 1;
                for (int dy = 0; dy < 2 && y * 2 + dy < current.Height; dy++)
                for (int dx = 0; dx < 2 && x * 2 + dx < current.Width; dx++)
                    farthest = Math.Min(farthest, current.Depth[(y * 2 + dy) * current.Width + x * 2 + dx]);
                next.Depth[y * next.Width + x] = farthest;
            }
            mips.Add(next);
            current = next;
        }
        return new(mips);
    }

    /// <summary>Rejects only when every sample covering the rectangle is strictly in front of its nearest depth.</summary>
    public bool IsOccluded(ProjectedOcclusionBounds bounds, float depthBias = 0.00001f)
    {
        if (!(depthBias >= 0) || !float.IsFinite(depthBias)) throw new ArgumentOutOfRangeException(nameof(depthBias));
        // Projection of near-crossing/behind-camera bounds is not reliable; leave them visible.
        if (bounds.CrossesNearPlane || !(bounds.NearestDepth > 0 && bounds.NearestDepth <= 1)
            || bounds.MinimumX < 0 || bounds.MinimumY < 0 || bounds.MaximumX > Width || bounds.MaximumY > Height
            || bounds.MinimumX >= bounds.MaximumX || bounds.MinimumY >= bounds.MaximumY) return false;

        int minX = bounds.MinimumX, minY = bounds.MinimumY;
        int maxX = bounds.MaximumX - 1, maxY = bounds.MaximumY - 1;
        int level = 0;
        // Inspect all overlapped texels, including unaligned rectangles. Sampling the center alone is unsafe.
        while (level + 1 < _mips.Count && (maxX - minX > 1 || maxY - minY > 1))
        {
            minX /= 2; minY /= 2; maxX /= 2; maxY /= 2;
            level++;
        }
        var mip = _mips[level];
        float farthest = 1;
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
            farthest = Math.Min(farthest, mip.Depth[y * mip.Width + x]);
        return bounds.NearestDepth < farthest - depthBias;
    }
}

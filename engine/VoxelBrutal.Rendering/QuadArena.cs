namespace VoxelBrutal.Rendering;

/// <summary>Render-thread-only best-fit allocator; old ranges are returned after their last GPU use completes.</summary>
internal sealed class QuadArena(int capacity)
{
    private readonly List<Range> _free = [new(0, capacity)];
    internal readonly record struct Range(int Offset, int Count);

    public bool TryAllocate(int count, out Range allocation)
    {
        allocation = default;
        if (count <= 0) return true;
        var best = -1;
        for (var i = 0; i < _free.Count; ++i)
            if (_free[i].Count >= count && (best < 0 || _free[i].Count < _free[best].Count)) best = i;
        if (best < 0) return false;
        var range = _free[best];
        if (range.Count == count) _free.RemoveAt(best);
        else _free[best] = new(range.Offset + count, range.Count - count);
        allocation = new(range.Offset, count);
        return true;
    }

    public void Free(Range range)
    {
        if (range.Count == 0) return;
        var i = 0;
        while (i < _free.Count && _free[i].Offset < range.Offset) ++i;
        _free.Insert(i, range);
        if (i > 0 && _free[i - 1].Offset + _free[i - 1].Count == range.Offset)
        {
            _free[i - 1] = new(_free[i - 1].Offset, _free[i - 1].Count + range.Count);
            _free.RemoveAt(i--);
        }
        if (i + 1 < _free.Count && _free[i].Offset + _free[i].Count == _free[i + 1].Offset)
        {
            _free[i] = new(_free[i].Offset, _free[i].Count + _free[i + 1].Count);
            _free.RemoveAt(i + 1);
        }
    }
}

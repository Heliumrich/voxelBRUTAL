namespace VoxelBrutal.Core.Lod;

/// <summary>
/// Publishes a whole mesh partition only after every exact topology version is GPU resident.
/// Old and candidate versions must coexist until this switch. All methods belong to the owner thread.
/// </summary>
public sealed class AtomicLodPublication
{
    private HashSet<ChunkMeshId> _required = [];
    public LodMeshPlan? Published { get; private set; }
    public LodMeshPlan? Pending { get; private set; }
    public long PublishedGeneration { get; private set; }
    public IReadOnlySet<ChunkMeshId> RequiredVersions => _required;

    public void Stage(LodMeshPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Pending is not null && Pending.HasSameVersions(plan)) return;
        Pending = Published is not null && Published.HasSameVersions(plan) ? null : plan;
        RefreshRequired();
    }

    /// <summary>The callback must include copy submission completion, not merely CPU build completion.</summary>
    public bool TryPublish(Func<ChunkMeshId, bool> isGpuResident)
    {
        ArgumentNullException.ThrowIfNull(isGpuResident);
        if (Pending is null) return false;
        foreach (var entry in Pending.Entries)
            if (!isGpuResident(entry.Version)) return false;
        Published = Pending;
        Pending = null;
        PublishedGeneration = checked(PublishedGeneration + 1);
        RefreshRequired();
        return true;
    }

    public IEnumerable<ChunkMeshId> MissingVersions(Func<ChunkMeshId, bool> isGpuResident)
    {
        ArgumentNullException.ThrowIfNull(isGpuResident);
        if (Pending is null) yield break;
        foreach (var entry in Pending.Entries.OrderByDescending(entry => entry.Key.Lod))
            if (!isGpuResident(entry.Version)) yield return entry.Version;
    }

    public void CancelPending()
    {
        Pending = null;
        RefreshRequired();
    }

    private void RefreshRequired()
    {
        _required = [];
        if (Published is not null) foreach (var entry in Published.Entries) _required.Add(entry.Version);
        if (Pending is not null) foreach (var entry in Pending.Entries) _required.Add(entry.Version);
    }
}

using System.Collections.Concurrent;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.Streaming;
using VoxelBrutal.Core.World;
using VoxelBrutal.Rendering;

namespace VoxelBrutal.Demo;

internal sealed record WorldViewSettings
{
    public int MinimumLod { get; init; }
    public int RootLod { get; init; } = 5;
    public int RadiusInBaseChunks { get; init; } = 32;
    public int MaximumSelectedChunks { get; init; } = 8192;
    public double PixelError { get; init; } = 6;
    public bool Uniform { get; init; }
    public int Workers { get; init; } = 8;
}

/// <summary>
/// Demand and atomic geometry publication. Previous and candidate mesh versions coexist until
/// every replacement and boundary patch is ready. Camera changes coalesce while a plan builds.
/// </summary>
internal sealed class WorldViewStreamer : IAsyncDisposable
{
    private readonly EditableVoxelSource source;
    private readonly TerrainSettings terrain;
    private readonly VulkanChunkRenderer renderer;
    private readonly WorldViewSettings settings;
    private readonly AtomicLodPublication publication = new();
    private readonly ConcurrentDictionary<ChunkMeshId, LodMeshPlan> buildPlans = new();
    private readonly Dictionary<ChunkKey, ulong> contentRevisions = [];
    private readonly ChunkPipeline pipeline;
    private IReadOnlySet<ChunkMeshId> active = new HashSet<ChunkMeshId>();
    private (long X, long Y, long Z, int Projection)? lastPlanCell;
    private bool editsPending;
    private readonly CancellationTokenSource stopping = new();
    private Task<PreparedPlan>? preparing;
    private long contentEpoch;
    private int blockedFrames, detailPenalty;
    public int CapacityCoarsenings => detailPenalty;
    public long PublishedGeneration => publication.PublishedGeneration;
    public int PublishedChunks => publication.Published?.Leaves.Count ?? 0;
    public int TargetChunks => publication.Pending?.Leaves.Count ?? PublishedChunks;
    public bool IsSettled => publication.Published is not null && publication.Pending is null && preparing is null
        && !editsPending && lastPlanCell.HasValue;
    public IReadOnlyDictionary<int, int> LodHistogram => (publication.Published?.Leaves ?? [])
        .GroupBy(k => k.Lod).ToDictionary(g => g.Key, g => g.Count());
    public StreamingStatistics Statistics => pipeline.Statistics;

    public WorldViewStreamer(EditableVoxelSource source, TerrainSettings terrain, VulkanChunkRenderer renderer, WorldViewSettings settings)
    {
        this.source = source; this.terrain = terrain; this.renderer = renderer; this.settings = settings;
        if (settings.RootLod < settings.MinimumLod || settings.RootLod > 10 || settings.RadiusInBaseChunks < 1)
            throw new ArgumentOutOfRangeException(nameof(settings));
        pipeline = ChunkPipeline.CreateMeshing((version, ct) =>
        {
            if (!buildPlans.TryGetValue(version, out LodMeshPlan? plan)) throw new OperationCanceledException("Mesh plan was superseded.");
            return source.BuildLodMesh(version, plan, ct);
        }, new StreamingOptions { WorkerCount = settings.Workers, MaximumTrackedChunks = settings.MaximumSelectedChunks * 2,
            MaximumDispatchPerTick = 24 });
        renderer.SetActiveMeshes(active);
    }

    public void Update(WorldPosition camera, double projectionScale)
    {
        // Selection hysteresis uses a 64-voxel cell; the continuous camera is still used for rendering.
        var cell = (ChunkKey.FloorDiv(camera.X, 64), ChunkKey.FloorDiv(camera.Y, 64), ChunkKey.FloorDiv(camera.Z, 64),
            (int)Math.Round(projectionScale / 64));
        bool changed = lastPlanCell != cell;
        if (preparing is { IsCompleted: true })
        {
            PreparedPlan prepared = preparing.GetAwaiter().GetResult();
            preparing = null;
            if (prepared.Epoch == contentEpoch)
            {
                Stage(prepared.Plan, camera);
                lastPlanCell = prepared.Bootstrap ? null : prepared.Cell;
                editsPending = false;
            }
            changed = lastPlanCell != cell;
        }
        if (preparing is null && publication.Pending is null && (publication.Published is null || changed || editsPending))
        {
            // Selection and topology hashing can take hundreds of milliseconds for large covers.
            // Prepare immutable data off-thread; only mesh requests and publication touch the owner.
            ChunkKey[] roots = Roots(camera);
            bool bootstrap = publication.Published is null;
            var revisions = new Dictionary<ChunkKey, ulong>(contentRevisions);
            long epoch = contentEpoch;
            int penalty = detailPenalty;
            preparing = Task.Run(() =>
            {
                stopping.Token.ThrowIfCancellationRequested();
                IReadOnlyList<ChunkKey> leaves = bootstrap ? roots : LodSelector.Select(roots, camera, new LodSettings
                {
                    MinimumLod = settings.MinimumLod, MaxSelectedChunks = settings.MaximumSelectedChunks,
                    PixelError = settings.PixelError * (1 << penalty), ProjectionScale = projectionScale, RequireUniformLod = settings.Uniform
                }).Desired;
                LodMeshPlan plan = LodMeshPlan.Create(roots, leaves, key => revisions.GetValueOrDefault(key));
                stopping.Token.ThrowIfCancellationRequested();
                return new PreparedPlan(plan, epoch, cell, bootstrap);
            }, stopping.Token);
        }

        pipeline.Tick(result =>
        {
            if (!publication.RequiredVersions.Contains(result.Version)) return true;
            if (result.Error is not null) throw new InvalidOperationException($"Mesh build failed for {result.Version}", result.Error);
            return renderer.TryQueueUpload(result.Mesh!);
        }, maximumCompletions: 24, milliseconds: 3);

        if (publication.TryPublish(renderer.IsResident))
        {
            active = publication.Published!.Entries.Select(e => e.Version).ToHashSet();
            renderer.SetActiveMeshes(active);
            renderer.RetainMeshes(publication.RequiredVersions);
            pipeline.RetainVersions(publication.RequiredVersions);
            TrimBuildPlans();
        }
        HandleCapacityPressure();
    }

    public void Edit(long x, long y, long z, ushort material)
    {
        foreach (ChunkKey key in source.SetVoxel(x, y, z, material, settings.RootLod)) contentRevisions[key] = source.Revision;
        // A pending topology captured before the edit cannot be published afterwards. Keep the old
        // complete geometry, cancel its candidate, then build one coherent edit/topology replacement.
        publication.CancelPending();
        renderer.RetainMeshes(publication.RequiredVersions);
        pipeline.RetainVersions(publication.RequiredVersions);
        editsPending = true;
        contentEpoch++;
    }

    private void HandleCapacityPressure()
    {
        if (publication.Pending is null || !renderer.UploadCapacityBlocked) { blockedFrames = 0; return; }
        if (++blockedFrames < 60) return; // Allow retired ranges several frame fences to become reusable.
        if (publication.Published is null || detailPenalty >= 8 || publication.Pending.Leaves.All(k => k.Lod == settings.RootLod))
            throw new InvalidOperationException("The coarsest complete terrain cover exceeds the GPU arena. Increase QuadCapacity or reduce the view.");
        publication.CancelPending();
        renderer.RetainMeshes(publication.RequiredVersions);
        pipeline.RetainVersions(publication.RequiredVersions);
        TrimBuildPlans();
        detailPenalty++;
        contentEpoch++;
        editsPending = true;
        lastPlanCell = null;
        blockedFrames = 0;
        Console.WriteLine($"GPU mesh arena pressure: preparing a coarser candidate (pixel error {settings.PixelError * (1 << detailPenalty)}).");
    }

    private void Stage(LodMeshPlan plan, WorldPosition camera)
    {
        publication.Stage(plan);
        if (publication.Pending is null) return;
        foreach (var entry in plan.Entries) buildPlans[entry.Version] = plan;
        renderer.RetainMeshes(publication.RequiredVersions);
        pipeline.RetainVersions(publication.RequiredVersions);
        foreach (var entry in plan.Entries.OrderBy(e => camera.DistanceSquaredTo(e.Key)))
            if (!renderer.IsResident(entry.Version) && !pipeline.Request(entry.Version, camera.DistanceSquaredTo(entry.Key)))
                throw new InvalidOperationException("Atomic old+candidate residency exceeds the explicit CPU budget.");
        TrimBuildPlans();
    }

    private void TrimBuildPlans()
    {
        foreach (ChunkMeshId version in buildPlans.Keys)
            if (!publication.RequiredVersions.Contains(version)) buildPlans.TryRemove(version, out _);
    }

    private ChunkKey[] Roots(WorldPosition camera)
    {
        long side = 32L << settings.RootLod, distance = settings.RadiusInBaseChunks * 32L;
        long minX = ChunkKey.FloorDiv(checked(camera.X - distance), side), maxX = ChunkKey.FloorDiv(checked(camera.X + distance), side);
        long minZ = ChunkKey.FloorDiv(checked(camera.Z - distance), side), maxZ = ChunkKey.FloorDiv(checked(camera.Z + distance), side);
        long minY = ChunkKey.FloorDiv(terrain.MinimumY, side), maxY = ChunkKey.FloorDiv((long)terrain.MaximumYExclusive - 1, side);
        long count = checked((maxX - minX + 1) * (maxZ - minZ + 1) * (maxY - minY + 1));
        if (count > settings.MaximumSelectedChunks) throw new ArgumentException("Root forest exceeds the chunk budget; reduce --radius or increase --max-lod.");
        var roots = new List<ChunkKey>((int)count);
        for (long z = minZ; z <= maxZ; z++)
        for (long y = minY; y <= maxY; y++)
        for (long x = minX; x <= maxX; x++) roots.Add(new(x, y, z, settings.RootLod));
        return roots.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try
        {
            if (preparing is not null)
                try { await preparing.ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
        finally { await pipeline.DisposeAsync().ConfigureAwait(false); stopping.Dispose(); }
    }

    private sealed record PreparedPlan(LodMeshPlan Plan, long Epoch, (long X, long Y, long Z, int Projection) Cell, bool Bootstrap);
}

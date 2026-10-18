using System.Diagnostics;
using VoxelBrutal.Core.Streaming;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.Meshing;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Tests;

internal static class StreamingTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("Streaming rejects late mesh after eviction and re-request", StaleResults);
        yield return ("Streaming bounds requests, jobs and failed generation", BoundsAndErrors);
        yield return ("Upload backpressure retains completed meshes and bounds generation", Backpressure);
        yield return ("Streaming rejects cancelled topology work at an unchanged content revision", TopologyTickets);
        yield return ("Edits capture immutable halos and invalidate boundary neighbors", Edits);
    }

    private static void TopologyTickets()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var old = new ChunkMeshId(new ChunkKey(0, 0, 0), 3, 10);
        var replacement = old with { TopologyStamp = 11 };
        var pipeline = ChunkPipeline.CreateMeshing((id, ct) =>
        {
            if (id == old) { entered.Set(); release.Wait(ct); }
            return new ChunkMesh(id.Key, id.Revision, [], topologyStamp: id.TopologyStamp);
        }, new() { WorkerCount = 1 });
        try
        {
            pipeline.Request(old);
            pipeline.Tick(_ => { });
            Check(entered.Wait(3000), "Old topology build did not start");
            pipeline.RetainVersions(new HashSet<ChunkMeshId> { replacement });
            pipeline.Request(replacement);
            release.Set();
            var accepted = new List<ChunkMeshId>();
            Pump(pipeline, result => accepted.Add(result.Version), () => pipeline.IsReady(replacement));
            Check(accepted.SequenceEqual(new[] { replacement }) && pipeline.Statistics.Discarded == 1,
                "An old topology completion escaped into the replacement plan");
        }
        finally { release.Set(); pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void StaleResults()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var key = new ChunkKey(0, 0, 0);
        var pipeline = new ChunkPipeline((k, r, ct) =>
        {
            if (r == 1) { entered.Set(); release.Wait(ct); }
            return ChunkSnapshot.Create(k, new ConstantSource(0), r, ct);
        }, new() { WorkerCount = 1, MaximumTrackedChunks = 4 });
        try
        {
            pipeline.Request(key, 1);
            pipeline.Tick(_ => throw new Exception("Unexpected completion"));
            Check(entered.Wait(3000), "Worker did not start");
            pipeline.Retain(new HashSet<ChunkKey>());
            pipeline.Request(key, 2);
            release.Set();
            List<ulong> revisions = [];
            Pump(pipeline, r => revisions.Add(r.Revision), () => pipeline.IsReady(key));
            Check(revisions.SequenceEqual(new ulong[] { 2 }), "Stale revision became visible");
            Check(pipeline.Statistics.Discarded == 1, "Stale result not counted");
        }
        finally { release.Set(); pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void BoundsAndErrors()
    {
        var pipeline = new ChunkPipeline((_, _, _) => throw new InvalidDataException("expected failure"),
            new() { WorkerCount = 2, MaximumTrackedChunks = 4 });
        try
        {
            for (int i = 0; i < 4; i++) Check(pipeline.Request(new(i, 0, 0), 1), "Request unexpectedly refused");
            Check(!pipeline.Request(new(4, 0, 0), 1), "Residency limit not enforced");
            int failures = 0;
            Pump(pipeline, result =>
            {
                Check(result.Error is InvalidDataException && result.Mesh is null, "Error was swallowed");
                failures++;
            }, () => failures == 4);
            Check(pipeline.Statistics.InFlight == 0 && pipeline.Statistics.Failed == 4, "Work leaked");
            Check(pipeline.Request(new(0, 0, 0), 1), "Same-revision failure could not retry");
            Pump(pipeline, _ => failures++, () => failures == 5);
        }
        finally { pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void Backpressure()
    {
        var pipeline = new ChunkPipeline((key, revision, ct) => ChunkSnapshot.Create(key, new ConstantSource(0), revision, ct),
            new() { WorkerCount = 1, MaximumTrackedChunks = 4 });
        try
        {
            for (int i = 0; i < 4; i++) pipeline.Request(new(i, 0, 0), 0);
            bool refused = false;
            var timer = Stopwatch.StartNew();
            while (!refused && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                pipeline.Tick(_ => { refused = true; return false; }, 8, 20);
                Thread.Sleep(1);
            }
            Check(refused && pipeline.Statistics.Accepted == 0 && pipeline.Statistics.InFlight <= 2,
                "Upload backpressure did not bound in-flight results");
            var acceptedKeys = new HashSet<ChunkKey>();
            Pump(pipeline, result => Check(acceptedKeys.Add(result.Key), "Completion was delivered twice"), () => acceptedKeys.Count == 4);
            Check(pipeline.Statistics.Accepted == 4, "Backpressure lost a completed mesh");
        }
        finally { pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void Edits()
    {
        using var source = new EditableVoxelSource(new ConstantSource(0));
        var key = ChunkKey.FromWorld(-1, 0, 0);
        ChunkSnapshot before = source.Capture(key, source.Revision, default);
        var affected = source.SetVoxel(-1, 0, 0, 3, 2);
        ChunkSnapshot after = source.Capture(key, source.Revision, default);
        Check(before[31, 0, 0] == 0 && after[31, 0, 0] == 3, "Snapshot changed after publication");
        Check(affected.Contains(key) && affected.Contains(key.Neighbor(1, 0, 0)) &&
              affected.Contains(key.Neighbor(0, -1, -1)), "Halo invalidations incomplete");
        Check(after.Revision == 1, "Revision not preserved");
        ChunkSnapshot neighbor = source.Capture(key.Neighbor(1, 0, 0), source.Revision, default);
        Check(neighbor[-1, 0, 0] == 3, "Neighbor captured stale halo");
        bool rejected = false;
        try { source.SetVoxel(long.MaxValue, 0, 0, 4); }
        catch (OverflowException) { rejected = true; }
        Check(rejected && source.Revision == 1 && source.Sample(long.MaxValue, 0, 0, 0) == 0,
            "An invalid edge edit partially mutated the world");
    }

    private static void Pump(ChunkPipeline pipeline, Action<ChunkBuildResult> integrate, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            pipeline.Tick(integrate, 16, 20);
            Check(pipeline.Statistics.InFlight <= 4, "In-flight bound exceeded");
            Thread.Sleep(1);
        }
        Check(done(), "Pipeline timed out");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private sealed class ConstantSource(ushort material) : IVoxelSource
    {
        public ushort Sample(long x, long y, long z, int lod) => material;
    }
}

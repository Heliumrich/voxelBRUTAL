using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using VoxelBrutal.Core.Meshing;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Core.Streaming;

public sealed record StreamingOptions
{
    public int WorkerCount { get; init; } = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
    public int MaximumTrackedChunks { get; init; } = 16_384;
    public int MaximumDispatchPerTick { get; init; } = 8;
}

public readonly record struct ChunkBuildResult(ChunkKey Key, ulong Revision, ChunkMesh? Mesh, Exception? Error);
public readonly record struct StreamingStatistics(int Tracked, int Pending, int InFlight, long Accepted,
    long Discarded, long Failed, double LastBuildMilliseconds);

/// <summary>
/// Single-owner request state plus a fixed worker set. Tick/Request/Retain run on the render
/// thread. Workers only receive immutable tickets and emit immutable results. Work, completion
/// backlog and desired state are bounded; stale tickets cannot resurrect an evicted chunk.
/// </summary>
public sealed class ChunkPipeline : IAsyncDisposable
{
    private readonly StreamingOptions options;
    private readonly Func<ChunkKey, ulong, CancellationToken, ChunkSnapshot> capture;
    private readonly Dictionary<ChunkKey, Entry> entries = [];
    private readonly PriorityQueue<Work, (double Distance, long Sequence)> pending = new();
    private readonly Channel<Work> work;
    private readonly ConcurrentQueue<Completion> completed = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Task[] workers;
    private long sequence, accepted, discarded, failed;
    private int inFlight;
    private double lastBuildMilliseconds;
    private bool disposed;

    public ChunkPipeline(Func<ChunkKey, ulong, CancellationToken, ChunkSnapshot> capture,
        StreamingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        this.capture = capture;
        this.options = options ?? new();
        if (this.options.WorkerCount < 1 || this.options.MaximumTrackedChunks < 1 ||
            this.options.MaximumDispatchPerTick < 1) throw new ArgumentOutOfRangeException(nameof(options));
        work = Channel.CreateBounded<Work>(new BoundedChannelOptions(this.options.WorkerCount)
        {
            SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        workers = Enumerable.Range(0, this.options.WorkerCount).Select(_ => Task.Run(WorkerLoop)).ToArray();
    }

    public StreamingStatistics Statistics => new(entries.Count, entries.Values.Count(e => e.State == State.Pending),
        inFlight, accepted, discarded, failed, lastBuildMilliseconds);

    /// <returns>False when the explicit residency limit refuses a new request.</returns>
    public bool Request(ChunkKey key, ulong revision, double distancePriority = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(distancePriority)) throw new ArgumentOutOfRangeException(nameof(distancePriority));
        if (entries.TryGetValue(key, out Entry? old) && old.Revision == revision && old.State != State.Failed) return true;
        if (old is null && entries.Count >= options.MaximumTrackedChunks) return false;
        long ticket = checked(++sequence);
        entries[key] = new Entry(revision, ticket, State.Pending, distancePriority);
        pending.Enqueue(new Work(key, revision, ticket), (distancePriority, ticket));
        // Edits/teleports may invalidate many pending tickets before dispatch.
        if (pending.Count > options.MaximumTrackedChunks * 2) RebuildPending();
        return true;
    }

    public bool IsReady(ChunkKey key) => entries.TryGetValue(key, out Entry? e) && e.State == State.Ready;
    public bool Contains(ChunkKey key) => entries.ContainsKey(key);

    /// <summary>Drop unwanted CPU state. Callers also retire the corresponding GPU meshes.</summary>
    public void Retain(IReadOnlySet<ChunkKey> desired)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(desired);
        foreach (ChunkKey key in entries.Keys.Where(k => !desired.Contains(k)).ToArray()) entries.Remove(key);
        if (pending.Count > Math.Max(32, entries.Count * 2)) RebuildPending();
    }

    /// <summary>
    /// Time and count bound integration. The callback hands accepted results to the upload queue;
    /// GPU residency is deliberately separate from CPU readiness.
    /// </summary>
    public int Tick(Action<ChunkBuildResult> integrate, int maximumCompletions = 8, double milliseconds = 2)
    {
        ArgumentNullException.ThrowIfNull(integrate);
        return Tick(result => { integrate(result); return true; }, maximumCompletions, milliseconds);
    }

    /// <summary>
    /// A false acceptance leaves the result in the bounded completion backlog and pauses dispatch,
    /// allowing GPU upload backpressure to propagate to generation without losing a completed mesh.
    /// </summary>
    public int Tick(Func<ChunkBuildResult, bool> integrate, int maximumCompletions = 8, double milliseconds = 2)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(integrate);
        if (maximumCompletions < 0 || !double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCompletions));
        long start = Stopwatch.GetTimestamp();
        int integrated = 0;
        bool integrationBlocked = false;
        while (integrated < maximumCompletions && Stopwatch.GetElapsedTime(start).TotalMilliseconds <= milliseconds &&
               completed.TryPeek(out Completion completion))
        {
            Work ticket = completion.Work;
            if (!entries.TryGetValue(ticket.Key, out Entry? entry) || entry.Ticket != ticket.Ticket)
            { completed.TryDequeue(out _); inFlight--; discarded++; continue; }
            if (!integrate(new ChunkBuildResult(ticket.Key, ticket.Revision, completion.Mesh, completion.Error)))
            { integrationBlocked = true; break; }
            completed.TryDequeue(out _);
            inFlight--;
            lastBuildMilliseconds = completion.Milliseconds;
            entry.State = completion.Error is null ? State.Ready : State.Failed;
            if (completion.Error is null) accepted++; else failed++;
            integrated++;
        }

        int dispatched = 0;
        int attempts = 0;
        // inFlight includes completed items until integrated: a slow consumer applies backpressure.
        while (!integrationBlocked && dispatched < options.MaximumDispatchPerTick && attempts++ < 128 &&
               Stopwatch.GetElapsedTime(start).TotalMilliseconds <= milliseconds && inFlight < options.WorkerCount * 2 &&
               pending.TryPeek(out Work ticket, out _))
        {
            if (!entries.TryGetValue(ticket.Key, out Entry? entry) || entry.Ticket != ticket.Ticket)
            { pending.Dequeue(); continue; }
            if (!work.Writer.TryWrite(ticket)) break;
            pending.Dequeue();
            entry.State = State.Building;
            inFlight++;
            dispatched++;
        }
        return integrated;
    }

    private async Task WorkerLoop()
    {
        try
        {
            await foreach (Work ticket in work.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    ChunkSnapshot snapshot = capture(ticket.Key, ticket.Revision, stopping.Token);
                    ChunkMesh mesh = BinaryGreedyMesher.Build(snapshot, stopping.Token);
                    completed.Enqueue(new Completion(ticket, mesh, null, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (Exception ex) { completed.Enqueue(new Completion(ticket, null, ex, Stopwatch.GetElapsedTime(start).TotalMilliseconds)); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private void RebuildPending()
    {
        pending.Clear();
        foreach ((ChunkKey key, Entry entry) in entries)
            if (entry.State == State.Pending)
                pending.Enqueue(new Work(key, entry.Revision, entry.Ticket), (entry.Priority, entry.Ticket));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        work.Writer.TryComplete();
        await Task.WhenAll(workers).ConfigureAwait(false);
        stopping.Dispose();
    }

    private enum State { Pending, Building, Ready, Failed }
    private sealed class Entry(ulong revision, long ticket, State state, double priority)
    {
        public readonly ulong Revision = revision;
        public readonly long Ticket = ticket;
        public readonly double Priority = priority;
        public State State = state;
    }
    private readonly record struct Work(ChunkKey Key, ulong Revision, long Ticket);
    private readonly record struct Completion(Work Work, ChunkMesh? Mesh, Exception? Error, double Milliseconds);
}

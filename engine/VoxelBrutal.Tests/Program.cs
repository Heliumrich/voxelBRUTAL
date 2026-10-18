using System.Diagnostics;
using System.Text.Json;
using VoxelBrutal.Core.Meshing;
using VoxelBrutal.Core.World;
using VoxelBrutal.Tests;

int failed = 0;
var cases = WorldMeshingTests.Cases().Concat(MixedLodMeshingTests.Cases()).Concat(TerrainGenerationTests.Cases()).Concat(LodVisibilityTests.Cases()).Concat(StreamingTests.Cases()).Concat(RaycastTests.Cases());
foreach ((string name, Action test) in cases)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"Tests complete: {failed} failures.");
if (failed != 0) return 1;
if (args.Contains("--benchmark")) Benchmark();
return 0;

void Benchmark()
{
    var source = new TerrainGenerator();
    var keys = (from z in Enumerable.Range(-3, 6) from y in Enumerable.Range(0, 4)
                from x in Enumerable.Range(-3, 6) select new ChunkKey(x, y, z)).ToArray();
    // Warm the JIT separately; results are descriptive of this process and machine, not guarantees.
    for (int i = 0; i < 16; i++) BinaryGreedyMesher.Build(ChunkSnapshot.Create(keys[i], source));
    var snapshots = new List<ChunkSnapshot>();
    var generation = Stopwatch.StartNew();
    foreach (var key in keys) snapshots.Add(ChunkSnapshot.Create(key, source));
    generation.Stop();
    long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
    List<double> times = [];
    long quads = 0, faces = 0;
    foreach (var snapshot in snapshots)
    {
        long start = Stopwatch.GetTimestamp();
        ChunkMesh mesh = BinaryGreedyMesher.Build(snapshot);
        times.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        quads += mesh.QuadCount;
        faces += mesh.VisibleUnitFaceCount;
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
    times.Sort();
    var result = new
    {
        MeasuredUtc = DateTime.UtcNow,
        Runtime = Environment.Version.ToString(),
        ProcessorCount = Environment.ProcessorCount,
        ChunkSize = 32, Chunks = keys.Length,
        TerrainGenerationMeanMicroseconds = generation.Elapsed.TotalMicroseconds / keys.Length,
        MeshingMedianMicroseconds = times[times.Count / 2],
        MeshingP95Microseconds = times[(int)(times.Count * 0.95)],
        TotalQuads = quads, VisibleUnitFaces = faces, PackedQuadBytes = quads * 8,
        MeshingAllocatedBytes = allocated,
        Note = "Single-threaded warmed mixed terrain sample; includes empty/solid chunks. No GPU timing."
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    int outputIndex = Array.IndexOf(args, "--output");
    if (outputIndex >= 0 && outputIndex + 1 < args.Length)
    {
        string output = Path.GetFullPath(args[outputIndex + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}

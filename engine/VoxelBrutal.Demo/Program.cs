using System.Diagnostics;
using System.Text.Json;
using Brutal.GlfwApi;
using Brutal.Numerics;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.Streaming;
using VoxelBrutal.Core.World;
using VoxelBrutal.Demo;
using VoxelBrutal.Rendering;

bool stress = args.Contains("--stress");
bool smoke = args.Contains("--smoke") || stress;
int lod = Option("--lod", 0, 0, 6);
int radius = Option("--radius", smoke ? 3 : 12, 1, 64);
int frameLimit = Option("--frames", stress ? 900 : smoke ? 240 : 0, 0, 100_000);
int workerCount = Option("--workers", Math.Clamp(Environment.ProcessorCount - 2, 1, 8), 1, 32);
bool validation = !args.Contains("--no-validation");
long selectedSide = 32L << lod;
long selectedHeight = ChunkKey.FloorDiv(146, selectedSide) - ChunkKey.FloorDiv(-30, selectedSide) + 1;
if ((2L * radius + 1) * (2L * radius + 1) * selectedHeight > 16_384)
    throw new ArgumentException("View exceeds the 16,384-chunk CPU budget. Reduce --radius or increase --lod.");
Console.WriteLine("Voxel BRUTAL foundation — BRUTAL copyright (c) 2024 Ahwoo Limited");
Console.WriteLine("WASD fly · Space/Ctrl vertical · Shift fast · Right mouse / arrows look · E remove · Q place · Esc exit");
Console.WriteLine($"Uniform LOD {lod}, {1 << lod} voxel scale, chunk radius {radius}. Mixed spatial seams are gated in this milestone.");
if (!Glfw.Init()) throw new InvalidOperationException("GLFW initialization failed.");
try
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
    Glfw.WindowHint(GlfwWindowHint.Visible, smoke ? 0 : 1);
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Voxel BRUTAL — foundation", Size = new int2(1440, 900) });
    using var renderer = new VulkanChunkRenderer(window, new RendererOptions { EnableValidation = validation });
    using var source = new EditableVoxelSource(new TerrainGenerator());
    var pipeline = new ChunkPipeline(source.Capture, new StreamingOptions { WorkerCount = workerCount });
    try
    {
        var camera = new FlyCamera();
        var desired = new HashSet<ChunkKey>();
        var known = new HashSet<ChunkKey>();
        (long X, long Z)? lastCenter = null;
        var time = Stopwatch.StartNew();
        double previousTime = time.Elapsed.TotalSeconds, titleTime = 0;
        int frames = 0;
        bool wasRemove = false, wasPlace = false;
        Console.WriteLine($"GPU: {renderer.DeviceName}");
        while (!window.ShouldClose && (frameLimit == 0 || frames < frameLimit))
        {
            Glfw.PollEvents();
            if (window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press) break;
            double now = time.Elapsed.TotalSeconds;
            float delta = (float)Math.Clamp(now - previousTime, 0, 0.1);
            previousTime = now;
            if (!smoke) camera.Update(window, delta);
            if (stress)
            {
                camera.Move(new System.Numerics.Vector3(delta * 90, 0, -delta * 30));
                camera.Yaw += delta * .2f;
                if (frames == 200 || frames == 500) camera.Move(new System.Numerics.Vector3(640, 0, -640));
                if (frames == 100) window.Size = new int2(1000, 700);
                if (frames == 300) window.Size = new int2(1440, 900);
            }
            // The demo is a finite-height terrain view. Core chunk addressing and meshing are fully 3D.
            long side = 32L << lod;
            long cx = ChunkKey.FloorDiv(camera.Position.X, side), cz = ChunkKey.FloorDiv(camera.Position.Z, side);
            if (lastCenter != (cx, cz))
            {
                desired.Clear();
                int minY = (int)ChunkKey.FloorDiv(-30, side), maxY = (int)ChunkKey.FloorDiv(146, side);
                for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                for (int y = minY; y <= maxY; y++) desired.Add(new(cx + dx, y, cz + dz, lod));
                foreach (ChunkKey key in known.Where(k => !desired.Contains(k)).ToArray()) renderer.Remove(key);
                pipeline.Retain(desired);
                known.IntersectWith(desired);
                foreach (ChunkKey key in desired.OrderBy(camera.Position.DistanceSquaredTo))
                {
                    if (known.Contains(key)) continue;
                    if (!pipeline.Request(key, source.Revision, camera.Position.DistanceSquaredTo(key)))
                        throw new InvalidOperationException("Requested view exceeds MaximumTrackedChunks; reduce --radius.");
                    known.Add(key);
                }
                lastCenter = (cx, cz);
            }

            if (stress && frames % 60 == 30)
            {
                // Stress both replacement publication and halo invalidation while camera demand moves.
                long editX = camera.Position.X, editZ = camera.Position.Z - 64;
                long editY = new TerrainGenerator().HeightAt(editX, editZ);
                foreach (ChunkKey key in source.SetVoxel(editX, editY, editZ, 0, lod))
                    if (known.Contains(key)) pipeline.Request(key, source.Revision, 0);
            }

            bool remove = window.GetKey(GlfwKey.E) == GlfwKeyAction.Press;
            bool place = window.GetKey(GlfwKey.Q) == GlfwKeyAction.Press;
            if ((remove && !wasRemove) || (place && !wasPlace))
            {
                VoxelHit? hit = VoxelRaycast.Cast(source, camera.Position, camera.Forward, 256);
                if (hit is { } voxel)
                {
                    long x = remove ? voxel.X : voxel.PreviousX, y = remove ? voxel.Y : voxel.PreviousY, z = remove ? voxel.Z : voxel.PreviousZ;
                    foreach (ChunkKey key in source.SetVoxel(x, y, z, remove ? (ushort)0 : TerrainGenerator.Grass, lod))
                        if (known.Contains(key)) pipeline.Request(key, source.Revision, 0);
                }
            }
            wasRemove = remove; wasPlace = place;
            pipeline.Tick(result =>
            {
                if (result.Error is not null) throw new InvalidOperationException($"Chunk generation failed: {result.Key}", result.Error);
                return renderer.TryQueueUpload(result.Mesh!);
            }, maximumCompletions: 16, milliseconds: 2);

            var size = window.FramebufferSize;
            if (size.X > 0 && size.Y > 0)
            {
                var matrix = camera.ViewProjection(size.X / (float)size.Y);
                renderer.Draw(new RenderCamera(camera.Position, matrix));
            }
            else Thread.Sleep(16);
            frames++;
            if (now - titleTime >= 1)
            {
                var s = renderer.Statistics;
                window.Title = $"Voxel BRUTAL | {s.ResidentChunks:N0} chunks | {s.ResidentQuads:N0} quads | {pipeline.Statistics.Pending} pending | LOD {lod} | {renderer.DeviceName}";
                titleTime = now;
            }
            // Smoke waits for worker scheduling as well as driving real WSI/compute/draw submissions.
            if (smoke) Thread.Sleep(5);
            if (smoke && time.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Vulkan smoke test exceeded 45s.");
        }
        renderer.Dispose(); // Include all submitted work and destruction diagnostics in smoke validation.
        Console.WriteLine(JsonSerializer.Serialize(new { renderer.DeviceName, renderer.ValidationErrorCount, Renderer = renderer.Statistics,
            Streaming = pipeline.Statistics, ElapsedSeconds = time.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }));
        if (smoke && renderer.Statistics.ResidentQuads == 0) throw new InvalidOperationException("Smoke test submitted no terrain geometry.");
        if (smoke && renderer.ValidationErrorCount != 0) throw new InvalidOperationException("Vulkan validation reported errors.");
    }
    finally { pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
finally { Glfw.Terminate(); }
return 0;

int Option(string flag, int fallback, int minimum, int maximum)
{
    int index = Array.IndexOf(args, flag);
    if (index < 0) return fallback;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value < minimum || value > maximum)
        throw new ArgumentException($"{flag} needs an integer in [{minimum}, {maximum}].");
    return value;
}

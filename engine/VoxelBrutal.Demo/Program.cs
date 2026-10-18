using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Brutal.GlfwApi;
using Brutal.Numerics;
using VoxelBrutal.Core.Streaming;
using VoxelBrutal.Core.World;
using VoxelBrutal.Demo;
using VoxelBrutal.Rendering;

bool gpuValidation = args.Contains("--validate-occlusion");
bool stress = args.Contains("--stress");
bool smoke = args.Contains("--smoke") || stress || gpuValidation;
bool uniform = args.Contains("--uniform");
int lod = Option("--lod", 0, 0, 6);
int rootLod = Option("--max-lod", uniform ? lod : Math.Max(5, lod), lod, 10);
int radius = Option("--radius", smoke ? 12 : 32, 1, 256);
int budget = Option("--chunk-budget", 8192, 64, 16_384);
int frameLimit = Option("--frames", stress ? 1800 : smoke ? 1200 : 0, 0, 100_000);
int workerCount = Option("--workers", Math.Clamp(Environment.ProcessorCount - 2, 1, 8), 1, 32);
int quadBudgetMiB = Option("--quad-budget-mib", 128, 4, 512);
bool validation = !args.Contains("--no-validation");
string? screenshot = TextOption("--screenshot"), report = TextOption("--report");
var terrainSettings = new TerrainSettings { MinimumY = Option("--min-y", -128, -1_048_576, 1_048_576),
    MaximumYExclusive = Option("--max-y", 512, -1_048_576, 1_048_576), EnableCaves = !args.Contains("--no-caves") };
var terrain = new TerrainGenerator(settings: terrainSettings);
Console.WriteLine("Voxel BRUTAL — BRUTAL copyright (c) 2024 Ahwoo Limited");
Console.WriteLine("WASD fly · Space/Ctrl vertical · Shift fast · Right mouse / arrows look · E remove · Q place · Esc exit");
Console.WriteLine($"Cubic32 chunks; vertical [{terrainSettings.MinimumY},{terrainSettings.MaximumYExclusive}); {(uniform ? "uniform" : "adaptive seamless")} LOD {lod}..{rootLod}; GPU Hi-Z {!args.Contains("--no-occlusion")}");
if (!Glfw.Init()) throw new InvalidOperationException("GLFW initialization failed.");
try
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
    Glfw.WindowHint(GlfwWindowHint.Visible, smoke ? 0 : 1);
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Voxel BRUTAL", Size = new int2(1440, 900) });
    if (gpuValidation) return OcclusionValidation.Run(window, validation);
    using var renderer = new VulkanChunkRenderer(window, new RendererOptions { EnableValidation = validation,
        EnableOcclusion = !args.Contains("--no-occlusion"), EnableColorReadback = screenshot is not null,
        QuadCapacity = quadBudgetMiB * 1024 * 1024 / 8 });
    using var source = new EditableVoxelSource(terrain);
    var streamer = new WorldViewStreamer(source, terrainSettings, renderer, new WorldViewSettings
    {
        MinimumLod = lod, RootLod = rootLod, RadiusInBaseChunks = radius, MaximumSelectedChunks = budget,
        Uniform = uniform, Workers = workerCount, PixelError = Option("--pixel-error", 6, 1, 128)
    });
    try
    {
        var camera = new FlyCamera(new(0, terrain.HeightAt(0, 190) + 70, 190));
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
            float delta = (float)Math.Clamp(now - previousTime, 0, .1);
            previousTime = now;
            if (!smoke) camera.Update(window, delta);
            if (stress)
            {
                camera.Move(new System.Numerics.Vector3(delta * 40, 0, -delta * 15));
                camera.Yaw += delta * .1f;
                if (frames == 500 || frames == 1100) camera.Move(new System.Numerics.Vector3(512, 0, -512));
                if (frames == 200) window.Size = new int2(997, 613);
                if (frames == 800) window.Size = new int2(1440, 900);
                if (frames is 400 or 1000 or 1500)
                {
                    long x = camera.Position.X, z = camera.Position.Z - 64;
                    streamer.Edit(x, terrain.HeightAt(x, z), z, TerrainGenerator.Air);
                }
            }
            bool remove = window.GetKey(GlfwKey.E) == GlfwKeyAction.Press, place = window.GetKey(GlfwKey.Q) == GlfwKeyAction.Press;
            if ((remove && !wasRemove) || (place && !wasPlace))
            {
                if (VoxelRaycast.Cast(source, camera.Position, camera.Forward, 256) is { } hit)
                    streamer.Edit(remove ? hit.X : hit.PreviousX, remove ? hit.Y : hit.PreviousY,
                        remove ? hit.Z : hit.PreviousZ, remove ? TerrainGenerator.Air : TerrainGenerator.Grass);
            }
            wasRemove = remove; wasPlace = place;
            var size = window.FramebufferSize;
            if (size.X > 0 && size.Y > 0)
            {
                streamer.Update(camera.Position, size.Y / (2 * Math.Tan(70 * Math.PI / 360)));
                renderer.Draw(new RenderCamera(camera.Position, camera.ViewProjection(size.X / (float)size.Y)));
            }
            else Thread.Sleep(16);
            frames++;
            if (now - titleTime >= 1)
            {
                var s = renderer.Statistics;
                window.Title = $"Voxel BRUTAL | {streamer.PublishedChunks:N0}/{streamer.TargetChunks:N0} chunks | {s.ResidentQuads:N0} quads | {s.Occluded:N0} Hi-Z culled | {renderer.DeviceName}";
                titleTime = now;
            }
            if (smoke) Thread.Sleep(5);
            if (smoke && time.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Vulkan smoke test exceeded 90s.");
        }
        renderer.WaitForLastFrame();
        if (screenshot is not null)
        {
            ColorFrame color = renderer.LastColorFrame ?? throw new InvalidOperationException("No GPU color readback.");
            string path = Path.GetFullPath(screenshot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            output.Write(Encoding.ASCII.GetBytes($"P6\n{color.Width} {color.Height}\n255\n"));
            ReadOnlySpan<byte> bgra = color.Pixels.Span;
            for (int i = 0; i < bgra.Length; i += 4) { output.WriteByte(bgra[i + 2]); output.WriteByte(bgra[i + 1]); output.WriteByte(bgra[i]); }
        }
        renderer.Dispose();
        string json = JsonSerializer.Serialize(new { renderer.DeviceName, renderer.ValidationErrorCount, Renderer = renderer.Statistics,
            Streaming = streamer.Statistics, streamer.PublishedGeneration, streamer.PublishedChunks, streamer.TargetChunks,
            streamer.IsSettled, streamer.LodHistogram, streamer.CapacityCoarsenings, QuadBudgetMiB = quadBudgetMiB,
            VerticalRange = new[] { terrainSettings.MinimumY, terrainSettings.MaximumYExclusive },
            ElapsedSeconds = time.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (report is not null) { string path = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); }
        if (smoke && streamer.PublishedChunks == 0) throw new InvalidOperationException("Smoke test published no terrain cover.");
        if (smoke && streamer.Statistics.Failed != 0) throw new InvalidOperationException("Chunk generation failed.");
        if (smoke && renderer.ValidationErrorCount != 0) throw new InvalidOperationException("Vulkan validation reported errors.");
        if (smoke && !stress && !uniform && streamer.LodHistogram.Count < 2)
            throw new InvalidOperationException("Smoke test did not publish a mixed-resolution cover.");
    }
    finally { streamer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
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
string? TextOption(string flag)
{
    int index = Array.IndexOf(args, flag);
    if (index < 0) return null;
    if (index + 1 >= args.Length) throw new ArgumentException($"{flag} needs a value.");
    return args[index + 1];
}

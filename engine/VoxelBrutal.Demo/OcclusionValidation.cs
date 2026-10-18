using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Brutal.GlfwApi;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.Meshing;
using VoxelBrutal.Core.World;
using VoxelBrutal.Rendering;

namespace VoxelBrutal.Demo;

/// <summary>Actual GPU visibility/readback comparisons, independent of terrain generation and CPU Hi-Z.</summary>
internal static class OcclusionValidation
{
    private readonly record struct Pose(WorldPosition Position, Vector3 Forward);
    private sealed record Result(ColorFrame Color, HashSet<ChunkMeshId> Visible, RendererStatistics Statistics);
    private sealed record Comparison(string Name, int Width, int Height, int MaximumByteDifference,
        int DifferentBytes, int Early, int Late, int Occluded, int FrustumVisible);

    public static int Run(GlfwWindow window, bool validation = true)
    {
        using var renderer = new VulkanChunkRenderer(window, new RendererOptions
        {
            EnableValidation = validation, EnableOcclusion = true, EnableColorReadback = true,
            QuadCapacity = 8192, MaxChunks = 64, UploadBytesPerFrame = 64 * 1024,
            MaxPendingUploadBytes = 256 * 1024
        });
        var directory = OutputDirectory();
        Directory.CreateDirectory(directory);
        var comparisons = new List<Comparison>();
        var front = Box(new(0, 0, 2), 1, 0, 32, 2);
        var hidden = Box(new(0, 0, 0), 1, 0, 32, 3);
        var side = Box(new(2, 0, 0), 1, 0, 32, 5);
        renderer.QueueUpload(front); renderer.QueueUpload(hidden); renderer.QueueUpload(side);
        var active = new HashSet<ChunkMeshId> { front.Version, hidden.Version, side.Version };
        renderer.SetActiveMeshes(active);
        var original = new Pose(new(16, 16, 128), -Vector3.UnitZ);

        Result Capture(Pose pose, bool enabled, int frames = 1)
        {
            renderer.OcclusionEnabled = enabled;
            for (int frame = 0; frame < frames; frame++)
            {
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    Glfw.PollEvents();
                    var size = window.FramebufferSize;
                    Require(size.X > 0 && size.Y > 0, "Validation window is minimized.");
                    var camera = new RenderCamera(pose.Position, ViewProjection(pose.Forward, size.X / (float)size.Y));
                    if (renderer.Draw(camera)) break;
                    if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Swapchain did not become drawable.");
                    Thread.Sleep(1);
                }
                renderer.WaitForLastFrame();
            }
            var color = renderer.LastColorFrame ?? throw new InvalidOperationException("GPU color readback was not produced.");
            Require(color.Serial == renderer.Statistics.SubmittedFrames && color.Serial == renderer.Statistics.ReadbackSerial,
                "Color and visibility readbacks must belong to the exact submitted frame.");
            Require(renderer.ValidationErrorCount == 0, "Vulkan validation reported an error.");
            return new(color, renderer.LastVisibleMeshes.ToHashSet(), renderer.Statistics);
        }

        void Compare(string name, Result baseline, Result actual, bool save = false)
        {
            Require(baseline.Color.Width == actual.Color.Width && baseline.Color.Height == actual.Color.Height,
                $"{name}: compared different framebuffer sizes.");
            var expected = baseline.Color.Pixels.Span;
            var rendered = actual.Color.Pixels.Span;
            Require(expected.Length == rendered.Length, $"{name}: readback length changed.");
            int maximum = 0, different = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                int delta = Math.Abs(expected[i] - rendered[i]);
                maximum = Math.Max(maximum, delta);
                if (delta != 0) different++;
            }
            var s = actual.Statistics;
            comparisons.Add(new(name, actual.Color.Width, actual.Color.Height, maximum, different,
                s.EarlyVisible, s.LateVisible, s.Occluded, s.FrustumVisible));
            if (save || maximum > 1)
            {
                SavePpm(Path.Combine(directory, name + "-baseline.ppm"), baseline.Color);
                SavePpm(Path.Combine(directory, name + "-actual.ppm"), actual.Color);
            }
            Require(maximum <= 1, $"{name}: GPU occlusion changed {different:N0} color bytes, maximum error {maximum}; captures: {directory}");
            Console.WriteLine($"PASS GPU {name}: {actual.Color.Width}x{actual.Color.Height}, max byte delta {maximum}, "
                + $"early {s.EarlyVisible}, late {s.LateVisible}, occluded {s.Occluded}, visible {actual.Visible.Count}");
        }

        // A baseline that submits every frustum-visible chunk, then converge the real two-pass GPU culler.
        var baseline = Capture(original, false, 2);
        Require(baseline.Visible.SetEquals(active), "The fixture must put all three chunk bounds in the frustum.");
        var culled = Capture(original, true, 5);
        Require(culled.Statistics.Occluded > 0 && !culled.Visible.Contains(hidden.Version),
            "The front solid must occlude the hidden solid in the actual GPU visibility readback.");
        Require(culled.Visible.Contains(front.Version) && culled.Visible.Contains(side.Version),
            "Visible occluder and side solid must survive GPU culling.");
        Compare("hidden-solid", baseline, culled, save: true);

        // Compare the first frame after removal, before an off-mode baseline can reset history.
        renderer.Remove(front.Version);
        active.Remove(front.Version);
        var exposed = Capture(original, true);
        Require(exposed.Visible.Contains(hidden.Version), "Removed occluder left a stale hidden object on the first frame.");
        Compare("remove-occluder-first-frame", Capture(original, false), exposed);

        var restored = Box(front.Key, 2, 0, 32, 2);
        renderer.QueueUpload(restored);
        Capture(original, true); // Upload while the prior active set remains intact.
        Require(renderer.IsResident(restored.Version), "Restored mesh version did not upload.");
        active.Add(restored.Version);
        var restoredFrame = Capture(original, true, 5);
        Compare("restore-occluder", Capture(original, false), restoredFrame);
        Require(!restoredFrame.Visible.Contains(hidden.Version), "Restored occluder never removes the hidden object from history.");
        Capture(original, true, 5);

        var rotated = new Pose(original.Position, Vector3.Normalize(new(64, 0, -112)));
        var rotatedFirst = Capture(rotated, true);
        Compare("rotate-first-frame", Capture(rotated, false), rotatedFirst);
        Capture(original, true, 5);
        var teleported = new Pose(new(80, 16, 128), -Vector3.UnitZ);
        var teleportedFirst = Capture(teleported, true);
        Require(teleportedFirst.Visible.Contains(hidden.Version), "Teleport must reveal the previously hidden solid immediately.");
        Compare("teleport-first-frame", Capture(teleported, false), teleportedFirst);

        // Bounds touch/cross the near plane and contain the eye; both must bypass unsafe projection.
        var near = new Pose(new(16, 16, 32, 0, 0, .1), -Vector3.UnitZ);
        var nearFirst = Capture(near, true);
        Require(nearFirst.Visible.Contains(hidden.Version), "Near-plane crossing AABB was incorrectly rejected.");
        Compare("near-plane-crossing", Capture(near, false), nearFirst);
        var inside = new Pose(new(16, 16, 16), -Vector3.UnitZ);
        var insideFirst = Capture(inside, true);
        Require(insideFirst.Visible.Contains(hidden.Version), "Eye-containing AABB was incorrectly rejected.");
        Compare("eye-inside-bounds", Capture(inside, false), insideFirst);

        // Same key, new material/revision/topology: old and new versions coexist until activation.
        var changed = Box(restored.Key, 3, 0, 32, 5, topologyStamp: 73);
        Capture(original, true, 5);
        var oldAppearance = Capture(original, true);
        renderer.QueueUpload(changed);
        Capture(original, true);
        Require(renderer.IsResident(restored.Version) && renderer.IsResident(changed.Version),
            "Publication requires old/new versions to coexist before activation.");
        active.Remove(restored.Version); active.Add(changed.Version);
        var changedFirst = Capture(original, true);
        Require(changedFirst.Visible.Contains(changed.Version) && !changedFirst.Visible.Contains(restored.Version),
            "Visibility history must use full mesh identities, including topology stamp.");
        var changedBaseline = Capture(original, false);
        Compare("mesh-version-replacement", changedBaseline, changedFirst);
        Require(!oldAppearance.Color.Pixels.Span.SequenceEqual(changedBaseline.Color.Pixels.Span),
            "Revision/material fixture did not change the rendered image.");
        renderer.RetainMeshes(active);
        Require(!renderer.IsResident(restored.Version), "Old version did not retire after activation.");

        window.Size = new Brutal.Numerics.int2(997, 613);
        var resizeTimer = Stopwatch.StartNew();
        while ((window.FramebufferSize.X == baseline.Color.Width || window.FramebufferSize.Y == baseline.Color.Height)
            && resizeTimer.Elapsed < TimeSpan.FromSeconds(2)) { Glfw.PollEvents(); Thread.Sleep(1); }
        var oddBaseline = Capture(original, false, 2);
        Require((oddBaseline.Color.Width & (oddBaseline.Color.Width - 1)) != 0
            && (oddBaseline.Color.Height & (oddBaseline.Color.Height - 1)) != 0, "Resize must exercise a non-power-of-two viewport.");
        Compare("npot-resize", oddBaseline, Capture(original, true, 5), save: true);

        // Packed position/extent carry bits: a 64-square half-step face must reproduce a regular 32-square face.
        var ordinary = Box(new(0, 0, 0), 10, 0, 32, 4);
        var half = Box(ordinary.Key, 11, 0, 64, 4, half: true, topologyStamp: 211);
        active = [ordinary.Version]; renderer.SetActiveMeshes(active);
        renderer.QueueUpload(ordinary);
        var ordinaryColor = Capture(original, false, 2);
        renderer.QueueUpload(half); Capture(original, true);
        active = [half.Version]; renderer.SetActiveMeshes(active);
        Compare("half-grid-carry-decode", ordinaryColor, Capture(original, true, 3), save: true);

        // True half-coordinate positions: translate both shape and camera by .5 for identical relative geometry.
        var integerBox = Box(ordinary.Key, 12, 0, 31, 4);
        var fractionBox = Box(ordinary.Key, 13, 1, 63, 4, half: true, topologyStamp: 213);
        renderer.QueueUpload(integerBox); active = [integerBox.Version]; renderer.SetActiveMeshes(active);
        var integerColor = Capture(original, false, 2);
        renderer.QueueUpload(fractionBox); Capture(original, true);
        active = [fractionBox.Version]; renderer.SetActiveMeshes(active);
        var translated = new Pose(new(16, 16, 128, .5, .5, .5), -Vector3.UnitZ);
        Compare("half-grid-fractional-decode", integerColor, Capture(translated, true, 3));

        Require(renderer.ValidationErrorCount == 0, "Vulkan validation errors occurred during GPU fixture tests.");
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new
        {
            renderer.DeviceName, renderer.ValidationErrorCount, Cases = comparisons,
            Note = "Actual Vulkan BGRA8 readbacks; maximum permitted channel difference is one byte. Visibility is GPU-written."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"GPU occlusion validation passed: {comparisons.Count} comparisons, "
            + $"{renderer.ValidationErrorCount} Vulkan errors. Artifacts: {directory}");
        return 0;
    }

    private static ChunkMesh Box(ChunkKey key, ulong revision, int minimum, int maximum, ushort material,
        bool half = false, ulong topologyStamp = 0)
    {
        int extent = maximum - minimum;
        PackedQuad[] quads =
        [
            new(minimum, minimum, minimum, extent, extent, FaceDirection.NegativeX, material, half),
            new(maximum, minimum, minimum, extent, extent, FaceDirection.PositiveX, material, half),
            new(minimum, minimum, minimum, extent, extent, FaceDirection.NegativeY, material, half),
            new(minimum, maximum, minimum, extent, extent, FaceDirection.PositiveY, material, half),
            new(minimum, minimum, minimum, extent, extent, FaceDirection.NegativeZ, material, half),
            new(minimum, minimum, maximum, extent, extent, FaceDirection.PositiveZ, material, half)
        ];
        return new(key, revision, quads, topologyStamp: topologyStamp);
    }

    private static Matrix4x4 ViewProjection(Vector3 forward, float aspect)
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.Normalize(forward), Vector3.UnitY);
        float f = 1 / MathF.Tan(70 * MathF.PI / 360);
        return view * new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, .2f, 0);
    }

    private static void SavePpm(string path, ColorFrame frame)
    {
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"));
        var pixels = frame.Pixels.Span;
        byte[] rgb = new byte[checked(frame.Width * 3)];
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int index = (y * frame.Width + x) * 4;
                rgb[x * 3] = pixels[index + 2]; rgb[x * 3 + 1] = pixels[index + 1]; rgb[x * 3 + 2] = pixels[index];
            }
            stream.Write(rgb);
        }
    }

    private static string OutputDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "engine", "VoxelBrutal.slnx"))) current = current.Parent;
        return Path.Combine(current?.FullName ?? Environment.CurrentDirectory, ".artifacts", "occlusion-validation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

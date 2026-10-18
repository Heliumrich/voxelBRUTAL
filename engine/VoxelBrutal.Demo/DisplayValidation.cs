using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Rendering;

namespace VoxelBrutal.Demo;

/// <summary>Opt-in native ImGui input, real WSI transitions, timestamps and pacing integration checks.</summary>
internal static class DisplayValidation
{
    public static int Run(GlfwWindow window, bool validation, string? screenshot, string? report)
    {
        var settings = new DisplaySettings();
        Require(settings.Borderless && settings.FrameRateLimit == 60 && settings.PresentMode == VkPresentModeKHR.MailboxKHR,
            "Unexpected display defaults.");
        int2 originalSize = window.Size, originalPosition = window.Position;
        var modes = new WindowModeController(window);
        modes.Apply(settings.Borderless);
        Glfw.PollEvents();
        Require(window.GetAttribute(GlfwWindowAttribute.Decorated) == 0, "Borderless window has decorations.");
        bool coversMonitor = false;
        foreach (var monitor in Glfw.GetMonitors())
            coversMonitor |= monitor.Size == window.Size && monitor.Position == window.Position;
        Require(coversMonitor, "Borderless bounds do not cover a monitor.");
        using var renderer = new VulkanChunkRenderer(window, new RendererOptions
        {
            EnableValidation = validation, EnableColorReadback = true, QuadCapacity = 8192, MaxChunks = 64,
            UploadBytesPerFrame = 64 * 1024, MaxPendingUploadBytes = 256 * 1024
        });
        renderer.LimitPresentationQueue = true;
        using var overlay = new ImGuiOverlay(window, renderer, settings) { SettingsOpen = true };
        using var usage = new GpuUsageMonitor(renderer.AdapterLuid, renderer.DeviceUuid);
        using var pacer = new FramePacer();
        var metrics = new FrameMetrics();
        var time = Stopwatch.StartNew();
        double previous = 0;
        void Render()
        {
            pacer.Wait(settings.FrameRateLimit);
            renderer.PrepareFrame();
            Glfw.PollEvents();
            double now = time.Elapsed.TotalSeconds, delta = now - previous;
            previous = now; metrics.Add(delta);
            overlay.BeginFrame(delta);
            overlay.Build(metrics.FramesPerSecond, metrics.FrameMilliseconds, usage.GpuUsagePercent);
            overlay.EndFrame();
            modes.Apply(settings.Borderless);
            if (renderer.RequestedPresentMode != settings.PresentMode) renderer.SetPresentMode(settings.PresentMode);
            var size = window.FramebufferSize;
            Require(size.X > 0 && size.Y > 0, "Validation window is minimized.");
            var camera = new FlyCamera(new WorldPosition(0, 64, 190));
            renderer.Draw(new RenderCamera(camera.Position, camera.ViewProjection(size.X / (float)size.Y)));
            renderer.WaitForLastFrame();
            Require(renderer.ValidationErrorCount == 0, "Vulkan validation reported an error.");
        }
        for (int i = 0; i < 4; i++) Render();
        OverlayInputValidation.Run(overlay, settings, Render);
        var exercisedModes = new List<string>();
        foreach (var mode in renderer.SupportedPresentModes.ToArray())
        {
            settings.PresentMode = mode;
            for (int i = 0; i < 4; i++) Render();
            Require(renderer.PresentMode == mode, $"Present mode {mode} failed to apply.");
            exercisedModes.Add(mode.ToString());
        }
        settings.PresentMode = (VkPresentModeKHR)int.MaxValue;
        Render(); Render();
        Require(renderer.PresentMode == VkPresentModeKHR.FifoKHR && renderer.PresentModeFallbackReason is not null,
            "Unavailable present mode did not fall back to FIFO with an explanation.");
        settings.PresentMode = VkPresentModeKHR.MailboxKHR;
        // Force incompatible recreation as well as compatible dynamic switches while ImGui owns resources.
        for (int i = 0; i < 6; i++)
        {
            settings.Borderless = i % 2 != 0;
            for (int frame = 0; frame < 3; frame++) Render();
            Require((window.GetAttribute(GlfwWindowAttribute.Decorated) == 0) == settings.Borderless,
                "Window decoration does not match the selected mode.");
            if (!settings.Borderless)
                Require(window.Size == originalSize && window.Position == originalPosition, "Windowed bounds were not restored.");
        }
        // Deadline timer measurement is independent of display refresh and validation/readback GPU overhead.
        var pacing = new List<object>();
        foreach (int cap in new[] { 30, 60, 144, 360 })
        {
            pacer.Wait(cap);
            var clock = Stopwatch.StartNew();
            int count = Math.Max(12, cap / 2);
            for (int frame = 0; frame < count; frame++) pacer.Wait(cap);
            double fps = count / clock.Elapsed.TotalSeconds;
            Require(fps <= cap * 1.05 && fps >= cap * .80, $"Frame pacing {cap}: measured {fps:0.0} FPS.");
            pacing.Add(new { Cap = cap, MeasuredFps = fps });
        }
        settings.FrameRateLimit = 60; settings.Borderless = true; settings.PresentMode = VkPresentModeKHR.MailboxKHR;
        for (int i = 0; i < 90; i++) Render();
        Require(renderer.GpuFrameMs is > 0, "No GPU timestamp result was returned.");
        ColorFrame color = renderer.LastColorFrame ?? throw new InvalidOperationException("No UI readback image.");
        if (screenshot is not null) SavePpm(Path.GetFullPath(screenshot), color);
        // Explicit ordered teardown also subjects backend and WSI resource retirement to validation.
        overlay.Dispose(); renderer.Dispose();
        Require(renderer.ValidationErrorCount == 0, "Vulkan teardown validation reported an error.");
        string json = JsonSerializer.Serialize(new
        {
            renderer.DeviceName, renderer.ValidationErrorCount, renderer.GpuFrameMs,
            usage.GpuUsagePercent, usage.Source, usage.Status,
            renderer.SwapchainMaintenanceExtension, renderer.PresentWaitSupported, renderer.DynamicPresentModeSwitching,
            ActualPresentMode = renderer.PresentMode.ToString(), renderer.PresentModeFallbackReason,
            UnavailableModeFallbackVerified = true,
            Window = new { modes.Borderless, Width = window.Size.X, Height = window.Size.Y },
            pacer.TimerKind, Pacing = pacing, PresentModes = exercisedModes,
            Input = "Native Ctrl+click typing: 144, 1=>30, 999=>360, 60; F1 toggle and input capture"
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (report is not null)
        {
            string path = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json);
        }
        return 0;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void SavePpm(string path, ColorFrame color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"P6\n{color.Width} {color.Height}\n255\n"));
        var pixels = color.Pixels.Span;
        for (int i = 0; i < pixels.Length; i += 4)
        { file.WriteByte(pixels[i + 2]); file.WriteByte(pixels[i + 1]); file.WriteByte(pixels[i]); }
    }
}

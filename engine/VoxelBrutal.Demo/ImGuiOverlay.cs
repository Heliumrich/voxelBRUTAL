using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.VulkanApi;
using VoxelBrutal.Rendering;

namespace VoxelBrutal.Demo;

/// <summary>BRUTAL's official GLFW input and Vulkan ImGui backends, with owner-thread lifecycle.</summary>
public sealed class ImGuiOverlay : IDisposable
{
    private readonly VulkanChunkRenderer renderer;
    private readonly GlfwWindow window;
    private readonly DisplaySettings settings;
    private readonly ImGuiContextPtr context;
    private GlfwBackend? platform;
    private VulkanBackend? backend;
    private bool inFrame, drawReady, disposed;
    private float uiScale = 1;
    internal Action<ImGuiIOPtr>? ValidationInput { get; set; }
    public bool SettingsOpen { get; set; }
    public bool WantsCaptureMouse => SettingsOpen || ImGui.GetIO().WantCaptureMouse;
    public bool WantsCaptureKeyboard => SettingsOpen || ImGui.GetIO().WantCaptureKeyboard;
    public float2 FrameRateControlCenter { get; private set; }
    public float2 WindowModeControlCenter { get; private set; }
    public float2 PresentModeControlCenter { get; private set; }

    public ImGuiOverlay(GlfwWindow window, VulkanChunkRenderer renderer, DisplaySettings settings)
    {
        this.renderer = renderer;
        this.window = window;
        this.settings = settings;
        context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            io.ConfigFlags = ImGuiConfigFlags.NavEnableKeyboard;
            io.IniFilename = default; // Runtime choices are owned by DisplaySettings, with no implicit file persistence.
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();
            style.WindowRounding = 6;
            style.FrameRounding = 4;
            style.WindowPadding = new float2(14, 12);
            style.ItemSpacing = new float2(9, 8);
            style.FontScaleMain = 17f / 13f;

            platform = new GlfwBackend(window, GlfwBackend.ClientApi.Vulkan);
            var gpu = renderer.OverlayContext;
            backend = new VulkanBackend(new VulkanBackend.CreateInfo
            {
                Device = gpu.Device,
                GraphicsQueue = gpu.GraphicsQueue,
                RenderPass = gpu.RenderPass,
                MinImageCount = 2,
                ImageCount = 2,
                SampleCount = VkSampleCountFlags._1Bit,
                DescriptorPoolSize = 256
            });
            renderer.OverlayRender = Render;
        }
        catch
        {
            backend?.Dispose();
            platform?.Dispose();
            ImGui.DestroyContext(context);
            throw;
        }
    }

    public void BeginFrame(double deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (inFrame) throw new InvalidOperationException("The previous ImGui frame has not ended.");
        ImGui.SetCurrentContext(context);
        ImString.SharedStorage.Clear();
        backend!.NewFrame();
        platform!.NewFrame();
        UpdateDisplayScale();
        if (double.IsFinite(deltaSeconds) && deltaSeconds > 0)
            ImGui.GetIO().DeltaTime = (float)Math.Clamp(deltaSeconds, 0.000001, 1);
        // Inject native ImGui events after the platform poll so hidden-window GPU tests can exercise widgets.
        ValidationInput?.Invoke(ImGui.GetIO());
        ImGui.NewFrame();
        inFrame = true;
        drawReady = false;
        if (ImGui.IsKeyPressed(ImGuiKey.F1, repeat: false)) SettingsOpen = !SettingsOpen;
    }

    public void Build(double fps, double frameMilliseconds, double? gpuPercent)
    {
        if (!inFrame) throw new InvalidOperationException("BeginFrame must precede UI construction.");
        ImGui.SetNextWindowPos(new float2(12 * uiScale, 12 * uiScale), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(.86f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNavFocus;
        if (ImGui.Begin("Performance##voxel-hud"u8, flags))
        {
            ImGui.Text($"{fps:0} FPS   {frameMilliseconds:0.00} ms/frame");
            ImGui.Text(gpuPercent is { } usage && double.IsFinite(usage)
                ? $"GPU usage (device)   {usage:0.0}%" : "GPU usage (device)   unavailable");
            ImGui.Text(renderer.GpuFrameMs is { } gpuMilliseconds && double.IsFinite(gpuMilliseconds)
                ? $"GPU frame   {gpuMilliseconds:0.00} ms" : "GPU frame   warming up");
            ImGui.TextDisabled(renderer.DeviceName);
            if (ImGui.Button("Settings  [F1]"u8)) SettingsOpen = !SettingsOpen;
        }
        ImGui.End();

        if (SettingsOpen) BuildSettings();
    }

    private void BuildSettings()
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new float2(Math.Max(24 * uiScale, (io.DisplaySize.X - 460 * uiScale) * .5f),
            Math.Max(24 * uiScale, (io.DisplaySize.Y - 290 * uiScale) * .5f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(460 * uiScale, 0), ImGuiCond.FirstUseEver);
        bool open = SettingsOpen;
        if (ImGui.Begin("Display settings"u8, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SeparatorText("Display"u8);
            string currentMode = settings.Borderless ? "Borderless" : "Windowed";
            if (ImGui.BeginCombo("Window mode"u8, currentMode))
            {
                if (ImGui.Selectable("Borderless"u8, settings.Borderless)) settings.Borderless = true;
                if (ImGui.Selectable("Windowed"u8, !settings.Borderless)) settings.Borderless = false;
                ImGui.EndCombo();
            }
            WindowModeControlCenter = ItemCenter();

            ImGui.SetNextItemWidth(285 * uiScale);
            // Native Dear ImGui Ctrl+click text entry remains enabled. AlwaysClamp also bounds typed values.
            ImGui.SliderInt("Frame rate"u8, ref settings.FrameRateLimit, 30, 360, "%d FPS"u8, ImGuiSliderFlags.AlwaysClamp);
            FrameRateControlCenter = ItemCenter();
            ImGui.TextDisabled("Ctrl+click the frame rate to type a value."u8);

            if (ImGui.BeginCombo("Present mode"u8, PresentModeName(settings.PresentMode)))
            {
                foreach (var mode in renderer.SupportedPresentModes.Distinct())
                {
                    bool selected = mode == settings.PresentMode;
                    if (ImGui.Selectable(PresentModeName(mode), selected)) settings.PresentMode = mode;
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            PresentModeControlCenter = ItemCenter();
            ImGui.TextDisabled("Present modes are reported by this display and GPU."u8);
            ImGui.Text($"Active: {PresentModeName(renderer.PresentMode)}");
            if (renderer.PresentModeFallbackReason is { } fallback) ImGui.Text(fallback);
            ImGui.Separator();
            ImGui.Text("F1 closes settings. Changes apply immediately."u8);
        }
        ImGui.End();
        SettingsOpen = open;
    }

    public void EndFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!inFrame) throw new InvalidOperationException("No ImGui frame is active.");
        ImGui.Render();
        inFrame = false;
        drawReady = true;
    }

    private void Render(Brutal.VulkanApi.CommandBuffer command)
    {
        if (!drawReady || disposed) return;
        ImGui.SetCurrentContext(context);
        backend!.RenderDrawData(ImGui.GetDrawData(), command);
    }

    public static string PresentModeName(VkPresentModeKHR mode) => mode switch
    {
        VkPresentModeKHR.MailboxKHR => "Mailbox",
        VkPresentModeKHR.FifoKHR => "FIFO (V-Sync)",
        VkPresentModeKHR.ImmediateKHR => "Immediate",
        VkPresentModeKHR.FifoRelaxedKHR => "FIFO relaxed",
        VkPresentModeKHR.FifoLatestReadyKHR => "FIFO latest ready",
        _ => mode.ToString()
    };

    private static float2 ItemCenter()
    {
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        return new((minimum.X + maximum.X) * .5f, (minimum.Y + maximum.Y) * .5f);
    }

    private void UpdateDisplayScale()
    {
        var io = ImGui.GetIO();
        float pixelRatio = io.DisplayFramebufferScale.X;
        float contentScale = window.ContentScale.X;
        // Windows commonly uses pixelRatio=1 at higher desktop DPI; Retina already applies its pixel ratio.
        float requested = float.IsFinite(contentScale) && contentScale > 0 && float.IsFinite(pixelRatio) && pixelRatio > 0
            ? Math.Clamp(contentScale / pixelRatio, 1, 4) : 1;
        if (MathF.Abs(requested - uiScale) < .001f) return;
        var style = ImGui.GetStyle();
        style.ScaleAllSizes(requested / uiScale);
        style.FontScaleMain = (17f / 13f) * requested;
        uiScale = requested;
    }

    public void Dispose()
    {
        if (disposed) return;
        renderer.WaitIdle();
        renderer.OverlayRender = null;
        ImGui.SetCurrentContext(context);
        if (inFrame) ImGui.EndFrame();
        backend?.Dispose();
        platform?.Dispose();
        ImGui.DestroyContext(context);
        disposed = true;
    }
}

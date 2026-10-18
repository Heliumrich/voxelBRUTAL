using System.Runtime.InteropServices;
using Brutal.VulkanApi;

namespace VoxelBrutal.Rendering;

public sealed unsafe partial class VulkanChunkRenderer
{
    private bool _surfaceCapabilities2, _surfaceMaintenanceKhr, _surfaceMaintenanceExt;
    private bool _swapchainMaintenance, _presentWaitSupported, _presentModeDirty;
    private bool _fifoLatestReadyEnabled;
    private VkPresentModeKHR _requestedPresentMode;
    private VkPresentModeKHR[] _switchablePresentModes = [];
    private VkFence[] _presentationFences = [];
    private bool[] _presentationFencePending = [];
    private long _presentSerial;
    private long _lastPresentId;
    private long _preparedPresentId = -1;
    public IReadOnlyList<VkPresentModeKHR> SupportedPresentModes { get; private set; } = Array.Empty<VkPresentModeKHR>();
    public VkPresentModeKHR PresentMode { get; private set; } = VkPresentModeKHR.FifoKHR;
    public VkPresentModeKHR RequestedPresentMode => _requestedPresentMode;
    public string? PresentModeFallbackReason { get; private set; }
    public string? SwapchainMaintenanceExtension { get; private set; }
    public bool PresentWaitSupported => _presentWaitSupported;
    public bool DynamicPresentModeSwitching => _swapchainMaintenance && _switchablePresentModes.Length > 1;
    /// <summary>Optional bounded host wait for an older present; disabled unless explicitly requested.</summary>
    public bool LimitPresentationQueue { get; set; }

    /// <summary>Call before polling input to wait for CPU/GPU frame capacity before sampling controls.</summary>
    public void PrepareFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = _frames[_frameIndex];
        Check(_device.WaitForFences([frame.Fence], true, -1), "prepare frame capacity");
        _completed = Math.Max(_completed, frame.Serial);
        ProcessReadback(frame);
        if (!LimitPresentationQueue || !_presentWaitSupported || _lastPresentId <= 1 || _preparedPresentId == _lastPresentId) return;
        _preparedPresentId = _lastPresentId;
        // Keep at most the newest presentation ahead when the presentation engine makes progress.
        // A finite timeout keeps input/window events responsive if the surface is occluded.
        var result = _device.WaitForPresentKHR(_swapchain, (nint)(_lastPresentId - 1), 16_000_000);
        if (result is not (VkResult.Success or VkResult.Timeout or VkResult.ErrorOutOfDateKHR)) Check(result, "presentation queue throttle");
    }

    public void SetPresentMode(VkPresentModeKHR mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _requestedPresentMode = mode;
        _presentModeDirty = true;
    }

    private void AddModernSurfaceExtensions(HashSet<string> extensions)
    {
        var properties = new VkExtensionProperties[Vulkan.EnumerateInstanceExtensionProperties_Count([])];
        Vulkan.EnumerateInstanceExtensionProperties_Write([], properties);
        HashSet<string> names = [];
        for (var i = 0; i < properties.Length; ++i)
            fixed (byte* name = properties[i].ExtensionName) names.Add(Marshal.PtrToStringUTF8((nint)name)!);
        _surfaceCapabilities2 = names.Contains("VK_KHR_get_surface_capabilities2");
        if (!_surfaceCapabilities2) return;
        extensions.Add("VK_KHR_get_surface_capabilities2");
        _surfaceMaintenanceKhr = names.Contains("VK_KHR_surface_maintenance1");
        _surfaceMaintenanceExt = names.Contains("VK_EXT_surface_maintenance1");
        if (_surfaceMaintenanceKhr) extensions.Add("VK_KHR_surface_maintenance1");
        if (_surfaceMaintenanceExt) extensions.Add("VK_EXT_surface_maintenance1");
    }

    private VkPresentModeKHR ResolvePresentMode() => SupportedPresentModes.Contains(_requestedPresentMode)
        ? _requestedPresentMode : VkPresentModeKHR.FifoKHR;

    private void ApplyPresentModeRequest()
    {
        if (!_presentModeDirty) return;
        var desired = ResolvePresentMode();
        if (desired != PresentMode && !(_swapchainMaintenance && _switchablePresentModes.Contains(desired)))
            RecreateSwapchain();
        else PresentMode = desired;
        UpdatePresentFallback();
        _presentModeDirty = false;
    }

    private void UpdatePresentFallback() => PresentModeFallbackReason = PresentMode == _requestedPresentMode ? null
        : $"{_requestedPresentMode} is unavailable on this surface; using guaranteed FIFO.";

    private void ConfigurePresentModes(PhysicalDevice physical, ref VkSurfaceCapabilitiesKHR capabilities)
    {
        var modes = new VkPresentModeKHR[physical.GetSurfacePresentModesKHR_Count(_surface)];
        physical.GetSurfacePresentModesKHR_Write(_surface, modes);
        SupportedPresentModes = modes.Where(mode => (mode is VkPresentModeKHR.ImmediateKHR or VkPresentModeKHR.MailboxKHR
            or VkPresentModeKHR.FifoKHR or VkPresentModeKHR.FifoRelaxedKHR)
            || (_fifoLatestReadyEnabled && mode == VkPresentModeKHR.FifoLatestReadyKHR)).Distinct().ToArray();
        PresentMode = ResolvePresentMode();
        UpdatePresentFallback();
        _switchablePresentModes = [PresentMode];
        if (_swapchainMaintenance && _surfaceCapabilities2)
        {
            var requested = new VkSurfacePresentModeKHR { PresentMode = PresentMode };
            var info = new VkPhysicalDeviceSurfaceInfo2KHR { Surface = _surface, Next = &requested };
            var compatibility = new VkSurfacePresentModeCompatibilityKHR();
            var caps2 = new VkSurfaceCapabilities2KHR { Next = &compatibility };
            physical.GetSurfaceCapabilities2KHR(info, ref caps2);
            if (compatibility.PresentModeCount > 0)
            {
                var compatible = new VkPresentModeKHR[compatibility.PresentModeCount];
                fixed (VkPresentModeKHR* pointer = compatible)
                {
                    compatibility.PresentModes = pointer;
                    physical.GetSurfaceCapabilities2KHR(info, ref caps2);
                }
                _switchablePresentModes = compatible.Take(Math.Min(compatible.Length, compatibility.PresentModeCount))
                    .Where(mode => SupportedPresentModes.Contains(mode)).Distinct().ToArray();
                if (_switchablePresentModes.Length == 0) _switchablePresentModes = [PresentMode];
            }
            // Every declared switchable mode must accept the chosen image count.
            foreach (var mode in _switchablePresentModes)
            {
                requested.PresentMode = mode;
                var modeCapabilities = new VkSurfaceCapabilities2KHR();
                physical.GetSurfaceCapabilities2KHR(info, ref modeCapabilities);
                capabilities.MinImageCount = Math.Max(capabilities.MinImageCount, modeCapabilities.SurfaceCapabilities.MinImageCount);
                var maximum = modeCapabilities.SurfaceCapabilities.MaxImageCount;
                if (maximum > 0) capabilities.MaxImageCount = capabilities.MaxImageCount == 0 ? maximum : Math.Min(capabilities.MaxImageCount, maximum);
            }
        }
        _presentModeDirty = false;
        _presentSerial = _lastPresentId = 0;
        _preparedPresentId = -1;
    }

    private VkResult PresentImage(int imageIndex)
    {
        var swapchain = _swapchain;
        var ready = _presentReady[imageIndex];
        var mode = PresentMode;
        var presentIdValue = (nint)(++_presentSerial);
        var id = new VkPresentIdKHR { SwapchainCount = 1, PresentIds = &presentIdValue };
        var presentMode = new VkSwapchainPresentModeInfoKHR { SwapchainCount = 1, PresentModes = &mode };
        VkFence fence = default;
        var fenceInfo = new VkSwapchainPresentFenceInfoKHR { SwapchainCount = 1, Fences = &fence };
        void* next = null;
        if (_presentWaitSupported) { id.Next = next; next = &id; }
        if (_swapchainMaintenance)
        {
            presentMode.Next = next; next = &presentMode;
            fence = _presentationFences[imageIndex];
            if (_presentationFencePending[imageIndex])
            {
                Check(_device.WaitForFences([fence], true, -1), "wait prior presentation fence");
                _device.ResetFences([fence]);
                // An OOM failure below enqueues nothing: never retain an old pending bit on a reset fence.
                _presentationFencePending[imageIndex] = false;
            }
            fenceInfo.Next = next; next = &fenceInfo;
        }
        var present = new VkPresentInfoKHR { Next = next, WaitSemaphoreCount = 1, WaitSemaphores = &ready,
            SwapchainCount = 1, Swapchains = &swapchain, ImageIndices = &imageIndex };
        var result = _queue.PresentKHR(present);
        if (_swapchainMaintenance && result is VkResult.Success or VkResult.SuboptimalKHR or VkResult.ErrorOutOfDateKHR or VkResult.ErrorSurfaceLostKHR)
            _presentationFencePending[imageIndex] = true;
        if (result is VkResult.Success or VkResult.SuboptimalKHR) _lastPresentId = _presentSerial;
        return result;
    }

    private void WaitPresentationResources()
    {
        if (_device is null) return;
        var pending = _presentationFences.Where((_, i) => _presentationFencePending[i]).ToArray();
        if (pending.Length > 0) Check(_device.WaitForFences(pending, true, -1), "retire swapchain presentations");
        // Device selection requires maintenance1. present_wait is pacing only: OUT_OF_DATE does
        // not prove retirement, so it must never substitute for these presentation fences.
    }
}

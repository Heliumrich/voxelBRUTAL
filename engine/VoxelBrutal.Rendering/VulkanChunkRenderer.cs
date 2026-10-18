using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using VoxelBrutal.Core.Meshing;
using VoxelBrutal.Core.Lod;
using VoxelBrutal.Core.World;

namespace VoxelBrutal.Rendering;

/// <summary>
/// Vulkan 1.3 renderer with packed-quad pulling, current-frame two-phase Hi-Z and indirect submission.
/// All public methods belong to the GLFW/render thread. Uploads are immutable publications.
/// </summary>
public sealed unsafe partial class VulkanChunkRenderer : IDisposable
{
    private const int FramesInFlight = 2;
    private const int Vulkan13 = (1 << 22) | (3 << 12);
    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.ComputeBit;
    private readonly GlfwWindow _window;
    private readonly RendererOptions _options;
    private Instance _instance = null!;
    private Device _device = null!;
    private Brutal.VulkanApi.Queue _queue = null!;
    private VkSurfaceKHR _surface;
    private VkDebugUtilsMessengerEXT _debugMessenger;
    private GCHandle _debugHandle;
    private int _validationErrors;
    private VkSwapchainKHR _swapchain;
    private VkRenderPass _overlayPass;
    private VkFramebuffer[] _overlayFramebuffers = [];
    private VkQueryPool _timestampPool;
    private float _timestampPeriod;
    private int _timestampValidBits;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkSemaphore[] _presentReady = [];
    private VkExtent2D _extent;
    private VkFormat _colorFormat;
    private VkDescriptorSetLayout _setLayout;
    private VkDescriptorPool _descriptorPool;
    private VkPipelineLayout _pipelineLayout;
    private VkPipeline _graphicsPipeline;
    private VkPipeline _cullPipeline;
    private VkDescriptorSetLayout _pyramidSetLayout;
    private VkDescriptorPool _pyramidDescriptorPool;
    private VkPipelineLayout _pyramidPipelineLayout;
    private VkPipeline _pyramidPipeline;
    private VkSampler _depthSampler;
    private int _pyramidWidth, _pyramidHeight, _pyramidMips;
    private GpuBuffer _quads = null!;
    private readonly List<FrameResources> _frames = [];
    private readonly QuadArena _arena;
    private readonly Dictionary<ChunkMeshId, Resident> _resident = [];
    private readonly Dictionary<ChunkKey, ChunkMeshId> _latestMeshByKey = [];
    private readonly Dictionary<ChunkMeshId, ChunkMesh> _pending = [];
    private readonly LinkedList<ChunkMeshId> _uploadOrder = [];
    private readonly Dictionary<ChunkMeshId, LinkedListNode<ChunkMeshId>> _uploadNodes = [];
    private long _pendingUploadBytes;
    private readonly Queue<(long Serial, QuadArena.Range Range)> _retired = [];
    private IReadOnlySet<ChunkKey>? _activeChunks;
    private IReadOnlySet<ChunkMeshId>? _activeMeshes;
    private HashSet<ChunkMeshId> _visibilityHistory = [];
    private long _readbackSerial;
    private int _earlyVisible, _lateVisible, _occluded, _frustumVisible;
    private long _drawnQuads;
    private long _submitted;
    private long _completed;
    private bool _disposed;
    private int _frameIndex;
    public string DeviceName { get; private set; } = "";
    public RendererStatistics Statistics { get; private set; }
    public int ValidationErrorCount => Volatile.Read(ref _validationErrors);
    public long PendingUploadBytes => _pendingUploadBytes;
    public int QuadCapacity => _options.QuadCapacity;
    /// <summary>The most recent upload pass stopped because no safe arena range could fit its next mesh.</summary>
    public bool UploadCapacityBlocked { get; private set; }
    public bool OcclusionEnabled { get; set; }
    public IReadOnlySet<ChunkMeshId> LastVisibleMeshes => _visibilityHistory;
    public ColorFrame? LastColorFrame { get; private set; }
    public ulong? AdapterLuid { get; private set; }
    public string? DeviceUuid { get; private set; }
    public double? GpuFrameMs { get; private set; }
    public Action<CommandBuffer>? OverlayRender { get; set; }
    public RendererOverlayContext OverlayContext => new((DeviceEx)_device, _queue, _overlayPass, FramesInFlight, FramesInFlight, _colorFormat);

    private sealed record Resident(ChunkMeshId Id, QuadArena.Range Range);
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuChunk
    {
        public Vector4 OriginScale;
        public uint FirstQuad, QuadCount, Reserved0, Reserved1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PushData
    {
        public Matrix4x4 ViewProjection;
        public uint Count, Pass, Occlusion, Reserved;
        public Vector4 ViewportPyramid;
    }

    private sealed class FrameResources
    {
        public VkCommandPool Pool;
        public VkCommandBuffer Command;
        public VkFence Fence;
        public VkSemaphore Acquired;
        public VkDescriptorSet Set;
        public GpuBuffer Staging = null!, Chunks = null!, Indirect = null!, Visibility = null!;
        public GpuBuffer? ColorReadback;
        public ChunkMeshId[] SubmittedMeshes = [];
        public VkImage Pyramid;
        public VkDeviceMemory PyramidMemory;
        public VkImageView PyramidView;
        public VkImageView[] PyramidMipViews = [];
        public VkDescriptorSet[] PyramidSets = [];
        public VkImage Depth;
        public VkImageView DepthView;
        public VkDeviceMemory DepthMemory;
        public long Serial;
        public int QueryIndex;
    }

    public VulkanChunkRenderer(GlfwWindow window, RendererOptions? options = null)
    {
        _window = window;
        _options = options ?? new();
        _requestedPresentMode = _options.PresentMode;
        OcclusionEnabled = _options.EnableOcclusion;
        if (_options.QuadCapacity <= 0 || _options.QuadCapacity > int.MaxValue / 8 || _options.MaxChunks <= 0
            || _options.UploadBytesPerFrame < 8 || _options.MaxPendingUploadBytes < _options.UploadBytesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(options));
        _arena = new(_options.QuadCapacity);
        try
        {
            CreateDevice();
            _quads = new(_device, checked(_options.QuadCapacity * 8), VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, false);
            CreateDescriptorsAndFrames();
            RecreateSwapchain();
            CreatePipelines();
        }
        catch { Dispose(); throw; }
    }

    public void QueueUpload(ChunkMesh mesh)
    {
        if (!TryQueueUpload(mesh)) throw new InvalidOperationException("Renderer pending-upload budget exceeded. Use TryQueueUpload and retain the publication until budget is available.");
    }

    /// <summary>Returns false for bounded-queue pressure; the caller retains and retries the immutable publication.</summary>
    public bool TryQueueUpload(ChunkMesh mesh)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = mesh.Version;
        if (_resident.ContainsKey(id) || _pending.ContainsKey(id)) return true;
        if (mesh.QuadCount > _options.QuadCapacity) throw new InvalidOperationException("One chunk exceeds QuadCapacity.");
        if (mesh.Quads.Length * 8L > _options.UploadBytesPerFrame)
            throw new InvalidOperationException("One chunk exceeds the upload staging budget. Increase UploadBytesPerFrame.");
        var newBytes = _pendingUploadBytes + mesh.ByteSize;
        if (newBytes > _options.MaxPendingUploadBytes || _pending.Count >= _options.MaxChunks) return false;
        _uploadNodes[id] = _uploadOrder.AddLast(id);
        _pending[id] = mesh;
        _pendingUploadBytes = newBytes;
        return true;
    }

    public void Remove(ChunkKey key)
    {
        foreach (var id in _pending.Keys.Concat(_resident.Keys).Where(id => id.Key == key).Distinct().ToArray()) Remove(id);
    }

    public void Remove(ChunkMeshId id)
    {
        if (_pending.Remove(id, out var pending))
        {
            _pendingUploadBytes -= pending.ByteSize;
            _uploadOrder.Remove(_uploadNodes[id]);
            _uploadNodes.Remove(id);
        }
        if (_resident.Remove(id, out var old)) _retired.Enqueue((_submitted, old.Range));
        if (_latestMeshByKey.TryGetValue(id.Key, out var latest) && latest == id)
        {
            var remaining = _resident.Keys.Where(other => other.Key == id.Key).OrderBy(other => other.Revision).ToArray();
            if (remaining.Length == 0) _latestMeshByKey.Remove(id.Key);
            else _latestMeshByKey[id.Key] = remaining[^1];
        }
        _visibilityHistory.Remove(id);
    }

    public bool IsResident(ChunkKey key) => _resident.Keys.Any(id => id.Key == key);
    public bool IsResident(ChunkMeshId id) => _resident.ContainsKey(id);

    public void RetainMeshes(IReadOnlySet<ChunkMeshId> ids)
    {
        foreach (var id in _pending.Keys.Concat(_resident.Keys).Where(id => !ids.Contains(id)).Distinct().ToArray()) Remove(id);
    }

    public void SetActiveMeshes(IReadOnlySet<ChunkMeshId>? ids) { _activeMeshes = ids; _activeChunks = null; }

    /// <summary>Restricts draws to an atomic resident cover; null draws every resident mesh.</summary>
    public void SetActiveChunks(IReadOnlySet<ChunkKey>? keys) { _activeChunks = keys; _activeMeshes = null; }

    /// <summary>Renders one frame. Returns false while minimized or when a resize needs a new acquire.</summary>
    public bool Draw(RenderCamera camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var size = _window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0) return false;
        ApplyPresentModeRequest();
        if (size.X != _extent.Width || size.Y != _extent.Height) RecreateSwapchain();
        var frame = _frames[_frameIndex];
        Check(_device.WaitForFences([frame.Fence], true, -1), "wait frame fence");
        ProcessReadback(frame);
        _completed = Math.Max(_completed, frame.Serial);
        while (_retired.TryPeek(out var retired) && retired.Serial <= _completed)
        {
            _retired.Dequeue();
            _arena.Free(retired.Range);
        }
        PrepareFrame(); // Also supports callers that do not use the pre-input PrepareFrame hook.
        var acquire = _device.AcquireNextImageKHR(_swapchain, -1, frame.Acquired, default, out var imageIndex);
        if (acquire == VkResult.ErrorOutOfDateKHR) { RecreateSwapchain(); return false; }
        if (acquire != VkResult.SuboptimalKHR) Check(acquire, "acquire swapchain image");
        _device.ResetCommandPool(frame.Pool, 0);
        var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_device.PInvoke.vkBeginCommandBuffer.Invoke(frame.Command, &begin), "begin commands");
        if (!_timestampPool.IsNull())
        {
            _device.PInvoke.vkCmdResetQueryPool.Invoke(frame.Command, _timestampPool, frame.QueryIndex, 2);
            _device.PInvoke.vkCmdWriteTimestamp2.Invoke(frame.Command, VkPipelineStageFlags2.TopOfPipeBit, _timestampPool, frame.QueryIndex);
        }
        var uploadBytes = RecordUploads(frame);
        var count = WriteChunks(frame, camera);
        RecordFrame(frame, imageIndex, camera, count, uploadBytes > 0);
        if (!_timestampPool.IsNull())
            _device.PInvoke.vkCmdWriteTimestamp2.Invoke(frame.Command, VkPipelineStageFlags2.AllCommandsBit, _timestampPool, frame.QueryIndex + 1);
        Check(_device.PInvoke.vkEndCommandBuffer.Invoke(frame.Command), "end commands");
        // Reset only once submission is certain; out-of-date acquire never leaves an unsignaled fence.
        _device.ResetFences([frame.Fence]);
        var wait = new VkSemaphoreSubmitInfo { Semaphore = frame.Acquired, StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
        var signal = new VkSemaphoreSubmitInfo { Semaphore = _presentReady[imageIndex], StageMask = VkPipelineStageFlags2.AllCommandsBit };
        var cmd = new VkCommandBufferSubmitInfo { CommandBuffer = frame.Command };
        var submit = new VkSubmitInfo2 { WaitSemaphoreInfoCount = 1, WaitSemaphoreInfos = &wait,
            CommandBufferInfoCount = 1, CommandBufferInfos = &cmd, SignalSemaphoreInfoCount = 1, SignalSemaphoreInfos = &signal };
        _queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), frame.Fence);
        frame.Serial = ++_submitted;
        var result = PresentImage(imageIndex);
        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR || acquire == VkResult.SuboptimalKHR) RecreateSwapchain();
        else Check(result, "present");
        _frameIndex = (_frameIndex + 1) % FramesInFlight;
        Statistics = new(_resident.Count, _resident.Values.Sum(r => (long)r.Range.Count), _pending.Count, uploadBytes, count, _submitted,
            _earlyVisible, _lateVisible, _occluded, _frustumVisible, _readbackSerial, _drawnQuads);
        return true;
    }

    private void CreateDevice()
    {
        var required = Glfw.GetRequiredInstanceExtensions(out var count);
        if (required == null) throw new InvalidOperationException("GLFW could not find a Vulkan loader.");
        HashSet<string> extensions = [];
        for (var i = 0; i < count; ++i) extensions.Add(Marshal.PtrToStringUTF8((nint)required[i])!);
        if (_options.EnableValidation) extensions.Add("VK_EXT_debug_utils");
        AddModernSurfaceExtensions(extensions);
        _instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = Vulkan13 }, EnabledExtensions = extensions,
            EnabledLayers = _options.EnableValidation ? ["VK_LAYER_KHRONOS_validation"] : [],
        });
        if (_options.EnableValidation)
        {
            _debugHandle = GCHandle.Alloc(this);
            _debugMessenger = _instance.CreateDebugUtilsMessengerEXT(new VkDebugUtilsMessengerCreateInfoEXT
            {
                MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
                MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
                PfnUserCallback = new(&ValidationCallback), UserData = (void*)GCHandle.ToIntPtr(_debugHandle),
            }, null);
        }
        Check((VkResult)Glfw.CreateWindowSurface(_instance.VkHandle, _window, out var surface), "create GLFW Vulkan surface");
        _surface = new((nint)surface);
        foreach (var physical in _instance.PhysicalDevices.OrderByDescending(p => p.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu))
        {
            var properties = physical.GetProperties();
            if (properties.ApiVersion < Vulkan13 || !physical.ExtensionNames.Contains("VK_KHR_swapchain")) continue;
            var v13 = new VkPhysicalDeviceVulkan13Features();
            string? maintenanceExtension = _surfaceMaintenanceKhr && physical.ExtensionNames.Contains("VK_KHR_swapchain_maintenance1")
                ? "VK_KHR_swapchain_maintenance1" : _surfaceMaintenanceExt && physical.ExtensionNames.Contains("VK_EXT_swapchain_maintenance1")
                    ? "VK_EXT_swapchain_maintenance1" : null;
            var hasPresentWait = physical.ExtensionNames.Contains("VK_KHR_present_id") && physical.ExtensionNames.Contains("VK_KHR_present_wait");
            var waitFeature = new VkPhysicalDevicePresentWaitFeaturesKHR();
            var idFeature = new VkPhysicalDevicePresentIdFeaturesKHR { Next = hasPresentWait ? &waitFeature : null };
            var maintenanceFeature = new VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR { Next = hasPresentWait ? &idFeature : null };
            v13.Next = maintenanceExtension is not null ? &maintenanceFeature : hasPresentWait ? &idFeature : null;
            var latestReadyExtension = physical.ExtensionNames.Contains("VK_KHR_present_mode_fifo_latest_ready")
                ? "VK_KHR_present_mode_fifo_latest_ready" : physical.ExtensionNames.Contains("VK_EXT_present_mode_fifo_latest_ready")
                    ? "VK_EXT_present_mode_fifo_latest_ready" : null;
            var latestReadyFeature = new VkPhysicalDevicePresentModeFifoLatestReadyFeaturesKHR { Next = v13.Next };
            if (latestReadyExtension is not null) v13.Next = &latestReadyFeature;
            var features = new VkPhysicalDeviceFeatures2 { Next = &v13 };
            physical.GetFeatures2(ref features);
            if (!v13.DynamicRendering || !v13.Synchronization2 || !features.Features.MultiDrawIndirect || !features.Features.DrawIndirectFirstInstance) continue;
            // Repeated mode/window changes need an explicit presentation-retirement primitive.
            // Device/queue idle alone does not prove legacy presentation-engine resource completion.
            if (!(maintenanceExtension is not null && maintenanceFeature.SwapchainMaintenance1)) continue;
            // BRUTAL represents these Vk uint32_t limits with signed int fields. Reinterpret bits:
            // modern drivers commonly return UINT32_MAX, which would otherwise look like -1.
            if ((uint)properties.Limits.MaxStorageBufferRange < Math.Max((long)_options.QuadCapacity * 8, (long)_options.MaxChunks * 32)
                || (uint)properties.Limits.MaxDrawIndirectCount < _options.MaxChunks
                || (uint)properties.Limits.MaxComputeWorkGroupCount[0] < (_options.MaxChunks + 63L) / 64) continue;
            var queues = new VkQueueFamilyProperties[physical.GetQueueFamilyProperties_Count()];
            physical.GetQueueFamilyProperties_Write(queues);
            for (var q = 0; q < queues.Length; ++q)
            {
                if ((queues[q].QueueFlags & (VkQueueFlags.GraphicsBit | VkQueueFlags.ComputeBit)) != (VkQueueFlags.GraphicsBit | VkQueueFlags.ComputeBit)
                    || !physical.GetSurfaceSupportKHR(q, _surface)) continue;
                var priority = 1f;
                var queueInfo = new VkDeviceQueueCreateInfo { QueueFamilyIndex = q, QueueCount = 1, QueuePriorities = &priority };
                var enabled = new VkPhysicalDeviceFeatures { MultiDrawIndirect = true, DrawIndirectFirstInstance = true };
                var enabled13 = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
                var enableMaintenance = maintenanceExtension is not null && maintenanceFeature.SwapchainMaintenance1;
                var enableWait = hasPresentWait && idFeature.PresentId && waitFeature.PresentWait;
                var enabledWait = new VkPhysicalDevicePresentWaitFeaturesKHR { PresentWait = enableWait };
                var enabledId = new VkPhysicalDevicePresentIdFeaturesKHR { PresentId = enableWait, Next = enableWait ? &enabledWait : null };
                var enabledMaintenance = new VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR { SwapchainMaintenance1 = enableMaintenance,
                    Next = enableWait ? &enabledId : null };
                enabled13.Next = enableMaintenance ? &enabledMaintenance : enableWait ? &enabledId : null;
                var enableLatestReady = latestReadyExtension is not null && latestReadyFeature.PresentModeFifoLatestReady;
                var enabledLatestReady = new VkPhysicalDevicePresentModeFifoLatestReadyFeaturesKHR
                { PresentModeFifoLatestReady = enableLatestReady, Next = enabled13.Next };
                if (enableLatestReady) enabled13.Next = &enabledLatestReady;
                HashSet<string> deviceExtensions = ["VK_KHR_swapchain"];
                if (enableMaintenance) deviceExtensions.Add(maintenanceExtension!);
                if (enableWait) { deviceExtensions.Add("VK_KHR_present_id"); deviceExtensions.Add("VK_KHR_present_wait"); }
                if (enableLatestReady) deviceExtensions.Add(latestReadyExtension!);
                _device = physical.CreateDevice(new DeviceEx.CreateInfo
                {
                    Extensions = deviceExtensions, Features = &enabled, Next = &enabled13,
                    QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queueInfo, 1),
                });
                _queue = _device.GetQueue(q, 0);
                _swapchainMaintenance = enableMaintenance;
                SwapchainMaintenanceExtension = enableMaintenance ? maintenanceExtension : null;
                _presentWaitSupported = enableWait;
                _fifoLatestReadyEnabled = enableLatestReady;
                _timestampValidBits = queues[q].TimestampValidBits;
                _timestampPeriod = properties.Limits.TimestampPeriod;
                var identity = new VkPhysicalDeviceIdProperties();
                var properties2 = new VkPhysicalDeviceProperties2 { Next = &identity };
                physical.GetProperties2(ref properties2);
                if (identity.DeviceLUIDValid)
                    AdapterLuid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(identity.DeviceLUID, 8));
                DeviceUuid = new Guid(new ReadOnlySpan<byte>(identity.DeviceUUID, 16), bigEndian: true).ToString("D");
                DeviceName = Marshal.PtrToStringUTF8((nint)properties.DeviceName)!;
                return;
            }
        }
        throw new InvalidOperationException("No GPU supports the required Vulkan 1.3 graphics/compute/present features and arena limits with safe presentation retirement. This renderer requires VK_KHR_swapchain_maintenance1 (or VK_EXT_swapchain_maintenance1) with the swapchainMaintenance1 feature enabled. VK_KHR_present_wait alone cannot guarantee retirement after an out-of-date present. Update the graphics driver or select a compatible GPU.");
    }

    private void CreateDescriptorsAndFrames()
    {
        if (_timestampValidBits > 0 && _timestampPeriod > 0)
            _timestampPool = _device.CreateQueryPool(new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = FramesInFlight * 2 }, null);
        var bindings = stackalloc VkDescriptorSetLayoutBinding[5];
        for (var i = 0; i < 5; ++i) bindings[i] = new() { Binding = i,
            DescriptorType = i == 3 ? VkDescriptorType.CombinedImageSampler : VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = Stages };
        _setLayout = _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 5, Bindings = bindings }, null);
        var poolSizes = stackalloc VkDescriptorPoolSize[2];
        poolSizes[0] = new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = FramesInFlight * 4 };
        poolSizes[1] = new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = FramesInFlight };
        _descriptorPool = _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = FramesInFlight, PoolSizeCount = 2, PoolSizes = poolSizes }, null);
        var layout = _setLayout;
        var push = new VkPushConstantRange { StageFlags = Stages, Size = (ByteSize32)96 };
        _pipelineLayout = _device.CreatePipelineLayout(new VkPipelineLayoutCreateInfo { SetLayoutCount = 1, SetLayouts = &layout, PushConstantRangeCount = 1, PushConstantRanges = &push }, null);
        var pyramidBindings = stackalloc VkDescriptorSetLayoutBinding[2];
        pyramidBindings[0] = new() { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        pyramidBindings[1] = new() { Binding = 1, DescriptorType = VkDescriptorType.StorageImage, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        _pyramidSetLayout = _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = pyramidBindings }, null);
        var pyramidLayout = _pyramidSetLayout;
        var pyramidPush = new VkPushConstantRange { StageFlags = VkShaderStageFlags.ComputeBit, Size = (ByteSize32)16 };
        _pyramidPipelineLayout = _device.CreatePipelineLayout(new VkPipelineLayoutCreateInfo { SetLayoutCount = 1, SetLayouts = &pyramidLayout,
            PushConstantRangeCount = 1, PushConstantRanges = &pyramidPush }, null);
        _depthSampler = _device.CreateSampler(new VkSamplerCreateInfo { MagFilter = VkFilter.Nearest, MinFilter = VkFilter.Nearest,
            MipmapMode = VkSamplerMipmapMode.Nearest, AddressModeU = VkSamplerAddressMode.ClampToEdge, AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge, MaxLod = 32 }, null);
        var infos = stackalloc VkDescriptorBufferInfo[4];
        var writes = stackalloc VkWriteDescriptorSet[4];
        for (var i = 0; i < FramesInFlight; ++i)
        {
            var f = new FrameResources();
            f.QueryIndex = i * 2;
            _frames.Add(f);
            f.Pool = _device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = _queue.Family }, null);
            f.Command = _device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = f.Pool, Level = VkCommandBufferLevel.Primary });
            f.Fence = _device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
            f.Acquired = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            f.Staging = new(_device, _options.UploadBytesPerFrame, VkBufferUsageFlags.TransferSrcBit, true);
            f.Chunks = new(_device, checked(_options.MaxChunks * 32), VkBufferUsageFlags.StorageBufferBit, true);
            f.Indirect = new(_device, checked(_options.MaxChunks * 16), VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.IndirectBufferBit, false);
            f.Visibility = new(_device, checked(_options.MaxChunks * 4 + 32), VkBufferUsageFlags.StorageBufferBit, true);
            VkDescriptorSet set;
            _device.AllocateDescriptorSets(new VkDescriptorSetAllocateInfo { DescriptorPool = _descriptorPool, DescriptorSetCount = 1, SetLayouts = &layout }, new Span<VkDescriptorSet>(&set, 1));
            f.Set = set;
            infos[0] = new() { Buffer = _quads.Handle, Range = (ByteSize64)_quads.Size };
            infos[1] = new() { Buffer = f.Chunks.Handle, Range = (ByteSize64)f.Chunks.Size };
            infos[2] = new() { Buffer = f.Indirect.Handle, Range = (ByteSize64)f.Indirect.Size };
            infos[3] = new() { Buffer = f.Visibility.Handle, Range = (ByteSize64)f.Visibility.Size };
            for (var b = 0; b < 4; ++b) writes[b] = new() { DstSet = set, DstBinding = b == 3 ? 4 : b, DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = infos + b };
            _device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, 4), []);
        }
    }

    private int RecordUploads(FrameResources frame)
    {
        UploadCapacityBlocked = false;
        var bytes = 0;
        while (_uploadOrder.First is { } first)
        {
            var key = first.Value;
            var mesh = _pending[key];
            var length = checked(mesh.Quads.Length * 8);
            if (bytes + length > _options.UploadBytesPerFrame) break;
            if (!_resident.ContainsKey(key) && _resident.Count >= _options.MaxChunks) break;
            // Residency pressure is backpressure, not a crash or permission to overwrite live data.
            if (!_arena.TryAllocate(mesh.Quads.Length, out var range))
            {
                UploadCapacityBlocked = true;
                break;
            }
            if (length > 0)
            {
                MemoryMarshal.AsBytes(mesh.Quads).CopyTo(new Span<byte>(frame.Staging.Mapped + bytes, length));
                var region = new VkBufferCopy { SrcOffset = (ByteSize64)bytes, DstOffset = (ByteSize64)checked(range.Offset * 8), Size = (ByteSize64)length };
                _device.PInvoke.vkCmdCopyBuffer.Invoke(frame.Command, frame.Staging.Handle, _quads.Handle, 1, &region);
            }
            if (_resident.Remove(key, out var old)) _retired.Enqueue((_submitted, old.Range));
            _resident[key] = new(key, range);
            if (!_latestMeshByKey.TryGetValue(key.Key, out var latest) || latest.Revision <= key.Revision)
                _latestMeshByKey[key.Key] = key;
            _pending.Remove(key);
            _pendingUploadBytes -= mesh.ByteSize;
            _uploadNodes.Remove(key);
            _uploadOrder.RemoveFirst();
            bytes += length;
        }
        return bytes;
    }

    private int WriteChunks(FrameResources frame, RenderCamera camera)
    {
        var span = new Span<GpuChunk>(frame.Chunks.Mapped, _options.MaxChunks);
        new Span<byte>(frame.Visibility.Mapped, 32).Clear();
        var ids = new List<ChunkMeshId>();
        var i = 0;
        foreach (var mesh in _resident.Values)
        {
            var k = mesh.Id.Key;
            if (_activeChunks != null && !_activeChunks.Contains(k)) continue;
            if (_activeMeshes != null && !_activeMeshes.Contains(mesh.Id)) continue;
            // Legacy key-only mode draws the latest publication while retaining backing versions.
            if (_activeMeshes == null && _latestMeshByKey[k] != mesh.Id) continue;
            span[i++] = new() { OriginScale = new(camera.Position.RelativeOrigin(k), k.VoxelScale),
                FirstQuad = (uint)mesh.Range.Offset, QuadCount = (uint)mesh.Range.Count,
                Reserved0 = _visibilityHistory.Contains(mesh.Id) ? 1u : 0u };
            ids.Add(mesh.Id);
        }
        frame.SubmittedMeshes = ids.ToArray();
        return i;
    }

    private void RecordFrame(FrameResources f, int imageIndex, RenderCamera camera, int count, bool uploaded)
    {
        var vk = _device.PInvoke;
        if (uploaded)
        {
            var memory = new VkMemoryBarrier2 { SrcStageMask = VkPipelineStageFlags2.CopyBit, SrcAccessMask = VkAccessFlags2.TransferWriteBit,
                DstStageMask = VkPipelineStageFlags2.VertexShaderBit, DstAccessMask = VkAccessFlags2.ShaderStorageReadBit };
            var dep = new VkDependencyInfo { MemoryBarrierCount = 1, MemoryBarriers = &memory };
            vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dep);
        }
        // System.Numerics stores a row-vector matrix; its bytes are the GLSL column-vector transpose.
        var push = new PushData { ViewProjection = camera.ViewProjection, Count = (uint)count,
            Occlusion = OcclusionEnabled ? 1u : 0u, ViewportPyramid = new(_extent.Width, _extent.Height, _pyramidWidth, _pyramidHeight) };
        // The descriptor is valid in both branches, including the first frame before any pyramid build.
        var initialPyramid = ImageBarrier(f.Pyramid, VkImageLayout.Undefined, VkImageLayout.General,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderSampledReadBit | VkAccessFlags2.ShaderStorageWriteBit,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit | VkAccessFlags2.ShaderSampledReadBit, VkImageAspectFlags.ColorBit);
        initialPyramid.SubresourceRange.LevelCount = _pyramidMips;
        var initialDependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &initialPyramid };
        vk.vkCmdPipelineBarrier2.Invoke(f.Command, &initialDependency);
        DispatchCull(f, push, count);
        var barriers = stackalloc VkImageMemoryBarrier2[2];
        barriers[0] = ImageBarrier(_images[imageIndex], VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
            // Chain the acquire wait at COLOR_ATTACHMENT_OUTPUT through this layout transition.
            VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.None,
            VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentWriteBit, VkImageAspectFlags.ColorBit);
        barriers[1] = ImageBarrier(f.Depth, VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal,
            VkPipelineStageFlags2.AllCommandsBit, VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit,
            VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit,
            VkAccessFlags2.DepthStencilAttachmentReadBit | VkAccessFlags2.DepthStencilAttachmentWriteBit, VkImageAspectFlags.DepthBit);
        var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 2, ImageMemoryBarriers = barriers };
        vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dependency);
        DrawChunkPass(f, imageIndex, count, clear: true);
        if (OcclusionEnabled)
        {
            BuildDepthPyramid(f);
            // The early indirect read must finish before the late compute overwrites its commands.
            var memory = new VkMemoryBarrier2
            {
                SrcStageMask = VkPipelineStageFlags2.DrawIndirectBit | VkPipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = VkAccessFlags2.IndirectCommandReadBit | VkAccessFlags2.ShaderStorageWriteBit,
                DstStageMask = VkPipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = VkAccessFlags2.ShaderStorageReadBit | VkAccessFlags2.ShaderStorageWriteBit | VkAccessFlags2.ShaderSampledReadBit,
            };
            dependency = new() { MemoryBarrierCount = 1, MemoryBarriers = &memory };
            vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dependency);
            push.Pass = 1;
            DispatchCull(f, push, count);
            // Different push-constant layouts were used by reduction; restore the complete graphics range.
            vk.vkCmdPushConstants.Invoke(f.Command, _pipelineLayout, Stages, 0, 96, &push);
            barriers[0] = ImageBarrier(f.Depth, VkImageLayout.DepthReadOnlyOptimal, VkImageLayout.DepthAttachmentOptimal,
                VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderSampledReadBit,
                VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit,
                VkAccessFlags2.DepthStencilAttachmentReadBit | VkAccessFlags2.DepthStencilAttachmentWriteBit, VkImageAspectFlags.DepthBit);
            barriers[1] = ImageBarrier(_images[imageIndex], VkImageLayout.ColorAttachmentOptimal, VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentWriteBit,
                VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentReadBit | VkAccessFlags2.ColorAttachmentWriteBit, VkImageAspectFlags.ColorBit);
            dependency = new() { ImageMemoryBarrierCount = 2, ImageMemoryBarriers = barriers };
            vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dependency);
            DrawChunkPass(f, imageIndex, count, clear: false);
        }
        RecordOverlay(f, imageIndex);
        FinishFrameReadbackAndPresent(f, imageIndex);
    }

    private void DrawChunkPass(FrameResources f, int imageIndex, int count, bool clear)
    {
        var vk = _device.PInvoke;
        var color = new VkRenderingAttachmentInfo { ImageView = _views[imageIndex], ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = clear ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = new float4(.48f, .64f, .79f, 1) } };
        var depth = new VkRenderingAttachmentInfo { ImageView = f.DepthView, ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = clear ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearDepthStencilValue { Depth = 0 } };
        var rendering = new VkRenderingInfo { RenderArea = new() { Extent = _extent }, LayerCount = 1,
            ColorAttachmentCount = 1, ColorAttachments = &color, DepthAttachment = &depth };
        vk.vkCmdBeginRendering.Invoke(f.Command, &rendering);
        var viewport = new VkViewport { Width = _extent.Width, Height = _extent.Height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new VkRect2D { Extent = _extent };
        vk.vkCmdSetViewport.Invoke(f.Command, 0, 1, &viewport);
        vk.vkCmdSetScissor.Invoke(f.Command, 0, 1, &scissor);
        vk.vkCmdBindPipeline.Invoke(f.Command, VkPipelineBindPoint.Graphics, _graphicsPipeline);
        var set = f.Set;
        vk.vkCmdBindDescriptorSets.Invoke(f.Command, VkPipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
        if (count > 0) vk.vkCmdDrawIndirect.Invoke(f.Command, f.Indirect.Handle, (ByteSize64)0, count, 16);
        vk.vkCmdEndRendering.Invoke(f.Command);
    }

    private static VkImageMemoryBarrier2 ImageBarrier(VkImage image, VkImageLayout oldLayout, VkImageLayout newLayout,
        VkPipelineStageFlags2 src, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dst, VkAccessFlags2 dstAccess, VkImageAspectFlags aspect) => new()
    {
        Image = image, OldLayout = oldLayout, NewLayout = newLayout, SrcStageMask = src, SrcAccessMask = srcAccess,
        DstStageMask = dst, DstAccessMask = dstAccess, SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
        SubresourceRange = new() { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
    };

    internal static void Check(VkResult result, string operation)
    {
        if (result != VkResult.Success) throw new InvalidOperationException($"Vulkan {operation}: {result}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 ValidationCallback(VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        // Exceptions must never unwind through the Vulkan driver.
        try
        {
            var renderer = (VulkanChunkRenderer)GCHandle.FromIntPtr((nint)user).Target!;
            if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0) Interlocked.Increment(ref renderer._validationErrors);
            Console.Error.WriteLine($"[Vulkan {severity}] {Marshal.PtrToStringUTF8((nint)data->Message)}");
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device != null)
        {
            _device.WaitIdle();
            foreach (var frame in _frames.OrderBy(f => f.Serial)) ProcessReadback(frame);
            DestroySwapchain();
            if (!_graphicsPipeline.IsNull()) _device.DestroyPipeline(_graphicsPipeline, null);
            if (!_cullPipeline.IsNull()) _device.DestroyPipeline(_cullPipeline, null);
            if (!_pyramidPipeline.IsNull()) _device.DestroyPipeline(_pyramidPipeline, null);
            if (!_timestampPool.IsNull()) _device.DestroyQueryPool(_timestampPool, null);
            if (!_overlayPass.IsNull()) _device.DestroyRenderPass(_overlayPass, null);
            foreach (var f in _frames)
            {
                f.Staging?.Dispose(); f.Chunks?.Dispose(); f.Indirect?.Dispose(); f.Visibility?.Dispose();
                if (!f.Acquired.IsNull()) _device.DestroySemaphore(f.Acquired, null);
                if (!f.Fence.IsNull()) _device.DestroyFence(f.Fence, null);
                if (!f.Pool.IsNull()) _device.DestroyCommandPool(f.Pool, null);
            }
            if (!_pipelineLayout.IsNull()) _device.DestroyPipelineLayout(_pipelineLayout, null);
            if (!_pyramidPipelineLayout.IsNull()) _device.DestroyPipelineLayout(_pyramidPipelineLayout, null);
            if (!_pyramidSetLayout.IsNull()) _device.DestroyDescriptorSetLayout(_pyramidSetLayout, null);
            if (!_depthSampler.IsNull()) _device.DestroySampler(_depthSampler, null);
            if (!_descriptorPool.IsNull()) _device.DestroyDescriptorPool(_descriptorPool, null);
            if (!_setLayout.IsNull()) _device.DestroyDescriptorSetLayout(_setLayout, null);
            _quads?.Dispose();
            _device.Dispose();
        }
        if (_instance != null)
        {
            if (!_surface.IsNull()) _instance.DestroySurfaceKHR(_surface, null);
            if (!_debugMessenger.IsNull()) _instance.DestroyDebugUtilsMessengerEXT(_debugMessenger, null);
            if (_debugHandle.IsAllocated) _debugHandle.Free();
            _instance.Dispose();
        }
    }
}

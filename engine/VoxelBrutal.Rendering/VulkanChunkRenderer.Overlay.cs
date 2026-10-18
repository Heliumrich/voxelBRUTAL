using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;

namespace VoxelBrutal.Rendering;

public sealed unsafe partial class VulkanChunkRenderer
{
    private void CreateOverlayPass()
    {
        if (!_overlayPass.IsNull()) return;
        var attachment = new VkAttachmentDescription
        {
            Format = _colorFormat, Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare, StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.ColorAttachmentOptimal, FinalLayout = VkImageLayout.ColorAttachmentOptimal,
        };
        var color = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription { PipelineBindPoint = VkPipelineBindPoint.Graphics, ColorAttachmentCount = 1, ColorAttachments = &color };
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = -1, DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit, DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit,
        };
        _overlayPass = _device.CreateRenderPass(new VkRenderPassCreateInfo { AttachmentCount = 1, Attachments = &attachment,
            SubpassCount = 1, Subpasses = &subpass, DependencyCount = 1, Dependencies = &dependency }, null);
    }

    private void CreateOverlayFramebuffers()
    {
        CreateOverlayPass();
        _overlayFramebuffers = new VkFramebuffer[_views.Length];
        for (var i = 0; i < _views.Length; ++i)
        {
            var view = _views[i];
            _overlayFramebuffers[i] = _device.CreateFramebuffer(new VkFramebufferCreateInfo
            {
                RenderPass = _overlayPass, AttachmentCount = 1, Attachments = &view,
                Width = _extent.Width, Height = _extent.Height, Layers = 1,
            }, null);
        }
    }

    private void RecordOverlay(FrameResources f, int imageIndex)
    {
        var callback = OverlayRender;
        if (callback is null) return;
        var begin = new VkRenderPassBeginInfo { RenderPass = _overlayPass, Framebuffer = _overlayFramebuffers[imageIndex],
            RenderArea = new() { Extent = _extent } };
        _device.PInvoke.vkCmdBeginRenderPass.Invoke(f.Command, &begin, VkSubpassContents.Inline);
        callback(new CommandBuffer(_device, f.Command));
        _device.PInvoke.vkCmdEndRenderPass.Invoke(f.Command);
    }

    private void ReadGpuTimestamps(FrameResources f)
    {
        if (_timestampPool.IsNull()) return;
        Span<ulong> results = stackalloc ulong[4];
        // The frame fence already completed. Never add VK_QUERY_RESULT_WAIT_BIT or a GPU stall.
        var result = _device.GetQueryPoolResults(_timestampPool, f.QueryIndex, 2, MemoryMarshal.AsBytes(results),
            (ByteSize64)16, VkQueryResultFlags._64Bit | VkQueryResultFlags.WithAvailabilityBit);
        if (result != VkResult.Success || results[1] == 0 || results[3] == 0) return;
        var mask = _timestampValidBits >= 64 ? ulong.MaxValue : (1UL << _timestampValidBits) - 1;
        var delta = unchecked(results[2] - results[0]) & mask;
        GpuFrameMs = delta * (double)_timestampPeriod / 1_000_000;
    }

    public void WaitIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.WaitIdle();
        _completed = _submitted;
        foreach (var frame in _frames.OrderBy(f => f.Serial)) ProcessReadback(frame);
    }
}

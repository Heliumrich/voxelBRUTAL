using Brutal;
using Brutal.VulkanApi;

namespace VoxelBrutal.Rendering;

public sealed unsafe partial class VulkanChunkRenderer
{
    private void RecreateSwapchain()
    {
        var size = _window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0) return;
        _device.WaitIdle();
        _completed = _submitted;
        DestroySwapchain();
        var physical = _device.PhysicalDevice;
        var caps = physical.GetSurfaceCapabilitiesKHR(_surface);
        var formats = new VkSurfaceFormatKHR[physical.GetSurfaceFormatsKHR_Count(_surface)];
        physical.GetSurfaceFormatsKHR_Write(_surface, formats);
        if (formats.Length == 0) throw new InvalidOperationException("Surface has no formats.");
        var format = formats.FirstOrDefault(f => f.Format == VkFormat.B8G8R8A8SRGB && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR);
        if (format.Format == VkFormat.Undefined) format = formats[0];
        if (format.Format == VkFormat.Undefined) format.Format = VkFormat.B8G8R8A8SRGB;
        if (_colorFormat != VkFormat.Undefined && _colorFormat != format.Format)
            throw new InvalidOperationException("Surface color format changed; restart to rebuild rendering pipelines.");
        _colorFormat = format.Format;
        _extent = caps.CurrentExtent.Width == -1
            ? new() { Width = Math.Clamp(size.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Height = Math.Clamp(size.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height) }
            : caps.CurrentExtent;
        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0) imageCount = Math.Min(imageCount, caps.MaxImageCount);
        var alpha = new[] { VkCompositeAlphaFlagsKHR.OpaqueBitKHR, VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR, VkCompositeAlphaFlagsKHR.InheritBitKHR }.First(a => (caps.SupportedCompositeAlpha & a) != 0);
        _swapchain = _device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
        {
            Surface = _surface, MinImageCount = imageCount, ImageFormat = _colorFormat, ImageColorSpace = format.ColorSpace,
            ImageExtent = _extent, ImageArrayLayers = 1, ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = VkSharingMode.Exclusive, PreTransform = caps.CurrentTransform,
            CompositeAlpha = alpha, PresentMode = VkPresentModeKHR.FifoKHR, Clipped = true,
        }, null);
        _images = new VkImage[_device.GetSwapchainImagesKHR_Count(_swapchain)];
        _device.GetSwapchainImagesKHR_Write(_swapchain, _images);
        _views = new VkImageView[_images.Length];
        _presentReady = new VkSemaphore[_images.Length];
        for (var i = 0; i < _images.Length; ++i)
        {
            _views[i] = CreateImageView(_images[i], _colorFormat, VkImageAspectFlags.ColorBit);
            // Presentation wait semaphores belong to acquired images, not CPU frame slots.
            _presentReady[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
        var formatProperties = physical.GetFormatProperties(VkFormat.D32SFloat);
        if ((formatProperties.OptimalTilingFeatures & VkFormatFeatureFlags.DepthStencilAttachmentBit) == 0)
            throw new InvalidOperationException("D32_SFLOAT depth attachments unsupported.");
        foreach (var f in _frames)
        {
            f.Depth = _device.CreateImage(new VkImageCreateInfo
            {
                ImageType = VkImageType._2D, Format = VkFormat.D32SFloat,
                Extent = new() { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
                MipLevels = 1, ArrayLayers = 1, Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal, Usage = VkImageUsageFlags.DepthStencilAttachmentBit,
                SharingMode = VkSharingMode.Exclusive,
            }, null);
            var req = _device.GetImageMemoryRequirements(f.Depth);
            f.DepthMemory = _device.AllocateMemory(new VkMemoryAllocateInfo
            {
                AllocationSize = req.Size, MemoryTypeIndex = physical.FindMemoryType(req.MemoryTypeBits, VkMemoryPropertyFlags.DeviceLocalBit),
            }, null);
            _device.BindImageMemory(f.Depth, f.DepthMemory, (ByteSize64)0);
            f.DepthView = CreateImageView(f.Depth, VkFormat.D32SFloat, VkImageAspectFlags.DepthBit);
        }
    }

    private VkImageView CreateImageView(VkImage image, VkFormat format, VkImageAspectFlags aspect) => _device.CreateImageView(new VkImageViewCreateInfo
    {
        Image = image, ViewType = VkImageViewType._2D, Format = format,
        SubresourceRange = new() { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
    }, null);

    private void DestroySwapchain()
    {
        foreach (var f in _frames)
        {
            if (!f.DepthView.IsNull()) _device.DestroyImageView(f.DepthView, null);
            if (!f.Depth.IsNull()) _device.DestroyImage(f.Depth, null);
            if (!f.DepthMemory.IsNull()) _device.FreeMemory(f.DepthMemory, null);
            f.DepthView = default; f.Depth = default; f.DepthMemory = default;
        }
        foreach (var view in _views) if (!view.IsNull()) _device.DestroyImageView(view, null);
        foreach (var semaphore in _presentReady) if (!semaphore.IsNull()) _device.DestroySemaphore(semaphore, null);
        if (!_swapchain.IsNull()) _device.DestroySwapchainKHR(_swapchain, null);
        _views = []; _presentReady = []; _images = []; _swapchain = default;
    }
}

using System.Numerics;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using VoxelBrutal.Core.Lod;

namespace VoxelBrutal.Rendering;

public sealed unsafe partial class VulkanChunkRenderer
{
    private void CreatePyramidDescriptorPool()
    {
        _pyramidWidth = checked((int)BitOperations.RoundUpToPowerOf2((uint)_extent.Width));
        _pyramidHeight = checked((int)BitOperations.RoundUpToPowerOf2((uint)_extent.Height));
        _pyramidMips = BitOperations.Log2((uint)Math.Max(_pyramidWidth, _pyramidHeight)) + 1;
        var format = _device.PhysicalDevice.GetFormatProperties(VkFormat.R32SFloat);
        const VkFormatFeatureFlags required = VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.StorageImageBit;
        if ((format.OptimalTilingFeatures & required) != required)
            throw new InvalidOperationException("R32_SFLOAT sampled/storage images are required for conservative Hi-Z.");
        var sizes = stackalloc VkDescriptorPoolSize[2];
        sizes[0] = new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = FramesInFlight * _pyramidMips };
        sizes[1] = new() { Type = VkDescriptorType.StorageImage, DescriptorCount = FramesInFlight * _pyramidMips };
        _pyramidDescriptorPool = _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
        { MaxSets = FramesInFlight * _pyramidMips, PoolSizeCount = 2, PoolSizes = sizes }, null);
    }

    private void CreateFramePyramid(FrameResources f)
    {
        f.Pyramid = _device.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D, Format = VkFormat.R32SFloat,
            Extent = new() { Width = _pyramidWidth, Height = _pyramidHeight, Depth = 1 },
            MipLevels = _pyramidMips, ArrayLayers = 1, Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal, Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.StorageBit,
            SharingMode = VkSharingMode.Exclusive,
        }, null);
        var requirements = _device.GetImageMemoryRequirements(f.Pyramid);
        f.PyramidMemory = _device.AllocateMemory(new VkMemoryAllocateInfo
        {
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _device.PhysicalDevice.FindMemoryType(requirements.MemoryTypeBits, VkMemoryPropertyFlags.DeviceLocalBit),
        }, null);
        _device.BindImageMemory(f.Pyramid, f.PyramidMemory, (ByteSize64)0);
        f.PyramidView = _device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = f.Pyramid, ViewType = VkImageViewType._2D, Format = VkFormat.R32SFloat,
            SubresourceRange = new() { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = _pyramidMips, LayerCount = 1 },
        }, null);
        var imageInfo = new VkDescriptorImageInfo { Sampler = _depthSampler, ImageView = f.PyramidView, ImageLayout = VkImageLayout.General };
        var update = new VkWriteDescriptorSet { DstSet = f.Set, DstBinding = 3, DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imageInfo };
        _device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(&update, 1), []);
        f.PyramidMipViews = new VkImageView[_pyramidMips];
        f.PyramidSets = new VkDescriptorSet[_pyramidMips];
        var layouts = Enumerable.Repeat(_pyramidSetLayout, _pyramidMips).ToArray();
        fixed (VkDescriptorSetLayout* layoutPointer = layouts)
            _device.AllocateDescriptorSets(new VkDescriptorSetAllocateInfo { DescriptorPool = _pyramidDescriptorPool,
                DescriptorSetCount = _pyramidMips, SetLayouts = layoutPointer }, f.PyramidSets);
        var infos = stackalloc VkDescriptorImageInfo[2];
        var writes = stackalloc VkWriteDescriptorSet[2];
        for (var mip = 0; mip < _pyramidMips; ++mip)
        {
            f.PyramidMipViews[mip] = _device.CreateImageView(new VkImageViewCreateInfo
            {
                Image = f.Pyramid, ViewType = VkImageViewType._2D, Format = VkFormat.R32SFloat,
                SubresourceRange = new() { AspectMask = VkImageAspectFlags.ColorBit, BaseMipLevel = mip, LevelCount = 1, LayerCount = 1 },
            }, null);
            infos[0] = new() { Sampler = _depthSampler, ImageView = mip == 0 ? f.DepthView : f.PyramidMipViews[mip - 1],
                ImageLayout = mip == 0 ? VkImageLayout.DepthReadOnlyOptimal : VkImageLayout.General };
            infos[1] = new() { ImageView = f.PyramidMipViews[mip], ImageLayout = VkImageLayout.General };
            writes[0] = new() { DstSet = f.PyramidSets[mip], DstBinding = 0, DescriptorCount = 1,
                DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = infos };
            writes[1] = new() { DstSet = f.PyramidSets[mip], DstBinding = 1, DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageImage, ImageInfo = infos + 1 };
            _device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, 2), []);
        }
        if (_options.EnableColorReadback)
            f.ColorReadback = new(_device, checked(_extent.Width * _extent.Height * 4), VkBufferUsageFlags.TransferDstBit, true);
    }

    private void DispatchCull(FrameResources f, PushData push, int count)
    {
        var vk = _device.PInvoke;
        vk.vkCmdPushConstants.Invoke(f.Command, _pipelineLayout, Stages, 0, 96, &push);
        if (count == 0) return;
        var set = f.Set;
        vk.vkCmdBindPipeline.Invoke(f.Command, VkPipelineBindPoint.Compute, _cullPipeline);
        vk.vkCmdBindDescriptorSets.Invoke(f.Command, VkPipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        vk.vkCmdDispatch.Invoke(f.Command, (count + 63) / 64, 1, 1);
        var memory = new VkMemoryBarrier2 { SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit, SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            DstStageMask = VkPipelineStageFlags2.DrawIndirectBit, DstAccessMask = VkAccessFlags2.IndirectCommandReadBit };
        var dependency = new VkDependencyInfo { MemoryBarrierCount = 1, MemoryBarriers = &memory };
        vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dependency);
    }

    private void BuildDepthPyramid(FrameResources f)
    {
        var vk = _device.PInvoke;
        var depth = ImageBarrier(f.Depth, VkImageLayout.DepthAttachmentOptimal, VkImageLayout.DepthReadOnlyOptimal,
            VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit, VkAccessFlags2.DepthStencilAttachmentWriteBit,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderSampledReadBit, VkImageAspectFlags.DepthBit);
        var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &depth };
        vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dependency);
        vk.vkCmdBindPipeline.Invoke(f.Command, VkPipelineBindPoint.Compute, _pyramidPipeline);
        for (var mip = 0; mip < _pyramidMips; ++mip)
        {
            var set = f.PyramidSets[mip];
            vk.vkCmdBindDescriptorSets.Invoke(f.Command, VkPipelineBindPoint.Compute, _pyramidPipelineLayout, 0, 1, &set, 0, null);
            var push = new uint4((uint)mip, (uint)_extent.Width, (uint)_extent.Height, 0);
            vk.vkCmdPushConstants.Invoke(f.Command, _pyramidPipelineLayout, VkShaderStageFlags.ComputeBit, 0, 16, &push);
            vk.vkCmdDispatch.Invoke(f.Command, (Math.Max(1, _pyramidWidth >> mip) + 7) / 8, (Math.Max(1, _pyramidHeight >> mip) + 7) / 8, 1);
            var barrier = ImageBarrier(f.Pyramid, VkImageLayout.General, VkImageLayout.General,
                VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
                VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderSampledReadBit, VkImageAspectFlags.ColorBit);
            barrier.SubresourceRange.BaseMipLevel = mip;
            dependency = new() { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
            vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dependency);
        }
    }

    private void FinishFrameReadbackAndPresent(FrameResources f, int imageIndex)
    {
        var vk = _device.PInvoke;
        var oldLayout = VkImageLayout.ColorAttachmentOptimal;
        var srcStage = VkPipelineStageFlags2.ColorAttachmentOutputBit;
        var srcAccess = VkAccessFlags2.ColorAttachmentWriteBit;
        if (f.ColorReadback is { } readback)
        {
            var toCopy = ImageBarrier(_images[imageIndex], oldLayout, VkImageLayout.TransferSrcOptimal,
                srcStage, srcAccess, VkPipelineStageFlags2.CopyBit, VkAccessFlags2.TransferReadBit, VkImageAspectFlags.ColorBit);
            var dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toCopy };
            vk.vkCmdPipelineBarrier2.Invoke(f.Command, &dependency);
            var copy = new VkBufferImageCopy { ImageSubresource = new() { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new() { Width = _extent.Width, Height = _extent.Height, Depth = 1 } };
            vk.vkCmdCopyImageToBuffer.Invoke(f.Command, _images[imageIndex], VkImageLayout.TransferSrcOptimal, readback.Handle, 1, &copy);
            oldLayout = VkImageLayout.TransferSrcOptimal;
            srcStage = VkPipelineStageFlags2.CopyBit;
            srcAccess = VkAccessFlags2.TransferReadBit;
        }
        var final = ImageBarrier(_images[imageIndex], oldLayout, VkImageLayout.PresentSrcKHR,
            srcStage, srcAccess, VkPipelineStageFlags2.None, VkAccessFlags2.None, VkImageAspectFlags.ColorBit);
        var host = new VkMemoryBarrier2 { SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit | VkPipelineStageFlags2.CopyBit,
            SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit | VkAccessFlags2.TransferWriteBit,
            DstStageMask = VkPipelineStageFlags2.HostBit, DstAccessMask = VkAccessFlags2.HostReadBit };
        var endDependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &final,
            MemoryBarrierCount = 1, MemoryBarriers = &host };
        vk.vkCmdPipelineBarrier2.Invoke(f.Command, &endDependency);
    }

    private void ProcessReadback(FrameResources f)
    {
        if (f.Serial <= _readbackSerial) return;
        var counters = new ReadOnlySpan<uint>(f.Visibility.Mapped, 8);
        _earlyVisible = (int)counters[0]; _lateVisible = (int)counters[1];
        _occluded = (int)counters[2]; _frustumVisible = (int)counters[3]; _drawnQuads = counters[4];
        var visibility = new ReadOnlySpan<uint>(f.Visibility.Mapped + 32, f.SubmittedMeshes.Length);
        HashSet<ChunkMeshId> history = [];
        for (var i = 0; i < visibility.Length; ++i)
            if (visibility[i] != 0 && _resident.ContainsKey(f.SubmittedMeshes[i])) history.Add(f.SubmittedMeshes[i]);
        _visibilityHistory = history;
        _readbackSerial = f.Serial;
        if (f.ColorReadback is { } color)
            LastColorFrame = new(_extent.Width, _extent.Height, f.Serial, new ReadOnlySpan<byte>(color.Mapped, color.Size).ToArray());
        Statistics = Statistics with { EarlyVisible = _earlyVisible, LateVisible = _lateVisible,
            Occluded = _occluded, FrustumVisible = _frustumVisible, ReadbackSerial = _readbackSerial, DrawnQuads = _drawnQuads };
    }

    /// <summary>Test/tool synchronization point; normal rendering reads results after ordinary frame fences.</summary>
    public void WaitForLastFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = _frames.MaxBy(f => f.Serial);
        if (frame is null || frame.Serial == 0) return;
        Check(_device.WaitForFences([frame.Fence], true, -1), "wait readback fence");
        _completed = Math.Max(_completed, frame.Serial);
        ProcessReadback(frame);
    }
}

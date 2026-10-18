using Brutal;
using Brutal.VulkanApi;

namespace VoxelBrutal.Rendering;

/// <summary>A few coarse allocations back the renderer. Chunk meshes suballocate the shared arena.</summary>
internal sealed unsafe class GpuBuffer : IDisposable
{
    private readonly Device _device;
    public VkBuffer Handle { get; }
    public VkDeviceMemory Memory { get; }
    public int Size { get; }
    public byte* Mapped { get; }

    public GpuBuffer(Device device, int size, VkBufferUsageFlags usage, bool mapped)
    {
        _device = device;
        Size = size;
        Handle = device.CreateBuffer(new VkBufferCreateInfo { Size = (ByteSize64)size, Usage = usage, SharingMode = VkSharingMode.Exclusive }, null);
        try
        {
            var req = device.GetBufferMemoryRequirements(Handle);
            var properties = mapped ? VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit : VkMemoryPropertyFlags.DeviceLocalBit;
            Memory = device.AllocateMemory(new VkMemoryAllocateInfo
            {
                AllocationSize = req.Size,
                MemoryTypeIndex = device.PhysicalDevice.FindMemoryType(req.MemoryTypeBits, properties),
            }, null);
            device.BindBufferMemory(Handle, Memory, (ByteSize64)0);
            if (mapped)
            {
                void* pointer;
                VulkanChunkRenderer.Check(device.PInvoke.vkMapMemory.Invoke(device, Memory, (ByteSize64)0, (ByteSize64)size, 0, &pointer), "map buffer");
                Mapped = (byte*)pointer;
            }
        }
        catch
        {
            device.DestroyBuffer(Handle, null);
            if (!Memory.IsNull()) device.FreeMemory(Memory, null);
            throw;
        }
    }

    public void Dispose()
    {
        if (Mapped != null) _device.PInvoke.vkUnmapMemory.Invoke(_device, Memory);
        _device.DestroyBuffer(Handle, null);
        _device.FreeMemory(Memory, null);
    }
}

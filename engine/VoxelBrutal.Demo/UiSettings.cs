using Brutal.VulkanApi;

namespace VoxelBrutal.Demo;

/// <summary>User choices consumed by the window controller, frame pacer and Vulkan renderer.</summary>
public sealed class DisplaySettings
{
    public bool Borderless = true;
    public int FrameRateLimit = 60;
    public VkPresentModeKHR PresentMode = VkPresentModeKHR.MailboxKHR;
}

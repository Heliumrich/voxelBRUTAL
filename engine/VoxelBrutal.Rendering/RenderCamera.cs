using System.Numerics;
using VoxelBrutal.Core.Lod;

namespace VoxelBrutal.Rendering;

/// <summary>The camera-relative matrix uses Vulkan 0..1 clip depth and infinite reversed Z.</summary>
public readonly record struct RenderCamera(WorldPosition Position, Matrix4x4 ViewProjection);

public sealed record RendererOptions
{
    public int QuadCapacity { get; init; } = 16 * 1024 * 1024;
    public int MaxChunks { get; init; } = 32_768;
    public int UploadBytesPerFrame { get; init; } = 8 * 1024 * 1024;
    public int MaxPendingUploadBytes { get; init; } = 64 * 1024 * 1024;
    public bool EnableValidation { get; init; }
}

public readonly record struct RendererStatistics(int ResidentChunks, long ResidentQuads, int PendingUploads,
    int UploadedBytes, int IndirectDraws, long SubmittedFrames);

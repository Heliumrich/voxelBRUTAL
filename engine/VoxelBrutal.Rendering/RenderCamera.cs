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
    public bool EnableOcclusion { get; init; } = true;
    public bool EnableColorReadback { get; init; }
}

public readonly record struct RendererStatistics(int ResidentChunks, long ResidentQuads, int PendingUploads,
    int UploadedBytes, int IndirectDraws, long SubmittedFrames, int EarlyVisible = 0, int LateVisible = 0,
    int Occluded = 0, int FrustumVisible = 0, long ReadbackSerial = 0, long DrawnQuads = 0);

/// <summary>Test-only final color copy. Pixels are packed BGRA8 in framebuffer row order.</summary>
public sealed record ColorFrame(int Width, int Height, long Serial, ReadOnlyMemory<byte> Pixels);

namespace VoxelBrutal.Core.Meshing;

/// <summary>Values are part of the CPU/GPU packed-quad contract.</summary>
public enum FaceDirection : byte
{
    NegativeX = 0,
    PositiveX = 1,
    NegativeY = 2,
    PositiveY = 3,
    NegativeZ = 4,
    PositiveZ = 5,
}

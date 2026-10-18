namespace VoxelBrutal.Core.World;

/// <summary>Generation limits are content settings, not limits of cubic chunk addressing.</summary>
public sealed record TerrainSettings
{
    public int MinimumY { get; init; } = -128;
    public int MaximumYExclusive { get; init; } = 512;
    public double BaseHeight { get; init; } = 144;
    public double ContinentalAmplitude { get; init; } = 176;
    public double HillsAmplitude { get; init; } = 64;
    public bool EnableCaves { get; init; } = true;
    public bool EnableBiomes { get; init; } = true;

    internal void Validate()
    {
        if ((long)MaximumYExclusive - MinimumY is < 64 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(MaximumYExclusive));
        if (!double.IsFinite(BaseHeight) || !double.IsFinite(ContinentalAmplitude) || !double.IsFinite(HillsAmplitude) ||
            ContinentalAmplitude < 0 || HillsAmplitude < 0)
            throw new ArgumentOutOfRangeException(nameof(BaseHeight));
    }
}

public readonly record struct BiomeWeights(double Temperate, double Arid, double Alpine);

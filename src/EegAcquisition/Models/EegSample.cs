namespace EegAcquisition.Models;

/// <summary>
/// A single EEG sample point with two channels, in microvolts.
/// </summary>
public readonly record struct EegSample(
    int PacketNumber,
    double Channel1,
    double Channel2
);

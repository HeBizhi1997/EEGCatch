namespace EegAcquisition.Models;

/// <summary>
/// A fully parsed serial data packet containing N EEG samples.
/// </summary>
public sealed class EegDataPacket
{
    public DateTime ReceivedTimestamp { get; init; } = DateTime.Now;
    public EegSample[] Samples { get; init; } = [];
    public int SampleCount { get; init; }
}

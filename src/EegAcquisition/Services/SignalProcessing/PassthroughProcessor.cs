using EegAcquisition.Models;

namespace EegAcquisition.Services.SignalProcessing;

/// <summary>
/// Default no-op signal processor. Passes data through without modification.
/// </summary>
public sealed class PassthroughProcessor : ISignalProcessor
{
    public string Name => "Passthrough";
    public bool IsEnabled { get; set; } = true;

    public EegDataPacket Process(EegDataPacket packet) => packet;
}

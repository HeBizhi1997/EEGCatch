using EegAcquisition.Models;

namespace EegAcquisition.Services.SignalProcessing;

/// <summary>
/// Reserved interface for signal processing modules (filters, FFT, artifact rejection, etc.).
/// </summary>
public interface ISignalProcessor
{
    string Name { get; }
    bool IsEnabled { get; set; }

    /// <summary>
    /// Process an entire data packet, returning a (potentially modified) packet.
    /// </summary>
    EegDataPacket Process(EegDataPacket packet);
}

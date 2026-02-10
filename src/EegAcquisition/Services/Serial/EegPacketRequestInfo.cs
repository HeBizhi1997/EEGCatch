using EegAcquisition.Models;
using TouchSocket.Core;

namespace EegAcquisition.Services.Serial;

/// <summary>
/// Parsed EEG packet data, implementing TouchSocket's IRequestInfo.
/// </summary>
public sealed class EegPacketRequestInfo : IRequestInfo
{
    public EegSample[] Samples { get; set; } = [];
    public int SampleCount { get; set; }
}

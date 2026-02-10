using EegAcquisition.Models;

namespace EegAcquisition.Services.Serial;

public interface ISerialPortService
{
    bool IsConnected { get; }
    event Action<EegDataPacket>? PacketReceived;
    Task ConnectAsync(SerialPortSettings settings, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    string[] GetAvailablePorts();
}

using System.IO.Ports;
using EegAcquisition.Models;
using Microsoft.Extensions.Logging;
using TouchSocket.Core;
using TouchSocket.SerialPorts;

namespace EegAcquisition.Services.Serial;

public sealed class SerialPortService : ISerialPortService, IDisposable
{
    private readonly ILogger<SerialPortService> _logger;
    private SerialPortClient? _client;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public event Action<EegDataPacket>? PacketReceived;

    public SerialPortService(ILogger<SerialPortService> logger)
    {
        _logger = logger;
    }

    public string[] GetAvailablePorts()
    {
        return System.IO.Ports.SerialPort.GetPortNames();
    }

    public async Task ConnectAsync(SerialPortSettings settings, CancellationToken ct = default)
    {
        if (_isConnected)
        {
            _logger.LogWarning("Already connected, disconnect first");
            return;
        }

        try
        {
            _client = new SerialPortClient();

            _client.Received = (c, e) =>
            {
                if (e.RequestInfo is EegPacketRequestInfo packetInfo)
                {
                    var packet = new EegDataPacket
                    {
                        ReceivedTimestamp = DateTime.Now,
                        Samples = packetInfo.Samples,
                        SampleCount = packetInfo.SampleCount
                    };
                    PacketReceived?.Invoke(packet);
                }
                return EasyTask.CompletedTask;
            };

            _client.Connected = (c, e) =>
            {
                _isConnected = true;
                _logger.LogInformation("Serial port {Port} connected", settings.PortName);
                return EasyTask.CompletedTask;
            };

            _client.Closed = (c, e) =>
            {
                _isConnected = false;
                _logger.LogInformation("Serial port {Port} disconnected", settings.PortName);
                return EasyTask.CompletedTask;
            };

            await _client.SetupAsync(new TouchSocketConfig()
                .SetSerialPortOption(new SerialPortOption
                {
                    PortName = settings.PortName,
                    BaudRate = settings.BaudRate,
                    DataBits = settings.DataBits,
                    Parity = settings.Parity,
                    StopBits = settings.StopBits
                })
                .SetSerialDataHandlingAdapter(() => new EegDataAdapter()));

            await _client.ConnectAsync(5000, ct);
            _logger.LogInformation("Serial port {Port} opened at {BaudRate} baud",
                settings.PortName, settings.BaudRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to serial port {Port}", settings.PortName);
            _client?.Dispose();
            _client = null;
            _isConnected = false;
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client == null) return;

        try
        {
            await _client.CloseAsync("User disconnect");
            _logger.LogInformation("Serial port closed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while disconnecting serial port");
        }
        finally
        {
            _client.Dispose();
            _client = null;
            _isConnected = false;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _isConnected = false;
    }
}

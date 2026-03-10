using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace EegAcquisition.Services.Pulse;

/// <summary>
/// HKG-07D 红外脉率传感器串口服务。
///
/// 实际行为：传感器命令无响应，约每 9 秒自动推送一帧：
///   F0 C0 MLH MLL CKSUM
///   BPM = (MLH &lt;&lt; 8) | MLL，无信号时为 0。
///   CKSUM = (0xF0 + 0xC0 + MLH + MLL) &amp; 0xFF
///
/// 只需打开串口被动监听，无需发送任何命令。
/// </summary>
public sealed class PulseSerialService : IPulseSerialService
{
    private readonly ILogger<PulseSerialService> _logger;
    private SerialPort? _port;
    private bool _isConnected;

    // 帧解析状态机
    private enum ParseState { Idle, WaitCtrl, WaitMlh, WaitMll, WaitCksum }
    private ParseState _state = ParseState.Idle;
    private byte _mlh;
    private byte _mll;

    public bool IsConnected => _isConnected;
    public event Action<int>? PulseRateReceived;

    public PulseSerialService(ILogger<PulseSerialService> logger)
    {
        _logger = logger;
    }

    public string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public Task ConnectAsync(string portName, CancellationToken ct = default)
    {
        if (_isConnected) return Task.CompletedTask;

        _port = new SerialPort(portName, 19200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout  = 500,
            WriteTimeout = 500
        };

        _port.DataReceived += OnDataReceived;
        _port.Open();
        _isConnected = true;
        _state = ParseState.Idle;

        _logger.LogInformation("Pulse sensor listening on {Port} (passive mode)", portName);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (_port == null || !_isConnected) return Task.CompletedTask;

        _port.DataReceived -= OnDataReceived;
        try { _port.Close(); } catch { /* ignore */ }
        _port.Dispose();
        _port = null;
        _isConnected = false;

        _logger.LogInformation("Pulse sensor disconnected");
        return Task.CompletedTask;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port == null || !_port.IsOpen) return;

        try
        {
            int available = _port.BytesToRead;
            for (int i = 0; i < available; i++)
            {
                int raw = _port.ReadByte();
                if (raw < 0) break;
                ProcessByte((byte)raw);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pulse serial read error");
            _state = ParseState.Idle;
        }
    }

    /// <summary>
    /// 状态机逐字节解析 F0 C0 MLH MLL CKSUM 帧。
    /// </summary>
    private void ProcessByte(byte b)
    {
        switch (_state)
        {
            case ParseState.Idle:
                if (b == 0xF0) _state = ParseState.WaitCtrl;
                break;

            case ParseState.WaitCtrl:
                if (b == 0xC0) _state = ParseState.WaitMlh;
                else            _state = ParseState.Idle;
                break;

            case ParseState.WaitMlh:
                _mlh   = b;
                _state = ParseState.WaitMll;
                break;

            case ParseState.WaitMll:
                _mll   = b;
                _state = ParseState.WaitCksum;
                break;

            case ParseState.WaitCksum:
                byte expected = (byte)(0xF0 + 0xC0 + _mlh + _mll);
                if (b == expected)
                {
                    int bpm = (_mlh << 8) | _mll;
                    _logger.LogDebug("Pulse received: {Bpm} bpm", bpm);
                    PulseRateReceived?.Invoke(bpm);
                }
                else
                {
                    _logger.LogWarning("Pulse frame checksum mismatch: expected 0x{E:X2}, got 0x{G:X2}", expected, b);
                }
                _state = ParseState.Idle;
                break;
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _port?.Dispose();
    }
}

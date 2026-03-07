using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace EegAcquisition.Services.Pulse;

/// <summary>
/// HKG-07D 红外脉率传感器串口服务。
///
/// 通信协议（19200 bps, 8N1）：
///   帧头: 0xF0
///   控制字: 0xC0（脉率）
///   数据: MLH MLL（脉率高低字节，单位 bpm）
///   校验: CKSUM = (帧头 + 控制字 + 所有数据字节) 的低字节
///
/// 启动命令: F0 C0 B0
/// 停止命令: F0 C1 B1
/// 每次心跳应答: F0 C0 MLH MLL CKSUM
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

    private System.Threading.Timer? _pollTimer;
    private bool _isPolling;

    public bool IsConnected => _isConnected;
    public bool IsPolling => _isPolling;
    public event Action<int>? PulseRateReceived;

    private static readonly byte[] QueryCommand = [0xF0, 0xC0, 0xB0]; // 查询脉率
    private static readonly byte[] StopCommand  = [0xF0, 0xC1, 0xB1]; // 停止上传

    public PulseSerialService(ILogger<PulseSerialService> logger)
    {
        _logger = logger;
    }

    public string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public async Task ConnectAsync(string portName, CancellationToken ct = default)
    {
        if (_isConnected) return;

        _port = new SerialPort(portName, 19200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout  = 500,
            WriteTimeout = 500
        };

        _port.DataReceived += OnDataReceived;
        _port.Open();
        _isConnected = true;
        _state = ParseState.Idle;

        // 连接后发送一次查询命令
        await Task.Run(() => _port.Write(QueryCommand, 0, QueryCommand.Length), ct);
        _logger.LogInformation("Pulse sensor connected on {Port}", portName);
    }

    public void StartPolling(int intervalMs = 1000)
    {
        if (!_isConnected) return;
        StopPolling();
        _isPolling = true;
        _pollTimer = new System.Threading.Timer(_ => SendQuery(), null, 0, intervalMs);
        _logger.LogInformation("Pulse polling started, interval={Interval}ms", intervalMs);
    }

    public void StopPolling()
    {
        _isPolling = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void SendQuery()
    {
        if (_port == null || !_port.IsOpen) return;
        try
        {
            _port.Write(QueryCommand, 0, QueryCommand.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pulse poll send failed");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_port == null || !_isConnected) return;

        StopPolling();

        try
        {
            if (_port.IsOpen)
            {
                await Task.Run(() =>
                {
                    try { _port.Write(StopCommand, 0, StopCommand.Length); }
                    catch { /* 忽略关闭时的写入错误 */ }
                });
            }
        }
        finally
        {
            _port.DataReceived -= OnDataReceived;
            _port.Close();
            _port.Dispose();
            _port = null;
            _isConnected = false;
            _logger.LogInformation("Pulse sensor disconnected");
        }
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
                // 只处理脉率应答帧 (0xC0)
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
        StopPolling();
        if (_isConnected)
            DisconnectAsync().GetAwaiter().GetResult();
        _port?.Dispose();
    }
}

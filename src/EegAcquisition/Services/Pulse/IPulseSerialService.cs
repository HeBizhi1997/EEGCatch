namespace EegAcquisition.Services.Pulse;

public interface IPulseSerialService : IDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// 每次检测到有效心跳时触发，参数为脉率（bpm）。
    /// 传感器无信号时输出 0。
    /// </summary>
    event Action<int>? PulseRateReceived;

    string[] GetAvailablePorts();

    /// <summary>
    /// 连接脉搏传感器，发送启动脉率上传命令。
    /// </summary>
    Task ConnectAsync(string portName, CancellationToken ct = default);

    Task DisconnectAsync();
}

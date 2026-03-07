namespace EegAcquisition.Services.Pulse;

public interface IPulseSerialService : IDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// 定时轮询是否开启（每隔 intervalMs 重发一次 0xC0 查询命令）。
    /// </summary>
    bool IsPolling { get; }

    /// <summary>
    /// 每次检测到有效心跳时触发，参数为脉率（bpm）。
    /// 传感器无信号时输出 0。
    /// </summary>
    event Action<int>? PulseRateReceived;

    string[] GetAvailablePorts();

    /// <summary>
    /// 连接脉搏传感器并发送一次启动命令。
    /// </summary>
    Task ConnectAsync(string portName, CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>
    /// 开启定时轮询，每隔 intervalMs 毫秒重发一次 0xC0 查询命令。
    /// </summary>
    void StartPolling(int intervalMs = 1000);

    /// <summary>
    /// 停止定时轮询。
    /// </summary>
    void StopPolling();
}

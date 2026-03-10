namespace EegAcquisition.Services.Pulse;

public interface IPulseSerialService : IDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// 每次接收到传感器主动推送的脉率帧时触发，参数为脉率（bpm）。
    /// 传感器约每 9 秒推送一次 F0 C0 MLH MLL CKSUM，无信号时输出 0。
    /// </summary>
    event Action<int>? PulseRateReceived;

    string[] GetAvailablePorts();

    /// <summary>
    /// 打开串口，被动等待传感器主动推送数据（传感器命令无效，仅需监听）。
    /// </summary>
    Task ConnectAsync(string portName, CancellationToken ct = default);

    Task DisconnectAsync();
}

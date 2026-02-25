using System.Buffers.Binary;
using System.IO.Ports;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const int BaudRate = 115200;
const int SamplesPerPacket = 256;

string portName = args.Length > 0 ? args[0] : "COM1";

PrintHeader();
PrintDecodingComparison();

Console.WriteLine($"发送端口: {portName}  波特率: {BaudRate}");
Console.WriteLine();
Console.WriteLine("选择测试模式:");
Console.WriteLine("  [1] 持续发送固定值 (+100000 / -100000 uV 交替, 1秒/包)");
Console.WriteLine("  [2] 持续发送正弦波 (10 Hz, 峰值 500000 uV)");
Console.WriteLine("  [3] 单次诊断包 (多组典型值, 发完退出)");
Console.WriteLine("  [q] 退出");
Console.Write("\n请选择: ");

var choice = Console.ReadKey().KeyChar;
Console.WriteLine();

if (choice == 'q') return;

SerialPort? port = null;
try
{
    port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One);
    port.Open();
    Console.WriteLine($"\n端口 {portName} 已打开");
    Console.WriteLine(choice != '3' ? "按 Ctrl+C 停止\n" : "");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    switch (choice)
    {
        case '1': await SendFixedValueLoop(port, cts.Token); break;
        case '2': await SendSineWaveLoop(port, cts.Token); break;
        case '3': SendDiagnosticPackets(port); break;
        default:
            Console.WriteLine("无效选择");
            break;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\n错误: {ex.Message}");
    Console.WriteLine("请确认 COM1 虚拟端口已创建，且 WPF 程序连接在 COM2 端。");
}
finally
{
    port?.Close();
    port?.Dispose();
    Console.WriteLine("\n端口已关闭。按任意键退出...");
    Console.ReadKey();
}

// ─────────────────────────────────────────────────────────────────────────────
// 解码对比表
// ─────────────────────────────────────────────────────────────────────────────

static void PrintHeader()
{
    Console.WriteLine("┌─────────────────────────────────────────────────────────────────────┐");
    Console.WriteLine("│               EEG 串口协议测试工具  v1.0                             │");
    Console.WriteLine("│  用途: 通过虚拟串口 COM1 向 WPF 程序(COM2)发送已知波形，验证解析正确性 │");
    Console.WriteLine("└─────────────────────────────────────────────────────────────────────┘");
    Console.WriteLine();
}

static void PrintDecodingComparison()
{
    Console.WriteLine("═══ 解码方式对比（帮助定位 WPF 端显示错误的原因）═══");
    Console.WriteLine("协议定义 : 符号幅度整数  value = D3<<24 + D2<<16 + D1<<8 + D0");
    Console.WriteLine("           最高位 bit31 为符号位（0=正，1=负），其余 31 位为幅度");
    Console.WriteLine();
    Console.WriteLine("原代码错误: 用 ReadSingleLittleEndian 把 4 字节当 IEEE-754 float 读取");
    Console.WriteLine("修复方式  : 用 ReadUInt32LittleEndian 读整数，再提取符号位和幅度");
    Console.WriteLine();

    Console.WriteLine($"{"发送值 (uV)",-18} {"原始字节 (D0 D1 D2 D3)",-22} {"正确解码 ✓",16} {"float 错误解码 ✗",20}");
    Console.WriteLine(new string('─', 80));

    int[] testValues = { 100, -100, 100_000, -100_000, 1_000_000, -1_000_000, 2_147_483_647, -2_147_483_647 };
    foreach (var v in testValues)
    {
        byte[] bytes = EncodeSignMagnitude(v);
        double correct = DecodeSignMagnitudeCorrect(bytes);
        float buggy = DecodeAsFloatBuggy(bytes);
        string buggyStr = float.IsNaN(buggy) || float.IsInfinity(buggy)
            ? buggy.ToString()
            : buggy.ToString("+0.######e+0;-0.######e+0");

        Console.WriteLine($"{v + " uV",-18} {BitConverter.ToString(bytes),-22} {correct,+16:0.##} {buggyStr,20}");
    }

    Console.WriteLine(new string('─', 80));
    Console.WriteLine("结论: float 解码结果完全错误（极小的非正规浮点数），导致波形显示为 0 附近的噪声");
    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// 发送逻辑
// ─────────────────────────────────────────────────────────────────────────────

static async Task SendFixedValueLoop(SerialPort port, CancellationToken ct)
{
    int packetIndex = 0;
    while (!ct.IsCancellationRequested)
    {
        // 奇偶包交替，方便在波形图上看到方波
        int ch1Val = (packetIndex % 2 == 0) ? 100_000 : -100_000;
        int ch2Val = -ch1Val;

        var ch1 = Enumerable.Repeat(ch1Val, SamplesPerPacket).ToArray();
        var ch2 = Enumerable.Repeat(ch2Val, SamplesPerPacket).ToArray();
        byte[] packet = BuildPacket(ch1, ch2);

        port.Write(packet, 0, packet.Length);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 包#{packetIndex:D4}  CH1={ch1Val,+10} uV  CH2={ch2Val,+10} uV  ({packet.Length} bytes)");

        packetIndex++;
        try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
    }
}

static async Task SendSineWaveLoop(SerialPort port, CancellationToken ct)
{
    const double Amplitude = 500_000.0; // uV
    const double Freq1Hz = 10.0;        // CH1: 10 Hz
    const double Freq2Hz = 20.0;        // CH2: 20 Hz

    int totalSamples = 0;
    int packetIndex = 0;

    while (!ct.IsCancellationRequested)
    {
        var ch1 = new int[SamplesPerPacket];
        var ch2 = new int[SamplesPerPacket];

        for (int i = 0; i < SamplesPerPacket; i++)
        {
            double t = (totalSamples + i) / (double)SamplesPerPacket;
            ch1[i] = (int)(Amplitude * Math.Sin(2 * Math.PI * Freq1Hz * t));
            ch2[i] = (int)(Amplitude * Math.Sin(2 * Math.PI * Freq2Hz * t));
        }

        byte[] packet = BuildPacket(ch1, ch2);
        port.Write(packet, 0, packet.Length);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 包#{packetIndex:D4}  CH1[0]={ch1[0],+10} uV  CH2[0]={ch2[0],+10} uV  ({packet.Length} bytes)");

        totalSamples += SamplesPerPacket;
        packetIndex++;
        try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
    }
}

static void SendDiagnosticPackets(SerialPort port)
{
    // 每组发整包 256 个相同值，让波形图上出现清晰的水平线，便于对比期望值
    var cases = new (int ch1, int ch2, string label)[]
    {
        (       100,        -100, "小正值 +100 uV / 小负值 -100 uV"),
        (   100_000,    -100_000, "±100,000 uV"),
        ( 1_000_000,  -1_000_000, "±1,000,000 uV"),
        (2_147_483_647, -2_147_483_647, "最大值 ±2,147,483,647 uV"),
        (         0,           0, "零值  0 uV"),
    };

    Console.WriteLine("\n开始发送诊断包，每包间隔 1.1 秒...\n");
    Console.WriteLine($"{"包#",-5} {"CH1 期望值 (uV)",22} {"CH2 期望值 (uV)",22}  说明");
    Console.WriteLine(new string('─', 75));

    for (int idx = 0; idx < cases.Length; idx++)
    {
        var (ch1Val, ch2Val, label) = cases[idx];
        var ch1 = Enumerable.Repeat(ch1Val, SamplesPerPacket).ToArray();
        var ch2 = Enumerable.Repeat(ch2Val, SamplesPerPacket).ToArray();
        byte[] packet = BuildPacket(ch1, ch2);

        port.Write(packet, 0, packet.Length);
        Console.WriteLine($"#{idx,-4} {ch1Val,+22} {ch2Val,+22}  {label}");

        if (idx < cases.Length - 1)
            Thread.Sleep(1100);
    }

    Console.WriteLine("\n诊断包发送完毕。请对照 WPF 波形图验证上列期望值是否正确显示。");
}

// ─────────────────────────────────────────────────────────────────────────────
// 协议编解码工具
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 将有符号整数编码为协议规定的符号幅度格式（小端序4字节）。
/// 正数: bit31=0, bits30-0=value
/// 负数: bit31=1, bits30-0=abs(value)
/// </summary>
static byte[] EncodeSignMagnitude(int value)
{
    uint raw = value >= 0
        ? (uint)value
        : (uint)(-value) | 0x80000000u;

    var bytes = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, raw);
    return bytes;
}

/// <summary>正确的符号幅度解码（修复后的版本）</summary>
static double DecodeSignMagnitudeCorrect(byte[] data)
{
    uint raw = BinaryPrimitives.ReadUInt32LittleEndian(data);
    bool isNegative = (raw & 0x80000000u) != 0;
    int magnitude = (int)(raw & 0x7FFFFFFFu);
    return isNegative ? -magnitude : magnitude;
}

/// <summary>错误的 float 解码（WPF 原代码的行为，仅用于展示 bug）</summary>
static float DecodeAsFloatBuggy(byte[] data)
{
    return BinaryPrimitives.ReadSingleLittleEndian(data);
}

/// <summary>
/// 构建完整数据包：0x2A + N×[PacketNum(1B) + CH1(4B) + CH2(4B)] + 0x40 0x40
/// </summary>
static byte[] BuildPacket(int[] ch1Values, int[] ch2Values)
{
    int n = ch1Values.Length;
    // Header(1) + n * SampleSize(9) + Tail(2)
    var packet = new byte[1 + n * 9 + 2];
    int pos = 0;

    packet[pos++] = 0x2A; // 包头

    for (int i = 0; i < n; i++)
    {
        packet[pos++] = (byte)(i & 0xFF); // 包编号

        var ch1Bytes = EncodeSignMagnitude(ch1Values[i]);
        var ch2Bytes = EncodeSignMagnitude(ch2Values[i]);

        Array.Copy(ch1Bytes, 0, packet, pos, 4); pos += 4;
        Array.Copy(ch2Bytes, 0, packet, pos, 4); pos += 4;
    }

    packet[pos++] = 0x40; // 包尾
    packet[pos++] = 0x40;

    return packet;
}

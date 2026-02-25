using System.Buffers.Binary;
using EegAcquisition.Models;
using TouchSocket.Core;

namespace EegAcquisition.Services.Serial;

/// <summary>
/// Custom TouchSocket data handling adapter for the EEG serial protocol.
///
/// Protocol format:
///   Header(0x2A) + N×[PacketNum(1B) + Ch1(4B) + Ch2(4B)] + Tail(0x40 0x40)
///
/// Each sample group is 9 bytes. Channel data is sign-magnitude 32-bit LE.
/// </summary>
public sealed class EegDataAdapter : CustomDataHandlingAdapter<EegPacketRequestInfo>
{
    private const byte Header = 0x2A;
    private const byte Tail = 0x40;
    private const int TailSize = 2; // 0x40 0x40
    private const int SampleSize = 9; // 1 + 4 + 4
    private const int MinPacketSize = 1 + SampleSize + TailSize; // header + 1 sample + tail = 12
    private const int ExpectedSamples = 256;
    private const int ExpectedPayloadLen = ExpectedSamples * SampleSize; // 2304

#pragma warning disable CS8765 // Nullability of parameter type doesn't match overridden member
    protected override FilterResult Filter<TByteBlock>(
        ref TByteBlock byteBlock,
        bool beCached,
        ref EegPacketRequestInfo? request,
        ref int tempCapacity)
#pragma warning restore CS8765
    {
        if (byteBlock.CanReadLength < MinPacketSize)
            return FilterResult.Cache;

        var startPos = byteBlock.Position;
        int available = (int)byteBlock.CanReadLength;

        // Read all available data into a span for scanning
        var data = byteBlock.ReadToSpan(available);

        // ── Step 1: 定位包头 0x2A ────────────────────────────────────────────
        int headerIdx = -1;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == Header)
            {
                headerIdx = i;
                break;
            }
        }

        if (headerIdx < 0)
        {
            // 无包头，丢弃全部字节
            return FilterResult.GoOn;
        }

        if (headerIdx > 0)
        {
            // 包头不在起始位置，跳过之前的脏字节
            byteBlock.Position = startPos + headerIdx;
            return FilterResult.GoOn;
        }

        // ── Step 2: 在包头之后扫描包尾 0x40 0x40 ───────────────────────────
        // 只有同时确认包头和包尾后，才进入数据拆解阶段
        // 载荷起始偏移 = 1（跳过包头字节）
        // 最短有效包尾位置 = 1 + SampleSize（至少一条采样数据）
        const int payloadStart = 1;
        int tailIdx = -1;

        for (int i = payloadStart + SampleSize; i <= data.Length - TailSize; i++)
        {
            if (data[i] != Tail || data[i + 1] != Tail)
                continue;

            // 验证包头到包尾之间的载荷长度是否为 SampleSize 的整数倍
            int payloadLen = i - payloadStart;
            if (payloadLen > 0 && payloadLen % SampleSize == 0)
            {
                tailIdx = i;
                break;
            }
        }

        if (tailIdx < 0)
        {
            // 包头已找到但包尾尚未到达，等待更多数据
            byteBlock.Position = startPos;
            return FilterResult.Cache;
        }

        // ── Step 3: 包头和包尾均已确认，执行完整包的数据拆解 ─────────────
        int totalPayloadLen = tailIdx - payloadStart;
        var payload = data.Slice(payloadStart, totalPayloadLen);
        request = ParseSamples(payload);
        byteBlock.Position = startPos + tailIdx + TailSize;
        return FilterResult.Success;
    }

    private static EegPacketRequestInfo ParseSamples(ReadOnlySpan<byte> payload)
    {
        int sampleCount = payload.Length / SampleSize;
        var samples = new EegSample[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * SampleSize;
            byte packetNum = payload[offset];
            double ch1 = DecodeSignMagnitude32(payload.Slice(offset + 1, 4));
            double ch2 = DecodeSignMagnitude32(payload.Slice(offset + 5, 4));
            samples[i] = new EegSample(packetNum, ch1, ch2);
        }

        return new EegPacketRequestInfo
        {
            Samples = samples,
            SampleCount = sampleCount
        };
    }

    /// <summary>
    /// 按协议定义解码符号幅度整数（Sign-Magnitude Int32，小端序）。
    /// 协议: D3&lt;&lt;24 + D2&lt;&lt;16 + D1&lt;&lt;8 + D0，最高位为符号位（0=正，1=负）。
    /// 值域: ±2,147,483,647 uV
    /// </summary>
    public static double DecodeSignMagnitude32(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            throw new ArgumentException("Data must be at least 4 bytes.");

        // 按小端序组装 32 位无符号整数：value = D3<<24 + D2<<16 + D1<<8 + D0
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(data);

        // 最高位为符号位，其余 31 位为幅度值
        bool isNegative = (raw & 0x80000000u) != 0;
        int magnitude = (int)(raw & 0x7FFFFFFFu);

        return isNegative ? -magnitude : magnitude;
    }
}

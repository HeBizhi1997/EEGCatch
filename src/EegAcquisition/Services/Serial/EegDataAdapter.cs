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
/// Each sample group is 9 bytes. Channel data is IEEE 754 float LE.
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

        // Scan for header byte 0x2A
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
            // No header found, discard all bytes
            return FilterResult.GoOn;
        }

        // If header is not at start, skip bytes before it and re-enter
        if (headerIdx > 0)
        {
            byteBlock.Position = startPos + headerIdx;
            return FilterResult.GoOn;
        }

        // Header is at index 0
        int payloadStart = 1;
        int remainingAfterHeader = data.Length - 1;

        // Try expected packet size first (256 samples)
        if (remainingAfterHeader >= ExpectedPayloadLen + TailSize)
        {
            int tailIdx = payloadStart + ExpectedPayloadLen;
            if (data[tailIdx] == Tail && data[tailIdx + 1] == Tail)
            {
                var payload = data.Slice(payloadStart, ExpectedPayloadLen);
                request = ParseSamples(payload);
                byteBlock.Position = startPos + tailIdx + TailSize;
                return FilterResult.Success;
            }
        }

        // Fallback: scan for any valid 2-byte tail (payload length divisible by 9)
        for (int i = payloadStart + SampleSize; i + 1 < data.Length; i++)
        {
            if (data[i] == Tail && data[i + 1] == Tail)
            {
                int payloadLen = i - payloadStart;
                if (payloadLen > 0 && payloadLen % SampleSize == 0)
                {
                    var payload = data.Slice(payloadStart, payloadLen);
                    request = ParseSamples(payload);
                    byteBlock.Position = startPos + i + TailSize;
                    return FilterResult.Success;
                }
            }
        }

        // Header found but no valid tail yet - cache from header position
        byteBlock.Position = startPos;
        return FilterResult.Cache;
    }

    private static EegPacketRequestInfo ParseSamples(ReadOnlySpan<byte> payload)
    {
        int sampleCount = payload.Length / SampleSize;
        var samples = new EegSample[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * SampleSize;
            byte packetNum = payload[offset];
            double ch1 = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(offset + 1, 4));
            double ch2 = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(offset + 5, 4));
            samples[i] = new EegSample(packetNum, ch1, ch2);
        }

        return new EegPacketRequestInfo
        {
            Samples = samples,
            SampleCount = sampleCount
        };
    }
}

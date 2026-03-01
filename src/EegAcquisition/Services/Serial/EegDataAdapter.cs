using System.Buffers.Binary;
using EegAcquisition.Models;
using TouchSocket.Core;

namespace EegAcquisition.Services.Serial;

/// <summary>
/// Custom TouchSocket data handling adapter for the EEG serial protocol.
///
/// Protocol format (per packet):
///   Header  (1B)  : 0x2A
///   PktNum  (1B)  : 0x01 ~ 0x04
///   Ch1Data (256B): 64 × 4-byte IEEE-754 LE float values
///   Ch2Data (256B): 64 × 4-byte IEEE-754 LE float values
///   Tail    (2B)  : 0x40 0x40
///
/// Total packet size: 516 bytes.
/// One second = 4 packets (numbered 1-4), each carrying 64 samples per channel.
/// </summary>
public sealed class EegDataAdapter : CustomDataHandlingAdapter<EegPacketRequestInfo>
{
    private const byte Header = 0x2A;
    private const byte Tail = 0x40;

    private const int SamplesPerPacket = 64;
    private const int SampleBytes = 4;                               // IEEE-754 single
    private const int ChannelBlockSize = SamplesPerPacket * SampleBytes; // 256
    private const int PayloadSize = 1 + ChannelBlockSize + ChannelBlockSize; // pktNum + ch1 + ch2 = 513
    private const int TailSize = 2;
    private const int PacketTotalSize = 1 + PayloadSize + TailSize;  // header + payload + tail = 516

#pragma warning disable CS8765
    protected override FilterResult Filter<TByteBlock>(
        ref TByteBlock byteBlock,
        bool beCached,
        ref EegPacketRequestInfo? request,
        ref int tempCapacity)
#pragma warning restore CS8765
    {
        if (byteBlock.CanReadLength < PacketTotalSize)
            return FilterResult.Cache;

        var startPos = byteBlock.Position;
        int available = (int)byteBlock.CanReadLength;
        var data = byteBlock.ReadToSpan(available);

        // Find next header byte 0x2A
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
            // No header in buffer, discard everything
            return FilterResult.GoOn;
        }

        if (headerIdx > 0)
        {
            // Skip garbage bytes before the header
            byteBlock.Position = startPos + headerIdx;
            return FilterResult.GoOn;
        }

        // Header is at index 0; check we have a full packet
        if (data.Length < PacketTotalSize)
        {
            byteBlock.Position = startPos;
            return FilterResult.Cache;
        }

        // Verify tail at the expected fixed offset
        int tailOffset = 1 + PayloadSize; // = 514
        if (data[tailOffset] == Tail && data[tailOffset + 1] == Tail)
        {
            // payload: bytes[1 .. 513]
            request = ParsePacket(data.Slice(1, PayloadSize));
            byteBlock.Position = startPos + PacketTotalSize;
            return FilterResult.Success;
        }

        // Tail mismatch — this header byte is spurious, skip it and resync
        byteBlock.Position = startPos + 1;
        return FilterResult.GoOn;
    }

    private static EegPacketRequestInfo ParsePacket(ReadOnlySpan<byte> payload)
    {
        // Layout: pktNum(1) | ch1Block(4) * 64+ch2Block(4) * 64 = 512
        byte packetNum = payload[0];
        var ch1Block = payload.Slice(1, ChannelBlockSize);
        var ch2Block = payload.Slice(1 + ChannelBlockSize, ChannelBlockSize);
        var samples = new EegSample[SamplesPerPacket];
        for (int i = 0; i < SamplesPerPacket; i++)
        {
            int offset = i * SampleBytes;
            float ch1 = BinaryPrimitives.ReadSingleLittleEndian(ch1Block.Slice(offset, SampleBytes));
            float ch2 = BinaryPrimitives.ReadSingleLittleEndian(ch2Block.Slice(offset, SampleBytes));
            samples[i] = new EegSample(packetNum, ch1, ch2);
        }

        return new EegPacketRequestInfo
        {
            Samples = samples,
            SampleCount = SamplesPerPacket
        };
    }
}
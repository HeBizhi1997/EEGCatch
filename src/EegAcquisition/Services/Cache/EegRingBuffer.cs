using EegAcquisition.Models;

namespace EegAcquisition.Services.Cache;

/// <summary>
/// Lock-based circular buffer storing up to <see cref="Capacity"/> EEG samples.
/// Uses separate double[] arrays for each channel for cache-friendly access during plotting.
/// </summary>
public sealed class EegRingBuffer : IEegRingBuffer
{
    private readonly double[] _ch1;
    private readonly double[] _ch2;
    private int _writeIndex;
    private int _count;
    private readonly object _lock = new();

    public int Capacity { get; }
    public int Count { get { lock (_lock) { return _count; } } }

    /// <param name="capacitySamples">Buffer capacity in samples. Default = 256,000 (1000s × 256Hz).</param>
    public EegRingBuffer(int capacitySamples = 256_000)
    {
        Capacity = capacitySamples;
        _ch1 = new double[capacitySamples];
        _ch2 = new double[capacitySamples];
    }

    public void Write(ReadOnlySpan<EegSample> samples)
    {
        lock (_lock)
        {
            foreach (var s in samples)
            {
                _ch1[_writeIndex] = s.Channel1;
                _ch2[_writeIndex] = s.Channel2;
                _writeIndex = (_writeIndex + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }
    }

    public int CopyLatest(int count, Span<double> ch1, Span<double> ch2)
    {
        lock (_lock)
        {
            int actual = Math.Min(count, _count);
            if (actual == 0) return 0;

            int startIdx = ((_writeIndex - actual) % Capacity + Capacity) % Capacity;

            if (startIdx + actual <= Capacity)
            {
                _ch1.AsSpan(startIdx, actual).CopyTo(ch1);
                _ch2.AsSpan(startIdx, actual).CopyTo(ch2);
            }
            else
            {
                int firstPart = Capacity - startIdx;
                int secondPart = actual - firstPart;

                _ch1.AsSpan(startIdx, firstPart).CopyTo(ch1);
                _ch1.AsSpan(0, secondPart).CopyTo(ch1.Slice(firstPart));

                _ch2.AsSpan(startIdx, firstPart).CopyTo(ch2);
                _ch2.AsSpan(0, secondPart).CopyTo(ch2.Slice(firstPart));
            }

            return actual;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _writeIndex = 0;
            _count = 0;
            Array.Clear(_ch1);
            Array.Clear(_ch2);
        }
    }
}

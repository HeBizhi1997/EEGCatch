using EegAcquisition.Models;

namespace EegAcquisition.Services.Cache;

public interface IEegRingBuffer
{
    /// <summary>Maximum number of samples the buffer can hold.</summary>
    int Capacity { get; }

    /// <summary>Current number of valid samples in the buffer.</summary>
    int Count { get; }

    /// <summary>Write a batch of samples into the ring buffer.</summary>
    void Write(ReadOnlySpan<EegSample> samples);

    /// <summary>
    /// Copy the most recent <paramref name="count"/> samples into the provided spans.
    /// Returns the actual number of samples copied.
    /// </summary>
    int CopyLatest(int count, Span<double> ch1, Span<double> ch2);

    /// <summary>Clear all data from the buffer.</summary>
    void Clear();
}

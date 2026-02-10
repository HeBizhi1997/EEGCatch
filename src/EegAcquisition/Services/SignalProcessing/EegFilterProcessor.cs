using EegAcquisition.Models;
using Microsoft.Extensions.Logging;

namespace EegAcquisition.Services.SignalProcessing;

/// <summary>
/// EEG signal processor with configurable filter chain:
///   1. 50 Hz notch filter  - removes power line interference
///   2. High-pass at 0.5 Hz - removes DC offset and slow drift
///   3. Low-pass at 45 Hz   - removes high-frequency noise / anti-alias
///
/// Each channel has independent filter states.
/// Filters can be toggled on/off at runtime via <see cref="IsEnabled"/>.
/// </summary>
public sealed class EegFilterProcessor : ISignalProcessor
{
    private const double SampleRate = 256.0;
    private readonly ILogger<EegFilterProcessor> _logger;

    // Per-channel filter instances (each maintains its own state)
    private readonly ChannelFilters _ch1Filters;
    private readonly ChannelFilters _ch2Filters;

    public string Name => "EEG Filter (50Hz Notch + 0.5-45Hz Bandpass)";
    public bool IsEnabled { get; set; }

    // Individual filter toggles
    public bool NotchEnabled { get; set; } = true;
    public bool BandpassEnabled { get; set; } = true;

    public EegFilterProcessor(ILogger<EegFilterProcessor> logger)
    {
        _logger = logger;
        _ch1Filters = new ChannelFilters(SampleRate);
        _ch2Filters = new ChannelFilters(SampleRate);
    }

    public EegDataPacket Process(EegDataPacket packet)
    {
        if (!IsEnabled || packet.SampleCount == 0)
            return packet;

        // Copy samples to avoid mutating the original packet
        var filtered = new EegSample[packet.SampleCount];
        Array.Copy(packet.Samples, filtered, packet.SampleCount);

        // Extract channel data into temp arrays for batch processing
        var ch1 = new double[packet.SampleCount];
        var ch2 = new double[packet.SampleCount];

        for (int i = 0; i < packet.SampleCount; i++)
        {
            ch1[i] = filtered[i].Channel1;
            ch2[i] = filtered[i].Channel2;
        }

        // Apply filters
        _ch1Filters.Apply(ch1.AsSpan(), NotchEnabled, BandpassEnabled);
        _ch2Filters.Apply(ch2.AsSpan(), NotchEnabled, BandpassEnabled);

        // Write back
        for (int i = 0; i < packet.SampleCount; i++)
        {
            filtered[i] = new EegSample(filtered[i].PacketNumber, ch1[i], ch2[i]);
        }

        return new EegDataPacket
        {
            ReceivedTimestamp = packet.ReceivedTimestamp,
            Samples = filtered,
            SampleCount = packet.SampleCount
        };
    }

    /// <summary>Reset all filter states (call when starting a new recording/session).</summary>
    public void Reset()
    {
        _ch1Filters.Reset();
        _ch2Filters.Reset();
        _logger.LogDebug("Filter states reset");
    }

    /// <summary>Holds a complete set of filters for one channel.</summary>
    private sealed class ChannelFilters
    {
        private readonly BiquadFilter _notch50Hz;
        private readonly BiquadFilter _highPass;
        private readonly BiquadFilter _lowPass;

        public ChannelFilters(double sampleRate)
        {
            _notch50Hz = BiquadFilter.Notch(sampleRate, 50.0, q: 30.0);
            _highPass = BiquadFilter.HighPass(sampleRate, 0.5);
            _lowPass = BiquadFilter.LowPass(sampleRate, 45.0);
        }

        public void Apply(Span<double> samples, bool notchEnabled, bool bandpassEnabled)
        {
            if (notchEnabled)
                _notch50Hz.Process(samples);

            if (bandpassEnabled)
            {
                _highPass.Process(samples);
                _lowPass.Process(samples);
            }
        }

        public void Reset()
        {
            _notch50Hz.Reset();
            _highPass.Reset();
            _lowPass.Reset();
        }
    }
}

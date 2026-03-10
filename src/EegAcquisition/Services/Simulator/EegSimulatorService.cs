using EegAcquisition.Models;
using EegAcquisition.Services.Pipeline;
using Microsoft.Extensions.Logging;

namespace EegAcquisition.Services.Simulator;

/// <summary>
/// Simulates an EEG acquisition device by generating synthetic waveform data
/// and pushing it directly into the pipeline at 256 samples/sec.
///
/// Channel 1: 10 Hz sine (alpha rhythm) + 50 Hz line noise + random noise
/// Channel 2: 22 Hz sine (beta rhythm) + 50 Hz line noise + random noise
/// </summary>
public sealed class EegSimulatorService : IDisposable
{
    private readonly IPipelineService _pipelineService;
    private readonly ILogger<EegSimulatorService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _simulatorTask;
    private volatile bool _isRunning;

    // Waveform parameters
    private const int SampleRate = 256;
    private const int PacketsPerSecond = 4;
    private const int SamplesPerPacket = SampleRate / PacketsPerSecond; // 64 samples per packet
    private const double Ch1FreqHz = 10.0;    // Alpha rhythm
    private const double Ch2FreqHz = 22.0;    // Beta rhythm
    private const double LineNoiseHz = 50.0;  // Power line interference
    private const double SignalAmplitude = 100.0;  // µV - main signal
    private const double NoiseAmplitude = 10.0;    // µV - random noise
    private const double LineNoiseAmplitude = 20.0; // µV - 50Hz interference

    public bool IsRunning => _isRunning;

    public EegSimulatorService(
        IPipelineService pipelineService,
        ILogger<EegSimulatorService> logger)
    {
        _pipelineService = pipelineService;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            _logger.LogWarning("Simulator is already running");
            return;
        }

        // Ensure pipeline is running
        if (!_pipelineService.IsRunning)
        {
            await _pipelineService.StartAsync();
        }

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _simulatorTask = Task.Run(() => SimulatorLoop(_cts.Token));
        _logger.LogInformation("EEG Simulator started: Ch1={Ch1}Hz, Ch2={Ch2}Hz, {Rate}Hz sample rate",
            Ch1FreqHz, Ch2FreqHz, SampleRate);
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;

        if (_cts != null)
        {
            await _cts.CancelAsync();

            if (_simulatorTask != null)
            {
                try
                {
                    await _simulatorTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Simulator stop timed out");
                }
                catch (OperationCanceledException) { }
            }

            _cts.Dispose();
            _cts = null;
        }

        _logger.LogInformation("EEG Simulator stopped");
    }

    private async Task SimulatorLoop(CancellationToken ct)
    {
        var random = new Random(42); // Fixed seed for reproducibility
        long totalSampleIndex = 0;
        byte packetNumber = 0;

        // 250ms interval: 4 packets per second, matching real hardware protocol
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000 / PacketsPerSecond));

        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            {
                var samples = new EegSample[SamplesPerPacket];

                for (int i = 0; i < SamplesPerPacket; i++)
                {
                    double t = (double)(totalSampleIndex + i) / SampleRate;

                    // Channel 1: Alpha rhythm (10 Hz) + 50 Hz noise + random noise
                    double ch1 = SignalAmplitude * Math.Sin(2 * Math.PI * Ch1FreqHz * t)
                               + LineNoiseAmplitude * Math.Sin(2 * Math.PI * LineNoiseHz * t)
                               + NoiseAmplitude * (random.NextDouble() * 2 - 1);

                    // Channel 2: Beta rhythm (22 Hz) + 50 Hz noise + random noise (different phase)
                    double ch2 = SignalAmplitude * 0.7 * Math.Sin(2 * Math.PI * Ch2FreqHz * t + Math.PI / 4)
                               + LineNoiseAmplitude * Math.Sin(2 * Math.PI * LineNoiseHz * t + Math.PI / 3)
                               + NoiseAmplitude * (random.NextDouble() * 2 - 1);

                    samples[i] = new EegSample(packetNumber, ch1, ch2);
                }

                // Packet number cycles 1-4, matching the real hardware
                packetNumber = (byte)(packetNumber % 4 + 1);

                var packet = new EegDataPacket
                {
                    ReceivedTimestamp = DateTime.Now,
                    Samples = samples,
                    SampleCount = SamplesPerPacket
                };

                await _pipelineService.PushAsync(packet);
                totalSampleIndex += SamplesPerPacket;

                _logger.LogDebug("Simulator sent packet #{PktNum}: {Samples} samples, total={Total}",
                    packetNumber, SamplesPerPacket, totalSampleIndex);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulator loop error");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

using System.Threading.Channels;
using EegAcquisition.Models;
using EegAcquisition.Services.Cache;
using EegAcquisition.Services.SignalProcessing;
using EegAcquisition.Services.Storage;
using Microsoft.Extensions.Logging;

namespace EegAcquisition.Services.Pipeline;

/// <summary>
/// Channel-based pipeline orchestrator.
///
/// Data flow:
///   Ingress → SignalProcessing → Fan-out(cacheChannel + storageChannel)
///   cacheChannel → RingBuffer.Write() → DisplayRefresh notification
///   storageChannel → EdfWriter.WriteDataRecord()
/// </summary>
public sealed class PipelineService : IPipelineService
{
    private readonly ISignalProcessor _signalProcessor;
    private readonly IEegRingBuffer _ringBuffer;
    private readonly IEdfWriter _edfWriter;
    private readonly ILogger<PipelineService> _logger;

    // Pipeline channels
    private readonly Channel<EegDataPacket> _ingressChannel;
    private readonly Channel<EegDataPacket> _cacheChannel;
    private readonly Channel<EegDataPacket> _storageChannel;

    private CancellationTokenSource? _cts;
    private readonly List<Task> _stageTasks = new();
    private volatile bool _isRunning;
    private volatile bool _isRecording;

    public bool IsRunning => _isRunning;
    public bool IsRecording => _isRecording;

    public event Action? DisplayRefreshRequested;

    public PipelineService(
        ISignalProcessor signalProcessor,
        IEegRingBuffer ringBuffer,
        IEdfWriter edfWriter,
        ILogger<PipelineService> logger)
    {
        _signalProcessor = signalProcessor;
        _ringBuffer = ringBuffer;
        _edfWriter = edfWriter;
        _logger = logger;

        _ingressChannel = Channel.CreateBounded<EegDataPacket>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
        _cacheChannel = Channel.CreateBounded<EegDataPacket>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
        _storageChannel = Channel.CreateBounded<EegDataPacket>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    }

    public async ValueTask PushAsync(EegDataPacket packet)
    {
        if (!_isRunning) return;
        await _ingressChannel.Writer.WriteAsync(packet);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning)
        {
            _logger.LogWarning("Pipeline is already running");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        _stageTasks.Clear();
        _stageTasks.Add(Task.Run(() => RunSignalProcessingStage(_cts.Token)));
        _stageTasks.Add(Task.Run(() => RunCacheStage(_cts.Token)));
        _stageTasks.Add(Task.Run(() => RunStorageStage(_cts.Token)));

        _logger.LogInformation("Pipeline started with {StageCount} stages", _stageTasks.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _logger.LogInformation("Stopping pipeline...");
        _isRunning = false;

        // Complete the ingress channel to signal downstream stages to drain
        _ingressChannel.Writer.TryComplete();

        if (_cts != null)
        {
            // Give stages time to drain, then cancel
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(_stageTasks).WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Pipeline drain timed out, cancelling stages");
                await _cts.CancelAsync();
            }

            _cts.Dispose();
            _cts = null;
        }

        _stageTasks.Clear();

        if (_isRecording)
        {
            StopRecording();
        }

        _logger.LogInformation("Pipeline stopped");
    }

    public void StartRecording(string filePath)
    {
        if (_isRecording)
        {
            _logger.LogWarning("Already recording");
            return;
        }

        _edfWriter.StartRecording(filePath, DateTime.Now);
        _isRecording = true;
        _logger.LogInformation("Recording started: {FilePath}", filePath);
    }

    public void StopRecording()
    {
        if (!_isRecording) return;

        _isRecording = false;
        _edfWriter.StopRecording();
        _logger.LogInformation("Recording stopped");
    }

    private async Task RunSignalProcessingStage(CancellationToken ct)
    {
        _logger.LogDebug("SignalProcessing stage started");
        try
        {
            await foreach (var packet in _ingressChannel.Reader.ReadAllAsync(ct))
            {
                var processed = _signalProcessor.Process(packet);

                // Fan-out to both cache and storage channels
                await _cacheChannel.Writer.WriteAsync(processed, ct);
                await _storageChannel.Writer.WriteAsync(processed, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            _cacheChannel.Writer.TryComplete();
            _storageChannel.Writer.TryComplete();
            _logger.LogDebug("SignalProcessing stage stopped");
        }
    }

    private async Task RunCacheStage(CancellationToken ct)
    {
        _logger.LogDebug("Cache stage started");
        try
        {
            await foreach (var packet in _cacheChannel.Reader.ReadAllAsync(ct))
            {
                _ringBuffer.Write(packet.Samples.AsSpan());
                DisplayRefreshRequested?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            _logger.LogDebug("Cache stage stopped");
        }
    }

    /// <summary>
    /// Number of samples per EDF data record (must match EdfWriter.SamplesPerRecord).
    /// New protocol sends 64 samples/packet × 4 packets/sec = 256 samples/sec.
    /// </summary>
    private const int EdfSamplesPerRecord = 256;

    private async Task RunStorageStage(CancellationToken ct)
    {
        _logger.LogDebug("Storage stage started");

        // Accumulation buffer: collect samples until we have a full EDF record (256 samples)
        var accumulator = new EegSample[EdfSamplesPerRecord];
        int accumulatedCount = 0;

        try
        {
            await foreach (var packet in _storageChannel.Reader.ReadAllAsync(ct))
            {
                if (!_isRecording) continue;

                var samples = packet.Samples;
                int remaining = samples.Length;
                int srcOffset = 0;

                while (remaining > 0)
                {
                    int space = EdfSamplesPerRecord - accumulatedCount;
                    int toCopy = Math.Min(remaining, space);

                    Array.Copy(samples, srcOffset, accumulator, accumulatedCount, toCopy);
                    accumulatedCount += toCopy;
                    srcOffset += toCopy;
                    remaining -= toCopy;

                    if (accumulatedCount >= EdfSamplesPerRecord)
                    {
                        WriteEdfRecord(accumulator);
                        accumulatedCount = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            // Flush any remaining samples as a partial record when stopping
            if (accumulatedCount > 0 && _isRecording)
            {
                // Zero-fill the rest so EDF record is complete
                Array.Clear(accumulator, accumulatedCount, EdfSamplesPerRecord - accumulatedCount);
                WriteEdfRecord(accumulator);
            }

            _logger.LogDebug("Storage stage stopped");
        }
    }

    /// <summary>
    /// Non-async helper to call WriteDataRecord (avoids Span in async context for C# 12).
    /// </summary>
    private void WriteEdfRecord(EegSample[] samples)
    {
        _edfWriter.WriteDataRecord(samples);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _edfWriter.Dispose();
    }
}

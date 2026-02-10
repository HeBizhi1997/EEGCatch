using EegAcquisition.Models;

namespace EegAcquisition.Services.Pipeline;

public interface IPipelineService : IAsyncDisposable
{
    /// <summary>
    /// Whether the pipeline is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Whether EDF recording is currently active.
    /// </summary>
    bool IsRecording { get; }

    /// <summary>
    /// Push a parsed data packet into the pipeline ingress.
    /// </summary>
    ValueTask PushAsync(EegDataPacket packet);

    /// <summary>
    /// Start all pipeline stages.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop all pipeline stages and drain remaining data.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Start EDF recording to the specified file path.
    /// </summary>
    void StartRecording(string filePath);

    /// <summary>
    /// Stop EDF recording and finalize the file.
    /// </summary>
    void StopRecording();

    /// <summary>
    /// Event fired when display should be refreshed with new data.
    /// </summary>
    event Action? DisplayRefreshRequested;
}

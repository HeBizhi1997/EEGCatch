using EegAcquisition.Models;

namespace EegAcquisition.Services.Storage;

public interface IEdfWriter : IDisposable
{
    bool IsRecording { get; }
    void StartRecording(string filePath, DateTime startTime);
    void WriteDataRecord(ReadOnlySpan<EegSample> samples);
    void StopRecording();
}

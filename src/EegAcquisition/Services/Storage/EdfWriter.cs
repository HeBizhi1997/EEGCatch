using System.IO;
using System.Text;
using EegAcquisition.Models;
using Microsoft.Extensions.Logging;

namespace EegAcquisition.Services.Storage;

/// <summary>
/// Custom EDF (European Data Format) file writer.
///
/// EDF specification: https://www.edfplus.info/specs/edf.html
///
/// Header: 256 bytes (general) + ns×256 bytes (per signal)
/// Data records: each record = duration seconds of data
///   Layout: [ns samples for signal 1][ns samples for signal 2]...
///   Each sample is a 16-bit signed integer (little-endian, two's complement)
/// </summary>
public sealed class EdfWriter : IEdfWriter
{
    private readonly ILogger<EdfWriter> _logger;
    private FileStream? _fileStream;
    private BinaryWriter? _writer;
    private int _dataRecordCount;
    private bool _isRecording;

    // EDF parameters
    private const int NumberOfSignals = 2;
    private const int SamplesPerRecord = 256;
    private const double RecordDuration = 1.0; // seconds
    private const double PhysicalMin = -500.0; // µV - typical EEG range
    private const double PhysicalMax = 500.0;  // µV
    private const short DigitalMin = -32768;
    private const short DigitalMax = 32767;

    // Precomputed scaling
    private static readonly double Scale =
        (DigitalMax - (double)DigitalMin) / (PhysicalMax - PhysicalMin);

    public bool IsRecording => _isRecording;

    public EdfWriter(ILogger<EdfWriter> logger)
    {
        _logger = logger;
    }

    public void StartRecording(string filePath, DateTime startTime)
    {
        if (_isRecording)
        {
            _logger.LogWarning("Already recording, stop first");
            return;
        }

        try
        {
            _fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _writer = new BinaryWriter(_fileStream, Encoding.ASCII, leaveOpen: true);

            WriteHeader(startTime);
            _dataRecordCount = 0;
            _isRecording = true;

            _logger.LogInformation("EDF recording started: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start EDF recording");
            _writer?.Dispose();
            _fileStream?.Dispose();
            _writer = null;
            _fileStream = null;
            throw;
        }
    }

    public void WriteDataRecord(ReadOnlySpan<EegSample> samples)
    {
        if (!_isRecording || _writer == null) return;

        try
        {
            // EDF data record: all samples for signal 1, then all samples for signal 2
            // Pad or truncate to exactly SamplesPerRecord
            int count = Math.Min(samples.Length, SamplesPerRecord);

            // Write Channel 1
            for (int i = 0; i < count; i++)
            {
                _writer.Write(ToDigital(samples[i].Channel1));
            }
            // Pad with zeros if fewer than SamplesPerRecord
            for (int i = count; i < SamplesPerRecord; i++)
            {
                _writer.Write(ToDigital(0));
            }

            // Write Channel 2
            for (int i = 0; i < count; i++)
            {
                _writer.Write(ToDigital(samples[i].Channel2));
            }
            for (int i = count; i < SamplesPerRecord; i++)
            {
                _writer.Write(ToDigital(0));
            }

            _writer.Flush();
            _dataRecordCount++;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write EDF data record");
        }
    }

    public void StopRecording()
    {
        if (!_isRecording || _fileStream == null) return;

        try
        {
            // Finalize: write actual data record count at byte offset 236
            _fileStream.Seek(236, SeekOrigin.Begin);
            var countStr = _dataRecordCount.ToString().PadRight(8);
            var countBytes = Encoding.ASCII.GetBytes(countStr);
            _fileStream.Write(countBytes, 0, 8);
            _fileStream.Flush();

            _logger.LogInformation("EDF recording stopped. {Records} data records written", _dataRecordCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize EDF file");
        }
        finally
        {
            _writer?.Dispose();
            _fileStream?.Dispose();
            _writer = null;
            _fileStream = null;
            _isRecording = false;
            _dataRecordCount = 0;
        }
    }

    public void Dispose()
    {
        if (_isRecording) StopRecording();
        _writer?.Dispose();
        _fileStream?.Dispose();
    }

    private void WriteHeader(DateTime startTime)
    {
        // Total header size: 256 + ns × 256 = 256 + 2 × 256 = 768 bytes
        int headerBytes = 256 + NumberOfSignals * 256;
        byte[] header = new byte[headerBytes];

        // Fill with spaces (ASCII 0x20) - EDF fields are space-padded
        Array.Fill(header, (byte)' ');

        // --- General header (256 bytes) ---
        WriteAscii(header, 0, 8, "0");                                      // Version
        WriteAscii(header, 8, 80, "X X X X");                               // Patient ID
        WriteAscii(header, 88, 80, $"Startdate {startTime:dd-MMM-yyyy}");   // Recording ID
        WriteAscii(header, 168, 8, startTime.ToString("dd.MM.yy"));         // Start date
        WriteAscii(header, 176, 8, startTime.ToString("HH.mm.ss"));         // Start time
        WriteAscii(header, 184, 8, headerBytes.ToString());                  // Header bytes
        WriteAscii(header, 192, 44, "");                                     // Reserved
        WriteAscii(header, 236, 8, "-1");                                    // Nr of data records (unknown)
        WriteAscii(header, 244, 8, RecordDuration.ToString("F6").Substring(0, 8)); // Duration
        WriteAscii(header, 252, 4, NumberOfSignals.ToString());              // Number of signals

        // --- Per-signal header fields (each field is ns × N bytes) ---
        int offset = 256;
        string[] labels = ["EEG Ch1", "EEG Ch2"];
        string[] transducer = ["AgAgCl electrode", "AgAgCl electrode"];
        string[] dimension = ["uV", "uV"];

        // Labels (ns × 16 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 16, 16, labels[s]);
        offset += NumberOfSignals * 16;

        // Transducer type (ns × 80 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 80, 80, transducer[s]);
        offset += NumberOfSignals * 80;

        // Physical dimension (ns × 8 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 8, 8, dimension[s]);
        offset += NumberOfSignals * 8;

        // Physical minimum (ns × 8 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 8, 8, PhysicalMin.ToString("F1"));
        offset += NumberOfSignals * 8;

        // Physical maximum (ns × 8 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 8, 8, PhysicalMax.ToString("F1"));
        offset += NumberOfSignals * 8;

        // Digital minimum (ns × 8 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 8, 8, DigitalMin.ToString());
        offset += NumberOfSignals * 8;

        // Digital maximum (ns × 8 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 8, 8, DigitalMax.ToString());
        offset += NumberOfSignals * 8;

        // Prefiltering (ns × 80 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 80, 80, "");
        offset += NumberOfSignals * 80;

        // Number of samples per data record (ns × 8 bytes)
        for (int s = 0; s < NumberOfSignals; s++)
            WriteAscii(header, offset + s * 8, 8, SamplesPerRecord.ToString());
        offset += NumberOfSignals * 8;

        // Reserved (ns × 32 bytes) - already filled with spaces

        _fileStream!.Write(header, 0, header.Length);
        _fileStream.Flush();
    }

    private static void WriteAscii(byte[] buffer, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value.PadRight(length));
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length));
    }

    private static short ToDigital(double physicalValue)
    {
        double clamped = Math.Clamp(physicalValue, PhysicalMin, PhysicalMax);
        double digital = (clamped - PhysicalMin) * Scale + DigitalMin;
        return (short)Math.Clamp(Math.Round(digital), DigitalMin, DigitalMax);
    }
}

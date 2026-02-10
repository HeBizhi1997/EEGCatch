using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EegAcquisition.Models;
using EegAcquisition.Services.Cache;
using EegAcquisition.Services.Pipeline;
using EegAcquisition.Services.Serial;
using EegAcquisition.Services.SignalProcessing;
using EegAcquisition.Services.Simulator;
using Microsoft.Extensions.Logging;

namespace EegAcquisition.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ISerialPortService _serialPortService;
    private readonly IPipelineService _pipelineService;
    private readonly IEegRingBuffer _ringBuffer;
    private readonly EegSimulatorService _simulator;
    private readonly EegFilterProcessor _filter;
    private readonly ILogger<MainWindowViewModel> _logger;

    private readonly DispatcherTimer _recordingTimer;
    private DateTime _recordingStartTime;

    private static readonly int[] WindowOptions = [5, 10, 30, 60];

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isSimulating;

    [ObservableProperty]
    private bool _isFilterEnabled;

    [ObservableProperty]
    private bool _isNotchEnabled = true;

    [ObservableProperty]
    private bool _isBandpassEnabled = true;

    [ObservableProperty]
    private string _selectedPort = "";

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private long _totalSamplesReceived;

    [ObservableProperty]
    private string _recordingDuration = "00:00:00";

    [ObservableProperty]
    private string _cacheUsageText = "0 / 256000";

    [ObservableProperty]
    private int _displayWindowIndex = 1; // default: 10s

    public int DisplayWindowSeconds => WindowOptions[Math.Clamp(DisplayWindowIndex, 0, WindowOptions.Length - 1)];

    public ObservableCollection<string> AvailablePorts { get; } = new();

    public MainWindowViewModel(
        ISerialPortService serialPortService,
        IPipelineService pipelineService,
        IEegRingBuffer ringBuffer,
        EegSimulatorService simulator,
        EegFilterProcessor filter,
        ILogger<MainWindowViewModel> logger)
    {
        _serialPortService = serialPortService;
        _pipelineService = pipelineService;
        _ringBuffer = ringBuffer;
        _simulator = simulator;
        _filter = filter;
        _logger = logger;

        _serialPortService.PacketReceived += OnPacketReceived;

        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingTimer.Tick += (_, _) =>
        {
            RecordingDuration = (DateTime.Now - _recordingStartTime).ToString(@"hh\:mm\:ss");
        };

        RefreshPorts();
    }

    public void UpdateCacheUsage()
    {
        CacheUsageText = $"{_ringBuffer.Count} / {_ringBuffer.Capacity}";
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var port in _serialPortService.GetAvailablePorts())
        {
            AvailablePorts.Add(port);
        }

        if (AvailablePorts.Count > 0 && string.IsNullOrEmpty(SelectedPort))
        {
            SelectedPort = AvailablePorts[0];
        }

        StatusMessage = $"发现 {AvailablePorts.Count} 个串口";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected) return;
        if (string.IsNullOrEmpty(SelectedPort))
        {
            StatusMessage = "请选择串口";
            return;
        }

        try
        {
            StatusMessage = $"正在连接 {SelectedPort}...";
            var settings = new SerialPortSettings { PortName = SelectedPort };

            await _pipelineService.StartAsync();
            await _serialPortService.ConnectAsync(settings);

            IsConnected = true;
            TotalSamplesReceived = 0;
            StatusMessage = $"已连接 {SelectedPort}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败: {ex.Message}";
            _logger.LogError(ex, "Failed to connect to {Port}", SelectedPort);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (!IsConnected) return;

        try
        {
            if (IsRecording) StopRecording();

            await _serialPortService.DisconnectAsync();
            await _pipelineService.StopAsync();

            IsConnected = false;
            StatusMessage = "已断开连接";
        }
        catch (Exception ex)
        {
            StatusMessage = $"断开连接出错: {ex.Message}";
            _logger.LogError(ex, "Error disconnecting");
        }
    }

    [RelayCommand]
    private void StartRecording()
    {
        if (IsRecording || !IsConnected) return;

        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");
            Directory.CreateDirectory(dir);
            var fileName = $"EEG_{DateTime.Now:yyyyMMdd_HHmmss}.edf";
            var filePath = Path.Combine(dir, fileName);

            _pipelineService.StartRecording(filePath);
            _recordingStartTime = DateTime.Now;
            _recordingTimer.Start();
            IsRecording = true;
            StatusMessage = $"正在录制: {fileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"录制启动失败: {ex.Message}";
            _logger.LogError(ex, "Failed to start recording");
        }
    }

    [RelayCommand]
    private void StopRecording()
    {
        if (!IsRecording) return;

        _pipelineService.StopRecording();
        _recordingTimer.Stop();
        IsRecording = false;
        StatusMessage = $"录制已停止，时长 {RecordingDuration}";
    }

    [RelayCommand]
    private void ToggleFilter()
    {
        IsFilterEnabled = !IsFilterEnabled;
        _filter.IsEnabled = IsFilterEnabled;
        _filter.NotchEnabled = IsNotchEnabled;
        _filter.BandpassEnabled = IsBandpassEnabled;

        if (IsFilterEnabled)
        {
            _filter.Reset();
            StatusMessage = "滤波已开启 (50Hz陷波 + 0.5-45Hz带通)";
        }
        else
        {
            StatusMessage = "滤波已关闭";
        }

        _logger.LogInformation("Filter {State}: Notch={Notch}, Bandpass={Bandpass}",
            IsFilterEnabled ? "enabled" : "disabled", IsNotchEnabled, IsBandpassEnabled);
    }

    [RelayCommand]
    private void UpdateFilterSettings()
    {
        _filter.NotchEnabled = IsNotchEnabled;
        _filter.BandpassEnabled = IsBandpassEnabled;
        _filter.Reset(); // Reset state when settings change

        var parts = new List<string>();
        if (IsNotchEnabled) parts.Add("50Hz陷波");
        if (IsBandpassEnabled) parts.Add("0.5-45Hz带通");
        string desc = parts.Count > 0 ? string.Join(" + ", parts) : "无";
        StatusMessage = $"滤波设置已更新: {desc}";
    }

    [RelayCommand]
    private async Task StartSimulationAsync()
    {
        if (IsSimulating) return;

        try
        {
            TotalSamplesReceived = 0;
            _ringBuffer.Clear();
            await _simulator.StartAsync();
            IsSimulating = true;
            IsConnected = true; // Enable recording controls
            StatusMessage = "模拟模式运行中 (Ch1: 10Hz Alpha, Ch2: 22Hz Beta)";
            _logger.LogInformation("Simulation started");
        }
        catch (Exception ex)
        {
            StatusMessage = $"模拟启动失败: {ex.Message}";
            _logger.LogError(ex, "Failed to start simulation");
        }
    }

    [RelayCommand]
    private async Task StopSimulationAsync()
    {
        if (!IsSimulating) return;

        try
        {
            if (IsRecording) StopRecording();

            await _simulator.StopAsync();
            await _pipelineService.StopAsync();

            IsSimulating = false;
            IsConnected = false;
            StatusMessage = "模拟已停止";
            _logger.LogInformation("Simulation stopped");
        }
        catch (Exception ex)
        {
            StatusMessage = $"模拟停止出错: {ex.Message}";
            _logger.LogError(ex, "Error stopping simulation");
        }
    }

    private async void OnPacketReceived(EegDataPacket packet)
    {
        try
        {
            await _pipelineService.PushAsync(packet);
            Helpers.DispatcherHelper.RunOnUI(() =>
            {
                TotalSamplesReceived += packet.SampleCount;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pushing packet to pipeline");
        }
    }

    public void Dispose()
    {
        _serialPortService.PacketReceived -= OnPacketReceived;
        _recordingTimer.Stop();
    }
}

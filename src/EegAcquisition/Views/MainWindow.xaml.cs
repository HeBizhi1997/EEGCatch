using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EegAcquisition.Services.Cache;
using EegAcquisition.Services.Pipeline;
using EegAcquisition.ViewModels;

namespace EegAcquisition.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IEegRingBuffer _ringBuffer;
    private readonly DispatcherTimer _displayTimer;

    private const int SampleRate = 256;
    private const double SamplePeriod = 1.0 / SampleRate;

    private double[] _displayCh1 = [];
    private double[] _displayCh2 = [];
    private ScottPlot.Plottables.Signal? _signalCh1;
    private ScottPlot.Plottables.Signal? _signalCh2;
    private int _lastDisplaySamples;

    // Crosshair and annotation for mouse hover
    private ScottPlot.Plottables.Crosshair? _crosshairCh1;
    private ScottPlot.Plottables.Crosshair? _crosshairCh2;
    private ScottPlot.Plottables.Annotation? _annotationCh1;
    private ScottPlot.Plottables.Annotation? _annotationCh2;

    // BPM chart: 滚动显示最近 BpmHistorySize 个心跳读数
    private const int BpmHistorySize = 300;
    private readonly double[] _bpmHistory = new double[BpmHistorySize];
    private ScottPlot.Plottables.Signal? _signalBpm;
    private bool _bpmDirty;

    public MainWindow(
        MainWindowViewModel viewModel,
        IEegRingBuffer ringBuffer,
        IPipelineService pipelineService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _ringBuffer = ringBuffer;

        DataContext = _viewModel;

        InitializePlots();

        // Mouse hover event handlers
        WpfPlotCh1.MouseMove += WpfPlotCh1_MouseMove;
        WpfPlotCh1.MouseLeave += WpfPlotCh1_MouseLeave;
        WpfPlotCh2.MouseMove += WpfPlotCh2_MouseMove;
        WpfPlotCh2.MouseLeave += WpfPlotCh2_MouseLeave;

        // 监听 PulseRate 属性变化，更新 BPM 图表缓冲区
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _displayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _displayTimer.Tick += OnDisplayTimerTick;
        _displayTimer.Start();
    }

    private void InitializePlots()
    {
        // Set ScottPlot default font to support Chinese characters
        ScottPlot.Fonts.Default = "Microsoft YaHei";

        WpfPlotCh1.Plot.Title("通道1 (Ch1)");
        WpfPlotCh1.Plot.YLabel("幅值 (µV)");
        WpfPlotCh1.Plot.XLabel("时间 (s)");

        WpfPlotCh2.Plot.Title("通道2 (Ch2)");
        WpfPlotCh2.Plot.YLabel("幅值 (µV)");
        WpfPlotCh2.Plot.XLabel("时间 (s)");

        SetupDisplayBuffers(_viewModel.DisplayWindowSeconds);
        InitBpmPlot();
    }

    private void InitBpmPlot()
    {
        WpfPlotBpm.Plot.Title("脉率 (BPM)");
        WpfPlotBpm.Plot.YLabel("BPM");
        WpfPlotBpm.Plot.XLabel("采样序号");

        // period=1 表示每个采样点间隔 1 个序号单位（实际约 1 秒/次轮询）
        _signalBpm = WpfPlotBpm.Plot.Add.Signal(_bpmHistory, period: 1.0);
        _signalBpm.Color = ScottPlot.Color.FromHex("#E91E63");
        WpfPlotBpm.Plot.Axes.SetLimitsX(0, BpmHistorySize);
        WpfPlotBpm.Plot.Axes.SetLimitsY(0, 200);
        WpfPlotBpm.Refresh();
    }

    private void SetupDisplayBuffers(int windowSeconds)
    {
        int samples = windowSeconds * SampleRate;
        if (samples == _lastDisplaySamples) return;

        _lastDisplaySamples = samples;
        _displayCh1 = new double[samples];
        _displayCh2 = new double[samples];

        // Signal plot: period = time between samples (1/256 s)
        WpfPlotCh1.Plot.Clear();
        _signalCh1 = WpfPlotCh1.Plot.Add.Signal(_displayCh1, SamplePeriod);
        _signalCh1.Color = ScottPlot.Color.FromHex("#2196F3");
        _crosshairCh1 = WpfPlotCh1.Plot.Add.Crosshair(0, 0);
        _crosshairCh1.IsVisible = false;
        _crosshairCh1.LineColor = ScottPlot.Colors.Gray;
        _annotationCh1 = WpfPlotCh1.Plot.Add.Annotation("");
        _annotationCh1.IsVisible = false;
        _annotationCh1.LabelFontColor = ScottPlot.Colors.White;
        _annotationCh1.LabelBackgroundColor = ScottPlot.Color.FromHex("#333333");
        WpfPlotCh1.Plot.Axes.SetLimitsX(0, windowSeconds);
        WpfPlotCh1.Plot.Axes.AutoScaleY();

        WpfPlotCh2.Plot.Clear();
        _signalCh2 = WpfPlotCh2.Plot.Add.Signal(_displayCh2, SamplePeriod);
        _signalCh2.Color = ScottPlot.Color.FromHex("#4CAF50");
        _crosshairCh2 = WpfPlotCh2.Plot.Add.Crosshair(0, 0);
        _crosshairCh2.IsVisible = false;
        _crosshairCh2.LineColor = ScottPlot.Colors.Gray;
        _annotationCh2 = WpfPlotCh2.Plot.Add.Annotation("");
        _annotationCh2.IsVisible = false;
        _annotationCh2.LabelFontColor = ScottPlot.Colors.White;
        _annotationCh2.LabelBackgroundColor = ScottPlot.Color.FromHex("#333333");
        WpfPlotCh2.Plot.Axes.SetLimitsX(0, windowSeconds);
        WpfPlotCh2.Plot.Axes.AutoScaleY();

        WpfPlotCh1.Refresh();
        WpfPlotCh2.Refresh();
    }

    private void UpdateCrosshair(
        ScottPlot.WPF.WpfPlot wpfPlot,
        double[] data,
        ScottPlot.Plottables.Crosshair? crosshair,
        ScottPlot.Plottables.Annotation? annotation,
        MouseEventArgs e,
        string channelName)
    {
        if (crosshair == null || annotation == null || data.Length == 0) return;

        var pos = e.GetPosition(wpfPlot);
        var pixel = new ScottPlot.Pixel((float)pos.X, (float)pos.Y);
        var coords = wpfPlot.Plot.GetCoordinates(pixel);

        int idx = (int)(coords.X / SamplePeriod);
        if (idx >= 0 && idx < data.Length)
        {
            double value = data[idx];
            double time = idx * SamplePeriod;
            crosshair.IsVisible = true;
            crosshair.Position = new ScottPlot.Coordinates(time, value);
            annotation.IsVisible = true;
            annotation.LabelStyle.Text = $"{channelName}: {value:F2} µV  t={time:F3}s";
            wpfPlot.Refresh();
        }
    }

    private void HideCrosshair(
        ScottPlot.WPF.WpfPlot wpfPlot,
        ScottPlot.Plottables.Crosshair? crosshair,
        ScottPlot.Plottables.Annotation? annotation)
    {
        if (crosshair != null) crosshair.IsVisible = false;
        if (annotation != null) annotation.IsVisible = false;
        wpfPlot.Refresh();
    }

    private void WpfPlotCh1_MouseMove(object sender, MouseEventArgs e)
        => UpdateCrosshair(WpfPlotCh1, _displayCh1, _crosshairCh1, _annotationCh1, e, "Ch1");

    private void WpfPlotCh1_MouseLeave(object sender, MouseEventArgs e)
        => HideCrosshair(WpfPlotCh1, _crosshairCh1, _annotationCh1);

    private void WpfPlotCh2_MouseMove(object sender, MouseEventArgs e)
        => UpdateCrosshair(WpfPlotCh2, _displayCh2, _crosshairCh2, _annotationCh2, e, "Ch2");

    private void WpfPlotCh2_MouseLeave(object sender, MouseEventArgs e)
        => HideCrosshair(WpfPlotCh2, _crosshairCh2, _annotationCh2);

    private void OnDisplayTimerTick(object? sender, EventArgs e)
    {
        SetupDisplayBuffers(_viewModel.DisplayWindowSeconds);

        if (_ringBuffer.Count == 0) return;

        int copied = _ringBuffer.CopyLatest(_lastDisplaySamples, _displayCh1.AsSpan(), _displayCh2.AsSpan());

        // Clear unfilled portion to avoid stale data
        if (copied < _lastDisplaySamples)
        {
            Array.Clear(_displayCh1, copied, _lastDisplaySamples - copied);
            Array.Clear(_displayCh2, copied, _lastDisplaySamples - copied);
        }

        _viewModel.UpdateCacheUsage();

        if (_viewModel.IsSimulating)
        {
            _viewModel.TotalSamplesReceived = _ringBuffer.Count;
        }

        WpfPlotCh1.Plot.Axes.AutoScaleY();
        WpfPlotCh2.Plot.Axes.AutoScaleY();
        WpfPlotCh1.Refresh();
        WpfPlotCh2.Refresh();

        // BPM 图表：只在有新数据时刷新
        if (_bpmDirty)
        {
            _bpmDirty = false;
            WpfPlotBpm.Plot.Axes.AutoScaleY();
            WpfPlotBpm.Refresh();
        }
    }

    /// <summary>
    /// 每当 PulseRate 属性变化时，将新值写入 BPM 滚动缓冲区（左移 + 尾部追加）。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.PulseRate)) return;

        int bpm = _viewModel.PulseRate;
        // 向左滚动一位，新值写在末尾
        Array.Copy(_bpmHistory, 1, _bpmHistory, 0, BpmHistorySize - 1);
        _bpmHistory[BpmHistorySize - 1] = bpm;
        _bpmDirty = true;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _displayTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }
}

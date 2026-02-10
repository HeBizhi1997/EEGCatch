using System.Windows;
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
    private const double SamplePeriod = 1.0 / SampleRate; // ~0.00390625s between samples

    private double[] _displayCh1 = [];
    private double[] _displayCh2 = [];
    private ScottPlot.Plottables.Signal? _signalCh1;
    private ScottPlot.Plottables.Signal? _signalCh2;
    private int _lastDisplaySamples;

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

        _displayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _displayTimer.Tick += OnDisplayTimerTick;
        _displayTimer.Start();
    }

    private void InitializePlots()
    {
        WpfPlotCh1.Plot.Title("通道1 (Ch1)");
        WpfPlotCh1.Plot.YLabel("幅值 (µV)");
        WpfPlotCh1.Plot.XLabel("时间 (s)");

        WpfPlotCh2.Plot.Title("通道2 (Ch2)");
        WpfPlotCh2.Plot.YLabel("幅值 (µV)");
        WpfPlotCh2.Plot.XLabel("时间 (s)");

        SetupDisplayBuffers(_viewModel.DisplayWindowSeconds);
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
        WpfPlotCh1.Plot.Axes.SetLimitsX(0, windowSeconds);
        WpfPlotCh1.Plot.Axes.AutoScaleY();

        WpfPlotCh2.Plot.Clear();
        _signalCh2 = WpfPlotCh2.Plot.Add.Signal(_displayCh2, SamplePeriod);
        _signalCh2.Color = ScottPlot.Color.FromHex("#4CAF50");
        WpfPlotCh2.Plot.Axes.SetLimitsX(0, windowSeconds);
        WpfPlotCh2.Plot.Axes.AutoScaleY();

        WpfPlotCh1.Refresh();
        WpfPlotCh2.Refresh();
    }

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
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _displayTimer.Stop();
        _viewModel.Dispose();
    }
}

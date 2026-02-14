using System.Windows;
using EegAcquisition.Services.Cache;
using EegAcquisition.Services.Logging;
using EegAcquisition.Services.Pipeline;
using EegAcquisition.Services.Serial;
using EegAcquisition.Services.SignalProcessing;
using EegAcquisition.Services.Simulator;
using EegAcquisition.Services.Storage;
using EegAcquisition.ViewModels;
using EegAcquisition.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace EegAcquisition;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureSerilog()
            .ConfigureServices((context, services) =>
            {
                // Services
                services.AddSingleton<ISerialPortService, SerialPortService>();
                services.AddSingleton<IEegRingBuffer>(new EegRingBuffer(256_000));
                services.AddSingleton<IEdfWriter, EdfWriter>();
                services.AddSingleton<EegFilterProcessor>();
                services.AddSingleton<ISignalProcessor>(sp => sp.GetRequiredService<EegFilterProcessor>());
                services.AddSingleton<IPipelineService, PipelineService>();
                services.AddSingleton<EegSimulatorService>();

                // ViewModels
                services.AddSingleton<MainWindowViewModel>();

                // Views
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        Log.Information("EEG Acquisition application started");

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("EEG Acquisition application shutting down");

        if (_host != null)
        {
            // Ensure pipeline and EDF are properly closed
            var pipeline = _host.Services.GetService<IPipelineService>();
            if (pipeline != null)
            {
                await pipeline.DisposeAsync();
            }

            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

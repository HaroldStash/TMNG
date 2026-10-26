using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Tmng.App.ViewModels;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App;

public partial class App : Application
{
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static bool _exiting;
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Really quits, as opposed to closing the window into the tray.</summary>
    public static void Exit()
    {
        _exiting = true;
        _desktop?.Shutdown();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settings = AppSettings.Load();
            var source = new WindowsTelemetrySource();
            var hub = new TelemetryHub(source, TimeSpan.FromSeconds(1));
            var traces = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TMNG", "Traces");
            var recorder = new FlightRecorder(hub, TimeSpan.FromMinutes(30), traces);
            recorder.Rules = settings.LoadAlertRules();

            var main = new MainViewModel(hub, source, recorder, settings);
            _window = new MainWindow { DataContext = main };
            _window.Closing += (_, e) =>
            {
                if (_exiting) return;
                if (!settings.CloseToTray)
                {
                    Exit();
                    return;
                }
                e.Cancel = true;
                _window.Hide();
                if (!settings.TrayHintShown)
                {
                    settings.TrayHintShown = true;
                    settings.Save();
                    Notifier.Show("TMNG is still running", "It keeps watching from the notification area. Right-click its icon to exit.");
                }
            };

            _ = new TrayController(hub, main.Appearance, _window, ShowWindow, Exit);
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(ShowWindow));

            // Notify the moment a rule fires. The trace may still be capturing the aftermath, so a
            // click opens it if it is saved by then and the Alerts page otherwise.
            recorder.AlertFired += alert =>
            {
                if (!settings.Notifications) return;
                string top = alert.TopProcess is { } p ? $"Top process: {p}. " : "";
                string capture = alert.CaptureUntil > alert.Time
                    ? $"Recording until {alert.CaptureUntil:T}, then the trace is saved."
                    : "Trace saved. Click to open it.";
                Notifier.Show(alert.Reason, top + capture, () => Dispatcher.UIThread.Post(async () =>
                {
                    ShowWindow();
                    if (alert.Path is { } path) await main.ShowTrace(path);
                    else main.ShowAlerts();
                }));
            };
            recorder.AlertSaved += alert =>
            {
                if (settings.Notifications && alert.Error is { } error)
                    Notifier.Show(alert.Reason, $"The trace could not be saved: {error}");
            };

            desktop.Exit += (_, _) =>
            {
                recorder.FlushAlerts();
                hub.Dispose();
                source.Dispose();
            };
            hub.Start();

            if (desktop.Args?.Contains(AutoStart.TrayArgument) != true)
                ShowWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}

using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly IBrush HealthyBrush = new SolidColorBrush(Color.Parse("#4CFF6A"));
    private static readonly IBrush DegradedBrush = new SolidColorBrush(Color.Parse("#FFB020"));

    private readonly TelemetryHub _hub;
    private readonly ITelemetrySource _source;
    private Snapshot? _pending;
    private readonly AppSettings _settings;
    private readonly FlightRecorderViewModel _flightRecorder;
    private readonly AlertsViewModel _alerts;
    private readonly SettingsViewModel _settingsPage;

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private PageViewModel? _selectedLive;
    [ObservableProperty] private PageViewModel? _selectedDeep;
    [ObservableProperty] private PageViewModel? _selectedSettings;
    [ObservableProperty] private string _status = "Starting providers…";
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private IBrush _statusBrush = HealthyBrush;

    public MainViewModel(TelemetryHub hub, ITelemetrySource source, FlightRecorder recorder, AppSettings settings)
    {
        _hub = hub;
        _source = source;
        _settings = settings;

        // Theme first, so every page builds its inks for it.
        Appearance.Scheme = settings.Scheme;
        Appearance.Theme = settings.Theme;
        ThemeState.Apply(settings.Theme);
        source.IncludeSelf = settings.IncludeSelf;

        _flightRecorder = new FlightRecorderViewModel(recorder, settings, Appearance);
        _alerts = new AlertsViewModel(recorder, settings, Appearance, ShowTrace);
        _settingsPage = new SettingsViewModel(settings, source, Appearance,
            openAlertRules: ShowAlerts,
            restartElevated: () => RestartElevatedCommand.Execute(null));
        SettingsPages = [_settingsPage];

        var summary = new SummaryViewModel(hub, Appearance);
        var processes = new ProcessesViewModel(Appearance, settings);
        var services = new ServicesViewModel(Appearance)
        {
            ShowProcess = pid =>
            {
                processes.Filter = pid.ToString();
                SelectedLive = processes;
            },
        };
        LivePages =
        [
            summary,
            new PerformanceViewModel(hub, Appearance),
            processes,
            new SystemInfoViewModel(Appearance),
            new StartupViewModel(Appearance),
            new UsersViewModel(Appearance),
            services,
        ];
        DeepPages =
        [
            new PowerViewModel(Appearance),
            new ConnectionsViewModel(Appearance),
            new InstalledAppsViewModel(Appearance),
            new DiskSpaceViewModel(Appearance),
            new BenchmarksViewModel(Appearance),
            _flightRecorder,
            _alerts,
        ];

        _currentPage = summary;
        _selectedLive = summary;
        hub.Sampled += OnSampled;
    }

    public Appearance Appearance { get; } = new();
    public IReadOnlyList<PageViewModel> LivePages { get; }
    public IReadOnlyList<PageViewModel> DeepPages { get; }
    public IReadOnlyList<PageViewModel> SettingsPages { get; }

    /// <summary>Drives the recording controls in the status bar.</summary>
    public FlightRecorderViewModel FlightRecorder => _flightRecorder;

    [RelayCommand]
    private void ShowFlightRecorder() => SelectedDeep = _flightRecorder;

    public void ShowAlerts() => SelectedDeep = _alerts;

    /// <summary>Jumps to the Flight Recorder and opens a saved trace.</summary>
    public async Task ShowTrace(string path)
    {
        SelectedDeep = _flightRecorder;
        await _flightRecorder.OpenPath(path);
    }

    public string ElevationText => Elevation.IsElevated ? "Administrator" : "Standard user · sensors limited";

    [ObservableProperty] private bool _driverInstalled = SensorDriver.IsInstalled;
    [ObservableProperty] private bool _installing;
    [ObservableProperty] private string? _bannerMessage;

    /// <summary>CPU temperature needs the driver and elevation; the banner offers whichever is missing.</summary>
    public bool ShowBanner => !DriverInstalled || !Elevation.IsElevated;

    public string BannerText => !DriverInstalled
        ? "CPU temperature needs the PawnIO sensor driver. TMNG installs the signed release from its official source through winget."
        : "CPU temperature and per-process network and disk traffic need administrator rights. Everything else, including CPU power, already works.";

    partial void OnDriverInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(BannerText));
    }

    [RelayCommand]
    private async Task InstallDriver()
    {
        Installing = true;
        BannerMessage = "Installing… approve the Windows prompt if one appears.";
        DriverInstalled = await SensorDriver.InstallAsync();
        Installing = false;
        BannerMessage = DriverInstalled
            ? "Driver installed. Restart TMNG as administrator to start reading it."
            : "The install did not complete. Check that winget is available, or install PawnIO from pawnio.eu.";
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (ElevationActions.RestartElevated())
            App.Exit();
        else
            BannerMessage = "The administrator prompt was declined.";
    }

    private void OnSampled(Snapshot snapshot)
    {
        // Latest wins: if the UI is behind, skip intermediate snapshots instead of queueing them.
        if (Interlocked.Exchange(ref _pending, snapshot) is null)
            Dispatcher.UIThread.Post(Drain);
    }

    private void Drain()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } snapshot) return;
        CurrentPage.Update(snapshot);
        UpdateStatus(snapshot);
    }

    private void UpdateStatus(Snapshot s)
    {
        var degraded = s.Health.Where(h => !h.Healthy).ToList();
        string providers = degraded.Count == 0
            ? "Native providers healthy"
            : $"Degraded: {string.Join(", ", degraded.Select(h => h.Provider).Distinct())}";
        Status = $"{providers} · {s.Processes.Count} processes · Generation {s.Generation}";
        StatusDetail = degraded.Count == 0 ? null : string.Join("\n", degraded.Select(h => $"{h.Provider}: {h.Detail}"));
        StatusBrush = degraded.Count == 0 ? HealthyBrush : DegradedBrush;
    }

    // Three sidebar lists act as one: selecting in any clears the other two.
    partial void OnSelectedLiveChanged(PageViewModel? value)
    {
        if (value is null) return;
        SelectedDeep = null;
        SelectedSettings = null;
        Show(value);
    }

    partial void OnSelectedDeepChanged(PageViewModel? value)
    {
        if (value is null) return;
        SelectedLive = null;
        SelectedSettings = null;
        Show(value);
    }

    partial void OnSelectedSettingsChanged(PageViewModel? value)
    {
        if (value is null) return;
        SelectedLive = null;
        SelectedDeep = null;
        Show(value);
    }

    private void Show(PageViewModel page)
    {
        CurrentPage = page;
        if (_hub.Latest is { } snapshot) page.Update(snapshot);
    }
}

using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

/// <summary>A theme as the picker shows it: name, one line on what it looks like, and preview colours.</summary>
public sealed record ThemeOption(AppTheme Theme, string Name, string Description, IBrush Background, IBrush Line, bool Glow);

public sealed record SchemeOption(ColorScheme Scheme, string Name);

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppSettings _settings;
    private readonly ITelemetrySource _source;
    private readonly Action _openAlertRules;
    private readonly Action _restartElevated;

    public SettingsViewModel(AppSettings settings, ITelemetrySource source, Appearance appearance, Action openAlertRules, Action restartElevated)
        : base("Settings", "", appearance)
    {
        _settings = settings;
        _source = source;
        _openAlertRules = openAlertRules;
        _restartElevated = restartElevated;
        _selectedTheme = Themes.First(t => t.Theme == appearance.Theme);
        _selectedScheme = Schemes.First(s => s.Scheme == appearance.Scheme);
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.Light, "Light", "Bright background, crisp lines. Easiest to read in a lit room.",
            new SolidColorBrush(Color.Parse("#F4F6F9")), new SolidColorBrush(Color.Parse("#1E8F36")), false),
        new(AppTheme.Dark, "Dark", "Flat and dark. Full colour, no glow.",
            new SolidColorBrush(Color.Parse("#0D1015")), new SolidColorBrush(Color.Parse("#4CFF6A")), false),
        new(AppTheme.Neon, "Neon", "Near-black with glowing traces, meters and readings.",
            new SolidColorBrush(Color.Parse("#05070A")), new SolidColorBrush(Color.Parse("#4CFF6A")), true),
    ];

    public IReadOnlyList<SchemeOption> Schemes { get; } =
        Enum.GetValues<ColorScheme>().Select(s => new SchemeOption(s, Appearance.NameOf(s))).ToList();

    [ObservableProperty] private ThemeOption _selectedTheme;
    [ObservableProperty] private SchemeOption _selectedScheme;

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        Appearance.Theme = value.Theme;
        _settings.Theme = value.Theme;
        _settings.Save();
    }

    partial void OnSelectedSchemeChanged(SchemeOption value)
    {
        Appearance.Scheme = value.Scheme;
        _settings.Scheme = value.Scheme;
        _settings.Save();
    }

    public bool IncludeSelf
    {
        get => _settings.IncludeSelf;
        set
        {
            _settings.IncludeSelf = value;
            _source.IncludeSelf = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { _settings.CloseToTray = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool Notifications
    {
        get => _settings.Notifications;
        set { _settings.Notifications = value; _settings.Save(); OnPropertyChanged(); }
    }

    [ObservableProperty] private bool _startAtSignIn = AutoStart.IsEnabled;
    [ObservableProperty] private string? _backgroundMessage = AutoStart.HasRunKey && Elevation.IsElevated
        ? "Sign-in start is set up unelevated. Turn it off and on again to start as administrator."
        : AutoStart.StartsElevated ? "Starts as administrator, so temperatures and per-app traffic work from sign-in." : null;

    partial void OnStartAtSignInChanged(bool value)
    {
        if (value == AutoStart.IsEnabled) return;
        var result = value ? AutoStart.Enable() : AutoStart.Disable();
        BackgroundMessage = result.Message;
        if (!result.Succeeded) Dispatcher.UIThread.Post(() => StartAtSignIn = AutoStart.IsEnabled);
    }

    public bool IsElevated => Elevation.IsElevated;

    public string ElevationText => Elevation.IsElevated
        ? "Running as administrator. Every reading and action is available."
        : "Running as a standard user. CPU temperature, per-app network and disk traffic, startup impact and some actions need administrator rights.";

    public string DriverText => SensorDriver.IsInstalled
        ? "PawnIO sensor driver installed."
        : "PawnIO sensor driver not installed, so CPU temperature is unavailable. The banner above offers to install it.";

    public string SettingsPath => Path.Combine(AppSettings.Folder, "settings.json");

    [RelayCommand]
    private void OpenAlertRules() => _openAlertRules();

    [RelayCommand]
    private void RestartElevated() => _restartElevated();

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        Directory.CreateDirectory(AppSettings.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Folder}\"") { UseShellExecute = true });
    }
}

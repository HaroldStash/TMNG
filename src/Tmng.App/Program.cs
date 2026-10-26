using Avalonia;
using Tmng.Platform.Windows;

namespace Tmng.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!SingleInstance.Claim(waitForExit: args.Contains(ElevationActions.RestartedArgument))) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

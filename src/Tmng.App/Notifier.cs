using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Tmng.App;

/// <summary>
/// Windows notifications for an unpackaged app. Windows needs an app identity to show them;
/// registering one under HKCU\Software\Classes\AppUserModelId gives the toast its name and icon
/// without an installer or Start menu shortcut.
/// </summary>
public static class Notifier
{
    private const string AppId = "TMNG.SystemMonitor";

    // A toast's Activated event only fires while the object is alive.
    private static readonly List<ToastNotification> Recent = [];
    private static bool _registered;

    public static string? Problem { get; private set; }

    public static void Show(string title, string body, Action? onClick = null)
    {
        try
        {
            Register();
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual></toast>");
            var toast = new ToastNotification(xml);
            if (onClick is not null) toast.Activated += (_, _) => onClick();
            lock (Recent)
            {
                Recent.Add(toast);
                if (Recent.Count > 20) Recent.RemoveAt(0);
            }
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
    }

    private static void Register()
    {
        if (_registered) return;
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "TMNG");
        string icon = Path.Combine(AppSettings.Folder, "tmng.png");
        if (File.Exists(icon)) key.SetValue("IconUri", icon);
        _registered = true;
    }
}

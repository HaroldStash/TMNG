using CommunityToolkit.Mvvm.ComponentModel;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App.ViewModels;

/// <summary>A page with actions that change the system. Destructive actions ask first in a modal dialog.</summary>
public abstract partial class ActionPageViewModel(string title, string glyph, Appearance appearance)
    : PageViewModel(title, glyph, appearance)
{
    [ObservableProperty] private string? _actionMessage;
    [ObservableProperty] private bool _busy;

    protected static Task<bool> Confirm(string title, string message, string confirmText) =>
        ConfirmDialog.Shared.AskAsync(title, message, confirmText);

    protected async Task RunAsync(Func<ActionResult> action)
    {
        Busy = true;
        ActionMessage = "Working…";
        try
        {
            ActionMessage = (await Task.Run(action)).Message;
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}

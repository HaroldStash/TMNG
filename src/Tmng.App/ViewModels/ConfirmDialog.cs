using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tmng.App.ViewModels;

/// <summary>The one modal confirmation shown over the main window before an action changes the system.</summary>
public sealed partial class ConfirmDialog : ObservableObject
{
    public static ConfirmDialog Shared { get; } = new();

    private TaskCompletionSource<bool>? _pending;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _confirmText = "";

    /// <summary>Shows the dialog and completes with true only if the user picks the action.</summary>
    public Task<bool> AskAsync(string title, string message, string confirmText)
    {
        _pending?.TrySetResult(false);
        _pending = new TaskCompletionSource<bool>();
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsOpen = true;
        return _pending.Task;
    }

    [RelayCommand]
    private void Accept() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool result)
    {
        IsOpen = false;
        _pending?.TrySetResult(result);
        _pending = null;
    }
}

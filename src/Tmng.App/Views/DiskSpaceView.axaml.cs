using Avalonia.Controls;
using Avalonia.Input;
using Tmng.App.ViewModels;

namespace Tmng.App.Views;

public partial class DiskSpaceView : UserControl
{
    public DiskSpaceView() => InitializeComponent();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DiskSpaceViewModel vm) vm.EnterCommand.Execute(vm.SelectedRow);
    }
}

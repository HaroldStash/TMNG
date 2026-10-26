using Avalonia.Controls;
using Tmng.App.ViewModels;

namespace Tmng.App.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView() => InitializeComponent();

    // The view model sorts so each app's processes stay under it; the grid's own sort would scatter them.
    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (DataContext is ProcessesViewModel vm && e.Column.SortMemberPath is { } path) vm.SortBy(path);
    }
}

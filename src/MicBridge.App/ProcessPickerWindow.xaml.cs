using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MicBridge.App.ViewModels;
using MicBridge.Audio.Devices;

namespace MicBridge.App;

public partial class ProcessPickerWindow : Window
{
    public ProcessPickerViewModel Model { get; }
    public ApplicationInfo? SelectedProcess => Model.SelectedProcess;
    public ProcessPickerWindow(int? selectedProcessId = null)
    {
        InitializeComponent(); Model = new(selectedProcessId); DataContext = Model;
        Loaded += async (_, _) =>
        {
            try { await Model.RefreshAsync(); SearchBox.Focus(); }
            catch (Exception error) { Model.ReportError(error); }
        };
    }
    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (!Model.CanAccept || SelectedProcess is null) return;
        var current = DeviceCatalog.GetApplication(SelectedProcess.ProcessId);
        if (current is null || SelectedProcess.StartTimeUtcTicks is long start && current.StartTimeUtcTicks != start)
        {
            Model.ReportError(new InvalidOperationException("无法确认这个进程的身份。它可能已退出、重启或受系统权限限制，请刷新或选择其他进程。"));
            return;
        }
        DialogResult = true;
    }
    private void OnProcessDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row confirms; blank list space must not accept an old selection.
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && node is not ListBoxItem)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is ListBoxItem) OnAccept(sender, e);
    }
}

using System.Windows;
using System.Windows.Input;
using MicBridge.App.ViewModels;
using MicBridge.App.Infrastructure;
using System.Windows.Media;

namespace MicBridge.App;

public partial class MainWindow : Window
{
    public MainViewModel Model { get; }
    public MainWindow(bool loadSettings = true)
    {
        InitializeComponent(); Model = new MainViewModel(loadSettings); DataContext = Model;
        SourceInitialized += (_, _) => { if (WindowBackdrop.Apply(this)) WindowSurface.Background = new SolidColorBrush(Color.FromArgb(176, 17, 17, 20)); };
        Loaded += async (_, _) =>
        {
            try { await Model.RefreshAsync(); if (loadSettings && Model.ShouldOfferDriverInstallation) Model.OpenDriverSetup(); }
            catch (Exception ex) { Model.ReportError(ex); }
        };
        Closed += (_, _) => Model.Dispose();
        StateChanged += (_, _) => Model.SetDisplayActive(WindowState != WindowState.Minimized);
    }
    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Model.AddPaths(paths);
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try { await Model.PlaySelectedAsync(); } catch (Exception ex) { Model.ReportError(ex); }
    }
    private void OnSeekStarted(object sender, MouseButtonEventArgs e) => Model.IsSeeking = true;
    private void OnSeekEnded(object sender, RoutedEventArgs e) => Model.IsSeeking = false;
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

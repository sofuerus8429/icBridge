using System.Windows;
using System.Windows.Input;
using MicBridge.App.ViewModels;

namespace MicBridge.App;

public partial class AdvancedSettingsWindow : Window
{
    private readonly MainViewModel _model;
    public AdvancedSettingsWindow(MainViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
    }
    private void OnDone(object sender, RoutedEventArgs e) => Close();
    private void OnLicenses(object sender, RoutedEventArgs e) => Services.LicenseNotices.Show(this);
    private async void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try { await _model.PlaySelectedAsync(); } catch (Exception error) { _model.ReportError(error); }
    }
}

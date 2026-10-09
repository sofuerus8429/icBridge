using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using MicBridge.App.Services;
using MicBridge.App.ViewModels;

namespace MicBridge.App;

public partial class UninstallWindow : Window
{
    private readonly MainViewModel _model;
    private bool _busy;
    public UninstallWindow(MainViewModel model)
    {
        InitializeComponent(); _model = model;
        Loaded += async (_, _) =>
        {
            try { await RefreshDriverAsync(); }
            catch (Exception error) { RemovalStatus.Text = error.Message; }
        };
    }
    internal async Task RefreshDriverAsync()
    {
        bool installed = await Task.Run(UninstallServices.HasVbCable);
        DriverCheckBox.IsEnabled = installed && DriverPackage.Supported && !_busy;
        DriverCheckBox.IsChecked = installed && DriverPackage.Supported;
        DriverStatus.Text = installed ? "已检测到 VB-CABLE。" : "未检测到 VB-CABLE，无需卸载该驱动。";
        UpdateAction();
    }
    internal void UpdateAction()
    {
        RemoveButton.IsEnabled = !_busy && _model.IsStopped && (DriverCheckBox.IsChecked == true || SettingsCheckBox.IsChecked == true);
        if (!_busy) RemovalStatus.Text = RemoveButton.IsEnabled ? "点击开始后执行所选操作。" : "未选择任何操作。";
    }
    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateAction();
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_busy || !_model.IsStopped || !RemoveButton.IsEnabled) return;
        bool removeDriver = DriverCheckBox.IsChecked == true, clearSettings = SettingsCheckBox.IsChecked == true;
        _busy = true; RemoveButton.IsEnabled = false; CloseButton.IsEnabled = false;
        DriverCheckBox.IsEnabled = false; SettingsCheckBox.IsEnabled = false;
        DriverStaging? staging = null;
        try
        {
            if (await Task.Run(UninstallServices.OtherMicBridgeInstanceIsRunning))
                throw new InvalidOperationException("检测到其他音桥窗口，请先关闭后再重试。");
            string result = "";
            if (removeDriver)
            {
                if (!await Task.Run(UninstallServices.HasVbCable))
                    throw new InvalidOperationException("未检测到 VB-CABLE，未启动官方工具。可以只选择清除音桥设置。");
                RemovalStatus.Text = "正在准备官方卸载工具…";
                staging = await Task.Run(() => DriverPackage.Prepare());
                var launch = await Task.Run(staging.CreateLaunchInfo);
                RemovalStatus.Text = "请在官方窗口点击 Remove Driver，并按提示重启。";
                using var process = Process.Start(launch) ?? throw new InvalidOperationException("无法打开官方卸载工具。");
                await Platform.WaitForExitAsync(process);
                await _model.RefreshAsync();
                result = await Task.Run(UninstallServices.HasVbCable)
                    ? "官方工具已关闭，仍检测到设备。如已完成卸载，请重启；如取消了卸载，驱动会保留。"
                    : "未再检测到 VB-CABLE。请按官方工具提示重启电脑。";
            }
            if (clearSettings)
            {
                _model.SuppressSettingsSave();
                UninstallServices.ClearSettings();
                result += " 音桥设置和错误日志已清除，本次会话不会重新保存配置。";
            }
            RemovalStatus.Text = result.Trim();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            RemovalStatus.Text = "管理员权限请求已取消，卸载未开始，设置未清理。";
        }
        catch (Exception error) { RemovalStatus.Text = error.Message; }
        finally
        {
            staging?.Dispose();
            string status = RemovalStatus.Text;
            if (staging?.CleanupError is not null) status += $" 临时文件未能清理：{staging.DirectoryPath}";
            _busy = false; CloseButton.IsEnabled = true; SettingsCheckBox.IsEnabled = true;
            try { await RefreshDriverAsync(); }
            catch (Exception error) { status += " " + error.Message; }
            RemovalStatus.Text = status;
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

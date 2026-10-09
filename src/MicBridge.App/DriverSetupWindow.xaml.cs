using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using MicBridge.App.Services;
using MicBridge.App.ViewModels;

namespace MicBridge.App;

public partial class DriverSetupWindow : Window
{
    private readonly MainViewModel _model;
    private bool _busy;
    public DriverSetupWindow(MainViewModel model)
    {
        InitializeComponent(); _model = model;
        InstallButton.IsEnabled = DriverPackage.Supported && !model.HasInstalledVirtualDriver;
        if (model.HasInstalledVirtualDriver) SetupStatus.Text = "驱动已安装，无需重复安装。" + model.CableReminder;
        else if (!DriverPackage.Supported) SetupStatus.Text = "内置安装入口支持 Windows x64；其他系统请从 VB-Audio 官网获取驱动。";
    }
    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy || !_model.IsStopped) return;
        _busy = true; InstallButton.IsEnabled = false; LaterButton.IsEnabled = false;
        DriverStaging? staging = null;
        try
        {
            await _model.RefreshAsync();
            if (_model.HasInstalledVirtualDriver) { SetupStatus.Text = "驱动已安装，无需重复安装。" + _model.CableReminder; return; }
            SetupStatus.Text = "正在准备官方驱动安装器…";
            staging = await Task.Run(() => DriverPackage.Prepare());
            var launch = await Task.Run(staging.CreateLaunchInfo);
            SetupStatus.Text = "请在官方安装器中完成安装，并按提示重启电脑。";
            using var process = Process.Start(launch) ?? throw new InvalidOperationException("无法打开驱动安装器。");
            await Platform.WaitForExitAsync(process);
            await _model.RefreshAsync();
            SetupStatus.Text = _model.HasVirtualMicrophone ? "已检测到虚拟音频设备。请按安装器提示重启电脑后使用。" :
                "安装器已关闭。如已完成安装，请重启电脑后再打开音桥；如取消了安装，可稍后再试。";
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            SetupStatus.Text = "管理员权限请求已取消，安装未开始。可以稍后再试。";
        }
        catch (Exception error)
        {
            SettingsStore.Log(error); SetupStatus.Text = error.Message;
        }
        finally
        {
            staging?.Dispose();
            if (staging?.CleanupError is not null) SetupStatus.Text += $" 临时文件未能清理：{staging.DirectoryPath}";
            _busy = false; LaterButton.IsEnabled = true; LaterButton.Content = "关闭";
            InstallButton.IsEnabled = DriverPackage.Supported && !_model.HasInstalledVirtualDriver;
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
    private void OnLater(object sender, RoutedEventArgs e) => Close();
    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }); }
        catch (Exception error) { SetupStatus.Text = error.Message; }
        e.Handled = true;
    }
    private void OnLicense(object sender, RoutedEventArgs e)
    {
        var body = new TextBox { Text = DriverPackage.ReadOriginalLicense(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(24), FontSize = 12 };
        new Window { Title = "VB-CABLE 原始许可 · VB-Audio", Owner = this, Width = 720, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body }.ShowDialog();
    }
}

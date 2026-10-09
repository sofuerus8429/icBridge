using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using MicBridge.App.Services;

namespace MicBridge.App;

public partial class App : Application
{
    private AudioVerificationLease? _audioVerificationLease;
    protected override void OnExit(ExitEventArgs e)
    {
        _audioVerificationLease?.Dispose(); base.OnExit(e);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--qa-settings-writer")
        {
            try { SettingsVerification.WriteFixture(e.Args[1], int.Parse(e.Args[2])); Shutdown(); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-settings")
        {
            Dispatcher.BeginInvoke(async () => { try { await SettingsVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--verify-endurance" or "--verify-hardware-preflight" or "--verify-hardware-audio"
            or "--verify-microphone-lifecycle" or "--verify-process-audio" or "--verify-process-load" or "--verify-simple-audio" or "--verify-performance" or "--verify-startup-cancellation" or "--verify-file-formats")
        {
            try { _audioVerificationLease = new(); }
            catch (InvalidOperationException error) { File.WriteAllText(e.Args[1], error.Message); Shutdown(2); return; }
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-file-formats")
        {
            Dispatcher.BeginInvoke(async () => { try { await FileFormatsVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-startup-cancellation")
        {
            Dispatcher.BeginInvoke(async () => { try { await StartupCancellationVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-endurance")
        {
            Dispatcher.BeginInvoke(async () => { try { await EnduranceVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-hardware-preflight")
        {
            Dispatcher.BeginInvoke(async () => { try { await HardwarePreflightVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-microphone-lifecycle")
        {
            Dispatcher.BeginInvoke(async () => { try { await MicrophoneLifecycleVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-device-notifications")
        {
            Dispatcher.BeginInvoke(async () => { try { await DeviceNotificationsVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-hardware-audio")
        {
            Dispatcher.BeginInvoke(async () => { try { await HardwareAudioVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { if (!File.Exists(e.Args[1])) File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--qa-tone")
        {
            Dispatcher.BeginInvoke(async () => { try { await ProcessAudioVerification.PlayToneAsync(double.Parse(e.Args[1])); Shutdown(); } catch { Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--verify-process-audio" or "--verify-process-load")
        {
            Dispatcher.BeginInvoke(async () => { try { await ProcessAudioVerification.RunAsync(e.Args[1], e.Args[0] == "--verify-process-load"); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-performance")
        {
            Dispatcher.BeginInvoke(async () => { try { await PerformanceVerification.RunAsync(e.Args[1]); Shutdown(); } catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); } }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--uninstall")
        {
            var model = new ViewModels.MainViewModel(false);
            var removal = new UninstallWindow(model) { ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            removal.Closed += (_, _) => model.Dispose();
            MainWindow = removal; removal.Show();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-uninstall")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try { await UninstallVerification.RunAsync(e.Args[1]); Shutdown(); }
                catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-bundled-driver")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try { await DriverPackageVerification.RunAsync(e.Args[1]); Shutdown(); }
                catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-single-file")
        {
            try { SingleFileVerification.Run(e.Args[1]); Shutdown(); }
            catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-console")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try { await ConsoleVerification.RunAsync(e.Args[1]); Shutdown(); }
                catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-simple-audio")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try { await SimpleAudioVerification.RunAsync(e.Args[1]); Shutdown(); }
                catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-process-picker")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try { await ProcessPickerVerification.RunAsync(int.Parse(e.Args[1]), e.Args[2]); Shutdown(); }
                catch (Exception error) { File.WriteAllText(e.Args[2], error.ToString()); Shutdown(1); }
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        bool renderPicker = e.Args.Length >= 2 && e.Args[0] == "--render-process-picker";
        bool renderDriverSetup = e.Args.Length >= 2 && e.Args[0] == "--render-driver-setup";
        bool renderUninstall = e.Args.Length >= 2 && e.Args[0] == "--render-uninstall";
        bool renderAdvanced = e.Args.Length >= 2 && e.Args[0] == "--render-advanced";
        bool renderPreview = renderPicker || renderAdvanced || renderDriverSetup || renderUninstall || e.Args.Length >= 2 && e.Args[0] == "--render-preview";
        TextWriterTraceListener? bindingLog = null;
        if (renderPreview)
        {
            bindingLog = new TextWriterTraceListener(e.Args[1] + ".bindings.log");
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            SettingsStore.Log(args.Exception);
            if (MainWindow is MainWindow window) { window.Model.ReportError(args.Exception); args.Handled = true; }
        };
        if (renderPreview)
        {
            Window window;
            Func<Task> refresh;
            Action cleanup;
            if (renderPicker)
            {
                var picker = new ProcessPickerWindow(); window = picker; cleanup = () => { };
                refresh = async () =>
                {
                    await picker.Model.RefreshAsync();
                    if (e.Args.Length > 2) picker.Model.SearchText = e.Args[2];
                    if (e.Args.Length > 3 && int.TryParse(e.Args[3], out int selectedId))
                        picker.Model.SelectedProcess = picker.Model.Processes.Cast<MicBridge.Audio.Devices.ApplicationInfo>().FirstOrDefault(p => p.ProcessId == selectedId);
                };
            }
            else if (renderUninstall)
            {
                var model = new ViewModels.MainViewModel(false);
                var removal = new UninstallWindow(model);
                window = removal; cleanup = model.Dispose;
                refresh = async () => { await model.RefreshAsync(); await removal.RefreshDriverAsync(); };
            }
            else if (renderDriverSetup)
            {
                var model = new ViewModels.MainViewModel(false);
                var setup = new DriverSetupWindow(model);
                window = setup; cleanup = model.Dispose;
                refresh = async () => { await model.RefreshAsync(); setup.InstallButton.IsEnabled = DriverPackage.Supported && !model.HasInstalledVirtualDriver; };
            }
            else if (renderAdvanced)
            {
                var model = new ViewModels.MainViewModel(false); var settings = new AdvancedSettingsWindow(model);
                if (e.Args.Length > 2)
                {
                    settings.DeviceSection.IsExpanded = e.Args[2] == "devices" || e.Args[2] == "hardware";
                    settings.MixSection.IsExpanded = e.Args[2] == "mix";
                    settings.PlaylistSection.IsExpanded = e.Args[2] == "playlist";
                    settings.ProcessingSection.IsExpanded = e.Args[2] == "processing";
                }
                window = settings; refresh = async () => { await model.RefreshAsync(); if (e.Args.Length > 2 && e.Args[2] == "hardware") model.HardwareLoopbackMode = true; }; cleanup = model.Dispose;
            }
            else
            {
                var main = new MainWindow(false); window = main; cleanup = main.Model.Dispose;
                refresh = async () => { await main.Model.RefreshAsync(); if (e.Args.Length > 4 && e.Args[4] == "process") await main.Model.ChangeSourceKindAsync(1); };
            }
            MainWindow = window;
            int width = renderPicker ? 740 : renderUninstall ? 660 : renderDriverSetup ? 620 : renderAdvanced ? 860 : e.Args.Length >= 4 ? Platform.Clamp(int.Parse(e.Args[2]), 900, 2160) : 1060;
            int height = renderPicker ? 580 : renderUninstall ? 600 : renderDriverSetup ? 550 : renderAdvanced ? 740 : e.Args.Length >= 4 ? Platform.Clamp(int.Parse(e.Args[3]), 690, 1440) : 790;
            // Render our own WPF visual tree for layout QA without starting audio or opening a window.
            var content = (UIElement)window.Content; window.Content = null;
            var preview = new Border { Background = window.Background, Child = content, DataContext = window.DataContext,
                Width = width, Height = height, UseLayoutRounding = true };
            TextElement.SetFontFamily(preview, window.FontFamily); TextElement.SetFontSize(preview, window.FontSize);
            TextElement.SetForeground(preview, window.Foreground);
            Dispatcher.BeginInvoke(async () =>
            {
                int exitCode = 0;
                try
                {
                    await refresh();
                    preview.Measure(new Size(width, height)); preview.Arrange(new Rect(0, 0, width, height)); preview.UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(preview);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.GetFullPath(e.Args[1])); encoder.Save(file);
                }
                catch (Exception error) { exitCode = 1; File.WriteAllText(e.Args[1] + ".error.log", error.ToString()); }
                finally { bindingLog?.Flush(); bindingLog?.Close(); cleanup(); Shutdown(exitCode); }
            }, DispatcherPriority.ApplicationIdle);
        }
        else { var main = new MainWindow(); MainWindow = main; main.Show(); }
    }
}

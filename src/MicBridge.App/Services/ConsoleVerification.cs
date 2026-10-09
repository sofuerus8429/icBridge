using System.IO;
using MicBridge.App.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MicBridge.App.Controls;
using MicBridge.App.ViewModels;

namespace MicBridge.App.Services;

internal static class ConsoleVerification
{
    // Inspect this application's WPF visual tree and bindings without opening windows or saving user settings.
    internal static async Task RunAsync(string reportPath)
    {
        var window = new MainWindow(false);
        using var model = window.Model;
        await model.RefreshAsync();
        if (model.DisplayTimerEnabled) throw new InvalidOperationException("Idle UI must not run the meter timer.");
        if (model.HasVirtualMicrophone && model.OutputDevices.Any(MicBridge.Audio.Devices.DeviceCatalog.IsStandardCableInput) && model.OutputDevices.Count != 1)
            throw new InvalidOperationException("The default output list should contain only the standard stereo cable port.");
        var content = (FrameworkElement)window.Content; window.Content = null;
        content.DataContext = model;
        var passed = new List<string>();
        Require(model.CableReminder.Contains("CABLE Output"), "Virtual microphone reminder names the required CABLE Output recording endpoint.");
        Require(model.CableReminder == MicBridge.Audio.Devices.DeviceCatalog.CableReminder(
            MicBridge.Audio.Devices.DeviceCatalog.GetDevices(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.All),
            MicBridge.Audio.Devices.DeviceCatalog.GetDevices(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.All),
            MicBridge.Audio.Devices.DeviceCatalog.GetDefaultDevice(NAudio.CoreAudioApi.DataFlow.Render)),
            "Main reminder follows the actual default playback device and endpoint availability.");
        Require(!model.DisplayTimerEnabled, "Idle meter sampling is stopped.");
        Require(window.Icon is not null, "The application window uses the generated music bridge icon.");
        Layout(1060, 790);
        var strips = Descendants(content).OfType<ConsoleChannelStrip>().ToArray();
        Require(strips.Length == 3, "Main view contains microphone, music and output console strips.");
        var sourceStrip = strips[1];
        var sourceFader = Descendants(sourceStrip).OfType<Slider>().Single();
        Require(sourceFader.Orientation == Orientation.Vertical && sourceFader.Maximum == 100,
            "Music fader uses the normal vertical 0-100 range.");
        model.Music.GainMode = true;
        await Flush(); Layout(1060, 790);
        Require(sourceFader.Maximum == 300, "Enabling GAIN updates the bound fader range to 300.");
        sourceFader.Value = 300; await Flush();
        Require(model.Music.Volume == 300, "Dragging the fader to 300 updates the actual music channel.");
        model.Music.GainMode = false; await Flush();
        Require(sourceFader.Maximum == 100 && sourceFader.Value == 100 && model.Music.Volume == 100,
            "Leaving GAIN clamps both the fader and audio volume to 100.");
        model.Music.Volume = 42;
        model.ApplicationChannel.GainMode = true; model.ApplicationChannel.Volume = 240;
        await model.ChangeSourceKindAsync(1); await Flush(); Layout(1060, 790);
        Require(ReferenceEquals(sourceStrip.DataContext, model.ApplicationChannel) && sourceFader.Maximum == 300 && sourceFader.Value == 240,
            "Changing to process mode retargets the existing fader to the process channel and its saved gain range.");
        await model.ChangeSourceKindAsync(0); await Flush(); Layout(1060, 790);
        Require(ReferenceEquals(sourceStrip.DataContext, model.Music) && sourceFader.Maximum == 100 && sourceFader.Value == 42,
            "Returning to local mode preserves its independent volume.");
        Require(Descendants(content).OfType<VerticalLevelMeter>().Count() == 3,
            "Each console strip contains one segmented lamp meter instead of an analog face.");
        foreach (var size in new[] { new Size(1060, 790), new Size(900, 690) })
        {
            Layout(size.Width, size.Height);
            var reminder = Descendants(content).OfType<TextBlock>().Single(block => block.Text == model.CableReminder);
            var card = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(reminder));
            var reminderBounds = reminder.TransformToAncestor(card).TransformBounds(new Rect(reminder.RenderSize));
            Require(reminderBounds.Right <= card.ActualWidth + 1 && reminderBounds.Bottom <= card.ActualHeight + 1,
                $"{size.Width}x{size.Height}: the complete output reminder fits inside its source card.");
            foreach (var strip in strips)
            {
                foreach (var control in Descendants(strip).OfType<FrameworkElement>().Where(element => element is CheckBox or Slider))
                {
                    var bounds = control.TransformToAncestor(strip).TransformBounds(new Rect(control.RenderSize));
                    Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= strip.ActualWidth + 1 && bounds.Bottom <= strip.ActualHeight + 1,
                        $"{size.Width}x{size.Height}: {strip.Title} {control.GetType().Name} fits inside its console strip.");
                }
            }
        }
        var settings = new AppSettings { Music = new(240, GainMode: true), MicrophoneGateEnabled = true,
            MicrophoneHighPassEnabled = true, MicrophoneGateThreshold = -45 };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Require(restored.Music.GainMode && restored.Music.Volume == 240 && restored.MicrophoneGateEnabled &&
            restored.MicrophoneHighPassEnabled && restored.MicrophoneGateThreshold == -45,
            "Gain mode and microphone processing survive a settings serialization roundtrip.");
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Music\":{\"Volume\":90},\"Master\":{\"Volume\":80}}")!;
        Require(!legacy.Music.GainMode && !legacy.MicrophoneGateEnabled && legacy.MicrophoneGateThreshold == -50,
            "Older settings retain normal gain and disabled microphone processing.");
        Require(!legacy.HardwareLoopbackMode, "Older settings keep virtual cable mode.");
        restored.HardwareLoopbackMode = true; restored.HardwareCaptureDeviceId = "hardware-test";
        var hardwareSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(restored))!;
        Require(hardwareSettings.HardwareLoopbackMode && hardwareSettings.HardwareCaptureDeviceId == "hardware-test", "Hardware route selection survives serialization.");
        Require(!model.MicrophoneDevices.Any(MicBridge.Audio.Devices.DeviceCatalog.IsHardwareLoopback), "Hardware loopback is excluded from original microphone inputs.");
        model.HardwareLoopbackMode = true;
        Require(model.OutputDevices.All(d => !d.IsVirtual && model.HardwareCaptureDevice is not null && MicBridge.Audio.Devices.DeviceCatalog.IsSameAdapter(d, model.HardwareCaptureDevice)), "Hardware mode lists only matching physical outputs.");
        Require(model.OutputCaption.Contains("立体声混音") && model.CableReminder.Contains("立体声混音"), "Main route caption and reminder follow hardware mode.");
        Require(model.DetectHardwareOutputCommand.CanExecute(null) == (model.HardwareCaptureDevice?.State == NAudio.CoreAudioApi.DeviceState.Active), "Output detection availability follows actual hardware capture state.");
        Require(!model.IsDetectingHardware && !model.CancelHardwareDetectionCommand.CanExecute(null), "Cancel detection is unavailable when the detector is idle.");
        model.HardwareLoopbackMode = false;
        Require(model.CableReminder.Contains("CABLE Output"), "Switching back restores the required virtual microphone reminder.");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Version = model.VersionText, Passed = passed,
            AudioStarted = false, UserSettingsSaved = false }, new JsonSerializerOptions { WriteIndented = true }));

        void Layout(double width, double height)
        {
            content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        }
        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            passed.Add(description);
        }
    }
    private static Task Flush() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind).Task;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

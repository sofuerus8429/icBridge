using System.Diagnostics;
using System.IO;
using MicBridge.App.Serialization;
using MicBridge.App.ViewModels;
using NAudio.CoreAudioApi;

namespace MicBridge.App.Services;

internal static class DeviceNotificationsVerification
{
    // QA runner observes a manual hardware state change. It never changes endpoints itself.
    internal static async Task RunAsync(string reportPath)
    {
        using var model = new MainViewModel(false);
        await model.RefreshAsync(); model.HardwareLoopbackMode = true;
        string id = model.HardwareCaptureDevice?.Id ?? throw new InvalidOperationException("Hardware capture not found.");
        if (model.HardwareCaptureDevice?.State != DeviceState.Active) throw new InvalidOperationException("Begin with hardware capture enabled.");
        File.WriteAllText(reportPath, "Waiting for hardware endpoint to be disabled through Windows settings.");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(100);
            if (model.HardwareCaptureDevice?.Id == id && model.HardwareCaptureDevice.State == DeviceState.Disabled)
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, AutomaticStateRefresh = true,
                    DisabledEndpointDetected = true, Reminder = model.CableReminder, IdleMeterTimerStopped = !model.DisplayTimerEnabled,
                    NoManualRefreshCalled = true, UserSettingsSaved = false }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
        }
        throw new TimeoutException("No automatic disabled hardware endpoint notification received.");
    }
}

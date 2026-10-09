using System.IO;
using MicBridge.App.Serialization;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;

namespace MicBridge.App.Services;

internal static class MicrophoneLifecycleVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        var output = DeviceCatalog.GetDevices(DataFlow.Render).First(DeviceCatalog.IsStandardCableInput);
        var microphone = DeviceCatalog.GetDevices(DataFlow.Capture).First(d => !d.IsVirtual && !DeviceCatalog.IsHardwareLoopback(d));
        using var engine = new AudioEngine(); var failures = new List<string>();
        engine.Failed += error => { lock (failures) failures.Add(error.Message); };
        engine.Master.Muted = true; engine.Microphone.Enabled = false;
        await engine.StartAsync(new(output.Id, microphone.Id));
        if (engine.IsMicrophoneCapturing) throw new InvalidOperationException("Disabled microphone still opened at start.");
        for (int cycle = 0; cycle < 3; cycle++)
        {
            engine.Microphone.Enabled = true; await Wait(true); await Task.Delay(200);
            engine.Microphone.Enabled = false; await Wait(false); await Task.Delay(200);
            if (!engine.IsRunning || engine.Microphone.InputPeak != 0 || engine.OutputPeak != 0) throw new InvalidOperationException("Disabling microphone broke routing or left a stale signal.");
        }
        await engine.StopAsync();
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, DisabledMicrophoneNotOpenedAtStart = true,
            EnableDisableCycles = 3, CaptureReleasedWhenDisabled = true, RoutingKeptAlive = true,
            OutputMuted = true, MicrophoneRecordingSaved = false, UserSettingsSaved = false }, new JsonSerializerOptions { WriteIndented = true }));
        async Task Wait(bool capturing)
        {
            for (int attempt = 0; attempt < 100; attempt++) { if (engine.IsMicrophoneCapturing == capturing) return; await Task.Delay(50); }
            throw new TimeoutException("Microphone capture state did not follow channel enablement.");
        }
    }
}

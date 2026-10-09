using System.Diagnostics;
using System.IO;
using MicBridge.App.Serialization;
using MicBridge.App.ViewModels;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicBridge.App.Services;

internal static class HardwarePreflightVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        string fixture = Path.Combine(directory, "preflight-tone-" + Guid.NewGuid().ToString("N") + ".wav");
        var checks = new List<string>();
        var cancellationTimes = new Dictionary<string, double>();
        using var model = new MainViewModel(false);
        await Configure(model);
        var capture = model.HardwareCaptureDevice!;
        var initialOutput = model.OutputDevices.FirstOrDefault(d => d.Name.Contains("耳机") || d.Name.Contains("Headphones", StringComparison.OrdinalIgnoreCase))
            ?? model.OutputDevices.First();
        model.OutputDevice = initialOutput;
        try
        {
            using (var writer = new WaveFileWriter(fixture, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                var samples = new float[48000 * 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = 0.004f * (float)Math.Sin(2 * Math.PI * 500 * (i / 2) / 48000);
                writer.WriteSamples(samples, 0, samples.Length);
            }
            await model.UseLocalFileAsync(fixture);
            await model.ToggleSourcePlaybackAsync();
            Require(model.IsRunning && model.LocalSourcePlaying && model.HardwareDetectionCount == 1,
                "First playback verifies the actual route before starting the local source.");
            string verifiedOutput = model.OutputDevice!.Id;
            await model.StopSourceAsync();
            await model.ToggleSourcePlaybackAsync();
            Require(model.IsRunning && model.HardwareDetectionCount == 1,
                "Restarting the same verified route reuses the session cache without another test tone.");
            await model.StopSourceAsync();

            if (initialOutput.Id != verifiedOutput)
            {
                model.OutputDevice = initialOutput;
                await model.ToggleSourcePlaybackAsync();
                Require(model.HardwareDetectionCount == 2 && model.OutputDevice!.Id == verifiedOutput,
                    "Selecting a different, unrecordable port triggers verification and returns to the working port.");
                await model.StopSourceAsync();
            }

            using (var enumerator = new MMDeviceEnumerator())
            using (var output = enumerator.GetDevice(verifiedOutput))
            using (var input = enumerator.GetDevice(capture.Id))
            {
                bool previousMute = output.AudioEndpointVolume.Mute;
                float previousLevel = input.AudioEndpointVolume.MasterVolumeLevelScalar;
                try
                {
                    output.AudioEndpointVolume.Mute = true;
                    await ExpectLevelFailure("混音输出设备");
                    output.AudioEndpointVolume.Mute = previousMute;
                    input.AudioEndpointVolume.MasterVolumeLevelScalar = 0;
                    await ExpectLevelFailure("立体声混音");
                }
                finally
                {
                    output.AudioEndpointVolume.Mute = previousMute;
                    input.AudioEndpointVolume.MasterVolumeLevelScalar = previousLevel;
                }
            }

            await CheckCancellation(dispose: false);
            await CheckCancellation(dispose: true);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, Checks = checks, InitialOutput = initialOutput, VerifiedOutputId = verifiedOutput,
                CancellationMilliseconds = cancellationTimes, RealMicrophoneCaptured = false,
                DriverInstalled = false, UserSettingsSaved = false, WindowsDefaultDevicesChanged = false,
                EndpointLevelsRestored = true, FixtureDeleted = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { model.Dispose(); if (File.Exists(fixture)) File.Delete(fixture); }

        async Task ExpectLevelFailure(string expected)
        {
            try { await model.ToggleSourcePlaybackAsync(); throw new InvalidOperationException("Playback unexpectedly accepted a silent Windows endpoint."); }
            catch (InvalidOperationException error) when (error.Message.Contains(expected))
            {
                Require(!model.IsRunning && !model.LocalSourcePlaying && !model.IsBusy,
                    "Silent Windows endpoint is rejected before playback: " + expected);
            }
        }
        async Task CheckCancellation(bool dispose)
        {
            using var fresh = new MainViewModel(false);
            await Configure(fresh); await fresh.UseLocalFileAsync(fixture);
            Task playback = fresh.ToggleSourcePlaybackAsync();
            await WaitUntil(() => fresh.IsDetectingHardware);
            await Task.Delay(500); // Cancel during the tone, after the baseline measurement.
            Require(fresh.CancelHardwareDetectionCommand.CanExecute(null), "Detection exposes an enabled cancel command.");
            var elapsed = Stopwatch.StartNew();
            if (dispose) fresh.Dispose(); else fresh.CancelHardwareDetectionCommand.Execute(null);
            try { await playback; throw new InvalidOperationException("Cancelled preflight unexpectedly completed playback."); }
            catch (OperationCanceledException)
            {
                cancellationTimes[dispose ? "CloseWindow" : "CancelButton"] = elapsed.Elapsed.TotalMilliseconds;
                Require(!fresh.IsRunning && !fresh.LocalSourcePlaying && !fresh.IsDetectingHardware,
                    dispose ? "Closing the model stops the detector and never starts playback." : "Cancelling the detector never starts playback.");
                Require(elapsed.Elapsed < TimeSpan.FromSeconds(2), "Cancellation finishes within two seconds.");
                if (!dispose) Require(!fresh.IsBusy && fresh.Status.Contains("取消"), "Cancelled detection restores the controls and explains the result.");
            }
        }
        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
    }
    private static async Task Configure(MainViewModel model)
    {
        await model.RefreshAsync(); model.HardwareLoopbackMode = true;
        if (model.HardwareCaptureDevice?.State != DeviceState.Active) throw new InvalidOperationException("Enable the hardware loopback endpoint before verification.");
        model.Microphone.Enabled = false; model.Master.Muted = true; model.Loop = true;
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Hardware detection did not start.");
            await Task.Delay(20);
        }
    }
}

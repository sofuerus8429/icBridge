using System.Diagnostics;
using System.IO;
using MicBridge.App.Serialization;
using MicBridge.App.ViewModels;
using MicBridge.Audio.Devices;
using MicBridge.Audio.Mixing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicBridge.App.Services;

internal static class SimpleAudioVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, $"micbridge-simple-{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(fixture, AudioFormat.MixFormat))
        {
            float[] block = new float[960];
            for (int frame = 0; frame < 400; frame++)
            {
                for (int i = 0; i < 480; i++) block[i * 2] = block[i * 2 + 1] = 0.02f * MathF.Sin(2 * MathF.PI * 220 * (frame * 480 + i) / 48000);
                writer.WriteSamples(block, 0, block.Length);
            }
        }
        try
        {
            using var model = new MainViewModel(false);
            await model.RefreshAsync();
            using var enumerator = new MMDeviceEnumerator();
            model.ShowOtherOutputDevices = true;
            using var defaultOutput = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var cable = model.OutputDevices.FirstOrDefault(DeviceCatalog.IsStandardCableInput);
            using var output = enumerator.GetDevice(cable?.Id ?? defaultOutput.ID);
            model.OutputDevice = model.OutputDevices.Single(device => device.Id == output.ID);
            model.Master.Muted = true; model.MixBalance = 0; model.Microphone.Enabled = false;
            var passed = new List<string>();
            await model.UseLocalFileAsync(fixture);
            Require(model.IsLocalSource && model.HasTrack, "Choosing a local file loads it and selects local mode.");
            await model.ToggleSourcePlaybackAsync();
            double playbackStartupMilliseconds = await WaitForAsync(() => model.IsRunning && model.SeekSeconds > 0.1 && model.SourceMeter > 0);
            Require(model.IsRunning && model.SeekSeconds > 0.1 && model.SourceMeter > 0, "The main play action advances the file and updates real source activity.");
            model.SourceChannel.GainMode = true; model.SourceChannel.Volume = 300;
            await WaitForAsync(() => model.SourceChannel.InputMeter > 0 && model.SourceChannel.Meter > model.SourceChannel.InputMeter);
            Require(model.SourceChannel.Volume == 300 && model.SourceChannel.InputMeter > 0 && model.SourceChannel.Meter > model.SourceChannel.InputMeter,
                "The 300 percent fader amplifies real local playback and preserves its pre-fader input meter.");
            Require(model.Master.InputMeter > 0 && model.Master.Meter == 0,
                "The output strip reports limiter output silence while retaining pre-master mix activity.");
            Require(model.OutputMeter == 0, "The real output remains silent under master mute.");
            await model.ToggleSourcePlaybackAsync(); await Task.Delay(80);
            double paused = model.SeekSeconds; await Task.Delay(150);
            Require(model.IsRunning && Math.Abs(model.SeekSeconds - paused) < 0.02, "Pause holds the file position while leaving the mix running for microphone input.");
            await model.ChangeSourceKindAsync(1);
            Require(!model.IsRunning && model.IsApplicationSource, "Changing the source stops the prior graph before changing mode.");
            await model.UseLocalFileAsync(fixture); await model.ToggleSourcePlaybackAsync(); await Task.Delay(150);
            Require(model.IsRunning, "Local playback restarts after a source change.");
            await model.StopSourceAsync(); await Task.Delay(80);
            Require(!model.IsRunning && model.SeekSeconds == 0, "The main stop action stops the graph and returns the file to its beginning.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = passed, OutputEndpoint = new { output.ID, output.FriendlyName },
                PlaybackStartupMilliseconds = playbackStartupMilliseconds, OutputKeptMuted = true, MicrophoneCaptured = false,
                AudioRecorded = false }, new JsonSerializerOptions { WriteIndented = true }));

            static async Task<double> WaitForAsync(Func<bool> condition)
            {
                var watch = Stopwatch.StartNew();
                while (!condition() && watch.ElapsedMilliseconds < 2000) await Task.Delay(50);
                return watch.Elapsed.TotalMilliseconds;
            }

            void Require(bool value, string description)
            {
                if (!value) throw new InvalidOperationException($"{description} Running={model.IsRunning}, Playing={model.LocalSourcePlaying}, Position={model.SeekSeconds:F3}s, FilePosition={model.LocalSourcePosition.TotalSeconds:F3}s, SourceMeter={model.SourceMeter:F3}, Timer={model.DisplayTimerEnabled}, Output={output.FriendlyName}, Status={model.Status}");
                passed.Add(description);
            }
        }
        finally { File.Delete(fixture); }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using MicBridge.App.Serialization;
using NAudio.Wave;

namespace MicBridge.App.Services;

internal static class PerformanceVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "performance-" + Guid.NewGuid().ToString("N") + ".wav");
        var window = new MainWindow(false);
        Application.Current.MainWindow = window;
        using var model = window.Model;
        try
        {
            using (var writer = new WaveFileWriter(fixture, new WaveFormat(48000, 16, 2)))
            {
                var block = new float[960];
                for (int frame = 0; frame < 2400; frame++)
                {
                    for (int i = 0; i < 480; i++)
                    {
                        double time = (frame * 480 + i) / 48000d;
                        float value = (float)(0.08 * (0.6 + 0.4 * Math.Sin(time * 3)) * Math.Sin(time * 220 * Math.PI * 2));
                        block[i * 2] = block[i * 2 + 1] = value;
                    }
                    writer.WriteSamples(block, 0, block.Length);
                }
            }
            window.Show();
            await Task.Delay(1200);
            while (model.IsBusy) await Task.Delay(50);
            if (model.DisplayTimerEnabled) throw new InvalidOperationException("Idle meter timer is still running.");
            var idle = await Measure("idle-visible");
            model.ShowOtherOutputDevices = true;
            model.OutputDevice = model.OutputDevices.FirstOrDefault(device => MicBridge.Audio.Devices.DeviceCatalog.IsStandardCableInput(device)) ?? model.OutputDevices.First();
            model.Microphone.Enabled = false; model.MixBalance = 0; model.Master.Muted = true;
            await model.UseLocalFileAsync(fixture); await model.ToggleSourcePlaybackAsync(); await Task.Delay(800);
            if (!model.DisplayTimerEnabled || model.SourceMeter <= 0) throw new InvalidOperationException("Playback and live meters did not start.");
            var playing = await Measure("local-playback-visible");
            window.WindowState = WindowState.Minimized;
            var minimized = await Measure("local-playback-minimized");
            await model.StopSourceAsync();
            if (model.DisplayTimerEnabled) throw new InvalidOperationException("The meter timer continued after stopping.");
            var stopped = await Measure("stopped-minimized");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Runtime = RuntimeInformation.FrameworkDescription, Processors = Environment.ProcessorCount,
                OutputMuted = true, MicrophoneCaptured = false, UserSettingsSaved = false, IdleTimerStopped = true, StoppedTimerStopped = true,
                Samples = new[] { idle, playing, minimized, stopped } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); if (File.Exists(fixture)) File.Delete(fixture); }

        static async Task<object> Measure(string phase)
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            double before = process.TotalProcessorTime.TotalMilliseconds;
            var watch = Stopwatch.StartNew();
            await Task.Delay(4000);
            process.Refresh();
            double cpu = process.TotalProcessorTime.TotalMilliseconds - before;
            return new { Phase = phase, Seconds = watch.Elapsed.TotalSeconds, CpuMilliseconds = cpu,
                CpuPercentOfOneCore = cpu / watch.Elapsed.TotalMilliseconds * 100,
                CpuPercentOfMachine = cpu / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
                ManagedMiB = GC.GetTotalMemory(false) / 1048576d };
        }
    }
}

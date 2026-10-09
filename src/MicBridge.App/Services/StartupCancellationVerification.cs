using System.Diagnostics;
using System.IO;
using MicBridge.App.Serialization;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using MicBridge.Audio.Compatibility;
using NAudio.CoreAudioApi;

namespace MicBridge.App.Services;

internal static class StartupCancellationVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        using var process = Process.GetCurrentProcess();
        using var target = Process.Start(new ProcessStartInfo(Platform.ProcessPath, "--qa-tone 500")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        var timings = new List<double>(); int cancelled = 0, active = 0, schedulingRegistrations = 0;
        try
        {
            await Task.Delay(500);
            var output = DeviceCatalog.GetDevices(DataFlow.Render).Single(DeviceCatalog.IsStandardCableInput);
            ResourceSnapshot? before = null;
            for (int n = -4; n < 24; n++)
            {
                if (n == 0) before = Snapshot();
                using var engine = new AudioEngine(); using var cancellation = new CancellationTokenSource();
                engine.Microphone.Enabled = false; engine.Master.Muted = true;
                Task startup = engine.StartAsync(new(output.Id, CaptureMode: ApplicationCaptureMode.Process, ProcessId: target.Id), cancellation.Token);
                int mode = (n + 4) % 4;
                if (mode == 3) { await startup; if (n >= 0) { active++; if (engine.ProcessCaptureUsesMultimediaScheduling) schedulingRegistrations++; } await Task.Delay(100); }
                else if (mode > 0) await Task.Delay(mode == 1 ? 2 : 20);
                var elapsed = Stopwatch.StartNew();
                if (mode < 2) cancellation.Cancel(); else engine.Dispose();
                try { await startup; }
                catch (OperationCanceledException) { if (n >= 0) cancelled++; }
                catch (ObjectDisposedException) when (mode >= 2) { if (n >= 0) cancelled++; }
                await engine.StopAsync(); engine.Dispose();
                if (n >= 0) timings.Add(elapsed.Elapsed.TotalMilliseconds);
                if (engine.IsRunning || engine.IsMicrophoneCapturing || engine.ProcessCaptureUsesMultimediaScheduling)
                    throw new InvalidOperationException("Startup cancellation left an audio route or multimedia scheduling registration running.");
                if (elapsed.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Startup cancellation waited too long.");
            }
            var immediate = Snapshot();
            await Task.Delay(500); // Let late native activation callbacks release their unused clients.
            var after = Snapshot();
            if (cancelled == 0 || active != 6) throw new InvalidOperationException("The verification did not exercise both cancelled and active process capture.");
            if (after.ManagedBytes - before!.ManagedBytes > 2 * 1024 * 1024 || after.Handles - before.Handles > 12)
                throw new InvalidOperationException($"Repeated cancellation retained resources: before={before}, immediate={immediate}, settled={after}.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, Cycles = 24, CancelledStartups = cancelled,
                FullyStartedProcessCaptures = active, MultimediaSchedulingRegistrations = schedulingRegistrations,
                MultimediaSchedulingReleasedAfterEachCycle = true, StopMilliseconds = timings, MaximumStopMilliseconds = timings.Max(),
                WarmupCycles = 4, Before = before, ImmediatelyAfter = immediate, After = after, CallbackDrainMilliseconds = 500,
                OutputMuted = true, RealMicrophoneCaptured = false, RecordingsWrittenToDisk = false,
                WindowsDefaultDevicesChanged = false, WindowsVolumeChanged = false, UserSettingsSaved = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (!target.HasExited) target.Kill(); }
        ResourceSnapshot Snapshot()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            return new(GC.GetTotalMemory(false), process.HandleCount, process.PrivateMemorySize64);
        }
    }
    private sealed record ResourceSnapshot(long ManagedBytes, int Handles, long PrivateBytes);
}

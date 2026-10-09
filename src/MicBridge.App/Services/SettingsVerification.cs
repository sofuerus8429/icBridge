using System.Diagnostics;
using System.IO;
using MicBridge.App.Serialization;

namespace MicBridge.App.Services;

internal static class SettingsVerification
{
    private const int Writers = 4, WritesPerWriter = 200;

    internal static void WriteFixture(string path, int worker)
    {
        if (worker < 0 || worker >= Writers) throw new ArgumentOutOfRangeException(nameof(worker));
        for (int i = 0; i < WritesPerWriter; i++) SettingsStore.SaveTo(path, Snapshot(worker, i));
    }

    internal static async Task RunAsync(string reportPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "settings-fixture-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "settings.json");
        var processes = new List<Process>();
        var stopwatch = Stopwatch.StartNew();
        int reads = 0;
        try
        {
            SettingsStore.SaveTo(path, Snapshot(0, 0));
            for (int i = 0; i < Writers; i++)
            {
                // Helpers are this executable, own only the F-drive fixture, and never open audio.
                var start = new ProcessStartInfo(Platform.ProcessPath)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = $"--qa-settings-writer \"{path}\" {i}"
                };
                processes.Add(Process.Start(start) ?? throw new IOException("Cannot start a settings verification helper."));
            }
            var exits = Task.WhenAll(processes.Select(Platform.WaitForExitAsync));
            while (!exits.IsCompleted)
            {
                CheckSnapshot(SettingsStore.LoadFrom(path)); reads++;
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Settings concurrency verification timed out.");
                await Task.Delay(2);
            }
            await exits;
            if (processes.Any(process => process.ExitCode != 0)) throw new IOException("A settings writer failed.");
            CheckSnapshot(SettingsStore.LoadFrom(path)); reads++;
            if (reads < 2 || Directory.GetFiles(root).Length != 1) throw new IOException("Settings snapshots or staging cleanup failed.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, Writers, WritesPerWriter, TotalWrites = Writers * WritesPerWriter, CompleteSnapshotsRead = reads,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds, StagingFilesRemaining = 0,
                UserSettingsSaved = false, AudioStarted = false, WindowsDefaultDevicesChanged = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
                finally { process.Dispose(); }
            }
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.GetFiles(root)) File.Delete(file);
                Directory.Delete(root);
            }
        }
    }

    private static AppSettings Snapshot(int worker, int iteration)
    {
        string identity = $"fixture|{worker}|{iteration}";
        return new AppSettings
        {
            OutputDeviceId = identity, SourceKind = worker % 2,
            Music = new ChannelSettings(iteration + 1, GainMode: true),
            Playlist = [identity, "音乐与人声 " + new string((char)('A' + worker), 8192)]
        };
    }

    private static void CheckSnapshot(AppSettings settings)
    {
        string[] identity = (settings.OutputDeviceId ?? "").Split('|');
        if (identity.Length != 3 || identity[0] != "fixture" || !int.TryParse(identity[1], out int worker) ||
            !int.TryParse(identity[2], out int iteration) || worker < 0 || worker >= Writers || iteration < 0 || iteration >= WritesPerWriter)
            throw new IOException("A reader observed a missing or incomplete settings identity.");
        var expected = Snapshot(worker, iteration);
        if (settings.SourceKind != expected.SourceKind || settings.Music != expected.Music ||
            !settings.Playlist.SequenceEqual(expected.Playlist)) throw new IOException("A reader observed fields from different settings versions.");
    }
}

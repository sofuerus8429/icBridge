using MicBridge.App.Services;

namespace MicBridge.Audio.Tests;

public sealed class AtomicSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "settings-test-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void UnicodeSettingsCreateTheirDirectoryAndReplaceThePreviousVersion()
    {
        AtomicSettingsFile.WriteAllText(SettingsPath, "{\"歌曲\":\"人声与音乐 🌉\"}");
        Assert.Equal("{\"歌曲\":\"人声与音乐 🌉\"}", AtomicSettingsFile.ReadAllText(SettingsPath));
        AtomicSettingsFile.WriteAllText(SettingsPath, "{\"volume\":300}");
        Assert.Equal("{\"volume\":300}", AtomicSettingsFile.ReadAllText(SettingsPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ConcurrentReadersOnlyObserveCompleteWriterSnapshots()
    {
        var versions = Enumerable.Range(0, 4).Select(i => $"{{\"writer\":{i},\"payload\":\"{new string((char)('a' + i), 32768)}\"}}").ToArray();
        AtomicSettingsFile.WriteAllText(SettingsPath, versions[0]);
        using var stop = new CancellationTokenSource();
        var beganReading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var reader = Task.Run(() =>
        {
            do
            {
                Assert.Contains(AtomicSettingsFile.ReadAllText(SettingsPath), versions);
                Interlocked.Increment(ref reads); beganReading.TrySetResult(true);
            } while (!stop.IsCancellationRequested);
        });
        await beganReading.Task;
        try
        {
            await Task.WhenAll(versions.Select(version => Task.Run(() =>
            {
                for (int i = 0; i < 40; i++) AtomicSettingsFile.WriteAllText(SettingsPath, version);
            })));
        }
        finally { stop.Cancel(); await reader; }
        Assert.True(reads > 1); Assert.Single(Directory.GetFiles(_directory));
        Assert.Contains(AtomicSettingsFile.ReadAllText(SettingsPath), versions);
    }

    [Fact]
    public async Task SimultaneousFirstSavesDoNotShareAStagingFile()
    {
        Directory.CreateDirectory(_directory);
        using var start = new ManualResetEventSlim(false);
        var versions = Enumerable.Range(0, 8).Select(i => $"{{\"first\":{i}}}").ToArray();
        var writes = versions.Select(version => Task.Run(() => { start.Wait(); AtomicSettingsFile.WriteAllText(SettingsPath, version); })).ToArray();
        start.Set(); await Task.WhenAll(writes);
        Assert.Contains(AtomicSettingsFile.ReadAllText(SettingsPath), versions);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void AFailedCommitPreservesTheLastConfigurationAndCleansItsStagingFile()
    {
        AtomicSettingsFile.WriteAllText(SettingsPath, "old settings");
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => AtomicSettingsFile.WriteAllText(SettingsPath, "new settings"));
        Assert.Equal("old settings", AtomicSettingsFile.ReadAllText(SettingsPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void AReaderKeepsItsSnapshotWhileTheNextConfigurationIsCommitted()
    {
        AtomicSettingsFile.WriteAllText(SettingsPath, "old settings");
        using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        AtomicSettingsFile.WriteAllText(SettingsPath, "new settings");
        using var reader = new StreamReader(stream);
        Assert.Equal("old settings", reader.ReadToEnd());
        Assert.Equal("new settings", AtomicSettingsFile.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CleanupAcceptsOnlyTheExactGeneratedTemporaryFilePattern()
    {
        Assert.True(AtomicSettingsFile.IsTemporaryName("settings.json." + Guid.NewGuid().ToString("N") + ".tmp", "settings.json"));
        foreach (string name in new[] { "settings.json.user-draft.tmp", "settings.json.tmp", "music.json." + Guid.NewGuid().ToString("N") + ".tmp",
            "settings.json." + new string('g', 32) + ".tmp", "settings.json." + Guid.NewGuid().ToString("D") + ".tmp" })
            Assert.False(AtomicSettingsFile.IsTemporaryName(name, "settings.json"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (string path in Directory.GetFiles(_directory)) File.Delete(path);
        Directory.Delete(_directory);
    }
}

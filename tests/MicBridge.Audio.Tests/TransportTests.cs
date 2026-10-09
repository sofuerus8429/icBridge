using MicBridge.Audio.Sources;
using NAudio.Wave;

namespace MicBridge.Audio.Tests;

public sealed class TransportTests : IDisposable
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, $"micbridge-test-{Guid.NewGuid():N}.wav");
    public TransportTests()
    {
        using var writer = new WaveFileWriter(_path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var samples = Enumerable.Range(0, 4800 * 2).Select(i => i < 2400 * 2 ? 0.1f : 0.3f).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
    }
    [Fact]
    public void PauseKeepsPositionAndResumeReadsFromTheSamePosition()
    {
        using var player = new FilePlaybackSource(); player.Load(_path); player.Play();
        var data = new float[960]; player.Read(data, 0, data.Length);
        TimeSpan position = player.Position; player.Pause(); player.Read(data, 0, data.Length);
        Assert.Equal(position, player.Position); Assert.All(data, x => Assert.Equal(0, x));
        player.Play(); player.Read(data, 0, data.Length); Assert.True(player.Position > position);
    }
    [Fact]
    public void SeekChangesDecodedAudioAndStopRewinds()
    {
        using var player = new FilePlaybackSource(); player.Load(_path); player.Play();
        player.Seek(TimeSpan.FromMilliseconds(60)); var data = new float[960]; player.Read(data, 0, data.Length);
        Assert.All(data, x => Assert.Equal(0.3f, x)); player.Stop();
        Assert.Equal(TimeSpan.Zero, player.Position); Assert.False(player.IsPlaying);
        player.Play(); player.Read(data, 0, data.Length); Assert.All(data, x => Assert.Equal(0.1f, x));
    }
    [Fact]
    public void EndOfFilePadsSilenceAndLoopCrossesTheBoundaryWithoutAGap()
    {
        using var player = new FilePlaybackSource(); player.Load(_path); player.Play();
        var data = new float[19200]; Assert.Equal(data.Length, player.Read(data, 0, data.Length));
        Assert.False(player.IsPlaying); Assert.All(data.Skip(9600), x => Assert.Equal(0, x));
        player.Loop = true; player.Stop(); player.Play(); player.Read(data, 0, data.Length);
        Assert.True(player.IsPlaying); Assert.All(data, x => Assert.True(x > 0));
    }
    [Fact]
    public void UnloadClearsTransportAndReleasesTheFile()
    {
        using var player = new FilePlaybackSource(); player.Load(_path); player.Play(); player.Unload();
        Assert.Null(player.FilePath); Assert.False(player.IsPlaying);
        Assert.Equal(TimeSpan.Zero, player.Duration); Assert.Equal(TimeSpan.Zero, player.Position);
        using var exclusive = File.Open(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }
    [Fact]
    public void FailedLoadPreservesTheExistingTrack()
    {
        using var player = new FilePlaybackSource(); player.Load(_path);
        Assert.Throws<FileNotFoundException>(() => player.Load(_path + ".missing.wav"));
        Assert.Equal(_path, player.FilePath); player.Play(); Assert.True(player.IsPlaying);
    }
    public void Dispose() => File.Delete(_path);
}

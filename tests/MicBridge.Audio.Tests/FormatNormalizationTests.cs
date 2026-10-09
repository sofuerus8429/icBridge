using MicBridge.Audio.Mixing;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.Audio.Tests;

public sealed class FormatNormalizationTests
{
    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(88200)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void MonoNormalizationPreservesThePreviousStereoWaveformAndEndOfStream(int sampleRate)
    {
        var random = new Random(17);
        var samples = Enumerable.Range(0, sampleRate / 5 + 11).Select(i => (float)(random.NextDouble() * 1.6 - 0.8)).ToArray();
        ISampleProvider previous = new MonoToStereoSampleProvider(new Samples(samples, sampleRate));
        if (sampleRate != AudioFormat.SampleRate) previous = new WdlResamplingSampleProvider(previous, AudioFormat.SampleRate);
        var normalized = AudioFormat.Normalize(new Samples(samples, sampleRate));
        Assert.Equal(AudioFormat.SampleRate, normalized.WaveFormat.SampleRate);
        Assert.Equal(2, normalized.WaveFormat.Channels);
        int[] requests = [2, 192, 960, 4096, 128]; bool ended = false;
        for (int block = 0; block < 2000; block++)
        {
            int count = requests[block % requests.Length];
            var expected = new float[count];
            var actual = Enumerable.Repeat(97f, count + 6).ToArray();
            // The output mixer reads the legacy resampler from offset zero. Compare that
            // waveform, and separately check the new mono wrapper's nonzero offsets.
            int expectedRead = previous.Read(expected, 0, count), actualRead = normalized.Read(actual, 3, count);
            Assert.Equal(expectedRead, actualRead);
            Assert.Equal(expected.Take(expectedRead), actual.Skip(3).Take(actualRead));
            Assert.All(actual.Take(3).Concat(actual.Skip(3 + actualRead)), value => Assert.Equal(97f, value));
            for (int frame = 3; frame < 3 + actualRead; frame += 2) Assert.Equal(actual[frame], actual[frame + 1]);
            if (actualRead == 0) { ended = true; break; }
        }
        Assert.True(ended, "The normalized finite source must reach end-of-stream.");
    }

    [Fact]
    public void AnAlreadyNormalizedStereoSourceKeepsItsExistingProvider()
    {
        var source = new SignalGenerator(AudioFormat.SampleRate, 2);
        Assert.Same(source, AudioFormat.Normalize(source));
    }

    private sealed class Samples(float[] data, int sampleRate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, read); _position += read;
            return read;
        }
    }
}

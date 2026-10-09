using MicBridge.Audio.Mixing;
using NAudio.Wave;

namespace MicBridge.Audio.Tests;

public sealed class LimiterTests
{
    [Fact]
    public void QuietAudioPassesThroughExactlyAndStillReportsItsPeak()
    {
        float[] input = [0.1234567f, -0.87f, PeakLimiter.Ceiling, -PeakLimiter.Ceiling];
        var limiter = new PeakLimiter(new ArraySource(input, 2));
        var output = Enumerable.Repeat(99f, 12).ToArray();
        Assert.Equal(input.Length, limiter.Read(output, 3, 6));
        Assert.Equal(input, output.Skip(3).Take(input.Length));
        Assert.Equal(PeakLimiter.Ceiling, limiter.Peak); Assert.Equal(0, limiter.ReductionDb);
        Assert.Equal(99, output[2]); Assert.Equal(99, output[7]);
    }
    [Fact]
    public void ATransientAtTheEndOfAQuietBlockIsLimitedInTheSameRead()
    {
        var input = Enumerable.Repeat(0.2f, 960).ToArray(); input[958] = 4; input[959] = -2;
        var limiter = new PeakLimiter(new ArraySource(input, 2)); var output = new float[960];
        limiter.Read(output, 0, output.Length);
        Assert.All(output.Take(958), value => Assert.Equal(0.2f, value));
        Assert.InRange(output[958], PeakLimiter.Ceiling - 0.00001f, PeakLimiter.Ceiling);
        Assert.Equal(-output[958] / 2, output[959], 6); Assert.True(limiter.ReductionDb > 10);
    }
    [Fact]
    public void CorruptQuietSamplesAreRemovedWithoutDamagingFiniteSamples()
    {
        var limiter = new PeakLimiter(new ArraySource([0.25f, float.NaN, float.PositiveInfinity, -0.4f], 2));
        var output = new float[4]; limiter.Read(output, 0, output.Length);
        Assert.Equal(new[] { 0.25f, 0, 0, -0.4f }, output); Assert.Equal(0.4f, limiter.Peak);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void QuietLoudAndReleasingBlocksMatchThePreviousProtection(int channels)
    {
        var random = new Random(2817); var input = new float[48000 * channels];
        for (int i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 0.6 - 0.3);
        for (int i = input.Length / 8; i < input.Length / 3; i += 677) input[i] = i % 2 == 0 ? 4 : -2;
        input[111] = float.NaN; input[input.Length / 2] = float.NegativeInfinity;
        var optimized = new PeakLimiter(new ArraySource(input, channels));
        var reference = new ReferenceLimiter(new ArraySource(input, channels));
        var actual = new float[969]; var expected = new float[969];
        for (int n = 0; ; n++)
        {
            int count = n % 4 == 0 ? 953 : 960;
            TestSamples.Fill(actual, 99); TestSamples.Fill(expected, 99);
            int read = optimized.Read(actual, 3, count);
            Assert.Equal(reference.Read(expected, 3, count), read);
            for (int i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 0.00001f);
            Assert.InRange(Math.Abs(optimized.Peak - reference.Peak), 0, 0.00001f);
            Assert.InRange(Math.Abs(optimized.ReductionDb - reference.ReductionDb), 0, 0.001f);
            if (read == 0) break;
        }
    }
    private sealed class ArraySource(float[] samples, int channels) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, read); _position += read; return read;
        }
    }
    // Frozen pre-optimization behavior: tests compare complete output blocks,
    // release envelope, stereo linking, non-finite recovery and partial reads.
    private sealed class ReferenceLimiter(ISampleProvider source) : ISampleProvider
    {
        private float _gain = 1;
        private readonly float _release = (float)Math.Exp(-1f / (source.WaveFormat.SampleRate * 0.1f));
        public WaveFormat WaveFormat => source.WaveFormat;
        public float Peak { get; private set; }
        public float ReductionDb { get; private set; }
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count); float peak = 0, minimumGain = 1;
            for (int i = 0; i < read; i += WaveFormat.Channels)
            {
                float framePeak = 0;
                for (int c = 0; c < WaveFormat.Channels && i + c < read; c++)
                {
                    int index = offset + i + c;
                    if (float.IsNaN(buffer[index]) || float.IsInfinity(buffer[index])) buffer[index] = 0;
                    framePeak = Math.Max(framePeak, Math.Abs(buffer[index]));
                }
                float wanted = framePeak > PeakLimiter.Ceiling ? PeakLimiter.Ceiling / framePeak : 1;
                _gain = wanted < _gain ? wanted : Math.Min(wanted, 1 - (1 - _gain) * _release);
                minimumGain = Math.Min(minimumGain, _gain);
                for (int c = 0; c < WaveFormat.Channels && i + c < read; c++)
                {
                    int index = offset + i + c;
                    buffer[index] = Math.Max(-PeakLimiter.Ceiling, Math.Min(PeakLimiter.Ceiling, buffer[index] * _gain));
                    peak = Math.Max(peak, Math.Abs(buffer[index]));
                }
            }
            Peak = peak; ReductionDb = -20 * (float)Math.Log10(Math.Max(minimumGain, 0.000001f)); return read;
        }
    }
}

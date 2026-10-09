using MicBridge.Audio.Mixing;
using MicBridge.Audio.Sources;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.Audio.Tests;

public sealed class MixingTests
{
    [Fact]
    public void ShortReadsClearOnlyTheirTailAndMuteStillAdvancesTheSource()
    {
        var source = new ShortSource(); var channel = new MixerChannel { Muted = true }; channel.SetSource(source);
        float[] data = Enumerable.Repeat(99f, 968).ToArray();
        Assert.Equal(960, channel.Read(data, 2, 960));
        Assert.Equal(1, source.Reads); Assert.Equal(0.2f, channel.InputPeak);
        Assert.All(data.Skip(2).Take(960), x => Assert.Equal(0, x));
        Assert.Equal(99, data[1]); Assert.Equal(99, data[962]);
        channel.Muted = false; channel.Read(data, 2, 960);
        // The 5ms ramp advances through the silent tail, too.
        channel.Read(data, 2, 960); Assert.InRange(data[2], 0.199f, 0.201f);
        channel.SetSource(null); channel.Read(data, 2, 960);
        Assert.Equal(0, channel.Peak); Assert.Equal(0, channel.InputPeak);
        Assert.Equal(99, data[1]); Assert.Equal(99, data[962]);
    }
    [Fact]
    public void GainChangesRemainStereoLinkedAndCorruptSamplesRecover()
    {
        var channel = new MixerChannel(); channel.SetSource(new ChannelPatternSource([0.2f, 0.1f]));
        var data = new float[960]; channel.Read(data, 0, data.Length); channel.GainDb = 6.0206f;
        channel.Read(data, 0, data.Length);
        for (int i = 0; i < data.Length; i += 2) Assert.Equal(data[i] / 2, data[i + 1], 5);
        Assert.InRange(data[data.Length - 2], 0.399f, 0.401f);
        channel.SetSource(new ChannelPatternSource([float.NaN, float.PositiveInfinity]));
        channel.Read(data, 0, data.Length); Assert.All(data, x => Assert.Equal(0, x));
    }
    private sealed class ShortSource : ISampleProvider
    {
        public int Reads { get; private set; }
        public WaveFormat WaveFormat => AudioFormat.MixFormat;
        public int Read(float[] buffer, int offset, int count) { Reads++; TestSamples.Fill(buffer, 0.2f, offset, 4); return 4; }
    }
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1, 1)]
    [InlineData(1.5f, 1.5f)]
    [InlineData(3, 3)]
    [InlineData(10, 3)]
    public void PercentageGainProducesTheActualAmplitudeAndClampsAtThree(float requested, float expected)
    {
        var channel = new MixerChannel { Volume = requested };
        channel.SetSource(new ConstantSource(0.1f));
        var data = new float[4096]; channel.Read(data, 0, data.Length);
        Assert.Equal(expected, channel.Volume);
        Assert.InRange(data[data.Length - 1], expected * 0.1f - 0.00001f, expected * 0.1f + 0.00001f);
        Assert.Equal(0.1f, channel.InputPeak);
    }

    [Fact]
    public void MutedFaderKeepsInputActivityButOutputsSilence()
    {
        var channel = new MixerChannel { Muted = true, Volume = 3 };
        channel.SetSource(new ConstantSource(0.2f));
        var data = new float[960]; channel.Read(data, 0, data.Length);
        Assert.Equal(0.2f, channel.InputPeak); Assert.Equal(0, channel.Peak);
        Assert.All(data, value => Assert.Equal(0, value));
        channel.SetSource(null); Assert.Equal(0, channel.InputPeak);
    }

    [Fact]
    public void BoostedMusicAndMicrophoneStillRespectTheOutputCeiling()
    {
        var music = new MixerChannel { Volume = 3 }; music.SetSource(new ConstantSource(0.5f));
        var mic = new MixerChannel { Volume = 3 }; mic.SetSource(new ConstantSource(0.25f));
        var mix = new MixingSampleProvider(AudioFormat.MixFormat) { ReadFully = true };
        mix.AddMixerInput(music); mix.AddMixerInput(mic);
        var limiter = new PeakLimiter(mix); var data = new float[4096]; limiter.Read(data, 0, data.Length);
        Assert.All(data, value => Assert.InRange(value, 0, PeakLimiter.Ceiling));
        Assert.InRange(data[data.Length - 1], PeakLimiter.Ceiling - 0.0001f, PeakLimiter.Ceiling);
    }

    [Theory]
    [InlineData(0, 0.1f)]
    [InlineData(25, 0.2f)]
    [InlineData(50, 0.3f)]
    [InlineData(75, 0.25f)]
    [InlineData(100, 0.2f)]
    public void MusicVoiceBalanceChangesTheMixWithoutOverwritingChannelControls(float position, float expected)
    {
        using var engine = new AudioEngine();
        engine.Music.Volume = 0.5f; engine.Music.SetSource(new ConstantSource(0.2f));
        engine.Microphone.GainDb = 6.0206f; engine.Microphone.SetSource(new ConstantSource(0.1f));
        engine.VoiceBalancePercent = position;
        var mixer = new MixingSampleProvider(AudioFormat.MixFormat) { ReadFully = true };
        mixer.AddMixerInput(engine.Music); mixer.AddMixerInput(engine.Microphone);
        var samples = new float[4096]; mixer.Read(samples, 0, samples.Length);
        Assert.InRange(samples[samples.Length - 1], expected - 0.001f, expected + 0.001f);
        Assert.Equal(0.5f, engine.Music.Volume); Assert.Equal(6.0206f, engine.Microphone.GainDb);
        Assert.Equal(engine.Music.MixWeight, engine.Application.MixWeight);
    }

    [Fact]
    public void BalanceCanRecoverFromNonFiniteInputAndZeroWeightIsImmediatelySilent()
    {
        using var engine = new AudioEngine(); engine.Music.SetSource(new ConstantSource(0.2f));
        var samples = new float[960]; engine.Music.Read(samples, 0, samples.Length);
        engine.VoiceBalancePercent = 100; engine.Music.Read(samples, 0, samples.Length);
        Assert.All(samples, value => Assert.Equal(0, value));
        engine.VoiceBalancePercent = float.NaN; engine.Music.Read(samples, 0, samples.Length);
        Assert.Equal(50, engine.VoiceBalancePercent); Assert.InRange(samples[samples.Length - 1], 0.199f, 0.201f);
    }

    [Fact]
    public void MuteIsSilentImmediatelyAndInvalidGainCanRecover()
    {
        var channel = new MixerChannel(); channel.SetSource(new ConstantSource(0.2f));
        var data = new float[960]; channel.Read(data, 0, data.Length); channel.Muted = true;
        channel.Read(data, 0, data.Length); Assert.All(data, x => Assert.Equal(0, x));
        channel.Muted = false; channel.Volume = float.NaN; channel.GainDb = float.PositiveInfinity;
        channel.Read(data, 0, data.Length); Assert.All(data, x => Assert.Equal(0, x));
        channel.Volume = 1; channel.Read(data, 0, data.Length); Assert.InRange(data[data.Length - 1], 0.199f, 0.201f);
    }
    [Fact]
    public void SourcesAreSummedWithIndependentVolumeAndGain()
    {
        var music = new MixerChannel { Volume = 0.5f };
        music.SetSource(new ConstantSource(0.2f));
        var mic = new MixerChannel { GainDb = 6.0206f };
        mic.SetSource(new ConstantSource(0.1f));
        var mixer = new MixingSampleProvider(AudioFormat.MixFormat) { ReadFully = true };
        mixer.AddMixerInput(music); mixer.AddMixerInput(mic);
        var samples = new float[4096];
        Assert.Equal(samples.Length, mixer.Read(samples, 0, samples.Length));
        Assert.InRange(samples[samples.Length - 1], 0.299f, 0.301f);
        mic.Muted = true;
        mixer.Read(samples, 0, samples.Length);
        Assert.InRange(samples[samples.Length - 1], 0.099f, 0.101f);
    }

    [Fact]
    public void RemovedOrDisabledInputProducesSilenceWithoutEndingTheMix()
    {
        var channel = new MixerChannel(); channel.SetSource(new ConstantSource(0.8f));
        var samples = new float[1024]; channel.Enabled = false;
        channel.Read(samples, 0, samples.Length);
        Assert.Equal(0, samples[samples.Length - 1]);
        channel.SetSource(null);
        Assert.Equal(samples.Length, channel.Read(samples, 0, samples.Length));
        Assert.All(samples, x => Assert.Equal(0, x));
    }

    [Fact]
    public void MonoAndDifferentSampleRatesNormalizeToStereo48Khz()
    {
        var source = AudioFormat.Normalize(new ConstantSource(0.25f, 44100, 1));
        Assert.Equal(48000, source.WaveFormat.SampleRate); Assert.Equal(2, source.WaveFormat.Channels);
        var data = new float[9600]; Assert.Equal(data.Length, source.Read(data, 0, data.Length));
        for (int i = 2000; i < data.Length; i += 2) Assert.Equal(data[i], data[i + 1]);
        Assert.InRange(data[data.Length - 1], 0.24f, 0.26f);
    }

    [Fact]
    public void SurroundChannelsContributeToDownmix()
    {
        var source = AudioFormat.Normalize(new ChannelPatternSource([0, 0, 0.6f, 0, 0, 0]));
        var data = new float[20]; source.Read(data, 0, 20);
        Assert.All(data, x => Assert.InRange(x, 0.099f, 0.101f));
    }

    [Fact]
    public void LimiterBoundsLoudAndNonFiniteSamplesAndPreservesStereoRatio()
    {
        var limiter = new PeakLimiter(new ChannelPatternSource([4, -2]));
        var samples = new float[1028]; TestSamples.Fill(samples, 99);
        Assert.Equal(1024, limiter.Read(samples, 2, 1024));
        Assert.Equal(99, samples[0]); Assert.Equal(99, samples[samples.Length - 1]);
        for (int i = 2; i < 1026; i += 2)
        {
            Assert.InRange(samples[i], -PeakLimiter.Ceiling, PeakLimiter.Ceiling);
            Assert.Equal(-samples[i] / 2, samples[i + 1], 5);
        }
        Assert.True(limiter.ReductionDb > 10);
        var corrupt = new PeakLimiter(new ChannelPatternSource([float.NaN, float.PositiveInfinity]));
        corrupt.Read(samples, 0, samples.Length); Assert.All(samples, x => Assert.Equal(0, x));
    }

    [Fact]
    public void LimiterReleasesAfterAnOverload()
    {
        var input = new MutableSource { Value = 4 };
        var limiter = new PeakLimiter(input); var block = new float[96000];
        limiter.Read(block, 0, 960); input.Value = 0.2f;
        limiter.Read(block, 0, block.Length);
        Assert.InRange(block[block.Length - 1], 0.199f, 0.201f);
    }

    [Fact]
    public void CaptureQueueDropsStaleAudioWhenProducerRunsAhead()
    {
        var buffer = new CaptureBuffer(AudioFormat.MixFormat);
        byte[] packet = new byte[48000 * 2 * 4 / 10];
        for (int i = 0; i < 100; i++) buffer.Write(packet, packet.Length);
        Assert.InRange(buffer.Provider.BufferedDuration.TotalMilliseconds, 0, 300);
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("Voicemeeter Input", true)]
    [InlineData("扬声器 (Realtek(R) Audio)", false)]
    public void RecognizesSupportedVirtualDeviceNames(string name, bool expected) =>
        Assert.Equal(expected, Devices.DeviceCatalog.IsVirtualName(name));

    private sealed class ConstantSource(float value, int rate = 48000, int channels = 2) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public int Read(float[] buffer, int offset, int count) { TestSamples.Fill(buffer, value, offset, count); return count; }
    }
    private sealed class ChannelPatternSource(float[] pattern) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, pattern.Length);
        public int Read(float[] buffer, int offset, int count) { for (int i = 0; i < count; i++) buffer[offset + i] = pattern[i % pattern.Length]; return count; }
    }
    private sealed class MutableSource : ISampleProvider
    {
        public float Value { get; set; }
        public WaveFormat WaveFormat => AudioFormat.MixFormat;
        public int Read(float[] buffer, int offset, int count) { TestSamples.Fill(buffer, Value, offset, count); return count; }
    }
}

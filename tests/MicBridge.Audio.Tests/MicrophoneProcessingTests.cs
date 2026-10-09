using MicBridge.Audio.Mixing;
using NAudio.Wave;

namespace MicBridge.Audio.Tests;

public sealed class MicrophoneProcessingTests
{
    [Fact]
    public void EnablingHighPassAfterBypassDoesNotCreateADcStep()
    {
        var options = new MicrophoneProcessingOptions();
        var processor = new MicrophoneProcessor(new StereoSource(0.2f, -0.1f), options);
        var samples = new float[960]; processor.Read(samples, 0, samples.Length);
        options.HighPassEnabled = true; processor.Read(samples, 0, samples.Length);
        Assert.All(samples, sample => Assert.InRange(Math.Abs(sample), 0, 0.00001f));
    }
    [Fact]
    public void GateThresholdChangesAreAppliedWithoutRecreatingTheProcessor()
    {
        var options = new MicrophoneProcessingOptions { GateEnabled = true, ThresholdDb = -80 };
        var processor = new MicrophoneProcessor(new StereoSource(0.001f, 0.0005f), options);
        var samples = new float[96000]; processor.Read(samples, 0, samples.Length);
        Assert.Equal(0.001f, samples[samples.Length - 2]);
        options.ThresholdDb = -20; processor.Read(samples, 0, samples.Length);
        Assert.InRange(Math.Abs(samples[samples.Length - 2]), 0, 0.0000001f);
        processor.Read(samples, 0, 24000); Assert.Equal(0, samples[23998]);
        options.GateEnabled = false; processor.Read(samples, 0, samples.Length);
        Assert.Equal(0.001f, samples[samples.Length - 2]);
    }
    [Fact]
    public void DisabledProcessingPreservesSamplesAndReadOffset()
    {
        var processor = new MicrophoneProcessor(new StereoSource(0.2f, -0.1f), new());
        float[] samples = Enumerable.Repeat(9f, 1004).ToArray();
        Assert.Equal(1000, processor.Read(samples, 2, 1000));
        Assert.Equal(9, samples[0]); Assert.Equal(9, samples[samples.Length - 1]);
        for (int i = 2; i < 1002; i += 2) { Assert.Equal(0.2f, samples[i]); Assert.Equal(-0.1f, samples[i + 1]); }
    }

    [Fact]
    public void GateSuppressesQuietBackgroundAndReopensForSpeechWithStereoLinkedGain()
    {
        var source = new StereoSource(0.001f, 0.0005f);
        var options = new MicrophoneProcessingOptions { GateEnabled = true, ThresholdDb = -50 };
        var processor = new MicrophoneProcessor(source, options);
        var samples = new float[96000]; processor.Read(samples, 0, samples.Length);
        Assert.Equal(0, samples[samples.Length - 1]); Assert.Equal(0, samples[samples.Length - 2]);
        source.Left = 0.1f; source.Right = 0.05f;
        processor.Read(samples, 0, 4800);
        Assert.InRange(samples[4798], 0.099f, 0.101f);
        Assert.Equal(samples[4798] / 2, samples[4799]);
        source.Left = 0.001f; source.Right = 0.0005f;
        processor.Read(samples, 0, 960); Assert.InRange(samples[958], 0.00099f, 0.00101f);
        processor.Read(samples, 0, samples.Length); Assert.InRange(Math.Abs(samples[samples.Length - 1]), 0, 0.00000001f);
        processor.Read(samples, 0, 24000); Assert.Equal(0, samples[23999]);
        options.GateEnabled = false; processor.Read(samples, 0, 960);
        Assert.Equal(0.001f, samples[958]);
    }

    [Fact]
    public void HighPassAttenuatesDcAndCanBeDisabledDuringRead()
    {
        var options = new MicrophoneProcessingOptions { HighPassEnabled = true };
        var processor = new MicrophoneProcessor(new StereoSource(0.2f, -0.1f), options);
        var samples = new float[48000]; processor.Read(samples, 0, samples.Length);
        Assert.InRange(Math.Abs(samples[samples.Length - 2]), 0, 0.00001f);
        Assert.InRange(Math.Abs(samples[samples.Length - 1]), 0, 0.00001f);
        options.HighPassEnabled = false; processor.Read(samples, 0, 1000);
        Assert.Equal(0.2f, samples[998]); Assert.Equal(-0.1f, samples[999]);
    }

    [Fact]
    public void NonFiniteInputAndThresholdRecoverWithoutPoisoningTheProcessor()
    {
        var source = new StereoSource(float.NaN, float.PositiveInfinity);
        var options = new MicrophoneProcessingOptions { ThresholdDb = float.NaN };
        Assert.Equal(-50, options.ThresholdDb);
        options.ThresholdDb = -100; Assert.Equal(-80, options.ThresholdDb);
        options.ThresholdDb = 10; Assert.Equal(-20, options.ThresholdDb);
        var processor = new MicrophoneProcessor(source, options);
        var samples = new float[960]; processor.Read(samples, 0, samples.Length);
        Assert.All(samples, value => Assert.Equal(0, value));
        source.Left = source.Right = 0.2f; processor.Read(samples, 0, samples.Length);
        Assert.All(samples, value => Assert.Equal(0.2f, value));
    }

    private sealed class StereoSource(float left, float right) : ISampleProvider
    {
        public float Left { get; set; } = left;
        public float Right { get; set; } = right;
        public WaveFormat WaveFormat => AudioFormat.MixFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = i % 2 == 0 ? Left : Right;
            return count;
        }
    }
}

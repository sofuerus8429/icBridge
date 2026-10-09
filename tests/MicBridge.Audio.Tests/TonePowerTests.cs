using MicBridge.Audio.Devices;

namespace MicBridge.Audio.Tests;

public sealed class TonePowerTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void DetectsToneAtNativeRateAndMatchesDirectFrequencyProjection(int rate)
    {
        var meter = new TonePowerMeter(rate, 733);
        double real = 0, imaginary = 0;
        for (int n = 0; n < rate / 2; n++)
        {
            float sample = (float)(0.01 * Math.Sin(2 * Math.PI * 733 * n / rate) + 0.02 * Math.Sin(2 * Math.PI * 900 * n / rate));
            meter.Add(sample); double angle = 2 * Math.PI * 733 * n / rate;
            real += sample * Math.Cos(angle); imaginary += sample * Math.Sin(angle);
        }
        double expected = (real * real + imaginary * imaginary) / ((double)meter.Samples * meter.Samples);
        Assert.InRange(meter.Power, expected * 0.99999, expected * 1.00001);
        Assert.InRange(meter.Power, 0.000024, 0.000026);
    }
    [Fact]
    public void SilenceCorruptSamplesAndResetDoNotPoisonLaterMeasurements()
    {
        var meter = new TonePowerMeter(48000, 733);
        meter.Add(float.NaN); meter.Add(float.PositiveInfinity); Assert.Equal(0, meter.Power);
        meter.Reset(); Assert.Equal(0, meter.Samples); Assert.Equal(0, meter.Power);
        for (int n = 0; n < 24000; n++) meter.Add((float)(0.01 * Math.Sin(2 * Math.PI * 733 * n / 48000)));
        Assert.InRange(meter.Power, 0.000024, 0.000026);
    }
    [Theory]
    [InlineData(0, 733)]
    [InlineData(48000, 0)]
    [InlineData(48000, 25000)]
    [InlineData(48000, double.NaN)]
    public void RejectsInvalidMeasurementFrequencies(int rate, double frequency) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TonePowerMeter(rate, frequency));
}

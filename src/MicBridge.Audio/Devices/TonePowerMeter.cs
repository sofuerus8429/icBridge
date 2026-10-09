namespace MicBridge.Audio.Devices;

/// <summary>Streaming single-frequency measurement at the capture device's native sample rate.</summary>
public sealed class TonePowerMeter
{
    private readonly double _coefficient;
    private double _previous, _older;
    private long _samples;
    public TonePowerMeter(int sampleRate, double frequency)
    {
        if (sampleRate <= 0 || !Platform.IsFinite(frequency) || frequency <= 0 || frequency >= sampleRate / 2d)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        _coefficient = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
    }
    public long Samples => _samples;
    public double Power => _samples == 0 ? 0 : Math.Max(0, _previous * _previous + _older * _older - _coefficient * _previous * _older) / ((double)_samples * _samples);
    public void Add(float sample)
    {
        double next = (Platform.IsFinite(sample) ? sample : 0) + _coefficient * _previous - _older;
        _older = _previous; _previous = next; _samples++;
    }
    public void Reset() { _previous = _older = 0; _samples = 0; }
}

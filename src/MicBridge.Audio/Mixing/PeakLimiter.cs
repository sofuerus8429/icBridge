using NAudio.Wave;

namespace MicBridge.Audio.Mixing;

/// <summary>Stereo-linked instantaneous attenuation with a 100 ms release, plus a hard safety ceiling.</summary>
public sealed class PeakLimiter(ISampleProvider source) : ISampleProvider
{
    public const float Ceiling = 0.97f;
    private float _gain = 1, _peak, _reductionDb;
    private readonly float _release = MathF.Exp(-1f / (source.WaveFormat.SampleRate * 0.1f));
    public WaveFormat WaveFormat => source.WaveFormat;
    public float Peak => Volatile.Read(ref _peak);
    public float ReductionDb => Volatile.Read(ref _reductionDb);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        if (_gain == 1 && TryPassQuiet(buffer, offset, read)) return read;
        if (WaveFormat.Channels == 2) ProcessStereoLimited(buffer, offset, read);
        else ProcessLimited(buffer, offset, read);
        return read;
    }
    private bool TryPassQuiet(float[] buffer, int offset, int read)
    {
        float peak = 0;
        int end = offset + read;
        for (int position = offset; position < end; position++)
        {
            ref float sample = ref buffer[position];
            if (!Platform.IsFinite(sample)) sample = 0;
            float absolute = Math.Abs(sample);
            if (absolute > Ceiling) return false;
            peak = Math.Max(peak, absolute);
        }
        // Unity gain needs no multiplication, clipping or release math.
        // A loud sample instead falls through in this same audio block.
        Volatile.Write(ref _peak, peak); Volatile.Write(ref _reductionDb, 0);
        return true;
    }
    private void ProcessLimited(float[] buffer, int offset, int read)
    {
        float peak = 0, minimumGain = 1, gain = _gain;
        int channels = WaveFormat.Channels;
        for (int i = 0; i < read; i += channels)
        {
            float framePeak = 0;
            for (int c = 0; c < channels && i + c < read; c++)
            {
                ref float sample = ref buffer[offset + i + c];
                if (!Platform.IsFinite(sample)) sample = 0;
                framePeak = Math.Max(framePeak, Math.Abs(sample));
            }
            float wanted = framePeak > Ceiling ? Ceiling / framePeak : 1;
            gain = wanted < gain ? wanted : Math.Min(wanted, 1 - (1 - gain) * _release);
            minimumGain = Math.Min(minimumGain, gain);
            for (int c = 0; c < channels && i + c < read; c++)
            {
                int index = offset + i + c;
                buffer[index] = Platform.Clamp(buffer[index] * gain, -Ceiling, Ceiling);
                peak = Math.Max(peak, Math.Abs(buffer[index]));
            }
        }
        _gain = gain;
        Volatile.Write(ref _peak, peak);
        Volatile.Write(ref _reductionDb, -20 * MathF.Log10(Math.Max(minimumGain, 0.000001f)));
    }
    private void ProcessStereoLimited(float[] buffer, int offset, int read)
    {
        float gain = _gain, peak = 0, minimumGain = 1;
        int end = offset + read, position = offset;
        for (; position + 1 < end; position += 2)
        {
            float left = buffer[position], right = buffer[position + 1];
            if (!Platform.IsFinite(left)) left = 0;
            if (!Platform.IsFinite(right)) right = 0;
            float framePeak = Math.Max(Math.Abs(left), Math.Abs(right));
            float wanted = framePeak > Ceiling ? Ceiling / framePeak : 1;
            gain = wanted < gain ? wanted : Math.Min(wanted, 1 - (1 - gain) * _release);
            minimumGain = Math.Min(minimumGain, gain);
            left = Platform.Clamp(left * gain, -Ceiling, Ceiling);
            right = Platform.Clamp(right * gain, -Ceiling, Ceiling);
            buffer[position] = left; buffer[position + 1] = right;
            peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
        }
        if (position < end)
        {
            float value = buffer[position];
            if (!Platform.IsFinite(value)) value = 0;
            float wanted = Math.Abs(value) > Ceiling ? Ceiling / Math.Abs(value) : 1;
            gain = wanted < gain ? wanted : Math.Min(wanted, 1 - (1 - gain) * _release);
            minimumGain = Math.Min(minimumGain, gain);
            value = Platform.Clamp(value * gain, -Ceiling, Ceiling);
            buffer[position] = value; peak = Math.Max(peak, Math.Abs(value));
        }
        _gain = gain;
        Volatile.Write(ref _peak, peak);
        Volatile.Write(ref _reductionDb, -20 * MathF.Log10(Math.Max(minimumGain, 0.000001f)));
    }
}

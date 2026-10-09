using NAudio.Wave;

namespace MicBridge.Audio.Mixing;

public sealed class MicrophoneProcessingOptions
{
    private int _gateEnabled, _highPassEnabled;
    private float _thresholdDb = -50;
    public bool GateEnabled { get => Volatile.Read(ref _gateEnabled) != 0; set => Volatile.Write(ref _gateEnabled, value ? 1 : 0); }
    public bool HighPassEnabled { get => Volatile.Read(ref _highPassEnabled) != 0; set => Volatile.Write(ref _highPassEnabled, value ? 1 : 0); }
    public float ThresholdDb { get => Volatile.Read(ref _thresholdDb); set => Volatile.Write(ref _thresholdDb, Platform.IsFinite(value) ? Platform.Clamp(value, -80, -20) : -50); }
}

/// <summary>Optional 80 Hz high-pass and a stereo-linked gate, before the microphone fader.</summary>
public sealed class MicrophoneProcessor(ISampleProvider source, MicrophoneProcessingOptions options) : ISampleProvider
{
    private readonly float[] _previousInput = new float[2], _previousOutput = new float[2];
    private readonly float _highPass = MathF.Exp(-2 * MathF.PI * 80 / AudioFormat.SampleRate);
    private readonly float _attack = MathF.Exp(-1f / (AudioFormat.SampleRate * 0.005f));
    private readonly float _release = MathF.Exp(-1f / (AudioFormat.SampleRate * 0.08f));
    private float _gateGain = 1;
    private int _holdFrames;
    private float _cachedThresholdDb = float.NaN, _threshold;
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        bool highPass = options.HighPassEnabled, gate = options.GateEnabled;
        if (!gate) { _gateGain = 1; _holdFrames = 0; }
        if (!highPass && !gate)
        {
            for (int i = offset; i < offset + read; i++)
                if (!Platform.IsFinite(buffer[i])) buffer[i] = 0;
            SeedHighPass(buffer, offset, read);
            return read;
        }
        if (gate && options.ThresholdDb != _cachedThresholdDb)
        {
            _cachedThresholdDb = options.ThresholdDb; _threshold = MathF.Pow(10, _cachedThresholdDb / 20);
        }
        for (int i = 0; i < read; i += 2)
        {
            int position = offset + i;
            bool hasRight = i + 1 < read;
            float left = Platform.IsFinite(buffer[position]) ? buffer[position] : 0;
            float right = hasRight && Platform.IsFinite(buffer[position + 1]) ? buffer[position + 1] : 0;
            if (highPass)
            {
                float filteredLeft = _highPass * (_previousOutput[0] + left - _previousInput[0]);
                _previousInput[0] = left;
                left = Platform.IsFinite(filteredLeft) ? filteredLeft : 0; _previousOutput[0] = left;
                if (hasRight)
                {
                    float filteredRight = _highPass * (_previousOutput[1] + right - _previousInput[1]);
                    _previousInput[1] = right;
                    right = Platform.IsFinite(filteredRight) ? filteredRight : 0; _previousOutput[1] = right;
                }
            }
            else { _previousInput[0] = left; _previousOutput[0] = 0; if (hasRight) { _previousInput[1] = right; _previousOutput[1] = 0; } }
            if (gate)
            {
                float framePeak = Math.Max(Math.Abs(left), Math.Abs(right));
                if (framePeak >= _threshold) _holdFrames = AudioFormat.SampleRate * 120 / 1000;
                else if (_holdFrames > 0) _holdFrames--;
                float target = _holdFrames > 0 ? 1 : 0;
                float coefficient = target > _gateGain ? _attack : _release;
                _gateGain = target + coefficient * (_gateGain - target);
                if (_gateGain < 0.00001f) _gateGain = 0;
                left *= _gateGain; right *= _gateGain;
            }
            buffer[position] = left; if (hasRight) buffer[position + 1] = right;
        }
        return read;
    }
    private void SeedHighPass(float[] buffer, int offset, int read)
    {
        // Avoid a DC step when the filter is enabled after bypass. No filter or
        // gate math is needed per sample while both options are off.
        for (int c = 0; c < 2 && c < read; c++)
        {
            int last = read - 1 - ((read - 1 - c) % 2);
            _previousInput[c] = buffer[offset + last]; _previousOutput[c] = 0;
        }
    }
}

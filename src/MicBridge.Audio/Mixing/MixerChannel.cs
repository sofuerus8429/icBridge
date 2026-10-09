using NAudio.Wave;

namespace MicBridge.Audio.Mixing;

/// <summary>A permanent mixer input. Swaps and reads share a lock so sources can be disposed safely.</summary>
public sealed class MixerChannel : ISampleProvider
{
    private readonly object _gate = new();
    private ISampleProvider? _source;
    private float _volume = 1, _mixWeight = 1, _gainDb, _peak, _inputPeak;
    private int _enabled = 1, _muted;
    private float _currentMultiplier, _lastTarget, _rampStep;
    private int _rampFrames;
    private float _cachedGainDb = float.NaN, _gainMultiplier = 1;
    public WaveFormat WaveFormat => AudioFormat.MixFormat;
    public float Volume { get => Volatile.Read(ref _volume); set => Volatile.Write(ref _volume, Platform.IsFinite(value) ? Platform.Clamp(value, 0, 3) : 0); }
    public float GainDb { get => Volatile.Read(ref _gainDb); set => Volatile.Write(ref _gainDb, Platform.IsFinite(value) ? Platform.Clamp(value, -24, 24) : 0); }
    public float MixWeight { get => Volatile.Read(ref _mixWeight); set => Volatile.Write(ref _mixWeight, Platform.IsFinite(value) ? Platform.Clamp(value, 0, 1) : 0); }
    public bool Enabled { get => Volatile.Read(ref _enabled) != 0; set { if (Interlocked.Exchange(ref _enabled, value ? 1 : 0) != (value ? 1 : 0)) EnabledChanged?.Invoke(value); } }
    public event Action<bool>? EnabledChanged;
    public bool Muted { get => Volatile.Read(ref _muted) != 0; set => Volatile.Write(ref _muted, value ? 1 : 0); }
    public float Peak => Volatile.Read(ref _peak);
    public float InputPeak => Volatile.Read(ref _inputPeak);

    public void SetSource(ISampleProvider? source)
    {
        lock (_gate)
        {
            _source = source is null ? null : AudioFormat.Normalize(source);
            _currentMultiplier = _lastTarget = 0; _rampFrames = 0;
            Volatile.Write(ref _peak, 0);
            Volatile.Write(ref _inputPeak, 0);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_source is null)
            {
                Array.Clear(buffer, offset, count);
                Volatile.Write(ref _peak, 0); Volatile.Write(ref _inputPeak, 0);
                return count;
            }
            int read = _source.Read(buffer, offset, count);
            Array.Clear(buffer, offset + read, count - read);
            float gainDb = GainDb;
            if (gainDb != _cachedGainDb) { _gainMultiplier = MathF.Pow(10, gainDb / 20); _cachedGainDb = gainDb; }
            float target = Enabled && !Muted ? Volume * MixWeight * _gainMultiplier : 0;
            if (target == 0) { _currentMultiplier = 0; _rampFrames = 0; _lastTarget = 0; }
            else if (target != _lastTarget)
            {
                _lastTarget = target;
                _rampFrames = AudioFormat.SampleRate / 200;
                _rampStep = (target - _currentMultiplier) / _rampFrames;
            }
            float peak = 0, inputPeak = 0;
            int i = 0;
            for (; i < read && _rampFrames > 0; i += 2)
            {
                // Apply the same ramp to both stereo channels, avoiding a pan shift.
                _currentMultiplier += _rampStep; _rampFrames--;
                for (int c = 0; c < 2 && i + c < read; c++)
                {
                    float input = buffer[offset + i + c];
                    if (Platform.IsFinite(input)) inputPeak = Math.Max(inputPeak, Math.Abs(input));
                    float sample = input * _currentMultiplier;
                    if (!Platform.IsFinite(sample)) sample = 0;
                    buffer[offset + i + c] = sample;
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }
            // The common steady-state path has no per-frame ramp branch.
            if (i < read) _currentMultiplier = target;
            for (; i < read; i++)
            {
                float input = buffer[offset + i];
                if (Platform.IsFinite(input)) inputPeak = Math.Max(inputPeak, Math.Abs(input));
                float sample = input * target;
                if (!Platform.IsFinite(sample)) sample = 0;
                buffer[offset + i] = sample; peak = Math.Max(peak, Math.Abs(sample));
            }
            int silentFrames = (count + 1) / 2 - (read + 1) / 2;
            if (_rampFrames > 0 && silentFrames > 0)
            {
                int advance = Math.Min(silentFrames, _rampFrames);
                _currentMultiplier += advance * _rampStep; _rampFrames -= advance;
                if (_rampFrames == 0) _currentMultiplier = target;
            }
            Volatile.Write(ref _peak, peak);
            Volatile.Write(ref _inputPeak, inputPeak);
            return count;
        }
    }
}

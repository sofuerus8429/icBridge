using MicBridge.Audio.Mixing;
using NAudio.Wave;

namespace MicBridge.Audio.Sources;

/// <summary>The file decoder is read only by the output clock; transport actions are serialized with reads.</summary>
public sealed class FilePlaybackSource : ISampleProvider, IDisposable
{
    private readonly object _gate = new();
    private AudioFileReader? _reader;
    private ISampleProvider? _normalized;
    private bool _playing, _loop;
    public WaveFormat WaveFormat => AudioFormat.MixFormat;
    public string? FilePath { get; private set; }
    public bool IsPlaying { get { lock (_gate) return _playing; } }
    public bool Loop { get { lock (_gate) return _loop; } set { lock (_gate) _loop = value; } }
    public TimeSpan Position { get { lock (_gate) return _reader?.CurrentTime ?? TimeSpan.Zero; } }
    public TimeSpan Duration { get { lock (_gate) return _reader?.TotalTime ?? TimeSpan.Zero; } }

    public void Load(string path)
    {
        var reader = new AudioFileReader(path);
        ISampleProvider normalized;
        try { normalized = AudioFormat.Normalize(reader); }
        catch { reader.Dispose(); throw; }
        lock (_gate)
        {
            _reader?.Dispose();
            _reader = reader;
            _normalized = normalized;
            FilePath = path;
            _playing = false;
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_reader is null) throw new InvalidOperationException("请先添加一首本地音乐。");
            if (_reader.Position >= _reader.Length) Rewind();
            _playing = true;
        }
    }
    public void Pause() { lock (_gate) _playing = false; }
    public void Stop() { lock (_gate) { _playing = false; Rewind(); } }
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_reader is null) return;
            _reader.CurrentTime = TimeSpan.FromSeconds(Platform.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds));
            _normalized = AudioFormat.Normalize(_reader); // discard the resampler's cached pre-seek samples
        }
    }
    private void Rewind()
    {
        if (_reader is null) return;
        _reader.Position = 0;
        _normalized = AudioFormat.Normalize(_reader);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            int total = 0;
            if (_playing && _normalized is not null)
            {
                while (total < count)
                {
                    int read = _normalized.Read(buffer, offset + total, count - total);
                    total += read;
                    if (read == 0)
                    {
                        if (_loop && _reader!.Length > 0)
                        {
                            Rewind();
                            read = _normalized!.Read(buffer, offset + total, count - total);
                            total += read;
                            if (read > 0) continue;
                        }
                        _playing = false;
                        break;
                    }
                }
            }
            Array.Clear(buffer, offset + total, count - total);
            return count;
        }
    }
    public void Unload()
    {
        lock (_gate) { _playing = false; _reader?.Dispose(); _reader = null; _normalized = null; FilePath = null; }
    }
    public void Dispose() => Unload();
}

using NAudio.Wave;

namespace MicBridge.Audio.Sources;

/// <summary>Bounded capture queue; drops stale audio instead of accumulating seconds of latency.</summary>
public sealed class CaptureBuffer
{
    private readonly object _gate = new();
    public BufferedWaveProvider Provider { get; }
    public CaptureBuffer(WaveFormat format)
    {
        Provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
    }
    public void Write(byte[] data, int count)
    {
        lock (_gate)
        {
            if (Provider.BufferedDuration > TimeSpan.FromMilliseconds(150)) Provider.ClearBuffer();
            Provider.AddSamples(data, 0, count);
        }
    }
}

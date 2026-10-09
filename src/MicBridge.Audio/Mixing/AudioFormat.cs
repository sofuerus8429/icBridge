using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.Audio.Mixing;

public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public static WaveFormat MixFormat => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static ISampleProvider Normalize(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1)
        {
            // Resample one channel before copying it to both sides of the stereo mix.
            // This keeps the waveform and avoids filtering the identical channel twice.
            if (source.WaveFormat.SampleRate != SampleRate)
                source = new WdlResamplingSampleProvider(source, SampleRate);
            return new MonoToStereoSampleProvider(source);
        }
        else if (source.WaveFormat.Channels > 2)
            source = new DownmixProvider(source);
        if (source.WaveFormat.SampleRate != SampleRate)
            source = new WdlResamplingSampleProvider(source, SampleRate);
        return source;
    }

    // Distribute all channels with equal weights; do not silently discard surround channels.
    private sealed class DownmixProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _scratch = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int channels = source.WaveFormat.Channels;
            int needed = count / 2 * channels;
            if (_scratch.Length < needed) _scratch = new float[needed];
            int read = source.Read(_scratch, 0, needed);
            int frames = read / channels;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += _scratch[i * channels + c];
                buffer[offset + i * 2] = buffer[offset + i * 2 + 1] = sum / channels;
            }
            return frames * 2;
        }
    }
}

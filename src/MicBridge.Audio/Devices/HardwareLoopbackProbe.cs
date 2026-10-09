using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.Audio.Devices;

/// <summary>Brief, low-volume test of the actual hardware recording endpoint. No files are recorded.</summary>
public static class HardwareLoopbackProbe
{
    public static async Task<AudioDeviceInfo?> FindOutputAsync(AudioDeviceInfo capture, IEnumerable<AudioDeviceInfo> outputs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var input = enumerator.GetDevice(capture.Id);
        if (input.AudioEndpointVolume.Mute) throw new InvalidOperationException("立体声混音在 Windows 中已静音，请先取消静音再检测。");
        if (input.AudioEndpointVolume.MasterVolumeLevelScalar <= 0) throw new InvalidOperationException("立体声混音在 Windows 中的录音音量为零，请先调高再检测。");
        foreach (var output in outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeviceCatalog.ValidateHardwareRoute(capture, output);
            using var device = enumerator.GetDevice(output.Id);
            if (device.AudioEndpointVolume.Mute || device.AudioEndpointVolume.MasterVolumeLevelScalar <= 0) continue;
            using var recorder = new WasapiCapture(input);
            using var player = new WasapiOut(device, AudioClientShareMode.Shared, false, 60);
            var measurement = new PacketMeasurement(recorder.WaveFormat);
            recorder.DataAvailable += (_, packet) => measurement.Add(packet.Buffer, packet.BytesRecorded);
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.RecordingStopped += (_, args) => { if (args.Exception is null) stopped.TrySetResult(true); else stopped.TrySetException(args.Exception); };
            player.Init(new SignalGenerator(48000, 2) { Frequency = 733, Gain = 0.004, Type = SignalGeneratorType.Sin });
            bool recordingStarted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                recorder.StartRecording(); recordingStarted = true;
                double baseline = await measurement.MeasureAsync(300, cancellationToken);
                player.Play(); await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                double power = await measurement.MeasureAsync(500, cancellationToken);
                if (power > 1e-10 && power > baseline * 20) return output;
            }
            finally
            {
                player.Stop();
                if (recordingStarted)
                {
                    recorder.StopRecording();
                    await Platform.WithTimeout(stopped.Task, TimeSpan.FromSeconds(5));
                }
            }
        }
        return null;
    }
    public static void ValidateLevels(AudioDeviceInfo capture, AudioDeviceInfo output)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var input = enumerator.GetDevice(capture.Id);
        using var render = enumerator.GetDevice(output.Id);
        if (input.AudioEndpointVolume.Mute || input.AudioEndpointVolume.MasterVolumeLevelScalar <= 0)
            throw new InvalidOperationException("Windows 中的立体声混音已静音或音量为零，请在录音设备属性 → 级别中恢复音量。");
        if (render.AudioEndpointVolume.Mute || render.AudioEndpointVolume.MasterVolumeLevelScalar <= 0)
            throw new InvalidOperationException("Windows 中的混音输出设备已静音或音量为零，请恢复播放设备音量，或检测其他输出。");
    }
    private sealed class PacketMeasurement
    {
        private readonly object _gate = new();
        private readonly PacketProvider _packet;
        private readonly ISampleProvider _source;
        private readonly TonePowerMeter _meter;
        private readonly float[] _samples = new float[4096];
        private int _channel;
        private bool _measuring;
        public PacketMeasurement(WaveFormat format)
        {
            _packet = new PacketProvider(format); _source = _packet.ToSampleProvider();
            _meter = new TonePowerMeter(format.SampleRate, 733);
        }
        public void Add(byte[] bytes, int count)
        {
            lock (_gate)
            {
                if (!_measuring) return;
                _packet.Set(bytes, count);
                int read;
                while ((read = _source.Read(_samples, 0, _samples.Length)) > 0)
                    for (int i = 0; i < read; i++)
                    {
                        if (_channel == 0) _meter.Add(_samples[i]);
                        _channel = (_channel + 1) % _source.WaveFormat.Channels;
                    }
            }
        }
        public async Task<double> MeasureAsync(int milliseconds, CancellationToken cancellationToken)
        {
            lock (_gate) { _meter.Reset(); _channel = 0; _measuring = true; }
            try
            {
                await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
                lock (_gate) return _meter.Samples >= _packet.WaveFormat.SampleRate * milliseconds / 2000 ? _meter.Power : 0;
            }
            finally { lock (_gate) _measuring = false; }
        }
    }
    private sealed class PacketProvider(WaveFormat format) : IWaveProvider
    {
        private byte[] _bytes = [];
        private int _position, _length;
        public WaveFormat WaveFormat => format;
        public void Set(byte[] bytes, int length) { _bytes = bytes; _length = length; _position = 0; }
        public int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _length - _position);
            if (read > 0) System.Buffer.BlockCopy(_bytes, _position, buffer, offset, read);
            _position += read; return read;
        }
    }
}

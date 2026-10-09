using System.Diagnostics;
using System.IO;
using MicBridge.App.Serialization;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using MicBridge.Audio.Mixing;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.App.Services;

internal static class EnduranceVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "endurance-" + Guid.NewGuid().ToString("N") + ".wav");
        using var engine = new AudioEngine();
        using var process = Process.GetCurrentProcess();
        var rows = new List<WindowResult>();
        var timer = Stopwatch.StartNew();
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, State = "Running", Phase = "Starting" }));
        try
        {
            using (var writer = new WaveFileWriter(fixture, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)))
            {
                var samples = new float[44100];
                for (int i = 0; i < samples.Length; i++) samples[i] = 0.004f * (float)Math.Sin(2 * Math.PI * 500 * i / 44100);
                writer.WriteSamples(samples, 0, samples.Length);
            }
            var render = DeviceCatalog.GetDevices(DataFlow.Render).Single(DeviceCatalog.IsStandardCableInput);
            var capture = DeviceCatalog.GetDevices(DataFlow.Capture).Single(d => DeviceCatalog.IsBundledCable(d) && d.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase));
            using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(capture.Id);
            using var recorder = new WasapiCapture(device);
            var measured = new PacketMeter(recorder.WaveFormat);
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.DataAvailable += (_, packet) => measured.Add(packet.Buffer, packet.BytesRecorded);
            recorder.RecordingStopped += (_, args) => { if (args.Exception is null) stopped.TrySetResult(true); else stopped.TrySetException(args.Exception); };
            engine.Microphone.Enabled = false; engine.VoiceBalancePercent = 0; engine.Master.Volume = 0.8f;
            engine.Player.Load(fixture); engine.Player.Loop = true;
            recorder.StartRecording();
            try
            {
                await engine.StartAsync(new(render.Id)); engine.Player.Play(); await Task.Delay(1200);
                var before = Resources();
                for (int n = 0; n < 12; n++)
                {
                    var row = await Measure("continuous-" + (n + 1), 10000);
                    if (row.Rms < 0.00001 || row.TonePower < 1e-11) throw new InvalidOperationException("Continuous playback lost the expected signal.");
                }
                double normal = rows.Average(row => row.Rms);
                if (rows.Any(row => row.Rms < normal * 0.8 || row.Rms > normal * 1.2)) throw new InvalidOperationException("Playback level drifted during the two-minute phase.");
                engine.Music.Volume = 3; await Task.Delay(500);
                var boosted = await Measure("gain300", 20000);
                double ratio = boosted.Rms / normal;
                if (ratio < 2.9 || ratio > 3.1) throw new InvalidOperationException("The sustained gain ratio did not remain three.");
                engine.Master.Muted = true; await Task.Delay(500);
                var mute = await Measure("mute", 10000);
                if (mute.Rms > normal * 0.001) throw new InvalidOperationException("The sustained mute phase was not silent.");
                engine.Music.Volume = 1; engine.Master.Muted = false; await Task.Delay(500);
                var resumed = await Measure("resume", 20000);
                if (resumed.Rms < normal * 0.9 || resumed.Rms > normal * 1.1) throw new InvalidOperationException("Playback failed to recover after mute.");
                engine.Music.SetSource(new SignalGenerator(48000, 2) { Frequency = 500, Gain = 2, Type = SignalGeneratorType.Sin });
                await Task.Delay(500);
                var overload = await Measure("protected-overload", 3000);
                // Windows may resample the limited stream to the endpoint's native format.
                float protectionDb = engine.LimiterReductionDb;
                if (overload.Peak > 0.98 || overload.Rms < normal * 50 || protectionDb < 3)
                    throw new InvalidOperationException("The actual recording did not preserve overload protection.");
                engine.Master.Muted = true; engine.Music.SetSource(engine.Player);
                for (int n = 0; n < 16; n++)
                {
                    await engine.StopAsync(); if (engine.IsRunning) throw new InvalidOperationException("Stop did not release the audio route.");
                    await engine.StartAsync(new(render.Id)); engine.Player.Play(); await Task.Delay(150);
                    if (!engine.IsRunning || engine.IsMicrophoneCapturing) throw new InvalidOperationException("Restart changed the expected capture state.");
                }
                var after = Resources();
                if (after.ManagedBytes - before.ManagedBytes > 2 * 1024 * 1024 || after.Handles - before.Handles > 8)
                    throw new InvalidOperationException("Repeated routing retained unexpected managed memory or handles.");
                await engine.StopAsync(); recorder.StopRecording(); await Platform.WithTimeout(stopped.Task, TimeSpan.FromSeconds(5));
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, ActualVirtualMicrophoneRecorded = true,
                    DurationSeconds = timer.Elapsed.TotalSeconds, ContinuousSeconds = 120, RestartCycles = 16, GainAmplitudeRatio = ratio,
                    Before = before, After = after, OverloadRequestedPeak = 1.6, OverloadReductionDb = protectionDb,
                    WindowsDefaultDevicesChanged = false, RealMicrophoneCaptured = false,
                    RecordingsWrittenToDisk = false, FixtureDeleted = true, UserSettingsSaved = false, WindowsVolumeChanged = false,
                    Capture = capture, Render = render, Windows = rows }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { await engine.StopAsync(); recorder.StopRecording(); }

            async Task<WindowResult> Measure(string name, int milliseconds)
            {
                measured.Reset(); await Task.Delay(milliseconds);
                var result = measured.Take(name);
                if (result.Seconds < milliseconds / 1000d * 0.8) throw new InvalidOperationException("The recording endpoint stopped delivering packets.");
                rows.Add(result);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, State = "Running", Phase = name,
                    ElapsedSeconds = timer.Elapsed.TotalSeconds, Windows = rows }, new JsonSerializerOptions { WriteIndented = true }));
                return result;
            }
            ResourceSnapshot Resources()
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
                return new(GC.GetTotalMemory(false), process.PrivateMemorySize64, process.HandleCount);
            }
        }
        finally { engine.Dispose(); if (File.Exists(fixture)) File.Delete(fixture); }
    }
    private sealed record ResourceSnapshot(long ManagedBytes, long PrivateBytes, int Handles);
    internal sealed record WindowResult(string Name, double Seconds, double Rms, float Peak, double TonePower);
    internal sealed class PacketMeter
    {
        private readonly object _gate = new();
        private readonly PacketProvider _packet;
        private readonly ISampleProvider _source;
        private readonly TonePowerMeter _tone;
        private readonly float[] _samples = new float[4096];
        private long _count;
        private double _squares;
        private float _peak;
        private int _channel;
        public PacketMeter(WaveFormat format)
        {
            _packet = new(format); _source = _packet.ToSampleProvider(); _tone = new(format.SampleRate, 500);
        }
        public void Add(byte[] bytes, int length)
        {
            lock (_gate)
            {
                _packet.Set(bytes, length); int read;
                while ((read = _source.Read(_samples, 0, _samples.Length)) > 0)
                    for (int i = 0; i < read; i++)
                    {
                        float value = _samples[i]; _count++; _squares += (double)value * value; _peak = Math.Max(_peak, Math.Abs(value));
                        if (_channel == 0) _tone.Add(value);
                        _channel = (_channel + 1) % _packet.WaveFormat.Channels;
                    }
            }
        }
        public void Reset() { lock (_gate) { _count = 0; _squares = 0; _peak = 0; _channel = 0; _tone.Reset(); } }
        public WindowResult Take(string name)
        {
            lock (_gate) return new(name, _count / (double)_packet.WaveFormat.Channels / _packet.WaveFormat.SampleRate,
                Math.Sqrt(_squares / Math.Max(1, _count)), _peak, _tone.Power);
        }
    }
    private sealed class PacketProvider(WaveFormat format) : IWaveProvider
    {
        private byte[] _bytes = []; private int _position, _length;
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

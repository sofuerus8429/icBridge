using System.IO;
using MicBridge.App.Serialization;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using MicBridge.Audio.Sources;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MicBridge.App.Services;

internal static class FileFormatsVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "formats-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, State = "Running" }));
        try
        {
            string wav = Path.Combine(root, "mono-44100.wav"), mp3 = Path.Combine(root, "mono-44100.mp3"), m4a = Path.Combine(root, "mono-44100.m4a");
            await Task.Run(() =>
            {
                var format = new WaveFormat(44100, 16, 1);
                var bytes = new byte[format.AverageBytesPerSecond * 6];
                for (int frame = 0; frame < 44100 * 6; frame++)
                {
                    int frequency = frame < 44100 * 3 ? 500 : 900;
                    short value = (short)(0.02 * short.MaxValue * Math.Sin(2 * Math.PI * frequency * frame / 44100));
                    bytes[frame * 2] = (byte)value; bytes[frame * 2 + 1] = (byte)(value >> 8);
                }
                using (var writer = new WaveFileWriter(wav, format)) writer.Write(bytes, 0, bytes.Length);
                MediaFoundationApi.Startup();
                using (var input = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToMp3(input, mp3, 128000);
                string mp4 = Path.Combine(root, "mono-44100.mp4");
                using (var input = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToAac(input, mp4, 128000);
                File.Move(mp4, m4a);
            });
            var rows = new List<object>();
            foreach (string file in new[] { wav, mp3, m4a })
            {
                var transport = CheckTransport(file);
                var actualRecording = await CheckRecording(file);
                rows.Add(new { Format = Path.GetExtension(file), transport, actualRecording });
            }
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, Formats = rows, ActualVirtualMicrophoneRecorded = true, RealMicrophoneCaptured = false,
                RecordingsWrittenToDisk = false, UserSettingsSaved = false, WindowsDefaultDevicesChanged = false, WindowsVolumeChanged = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    private static object CheckTransport(string path)
    {
        using var player = new FilePlaybackSource(); player.Load(path); player.Play();
        if (player.Duration.TotalSeconds < 5.8 || player.Duration.TotalSeconds > 6.5) throw new IOException("Decoded duration is outside the fixture range.");
        var beginning = Measure(player, 100);
        RequireTone(beginning, true);
        player.Pause(); TimeSpan paused = player.Position;
        var silence = Measure(player, 10);
        if (silence.Rms != 0 || player.Position != paused) throw new IOException("Pause did not preserve the compressed file position.");
        player.Seek(TimeSpan.FromSeconds(4)); player.Play();
        var sought = Measure(player, 100); RequireTone(sought, false);
        player.Stop(); player.Play(); var rewound = Measure(player, 100); RequireTone(rewound, true);
        player.Loop = true; player.Seek(player.Duration - TimeSpan.FromMilliseconds(100));
        var loop = Measure(player, 50);
        if (!player.IsPlaying || loop.Power500 < loop.Power900 * 4 || loop.Power500 < 1e-7)
            throw new IOException("Looping did not cross the compressed file boundary into its beginning.");
        return new { DurationSeconds = player.Duration.TotalSeconds, Beginning = beginning, Seek = sought, Rewind = rewound,
            Loop = loop, PausePositionPreserved = true, StereoChannelsMatch = true };
    }

    private static async Task<object> CheckRecording(string file)
    {
        using var engine = new AudioEngine(); engine.Microphone.Enabled = false; engine.Master.Volume = 0.8f;
        engine.Player.Load(file); engine.Player.Play();
        string output = DeviceCatalog.GetDevices(DataFlow.Render).First(DeviceCatalog.IsStandardCableInput).Id;
        string input = DeviceCatalog.GetDevices(DataFlow.Capture).First(device => DeviceCatalog.IsBundledCable(device) &&
            device.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase)).Id;
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(input);
        using var recorder = new WasapiCapture(device);
        var measured = new EnduranceVerification.PacketMeter(recorder.WaveFormat);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.DataAvailable += (_, packet) => measured.Add(packet.Buffer, packet.BytesRecorded);
        recorder.RecordingStopped += (_, args) => { if (args.Exception is null) stopped.TrySetResult(true); else stopped.TrySetException(args.Exception); };
        recorder.StartRecording();
        try
        {
            await engine.StartAsync(new RoutingOptions(output)); await Task.Delay(400);
            measured.Reset(); await Task.Delay(500); var play = measured.Take("play");
            engine.Master.Muted = true; await Task.Delay(300);
            measured.Reset(); await Task.Delay(300); var mute = measured.Take("mute");
            engine.Master.Muted = false; engine.Player.Seek(TimeSpan.Zero); await Task.Delay(300);
            measured.Reset(); await Task.Delay(500); var resume = measured.Take("resume");
            if (play.Seconds < 0.35 || play.Rms < 0.008 || play.Rms > 0.015 || play.TonePower < 1e-5 ||
                mute.Rms > play.Rms * 0.001 || Math.Abs(resume.Rms / play.Rms - 1) > 0.03)
                throw new IOException("The actual virtual microphone did not preserve playback, mute and resumption.");
            await engine.StopAsync(); recorder.StopRecording();
            await Platform.WithTimeout(stopped.Task, TimeSpan.FromSeconds(5));
            return new { Play = play, Mute = mute, Resume = resume };
        }
        finally { await engine.StopAsync(); recorder.StopRecording(); }
    }

    private static SignalResult Measure(FilePlaybackSource player, int blocks)
    {
        var low = new TonePowerMeter(48000, 500); var high = new TonePowerMeter(48000, 900);
        var buffer = new float[960]; double squares = 0; int count = 0;
        for (int block = 0; block < blocks; block++)
        {
            player.Read(buffer, 0, buffer.Length);
            for (int sample = 0; sample < buffer.Length; sample += 2)
            {
                if (buffer[sample] != buffer[sample + 1]) throw new IOException("The normalized mono file has different left and right samples.");
                low.Add(buffer[sample]); high.Add(buffer[sample]); squares += (double)buffer[sample] * buffer[sample]; count++;
            }
        }
        return new SignalResult(Math.Sqrt(squares / count), low.Power, high.Power);
    }
    private static void RequireTone(SignalResult signal, bool beginning)
    {
        double wanted = beginning ? signal.Power500 : signal.Power900, other = beginning ? signal.Power900 : signal.Power500;
        if (wanted < 1e-7 || wanted < other * 20) throw new IOException("Decoded audio does not match the requested file position.");
    }
    private sealed record SignalResult(double Rms, double Power500, double Power900);
}

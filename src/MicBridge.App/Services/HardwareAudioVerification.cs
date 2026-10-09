using System.IO;
using MicBridge.App.Serialization;
using MicBridge.App.ViewModels;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.App.Services;

internal static class HardwareAudioVerification
{
    internal static async Task RunAsync(string reportPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        string fixture = Path.Combine(directory, "hardware-tone-" + Guid.NewGuid().ToString("N") + ".wav");
        var results = new List<object>(); bool anyPassed = false;
        using var model = new MainViewModel(false);
        await model.RefreshAsync(); model.HardwareLoopbackMode = true;
        if (model.MicrophoneDevices.Any(DeviceCatalog.IsHardwareLoopback)) throw new InvalidOperationException("Hardware loopback must not be mixed back as a microphone.");
        var capture = model.HardwareCaptureDevice ?? throw new InvalidOperationException("No hardware loopback capture endpoint.");
        var candidates = model.OutputDevices.ToArray();
        var detectedOutput = await Task.Run(() => HardwareLoopbackProbe.FindOutputAsync(capture, candidates));
        if (detectedOutput is null) throw new InvalidOperationException("The production hardware detector did not find a recording path.");
        try
        {
            using (var writer = new WaveFileWriter(fixture, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                var samples = new float[48000 * 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = 0.004f * (float)Math.Sin(2 * Math.PI * 500 * (i / 2) / 48000);
                writer.WriteSamples(samples, 0, samples.Length);
            }
            foreach (var render in candidates)
            {
                string recording = Path.Combine(directory, "hardware-recording-" + Guid.NewGuid().ToString("N") + ".wav");
                using var engine = new AudioEngine();
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(capture.Id);
                using var output = enumerator.GetDevice(render.Id);
                bool wasMuted = output.AudioEndpointVolume.Mute;
                using var recorder = new WasapiCapture(device);
                var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var sync = new object(); var phases = new List<Phase>();
                try
                {
                    DeviceCatalog.ValidateHardwareRoute(capture, render);
                    // Low-level generated signals only; no real microphone or user music.
                    output.AudioEndpointVolume.Mute = false;
                    engine.Player.Load(fixture); engine.Player.Loop = true;
                    engine.Microphone.SetSource(new SignalGenerator(48000, 2) { Frequency = 900, Gain = 0.004, Type = SignalGeneratorType.Sin });
                    engine.Microphone.Muted = true;
                    using (var writer = new WaveFileWriter(recording, recorder.WaveFormat))
                    {
                        recorder.DataAvailable += (_, packet) => { lock (sync) writer.Write(packet.Buffer, 0, packet.BytesRecorded); };
                        recorder.RecordingStopped += (_, args) => { if (args.Exception is null) stopped.TrySetResult(true); else stopped.TrySetException(args.Exception); };
                        recorder.StartRecording(); engine.Player.Play();
                        await engine.StartAsync(new(render.Id));
                        await Record("normal"); engine.Music.Volume = 3; await Record("gain300");
                        engine.Music.Volume = 1; engine.Microphone.Muted = false; await Record("mix");
                        engine.Master.Muted = true; await Record("mute");
                        await engine.StopAsync(); recorder.StopRecording(); await Platform.WithTimeout(stopped.Task, TimeSpan.FromSeconds(5));

                        async Task Record(string name)
                        {
                            await Task.Delay(450); long first; lock (sync) first = writer.Length;
                            await Task.Delay(650); long last; lock (sync) last = writer.Length;
                            phases.Add(new(name, first, last));
                        }
                    }
                    var values = new Dictionary<string, double>(); double musicPower = 0, voicePower = 0;
                    using (var reader = new WaveFileReader(recording))
                    {
                        foreach (var phase in phases)
                        {
                            reader.Position = phase.FirstByte;
                            var bytes = new byte[checked((int)(phase.LastByte - phase.FirstByte))];
                            int length = reader.Read(bytes, 0, bytes.Length);
                            using var stream = new RawSourceWaveStream(new MemoryStream(bytes, 0, length, false), reader.WaveFormat);
                            var source = stream.ToSampleProvider(); var samples = new float[4096]; var mono = new List<float>();
                            double square = 0; int count = 0, read;
                            while ((read = source.Read(samples, 0, samples.Length)) > 0)
                                for (int i = 0; i < read; i++) { square += (double)samples[i] * samples[i]; count++; if (i % reader.WaveFormat.Channels == 0) mono.Add(samples[i]); }
                            values[phase.Name] = Math.Sqrt(square / Math.Max(1, count));
                            if (phase.Name == "mix") { musicPower = Power(mono, 500, reader.WaveFormat.SampleRate); voicePower = Power(mono, 900, reader.WaveFormat.SampleRate); }
                        }
                    }
                    double ratio = values["gain300"] / Math.Max(1e-12, values["normal"]);
                    bool passed = values["normal"] > 0.00001 && ratio > 2.8 && ratio < 3.2 && values["mute"] < values["normal"] * 0.03
                        && musicPower > 1e-10 && voicePower > 1e-10;
                    anyPassed |= passed;
                    results.Add(new { Passed = passed, Render = render, Capture = capture, Rms = values, GainAmplitudeRatio = ratio, MusicPower = musicPower, SyntheticVoicePower = voicePower });
                }
                catch (Exception error) { results.Add(new { Passed = false, Render = render, Error = error.Message }); }
                finally
                {
                    await engine.StopAsync(); recorder.StopRecording(); output.AudioEndpointVolume.Mute = wasMuted;
                    if (File.Exists(recording)) File.Delete(recording);
                }
            }
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = anyPassed, DriverInstalled = false,
                ProductionDetectorOutput = detectedOutput,
                ActualHardwareCaptureRecorded = true, RealMicrophoneCaptured = false, UserSettingsSaved = false,
                WindowsDefaultDevicesChanged = false, EndpointMuteRestored = true, RecordingsDeleted = true, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
            if (!anyPassed) throw new InvalidOperationException("No physical render/capture pair passed the hardware recording check; see report.");
        }
        finally { if (File.Exists(fixture)) File.Delete(fixture); }
    }
    private sealed record Phase(string Name, long FirstByte, long LastByte);
    private static double Power(List<float> samples, double frequency, int rate)
    {
        double real = 0, imaginary = 0;
        for (int i = 0; i < samples.Count; i++) { double angle = 2 * Math.PI * frequency * i / rate; real += samples[i] * Math.Cos(angle); imaginary += samples[i] * Math.Sin(angle); }
        return (real * real + imaginary * imaginary) / Math.Max(1d, (double)samples.Count * samples.Count);
    }
}

using System.Diagnostics;
using System.IO;
using MicBridge.App.Serialization;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridge.App.Services;

internal static class ProcessAudioVerification
{
    internal static async Task PlayToneAsync(double frequency)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var outputs = DeviceCatalog.GetDevices(DataFlow.Render);
        var selected = outputs.FirstOrDefault(d => d.Id == defaultDevice.ID && !d.IsVirtual)
            ?? outputs.Where(d => !d.IsVirtual).OrderByDescending(d => d.Name.Contains("耳机") || d.Name.Contains("Headphones", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
            ?? throw new InvalidOperationException("音频隔离验证需要一个独立的普通播放设备；测试源不能直接播放到虚拟录音线缆。");
        using var device = enumerator.GetDevice(selected.Id);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 60);
        output.Init(new SignalGenerator(48000, 2) { Frequency = frequency, Gain = 0.002, Type = SignalGeneratorType.Sin });
        output.Play(); await Task.Delay(20000); output.Stop();
    }

    // Read the actual virtual microphone, not the capture source or mixer buffer.
    internal static async Task RunAsync(string reportPath, bool withCpuLoad = false)
    {
        Process? target = null, other = null;
        string fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "process-recording-" + Guid.NewGuid().ToString("N") + ".wav");
        using var engine = new AudioEngine();
        using var cpuLoad = new VerificationCpuLoad(withCpuLoad);
        bool multimediaScheduling = false;
        try
        {
            target = SpawnTone(500); other = SpawnTone(900);
            var render = DeviceCatalog.GetDevices(DataFlow.Render).Single(DeviceCatalog.IsStandardCableInput);
            var capture = DeviceCatalog.GetDevices(DataFlow.Capture).Single(device => DeviceCatalog.IsBundledCable(device) && device.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase));
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(capture.Id);
            using var recorder = new WasapiCapture(device);
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            var phases = new List<RecordingPhase>();
            var liveSamples = new List<float>();
            double liveRms = 0, muteRms = 0, resumedRms = 0;
            using (var writer = new WaveFileWriter(fixture, recorder.WaveFormat))
            {
                recorder.DataAvailable += (_, data) => { lock (gate) writer.Write(data.Buffer, 0, data.BytesRecorded); };
                recorder.RecordingStopped += (_, data) => { if (data.Exception is null) stopped.TrySetResult(true); else stopped.TrySetException(data.Exception); };
                engine.Microphone.Enabled = false; engine.Music.Enabled = false; engine.VoiceBalancePercent = 0; engine.Master.Volume = 0.8f;
                recorder.StartRecording();
                await engine.StartAsync(new(render.Id, CaptureMode: ApplicationCaptureMode.Process, ProcessId: target.Id));
                multimediaScheduling = engine.ProcessCaptureUsesMultimediaScheduling;
                await Phase("play", false, 1700); await Phase("mute", true, 1300); await Phase("resume", false, 1700);
                await engine.StopAsync(); recorder.StopRecording(); await Platform.WithTimeout(stopped.Task, TimeSpan.FromSeconds(5));

                async Task Phase(string name, bool muted, int milliseconds)
                {
                    engine.Master.Muted = muted; await Task.Delay(600);
                    long first; lock (gate) first = writer.Length;
                    await Task.Delay(milliseconds);
                    long last; lock (gate) { last = writer.Length; writer.Flush(); }
                    phases.Add(new(name, first, last, engine.Application.Peak));
                }
            }
            using (var reader = new WaveFileReader(fixture))
            {
                // Offsets are the number of audio bytes written, independent of the WAV header.
                foreach (var phase in phases)
                {
                    reader.Position = phase.FirstByte;
                    int bytes = checked((int)(phase.LastByte - phase.FirstByte));
                    var raw = new byte[bytes]; int read = reader.Read(raw, 0, raw.Length);
                    var source = new RawSourceWaveStream(new MemoryStream(raw, 0, read, false), reader.WaveFormat);
                    var samples = source.ToSampleProvider(); var block = new float[4096];
                    double squares = 0; int count = 0, n;
                    using (source) while ((n = samples.Read(block, 0, block.Length)) > 0)
                        for (int i = 0; i < n; i++) { squares += (double)block[i] * block[i]; count++; if (phase.Name == "play" && i % reader.WaveFormat.Channels == 0) liveSamples.Add(block[i]); }
                    double rms = Math.Sqrt(squares / Math.Max(1, count));
                    if (phase.Name == "play") liveRms = rms; else if (phase.Name == "mute") muteRms = rms; else resumedRms = rms;
                }
                double wanted = Power(liveSamples, 500, reader.WaveFormat.SampleRate), excluded = Power(liveSamples, 900, reader.WaveFormat.SampleRate);
                if (liveRms < 0.0001 || resumedRms < 0.0001 || muteRms > liveRms * 0.02 || wanted < excluded * 20)
                    throw new InvalidOperationException($"Virtual microphone failed: live={liveRms:E3}, mute={muteRms:E3}, resume={resumedRms:E3}, target={wanted:E3}, excluded={excluded:E3}.");
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, RenderEndpoint = render, CaptureEndpoint = capture,
                    MultimediaSchedulingActive = multimediaScheduling, ControlledCpuLoadWorkers = cpuLoad.Workers,
                    RecordingDurationSeconds = reader.TotalTime.TotalSeconds, ActualVirtualMicrophoneRecorded = true,
                    TargetFrequency = 500, ExcludedFrequency = 900, TargetPower = wanted, UnrelatedPower = excluded,
                    PlayRms = liveRms, MuteRms = muteRms, ResumeRms = resumedRms, RecordingDeletedAfterTest = true,
                    MicrophoneCaptured = false, UserSettingsSaved = false, Phases = phases }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            await engine.StopAsync();
            foreach (var process in new[] { target, other }) if (process is not null) { if (!process.HasExited) process.Kill(); process.Dispose(); }
            if (File.Exists(fixture)) File.Delete(fixture);
        }
    }
    private sealed record RecordingPhase(string Name, long FirstByte, long LastByte, float SourcePeak);
    private static Process SpawnTone(int frequency) => Process.Start(new ProcessStartInfo(Platform.ProcessPath, "--qa-tone " + frequency) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
    private static double Power(List<float> samples, double frequency, int rate)
    {
        double real = 0, imaginary = 0;
        for (int i = 0; i < samples.Count; i++) { double angle = 2 * Math.PI * frequency * i / rate; real += samples[i] * Math.Cos(angle); imaginary += samples[i] * Math.Sin(angle); }
        return (real * real + imaginary * imaginary) / Math.Max(1d, (double)samples.Count * samples.Count);
    }
}

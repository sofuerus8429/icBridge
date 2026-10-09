using System.Text.Json;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using MicBridge.Audio.Interop;
using MicBridge.Audio.Mixing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal static class ApplicationProbe
{
    public static async Task CaptureAsync(int processId, int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 20);
        using var capture = new ProcessLoopbackCapture(processId);
        await capture.StartAsync();
        await Task.Delay(200);
        var provider = capture.Buffer.Provider.ToSampleProvider();
        var block = new float[960];
        long samples = 0, nonzero = 0;
        double squares = 0;
        float peak = 0;
        for (int n = 0; n < seconds * 100; n++)
        {
            int read = provider.Read(block, 0, block.Length);
            for (int i = 0; i < read; i++)
            {
                float value = block[i];
                squares += value * value; peak = Math.Max(peak, Math.Abs(value));
                if (value != 0) nonzero++;
            }
            samples += read;
            await Task.Delay(10);
        }
        double rms = Math.Sqrt(squares / Math.Max(1, samples));
        Console.WriteLine(JsonSerializer.Serialize(new { ProcessId = processId, Samples = samples, NonzeroSamples = nonzero,
            Peak = peak, Rms = rms, RmsDbFs = rms > 0 ? 20 * Math.Log10(rms) : (double?)null }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task ControlsAsync(int processId)
    {
        using var capture = new ProcessLoopbackCapture(processId);
        await capture.StartAsync(); await Task.Delay(200);
        var raw = new RmsTap(capture.Buffer.Provider.ToSampleProvider());
        var channel = new MixerChannel(); channel.SetSource(raw);
        await CheckGain("100% / 0 dB", channel, raw, 1);
        channel.Volume = 0.5f;
        await CheckGain("50% / 0 dB", channel, raw, 0.5);
        channel.GainDb = 6.0206f;
        await CheckGain("50% / +6.02 dB", channel, raw, 1);
        channel.Muted = true;
        await CheckGain("mute", channel, raw, 0);
        channel.Muted = false; channel.Enabled = false;
        await CheckGain("excluded from mix", channel, raw, 0);
        channel.Enabled = true;
        await CheckGain("resume", channel, raw, 1);
        Console.WriteLine("PASS: real application samples obey independent volume, gain, mute, inclusion, and resume controls.");
    }

    private static async Task CheckGain(string label, MixerChannel channel, RmsTap raw, double expected)
    {
        var block = new float[960]; int checkedBlocks = 0;
        double ratioSum = 0;
        for (int n = 0; n < 35; n++)
        {
            channel.Read(block, 0, block.Length);
            if (n > 2 && raw.Rms > 0.000001)
            {
                double rms = Math.Sqrt(block.Sum(value => (double)value * value) / block.Length);
                double ratio = rms / raw.Rms;
                if (Math.Abs(ratio - expected) > 0.003) throw new Exception($"{label}: amplitude ratio {ratio} differs from {expected}.");
                ratioSum += ratio; checkedBlocks++;
            }
            await Task.Delay(10);
        }
        if (checkedBlocks < 5) throw new Exception($"{label}: insufficient live audio. Start the selected application playing first.");
        Console.WriteLine($"PASS {label}: measured amplitude ratio {ratioSum / checkedBlocks:F6}, {checkedBlocks} live blocks.");
    }

    public static async Task EngineAsync(int processId)
    {
        string path = Path.Combine(Path.GetTempPath(), $"micbridge-fixture-{Guid.NewGuid():N}.wav");
        WriteFixture(path);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var microphone = DeviceCatalog.GetDevices(DataFlow.Capture).FirstOrDefault(d => !d.IsVirtual);
            using var engine = new AudioEngine();
            Exception? failure = null; engine.Failed += error => failure = error;
            engine.Master.Muted = true;
            engine.Player.Load(path); engine.Player.Loop = true;
            await engine.StartAsync(new(output.ID, microphone?.Id, ApplicationCaptureMode.Process, processId));
            engine.Player.Play();
            float appPeak = 0, filePeak = 0, micPeak = 0;
            for (int n = 0; n < 40; n++)
            {
                await Task.Delay(25);
                appPeak = Math.Max(appPeak, engine.Application.Peak); filePeak = Math.Max(filePeak, engine.Music.Peak);
                micPeak = Math.Max(micPeak, engine.Microphone.Peak);
                if (engine.OutputPeak != 0) throw new Exception("Master mute leaked audio.");
            }
            if (failure is not null) throw new Exception("Audio graph failed.", failure);
            if (appPeak < 0.00001 || filePeak < 0.00001) throw new Exception("Application or file source did not reach the mixer.");
            Console.WriteLine(JsonSerializer.Serialize(new { ProcessId = processId, ApplicationPeak = appPeak, FilePeak = filePeak,
                MicrophonePeak = micPeak, OutputPeakWhileMuted = engine.OutputPeak, PlayerPositionSeconds = engine.Player.Position.TotalSeconds }));
            await engine.StopAsync();
            if (engine.IsRunning || engine.Player.IsPlaying) throw new Exception("Stop did not stop the graph and pause the file.");
            await engine.StartAsync(new(output.ID, microphone?.Id, ApplicationCaptureMode.Process, processId));
            await Task.Delay(500);
            if (failure is not null) throw new Exception("Restart failed.", failure);
            await engine.StopAsync();
            Console.WriteLine("PASS: live application, local WAV, and available microphone initialized in one output graph; master mute, stop, and restart passed.");
        }
        finally { File.Delete(path); }
    }

    public static void WriteFixture(string path)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new WaveFileWriter(path, AudioFormat.MixFormat);
        var block = new float[960];
        for (int n = 0; n < 2000; n++)
        {
            for (int i = 0; i < 480; i++)
                block[i * 2] = block[i * 2 + 1] = 0.02f * MathF.Sin(2 * MathF.PI * 220 * (n * 480 + i) / 48000);
            writer.WriteSamples(block, 0, block.Length);
        }
        Console.WriteLine($"Generated local test fixture: {path}");
    }

    private sealed class RmsTap(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public double Rms { get; private set; }
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            double squares = 0;
            for (int i = 0; i < read; i++) squares += (double)buffer[offset + i] * buffer[offset + i];
            Rms = Math.Sqrt(squares / Math.Max(1, read)); return read;
        }
    }
}

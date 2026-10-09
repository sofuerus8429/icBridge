using System.Text.Json;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Record the actual Windows capture endpoint. Hardware loopback must be selected
// explicitly; never silently substitute another source for a missing virtual mic.
internal static class VirtualMicrophoneProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(int processId, string recordingPath, string? renderId, string? captureId, bool hardware = false)
    {
        var renders = DeviceCatalog.GetDevices(DataFlow.Render);
        var captures = DeviceCatalog.GetDevices(DataFlow.Capture);
        var output = renderId is null ? FindCable(renders, "CABLE Input") : renders.SingleOrDefault(d => d.Id == renderId && (hardware ? !d.IsVirtual : d.IsVirtual));
        var input = captureId is null ? FindCable(captures, "CABLE Output") : captures.SingleOrDefault(d => d.Id == captureId && (hardware ? DeviceCatalog.IsHardwareLoopback(d) : d.IsVirtual));
        recordingPath = Path.GetFullPath(recordingPath);
        string reportPath = Path.ChangeExtension(recordingPath, ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(recordingPath)!);
        if (File.Exists(recordingPath) || File.Exists(reportPath)) throw new IOException("Choose a new recording path; existing recordings/reports will not be overwritten.");
        if (output is null || input is null)
        {
            var missing = new { Passed = false, Status = "MissingVirtualEndpoints", Recorded = false,
                Reason = "No active virtual render/capture pair. Install a virtual audio driver before testing the microphone recording path.",
                Render = renders, Capture = captures };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(missing, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(missing, JsonOptions));
            return 2;
        }
        var process = DeviceCatalog.GetApplication(processId);
        if (process is null || !process.CanCapture) throw new ArgumentException("Select a currently running source process.");
        if (hardware) DeviceCatalog.ValidateHardwareRoute(input, output);

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(input.Id);
        using var recorder = new WasapiCapture(device);
        using var engine = new AudioEngine();
        using var stream = new FileStream(recordingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new WaveFileWriter(stream, recorder.WaveFormat);
        object recordingLock = new();
        long bytes = 0;
        Exception? graphFailure = null;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.DataAvailable += (_, e) =>
        {
            lock (recordingLock) { writer.Write(e.Buffer, 0, e.BytesRecorded); bytes += e.BytesRecorded; }
        };
        recorder.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
        engine.Failed += error => graphFailure = error;
        engine.Music.Enabled = false;
        engine.Microphone.Enabled = false;
        engine.Application.Enabled = true;
        engine.VoiceBalancePercent = 0;
        engine.Master.Volume = hardware ? 0.05f : 0.8f;
        var phases = new List<Phase>();
        bool recordingStarted = false;
        try
        {
            recorder.StartRecording(); recordingStarted = true;
            await engine.StartAsync(new(output.Id, CaptureMode: ApplicationCaptureMode.Process, ProcessId: processId));
            if (hardware) await MeasurePhase("baseline", muted: true, seconds: 1);
            await MeasurePhase("play", muted: false, seconds: 3);
            await MeasurePhase("mute", muted: true, seconds: 2);
            await MeasurePhase("resume", muted: false, seconds: 3);
        }
        finally
        {
            await engine.StopAsync();
            if (recordingStarted)
            {
                recorder.StopRecording();
                var recorderFailure = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (recorderFailure is not null) graphFailure ??= recorderFailure;
            }
            lock (recordingLock) writer.Dispose();
        }

        // Decode the file saved by WasapiCapture, and measure only the settled
        // portions of each phase so cable/engine buffering cannot mimic a failure.
        using var reader = new WaveFileReader(recordingPath);
        var samples = reader.ToSampleProvider();
        long samplePosition = 0;
        var block = new float[4096];
        int read;
        while ((read = samples.Read(block, 0, block.Length)) > 0)
        {
            for (int i = 0; i < read; i++, samplePosition++)
            {
                double time = samplePosition / (double)(reader.WaveFormat.SampleRate * reader.WaveFormat.Channels);
                var phase = phases.FirstOrDefault(p => time >= p.StartSeconds && time < p.EndSeconds);
                phase?.Add(block[i]);
            }
        }
        double liveRms = phases.Where(p => p.Label == "play" || p.Label == "resume").Select(p => p.Rms).DefaultIfEmpty(0).Min();
        var mute = phases.Single(p => p.Label == "mute");
        double baselineRms = phases.FirstOrDefault(p => p.Label == "baseline")?.Rms ?? 0;
        double muteLimit = hardware ? Math.Max(liveRms * 0.02, baselineRms * 1.5 + 1e-7) : Math.Max(0.00001, liveRms * 0.02);
        bool passed = graphFailure is null && phases.All(p => p.Samples > reader.WaveFormat.SampleRate / 2) &&
            liveRms > 0.0001 && mute.Rms < muteLimit && (!hardware || baselineRms < liveRms * 0.05) &&
            phases.Where(p => p.Label == "play" || p.Label == "resume").All(p => p.SourcePeak > 0.0001f);
        var report = new { Passed = passed, Status = passed ? "Passed" : "AudioValidationFailed", Recorded = true,
            Source = process, RenderEndpoint = output, RecordingEndpoint = input, RecordingPath = recordingPath,
            Format = reader.WaveFormat.ToString(), DurationSeconds = reader.TotalTime.TotalSeconds,
            MicrophoneMixed = false, MasterVolume = engine.Master.Volume, HardwareLoopback = hardware,
            ExtraVirtualDriverUsed = !hardware, RecordingDeletedAfterTest = hardware, Failure = graphFailure?.Message,
            HardwareBaselineRms = baselineRms, MuteRmsLimit = muteLimit,
            Phases = phases.Select(p => new { p.Label, p.StartSeconds, p.EndSeconds, p.Samples, p.NonzeroSamples, p.Peak, p.Rms,
                RmsDbFs = p.Rms > 0 ? 20 * Math.Log10(p.Rms) : (double?)null, p.SourcePeak }) };
        string json = JsonSerializer.Serialize(report, JsonOptions);
        if (hardware) { reader.Dispose(); File.Delete(recordingPath); }
        await File.WriteAllTextAsync(reportPath, json);
        Console.WriteLine(json);
        return passed ? 0 : 3;

        async Task MeasurePhase(string label, bool muted, int seconds)
        {
            engine.Master.Muted = muted;
            await Task.Delay(600);
            var phase = new Phase(label);
            lock (recordingLock) phase.StartSeconds = bytes / (double)recorder.WaveFormat.AverageBytesPerSecond;
            for (int i = 0; i < seconds * 20; i++)
            {
                await Task.Delay(50);
                if (graphFailure is not null) throw new IOException("Audio route failed.", graphFailure);
                if (stopped.Task.IsCompleted) throw new IOException("Recording endpoint stopped unexpectedly.");
                phase.SourcePeak = Math.Max(phase.SourcePeak, engine.Application.Peak);
            }
            lock (recordingLock) phase.EndSeconds = bytes / (double)recorder.WaveFormat.AverageBytesPerSecond;
            phases.Add(phase);
        }
    }

    private static AudioDeviceInfo? FindCable(IReadOnlyList<AudioDeviceInfo> devices, string name)
    {
        var matches = devices.Where(d => d.IsVirtual && d.Name.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private sealed class Phase(string label)
    {
        public string Label { get; } = label;
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public long Samples { get; private set; }
        public long NonzeroSamples { get; private set; }
        public float Peak { get; private set; }
        public float SourcePeak { get; set; }
        private double Squares { get; set; }
        public double Rms => Math.Sqrt(Squares / Math.Max(1, Samples));
        public void Add(float value)
        {
            Samples++; if (value != 0) NonzeroSamples++;
            Peak = Math.Max(Peak, Math.Abs(value)); Squares += (double)value * value;
        }
    }
}

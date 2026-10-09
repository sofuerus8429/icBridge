using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using MicBridge.Audio.Mixing;
using NAudio.Wave;

internal static class DspBenchmark
{
    internal static void Run(string reportPath, string? corePath, bool resamplingOnly = false)
    {
        Assembly core = typeof(MixerChannel).Assembly;
        if (corePath is not null)
        {
            var context = new AssemblyLoadContext("benchmark-core", isCollectible: true);
            context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            core = context.LoadFromAssemblyPath(Path.GetFullPath(corePath));
        }
        var results = new List<object>();
        var channelType = core.GetType("MicBridge.Audio.Mixing.MixerChannel")!;
        var processorType = core.GetType("MicBridge.Audio.Mixing.MicrophoneProcessor")!;
        var optionsType = core.GetType("MicBridge.Audio.Mixing.MicrophoneProcessingOptions")!;
        var limiterType = core.GetType("MicBridge.Audio.Mixing.PeakLimiter")!;
        if (resamplingOnly)
        {
            var normalize = core.GetType("MicBridge.Audio.Mixing.AudioFormat")!.GetMethod("Normalize")!;
            foreach (int sampleRate in new[] { 16000, 44100, 96000 })
                Measure($"mono-resample-{sampleRate}", () => (ISampleProvider)normalize.Invoke(null, [new Mono(sampleRate)])!);
        }
        else
        {
        Measure("empty-channel", () => (ISampleProvider)Activator.CreateInstance(channelType)!);
        Measure("steady-gain-channel", () => { var channel = Activator.CreateInstance(channelType)!; channelType.GetMethod("SetSource")!.Invoke(channel, [new Constant()]); return (ISampleProvider)channel; });
        Measure("bypassed-microphone", () => CreateProcessor(false));
        Measure("processed-microphone", () => CreateProcessor(true));
        Measure("limiter-quiet", () => (ISampleProvider)Activator.CreateInstance(limiterType, new Constant())!);
        Measure("limiter-overload", () => (ISampleProvider)Activator.CreateInstance(limiterType, new Constant(4))!);
        Measure("limiter-pulsed", () => (ISampleProvider)Activator.CreateInstance(limiterType, new Pulsed())!);
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            CoreVersion = core.GetName().Version?.ToString(), BlockSamples = 960, BlocksPerTrial = 100000, Trials = 7, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        ISampleProvider CreateProcessor(bool processed)
        {
            var options = Activator.CreateInstance(optionsType)!;
            optionsType.GetProperty("GateEnabled")!.SetValue(options, processed);
            optionsType.GetProperty("HighPassEnabled")!.SetValue(options, processed);
            return (ISampleProvider)Activator.CreateInstance(processorType, new Constant(), options)!;
        }
        void Measure(string name, Func<ISampleProvider> create)
        {
            var provider = create(); var buffer = new float[960];
            for (int n = 0; n < 30000; n++) provider.Read(buffer, 0, buffer.Length);
            var timings = new List<double>();
            for (int trial = 0; trial < 7; trial++)
            {
                var watch = Stopwatch.StartNew();
                for (int n = 0; n < 100000; n++) provider.Read(buffer, 0, buffer.Length);
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }
            timings.Sort(); results.Add(new { Name = name, MedianMilliseconds = timings[3], TimingsMilliseconds = timings });
        }
    }
    private sealed class Mono(int sampleRate) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++) buffer[i] = 0.2f;
            return count;
        }
    }
    private sealed class Constant(float value = 0.2f) : ISampleProvider
    {
        public WaveFormat WaveFormat => AudioFormat.MixFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++) buffer[i] = value;
            return count;
        }
    }
    private sealed class Pulsed : ISampleProvider
    {
        private int _blocks;
        public WaveFormat WaveFormat => AudioFormat.MixFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            float value = _blocks++ % 20 == 0 ? 4 : 0.2f;
            for (int i = offset; i < offset + count; i++) buffer[i] = value;
            return count;
        }
    }
}

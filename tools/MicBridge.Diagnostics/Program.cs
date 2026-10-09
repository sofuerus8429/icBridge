using System.Diagnostics;
using System.Text.Json;
using MicBridge.Audio;
using MicBridge.Audio.Devices;
using MicBridge.Audio.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

if (args.Length > 0 && args[0] == "tone")
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 60);
    output.Init(new SignalGenerator(48000, 2) { Frequency = double.Parse(args[1]), Gain = 0.002, Type = SignalGeneratorType.Sin });
    output.Play(); await Task.Delay(8000); output.Stop(); return;
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length > 1 && args[0] is "dsp-benchmark" or "resampling-benchmark")
{
    DspBenchmark.Run(args[1], args.Length > 2 ? args[2] : null, args[0] == "resampling-benchmark"); return;
}
if (args.Length > 0 && args[0] == "defaults")
{
    using var enumerator = new MMDeviceEnumerator();
    var defaults = new List<object>();
    foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            defaults.Add(new { Flow = flow.ToString(), Role = role.ToString(), Id = device.ID,
                Name = device.FriendlyName, Muted = device.AudioEndpointVolume.Mute,
                Volume = device.AudioEndpointVolume.MasterVolumeLevelScalar });
        }
    Console.WriteLine(JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Length > 0 && args[0] == "endpoints")
{
    using var enumerator = new MMDeviceEnumerator();
    var endpoints = new List<object>();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
    {
        using (device) endpoints.Add(new { Id = device.ID, Name = device.FriendlyName, AdapterName = device.DeviceFriendlyName,
            Flow = device.DataFlow.ToString(), State = device.State.ToString(), IsVirtual = DeviceCatalog.IsVirtualName(device.FriendlyName) });
    }
    Console.WriteLine(JsonSerializer.Serialize(endpoints, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Length > 2 && (args[0] == "roundtrip" || args[0] == "hardware-roundtrip"))
{
    if (args[0] == "hardware-roundtrip" && args.Length < 5) throw new ArgumentException("Hardware mode requires explicit playback and recording endpoint IDs.");
    Environment.ExitCode = await VirtualMicrophoneProbe.RunAsync(int.Parse(args[1]), args[2],
        args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null, hardware: args[0] == "hardware-roundtrip");
    return;
}
if (args.Length > 0 && args[0] == "idle")
{
    await Task.Delay(15000); return;
}
if (args.Length > 0 && args[0] == "processes")
{
    string query = args.Length > 1 ? args[1] : "";
    var processes = DeviceCatalog.GetApplications(includeBackground: true).Where(process =>
        process.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || process.WindowTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        process.ProcessId.ToString().Contains(query, StringComparison.Ordinal));
    Console.WriteLine(JsonSerializer.Serialize(processes, new JsonSerializerOptions { WriteIndented = true })); return;
}
if (args.Length > 1 && args[0] == "capture")
{
    await ApplicationProbe.CaptureAsync(int.Parse(args[1]), args.Length > 2 ? int.Parse(args[2]) : 3);
    return;
}
if (args.Length > 1 && args[0] == "controls")
{
    await ApplicationProbe.ControlsAsync(int.Parse(args[1]));
    return;
}
if (args.Length > 1 && args[0] == "engine")
{
    await ApplicationProbe.EngineAsync(int.Parse(args[1]));
    return;
}
if (args.Length > 1 && args[0] == "fixture")
{
    ApplicationProbe.WriteFixture(args[1]);
    return;
}
var render = DeviceCatalog.GetDevices(DataFlow.Render);
var captureDevices = DeviceCatalog.GetDevices(DataFlow.Capture);
Console.WriteLine(JsonSerializer.Serialize(new { OS = Environment.OSVersion.ToString(), ProcessLoopback = ProcessLoopbackCapture.IsSupported, Render = render, Capture = captureDevices }, new JsonSerializerOptions { WriteIndented = true }));
if (args.Length == 0 || args[0] != "probe") return;
if (!ProcessLoopbackCapture.IsSupported) throw new PlatformNotSupportedException("Process capture is unavailable.");
Process? target = null, other = null;
try
{
    target = SpawnTone("500"); other = SpawnTone("900");
    using (var capture = new ProcessLoopbackCapture(target.Id))
    {
        await capture.StartAsync().WaitAsync(TimeSpan.FromSeconds(12));
        await Task.Delay(600);
        var provider = capture.Buffer.Provider.ToSampleProvider(); var values = new List<float>();
        var block = new float[960]; var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            int read = provider.Read(block, 0, block.Length);
            for (int i = 0; i < read; i += 2) values.Add(block[i]);
            await Task.Delay(10);
        }
        double targetPower = Power(values, 500), otherPower = Power(values, 900);
        Console.WriteLine($"Captured samples: {values.Count}; 500 Hz={targetPower:E3}; unrelated 900 Hz={otherPower:E3}");
        if (targetPower < 1e-8 || targetPower < otherPower * 20) throw new Exception("Process audio isolation check failed.");
        Console.WriteLine("PASS: target process audio captured and unrelated process excluded.");
    }
    // Start, stop, and restart a real output graph with its master muted.
    using var enumerator = new MMDeviceEnumerator();
    using var outputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var engine = new AudioEngine(); engine.Master.Muted = true;
    var mic = captureDevices.FirstOrDefault(d => !d.IsVirtual);
    for (int i = 0; i < 2; i++)
    {
        await engine.StartAsync(new(outputDevice.ID, mic?.Id, i == 0 ? ApplicationCaptureMode.Process : ApplicationCaptureMode.None, target.Id));
        await Task.Delay(500);
        if (i == 0 && engine.Application.Peak < 0.00001f) throw new Exception("Process source did not reach the mixer.");
        if (!engine.IsRunning) throw new Exception("Output graph did not start.");
        await engine.StopAsync(); if (engine.IsRunning) throw new Exception("Output graph did not stop.");
    }
    Console.WriteLine("PASS: captured process reached the mixer; WASAPI output and available microphone initialized, stopped, and restarted.");
}
finally
{
    foreach (var process in new[] { target, other })
    {
        if (process is null) continue;
        if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        process.Dispose();
    }
}

static Process SpawnTone(string frequency)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    // When invoked through dotnet rather than the apphost, supply our assembly path as the first argument.
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("tone"); start.ArgumentList.Add(frequency);
    return Process.Start(start) ?? throw new Exception("Could not start test renderer.");
}
static double Power(List<float> values, double frequency)
{
    double real = 0, imaginary = 0;
    for (int i = 0; i < values.Count; i++)
    {
        double angle = 2 * Math.PI * frequency * i / 48000;
        real += values[i] * Math.Cos(angle); imaginary -= values[i] * Math.Sin(angle);
    }
    return (real * real + imaginary * imaginary) / (values.Count * (double)values.Count);
}

namespace MicBridge.Audio.Tests;

public sealed class RoutingTests
{
    [Fact]
    public async Task DisposedEngineCannotBeRestartedAndRepeatedStopIsSafe()
    {
        var engine = new AudioEngine(); engine.Dispose(); engine.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StartAsync(new("never-open-this-device")));
        engine.Microphone.Enabled = false; engine.Microphone.Enabled = true;
        await engine.StopAsync(); Assert.False(engine.IsRunning); Assert.False(engine.IsMicrophoneCapturing);
    }
    [Fact]
    public void RejectsFeedbackThroughCapturedOutputOrMonitor()
    {
        Assert.Throws<ArgumentException>(() => AudioEngine.ValidateRouting(new("out", CaptureMode: ApplicationCaptureMode.DeviceLoopback, LoopbackDeviceId: "out")));
        Assert.Throws<ArgumentException>(() => AudioEngine.ValidateRouting(new("out", CaptureMode: ApplicationCaptureMode.DeviceLoopback, LoopbackDeviceId: "headphones", MonitorDeviceId: "headphones")));
        Assert.Throws<ArgumentException>(() => AudioEngine.ValidateRouting(new("out", MonitorDeviceId: "out")));
    }
    [Fact]
    public void RejectsMissingDevicesAndSelfCapture()
    {
        Assert.Throws<ArgumentException>(() => AudioEngine.ValidateRouting(new("")));
        Assert.Throws<ArgumentException>(() => AudioEngine.ValidateRouting(new("out", CaptureMode: ApplicationCaptureMode.Process, ProcessId: MicBridge.Audio.Compatibility.Platform.ProcessId)));
        Assert.Throws<ArgumentException>(() => AudioEngine.ValidateRouting(new("out", CaptureMode: ApplicationCaptureMode.DeviceLoopback)));
    }
    [Fact]
    public void AllowsIsolatedProcessOrIndependentLoopbackRoutes()
    {
        AudioEngine.ValidateRouting(new("virtual-out", "mic", ApplicationCaptureMode.Process, 123, MonitorDeviceId: "headphones"));
        AudioEngine.ValidateRouting(new("virtual-out", "mic", ApplicationCaptureMode.DeviceLoopback, LoopbackDeviceId: "speakers", MonitorDeviceId: "headphones"));
    }
}

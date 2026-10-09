using MicBridge.Audio.Devices;

namespace MicBridge.Audio.Tests;

public sealed class HardwareCacheTests
{
    [Fact]
    public void VerificationRequiresTheSameCaptureAndOutputPair()
    {
        var cache = new HardwareRouteCache(); Assert.True(cache.Confirm("capture", "speaker", cache.Revision));
        Assert.True(cache.Contains("capture", "speaker"));
        Assert.False(cache.Contains("capture", "headphones")); Assert.False(cache.Contains("other-capture", "speaker"));
    }
    [Fact]
    public void DeviceChangeDiscardsRoutesAndRejectsLateDetectionResults()
    {
        var cache = new HardwareRouteCache(); int revision = cache.Revision;
        Assert.True(cache.Confirm("capture", "speaker", revision)); cache.Invalidate();
        Assert.False(cache.Contains("capture", "speaker")); Assert.False(cache.Confirm("capture", "speaker", revision));
        Assert.True(cache.Confirm("capture", "speaker", cache.Revision));
    }
    [Fact]
    public async Task CancelledDetectionDoesNotEvenOpenTheDevice()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HardwareLoopbackProbe.FindOutputAsync(new("not-a-device", "Stereo Mix", false), [], cancellation.Token));
    }
}

using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;

namespace MicBridge.Audio.Tests;

public sealed class HardwareRouteTests
{
    private static AudioDeviceInfo Capture(DeviceState state = DeviceState.Active) => new("record", "立体声混音 (Realtek)", false, state, "Realtek");
    private static AudioDeviceInfo Output(string adapter = "Realtek") => new("play", "扬声器", false, DeviceState.Active, adapter);
    [Theory]
    [InlineData("Stereo Mix (Realtek)", true)]
    [InlineData("立体声混音 (Realtek)", true)]
    [InlineData("What U Hear", true)]
    [InlineData("麦克风阵列 (Realtek)", false)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", false)]
    public void RecognizesOnlyHardwareCaptureNames(string name, bool expected) =>
        Assert.Equal(expected, DeviceCatalog.IsHardwareLoopback(new("id", name, DeviceCatalog.IsVirtualName(name))));
    [Fact]
    public void ActiveHardwareCaptureAcceptsOnlyTheSamePhysicalAdapter()
    {
        DeviceCatalog.ValidateHardwareRoute(Capture(), Output());
        Assert.Throws<InvalidOperationException>(() => DeviceCatalog.ValidateHardwareRoute(Capture(), Output("USB")));
        Assert.Throws<InvalidOperationException>(() => DeviceCatalog.ValidateHardwareRoute(Capture(), Output() with { IsVirtual = true }));
        Assert.Throws<InvalidOperationException>(() => DeviceCatalog.ValidateHardwareRoute(Capture(), Output("")));
    }
    [Fact]
    public void MissingOrDisabledCaptureCannotPretendToBeReady()
    {
        Assert.Throws<InvalidOperationException>(() => DeviceCatalog.ValidateHardwareRoute(null, Output()));
        Assert.Contains("禁用", Assert.Throws<InvalidOperationException>(() => DeviceCatalog.ValidateHardwareRoute(Capture(DeviceState.Disabled), Output())).Message);
    }
    [Fact]
    public void MonitorOnTheCaptureAdapterIsRejectedButAnotherAdapterIsAccepted()
    {
        Assert.Throws<InvalidOperationException>(() => DeviceCatalog.ValidateHardwareRoute(Capture(), Output(), Output()));
        DeviceCatalog.ValidateHardwareRoute(Capture(), Output(), Output("USB"));
    }
}

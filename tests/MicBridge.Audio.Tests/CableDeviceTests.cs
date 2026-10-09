using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;

namespace MicBridge.Audio.Tests;

public sealed class CableDeviceTests
{
    private static AudioDeviceInfo Input(DeviceState state = DeviceState.Active) => new("render", "CABLE Input (VB-Audio Virtual Cable)", true, state);
    private static AudioDeviceInfo Output(DeviceState state = DeviceState.Active) => new("capture", "CABLE Output (VB-Audio Virtual Cable)", true, state);
    [Fact] public void DisabledMicrophoneGivesReenableInstructions()
    {
        string hint = DeviceCatalog.CableReminder([Input()], [Output(DeviceState.Disabled)]);
        Assert.Contains("CABLE Output 已被禁用", hint);
        Assert.Contains("输入设备", hint);
        Assert.Contains("允许", hint);
    }
    [Fact] public void DisabledRenderGivesOutputInstructions()
    {
        string hint = DeviceCatalog.CableReminder([Input(DeviceState.Disabled)], [Output()]);
        Assert.Contains("CABLE Input 已被禁用", hint);
        Assert.Contains("输出设备", hint);
    }
    [Fact] public void ActivePairStillWarnsAgainstDisablingMicrophone()
    {
        string hint = DeviceCatalog.CableReminder([Input()], [Output()]);
        Assert.Contains("请勿禁用", hint);
        Assert.DoesNotContain("已被禁用", hint);
    }
    [Fact] public void MissingRecordingEndpointExplainsRestartAndPermission()
    {
        string hint = DeviceCatalog.CableReminder([Input()], []);
        Assert.Contains("尚未找到 CABLE Output", hint);
        Assert.Contains("重启", hint);
    }
    [Fact] public void MultichannelPortIsNotPreferredStereoPort()
    {
        Assert.True(DeviceCatalog.IsStandardCableInput(Input()));
        Assert.False(DeviceCatalog.IsStandardCableInput(new("multi", "CABLE In 16ch (VB-Audio Virtual Cable)", true)));
        Assert.False(DeviceCatalog.IsStandardCableInput(new("real", "Speakers (Realtek)", false)));
    }
    [Fact] public void DefaultPlaybackThroughCableExplainsTheVolumeBypass()
    {
        string hint = DeviceCatalog.CableReminder([Input()], [Output()], Input());
        Assert.Contains("绕过音桥音量", hint); Assert.Contains("耳机/扬声器", hint); Assert.Contains("请勿禁用 CABLE Output", hint);
        Assert.DoesNotContain("绕过", DeviceCatalog.CableReminder([Input()], [Output()], new("speaker", "Speakers (Realtek)", false)));
    }
    [Fact] public void ADisabledMicrophoneTakesPriorityOverTheDefaultPlaybackWarning()
    {
        string hint = DeviceCatalog.CableReminder([Input()], [Output(DeviceState.Disabled)], Input());
        Assert.Contains("CABLE Output 已被禁用", hint); Assert.DoesNotContain("绕过", hint);
    }
}

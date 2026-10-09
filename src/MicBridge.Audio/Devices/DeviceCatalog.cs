using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MicBridge.Audio.Devices;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsVirtual, DeviceState State = DeviceState.Active, string AdapterName = "")
{
    public override string ToString() => Name;
}
public sealed record ApplicationInfo(int ProcessId, string Name, string WindowTitle = "",
    bool HasAudioSession = false, long? StartTimeUtcTicks = null, int? SessionId = null)
{
    public bool HasWindow => !string.IsNullOrWhiteSpace(WindowTitle);
    public bool CanCapture => ProcessId > 0 && ProcessId != Platform.ProcessId && !Name.Equals("MicBridge", StringComparison.OrdinalIgnoreCase);
    public string SourceKind => !CanCapture ? "不可采集" : HasAudioSession ? "有音频会话" : HasWindow ? "窗口进程" : SessionId == 0 ? "系统进程" : "后台进程";
    public override string ToString() => $"{Name} · PID {ProcessId}";
}

public static class DeviceCatalog
{
    public static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow, DeviceState states = DeviceState.Active)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, states))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName, IsVirtualName(device.FriendlyName), device.State, device.DeviceFriendlyName));
        }
        return result.OrderByDescending(d => d.IsVirtual).ThenBy(d => d.Name).ToList();
    }

    public static bool IsVirtualName(string name) =>
        new[] { "CABLE", "Voicemeeter", "Virtual Audio Cable", "VB-Audio" }
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    public static AudioDeviceInfo? GetDefaultDevice(DataFlow flow, Role role = Role.Multimedia)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return new(device.ID, device.FriendlyName, IsVirtualName(device.FriendlyName), device.State, device.DeviceFriendlyName);
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    public static bool IsStandardCableInput(AudioDeviceInfo device) => device.Name.StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase)
        && device.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);
    public static bool IsBundledCable(AudioDeviceInfo device) => device.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);

    // These are capture endpoints supplied by the existing sound-card driver.
    // WASAPI software loopback alone cannot create a game-selectable microphone.
    public static bool IsHardwareLoopback(AudioDeviceInfo device) => !device.IsVirtual &&
        new[] { "Stereo Mix", "立体声混音", "立體聲混音", "Waveout Mix", "Mixed Output", "What U Hear", "What You Hear" }
            .Any(token => device.Name.Contains(token, StringComparison.OrdinalIgnoreCase));

    public static bool IsSameAdapter(AudioDeviceInfo left, AudioDeviceInfo right) =>
        !string.IsNullOrWhiteSpace(left.AdapterName) && !string.IsNullOrWhiteSpace(right.AdapterName)
        && left.AdapterName.Equals(right.AdapterName, StringComparison.OrdinalIgnoreCase);

    public static void ValidateHardwareRoute(AudioDeviceInfo? capture, AudioDeviceInfo output, AudioDeviceInfo? monitor = null)
    {
        if (capture is null || !IsHardwareLoopback(capture))
            throw new InvalidOperationException("声卡未提供立体声混音。请改用虚拟线缆模式。");
        if (capture.State != DeviceState.Active)
            throw new InvalidOperationException("立体声混音已禁用。打开 Windows 录音设备，显示已禁用的设备并启用它，然后刷新。");
        if (output.State != DeviceState.Active || output.IsVirtual || !IsSameAdapter(capture, output))
            throw new InvalidOperationException("请选择与立体声混音属于同一声卡的播放设备。");
        if (monitor is not null && IsSameAdapter(capture, monitor))
            throw new InvalidOperationException("监听设备与立体声混音属于同一声卡，会重复混入声音。请关闭额外监听，或选择其他声卡。");
    }

    public static string CableReminder(IEnumerable<AudioDeviceInfo> render, IEnumerable<AudioDeviceInfo> capture)
        => CableReminder(render, capture, null);

    public static string CableReminder(IEnumerable<AudioDeviceInfo> render, IEnumerable<AudioDeviceInfo> capture, AudioDeviceInfo? defaultRender)
    {
        var inputs = render.Where(IsBundledCable).ToArray();
        var outputs = capture.Where(IsBundledCable).ToArray();
        if (outputs.Any(device => device.State == DeviceState.Disabled))
            return "CABLE Output 已被禁用。请在 Windows 声音设置 → 所有声音设备 → 输入设备中允许它，然后刷新设备。";
        if (inputs.Any(device => IsStandardCableInput(device) && device.State == DeviceState.Disabled))
            return "CABLE Input 已被禁用。请在 Windows 声音设置 → 所有声音设备 → 输出设备中允许它，然后刷新设备。";
        if (inputs.Length > 0 && !outputs.Any(device => device.State == DeviceState.Active))
            return "尚未找到 CABLE Output。安装驱动后请重启，并检查 Windows 的输入设备是否已允许。";
        if (defaultRender is not null && IsBundledCable(defaultRender))
            return "系统默认播放正使用虚拟线缆，其他声音可能绕过音桥音量。建议改为耳机/扬声器；请勿禁用 CABLE Output。";
        return "游戏麦克风选 CABLE Output，请勿禁用它。";
    }

    public static IReadOnlyList<ApplicationInfo> GetApplications(bool includeBackground = true)
    {
        var audioProcessIds = new HashSet<int>();
        var result = new List<ApplicationInfo>();
        // A session may be inactive but still useful when the player resumes.
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var session = sessions[i];
                            audioProcessIds.Add((int)session.GetProcessID);
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException) { /* A device may disappear during refresh. */ }
                }
            }
        }
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var info = ReadApplication(process, audioProcessIds.Contains(process.Id));
                    if (includeBackground || info.HasAudioSession || info.HasWindow || info.Name.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
                        result.Add(info);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return result.OrderByDescending(p => p.Name.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.HasWindow).ThenByDescending(p => p.HasAudioSession)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId).ToList();
    }

    public static ApplicationInfo? GetApplication(int processId)
    {
        if (processId <= 0 || processId == Platform.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : ReadApplication(process, false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static ApplicationInfo ReadApplication(Process process, bool hasAudioSession)
    {
        string name, title = "";
        try { name = process.ProcessName; }
        catch (System.ComponentModel.Win32Exception) { name = $"进程 {process.Id}"; }
        long? startTime = null;
        int? sessionId = null;
        try { title = process.MainWindowTitle; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { startTime = process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { sessionId = process.SessionId; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return new(process.Id, name, title, hasAudioSession, startTime, sessionId);
    }
}

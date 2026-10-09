using System.Diagnostics;
using System.IO;
using MicBridge.Audio.Devices;
using NAudio.CoreAudioApi;

namespace MicBridge.App.Services;

internal static class UninstallServices
{
    internal static bool IsBundledCable(string name) => name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);

    internal static bool HasVbCable()
    {
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active | DeviceState.Disabled))
        {
            using (device) if (IsBundledCable(device.FriendlyName)) return true;
        }
        return false;
    }

    internal static bool OtherMicBridgeInstanceIsRunning() => DeviceCatalog.GetApplications(includeBackground: true)
        .Any(process => process.ProcessId != Platform.ProcessId && process.Name.Equals("MicBridge", StringComparison.OrdinalIgnoreCase));

    // Only known application data files are removed; playlists' referenced audio files are never touched.
    internal static void ClearSettings(string? directory = null)
    {
        string root = Path.GetFullPath(directory ?? SettingsStore.DirectoryPath);
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("音桥设置目录是重解析点，未清理。");
        var known = new[] { "settings.json", "settings.json.tmp", "errors.log" }
            .Select(name => Path.Combine(root, name));
        var staging = Directory.EnumerateFiles(root)
            .Where(path => AtomicSettingsFile.IsTemporaryName(Path.GetFileName(path), "settings.json"));
        foreach (string path in known.Concat(staging).ToArray())
        {
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("音桥设置文件是重解析点，未清理。");
            File.Delete(path);
        }
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }
}

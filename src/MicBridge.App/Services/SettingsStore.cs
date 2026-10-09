using System.IO;
using MicBridge.App.Serialization;

namespace MicBridge.App.Services;

public sealed record ChannelSettings(double Volume = 100, double Gain = 0, bool Enabled = true, bool Muted = false, bool GainMode = false);
public sealed class AppSettings
{
    public string? OutputDeviceId { get; set; }
    public bool HardwareLoopbackMode { get; set; }
    public string? HardwareCaptureDeviceId { get; set; }
    public string? HardwareVerifiedOutputDeviceId { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public string? LoopbackDeviceId { get; set; }
    public int CaptureMode { get; set; }
    public int? SourceKind { get; set; }
    public double MixBalance { get; set; } = 50;
    public int LatencyMs { get; set; } = 60;
    public bool MonitorEnabled { get; set; }
    public double MonitorVolume { get; set; } = 70;
    public bool Loop { get; set; }
    public bool MicrophoneGateEnabled { get; set; }
    public bool MicrophoneHighPassEnabled { get; set; }
    public double MicrophoneGateThreshold { get; set; } = -50;
    public List<string> Playlist { get; set; } = [];
    public ChannelSettings Music { get; set; } = new();
    public ChannelSettings Application { get; set; } = new();
    public ChannelSettings Microphone { get; set; } = new();
    public ChannelSettings Master { get; set; } = new(80);
}

public static class SettingsStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicBridge");
    public static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public static AppSettings Load()
    {
        try
        {
            return LoadFrom(SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log(ex); return new(); }
    }
    internal static AppSettings LoadFrom(string path)
    {
        string? json = AtomicSettingsFile.ReadIfExists(path);
        var settings = json is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json) ?? new();
        settings.Playlist ??= []; settings.Music ??= new(); settings.Application ??= new();
        settings.Microphone ??= new(); settings.Master ??= new(80);
        return settings;
    }
    public static void Save(AppSettings settings) => SaveTo(SettingsPath, settings);
    internal static void SaveTo(string path, AppSettings settings) =>
        AtomicSettingsFile.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    public static void Log(Exception ex)
    {
        try { Directory.CreateDirectory(DirectoryPath); File.AppendAllText(Path.Combine(DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O} {ex}\n"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

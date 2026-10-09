using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MicBridge.App.Services;

internal static class DriverPackage
{
    internal const string ResourceName = "MicBridge.Drivers.VbCablePack45";
    internal const string PackageHash = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    internal const string InstallerHash = "734C35DFA6D98F48782A451633CEB471166EC70D60482FD89A1123D0EE3C4F41";
    internal static bool Supported => Platform.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    internal static byte[] ReadVerifiedPackage()
    {
        using var source = typeof(DriverPackage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("此版本未包含虚拟音频驱动包。");
        using var memory = new MemoryStream(); source.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (!Platform.Sha256(bytes).Equals(PackageHash, StringComparison.Ordinal))
            throw new InvalidDataException("内置驱动包校验失败，安装未开始。");
        return bytes;
    }

    internal static string ReadOriginalLicense()
    {
        using var memory = new MemoryStream(ReadVerifiedPackage());
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        using var source = (archive.GetEntry("readme.txt") ?? throw new InvalidDataException("驱动许可文件缺失。")).Open();
        using var reader = new StreamReader(source, Encoding.GetEncoding(28591));
        return reader.ReadToEnd();
    }

    internal static DriverStaging Prepare(string? parentDirectory = null)
    {
        if (!Supported) throw new PlatformNotSupportedException("内置安装入口目前支持 Windows x64。请从 VB-Audio 官网选择适合系统的驱动。");
        byte[] bytes = ReadVerifiedPackage();
        string parent = Path.GetFullPath(parentDirectory ?? Path.GetTempPath());
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("驱动临时目录不能是重解析点。");
        string directory = Path.Combine(parent, "MicBridge-driver-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("驱动临时目录已存在。");
        Directory.CreateDirectory(directory);
        var staging = new DriverStaging(directory);
        try
        {
            using var memory = new MemoryStream(bytes);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                // The pinned official package is flat. Reject paths, duplicates and oversized entries.
                if (entry.FullName != Path.GetFileName(entry.FullName) || entry.FullName.Contains('/') || entry.FullName.Contains('\\') ||
                    entry.FullName.Contains(':') || entry.Length is <= 0 or > 2_000_000 || staging.FileHashes.ContainsKey(entry.FullName))
                    throw new InvalidDataException("内置驱动包文件结构异常。");
                string path = Path.Combine(directory, entry.FullName);
                using var source = entry.Open();
                using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(target);
                using var file = File.OpenRead(path);
                staging.FileHashes.Add(entry.FullName, Platform.Sha256(file));
            }
            staging.VerifyFiles();
            Authenticode.Verify(staging.InstallerPath);
            return staging;
        }
        catch
        {
            staging.Dispose();
            throw;
        }
    }
}

internal sealed class DriverStaging : IDisposable
{
    internal string DirectoryPath { get; }
    internal string InstallerPath => Path.Combine(DirectoryPath, "VBCABLE_Setup_x64.exe");
    internal Dictionary<string, string> FileHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal string? CleanupError { get; private set; }
    private bool _disposed;

    internal DriverStaging(string directory) => DirectoryPath = directory;

    internal void VerifyFiles()
    {
        if (_disposed || (File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0 ||
            Directory.GetFileSystemEntries(DirectoryPath).Length != FileHashes.Count || FileHashes.Count != 31)
            throw new InvalidDataException("驱动安装文件发生变化。");
        foreach (var pair in FileHashes)
        {
            string path = Path.Combine(DirectoryPath, pair.Key);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("驱动文件不能是重解析点。");
            using var file = File.OpenRead(path);
            if (Platform.Sha256(file) != pair.Value) throw new InvalidDataException("驱动安装文件校验失败。");
        }
        if (!FileHashes.TryGetValue("VBCABLE_Setup_x64.exe", out string? hash) || hash != DriverPackage.InstallerHash)
            throw new InvalidDataException("驱动安装器校验失败。");
    }

    internal ProcessStartInfo CreateLaunchInfo()
    {
        VerifyFiles(); Authenticode.Verify(InstallerPath);
        return new ProcessStartInfo(InstallerPath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = DirectoryPath };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Only remove the fresh directory created for this preparation; never follow reparse points.
            if (!Directory.Exists(DirectoryPath)) return;
            if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(DirectoryPath).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("临时目录包含重解析点，未自动清理。");
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CleanupError = error.Message;
            SettingsStore.Log(error);
        }
    }
}

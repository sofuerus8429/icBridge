using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MicBridge.Audio.Compatibility;

public static class Platform
{
    public static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
    public static float Clamp(float value, float min, float max) => Math.Min(max, Math.Max(min, value));
    public static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static readonly int ProcessId = GetProcessId();
    public static string ProcessPath { get { using var process = Process.GetCurrentProcess(); return process.MainModule!.FileName; } }
    private static int GetProcessId() { using var process = Process.GetCurrentProcess(); return process.Id; }
    public static bool IsWindows() => Environment.OSVersion.Platform == PlatformID.Win32NT;
    private static readonly Version WindowsVersion = GetWindowsVersion();
    public static bool IsWindowsVersionAtLeast(int major, int minor, int build) => IsWindows() && WindowsVersion >= new Version(major, minor, build);
    private static Version GetWindowsVersion()
    {
        if (!IsWindows()) return new Version(0, 0, 0);
        var info = new OsVersionInfo { Size = Marshal.SizeOf<OsVersionInfo>(), ServicePack = "" };
        return RtlGetVersion(ref info) == 0 ? new Version(info.Major, info.Minor, info.Build) : Environment.OSVersion.Version;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public int Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
    }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref OsVersionInfo info);
    public static string Sha256(byte[] data) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(data)); }
    public static string Sha256(Stream data) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(data)); }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
    public static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination)) File.Replace(source, destination, null);
        else File.Move(source, destination);
    }
    public static Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout) => WithTimeout(task, timeout, CancellationToken.None);
    public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancel = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            try
            {
                Task completed = await Task.WhenAny(task, Task.Delay(timeout, cancel.Token), cancelled.Task).ConfigureAwait(false);
                if (completed == cancelled.Task) throw new OperationCanceledException(cancellationToken);
                if (completed != task) throw new TimeoutException();
                return await task.ConfigureAwait(false);
            }
            finally { cancel.Cancel(); }
        }
    }
    public static Task WaitForExitAsync(Process process)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) => { process.Exited -= handler; completion.TrySetResult(true); };
        process.Exited += handler;
        process.EnableRaisingEvents = true;
        if (process.HasExited) { process.Exited -= handler; completion.TrySetResult(true); }
        return completion.Task;
    }
}

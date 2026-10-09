using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MicBridge.App.Serialization;

namespace MicBridge.App.Services;

internal static class SingleFileVerification
{
    internal static void Run(string reportPath)
    {
        string[] notices = LicenseNotices.EmbeddedNames;
        int expectedNotices = 3;
#if !NETFRAMEWORK
        expectedNotices = 6;
#endif
        if (notices.Length != expectedNotices || !notices.Contains("MicBridge.Notices.VbCable") || !LicenseNotices.Read().Contains("Permission is hereby granted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The executable must include project, dependency and runtime license notices.");
        using var process = Process.GetCurrentProcess();
        string? coreClr = process.Modules.Cast<ProcessModule>().FirstOrDefault(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
        string[] sidecars = Directory.GetFileSystemEntries(AppContext.BaseDirectory);
        if (sidecars.Length != 1 || !sidecars[0].Equals(Platform.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the copied executable from a directory containing only that EXE.");
#if NETFRAMEWORK
        if (coreClr is not null || !RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal))
            throw new InvalidOperationException("The lightweight executable must use the Windows .NET Framework runtime.");
        string[] embedded = typeof(App).Assembly.GetManifestResourceNames();
        if (!embedded.Any(name => name.Contains("micbridge.audio.dll")) || !embedded.Any(name => name.Contains("naudio.wasapi.dll")))
            throw new InvalidOperationException("The lightweight executable must embed its audio dependencies.");
        foreach (string omitted in new[] { "naudio.asio", "naudio.midi", "naudio.winforms" })
            if (embedded.Any(name => name.StartsWith("costura." + omitted + ".", StringComparison.Ordinal)))
                throw new InvalidOperationException("The lightweight executable should omit unused audio UI, MIDI and ASIO modules.");
#else
        if (!RuntimeInformation.FrameworkDescription.Equals(".NET 8.0.31", StringComparison.Ordinal))
            throw new InvalidOperationException("The executable did not load the bundled runtime version.");
#endif
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, Runtime = RuntimeInformation.FrameworkDescription,
            Executable = Platform.ProcessPath, DirectoryEntries = sidecars, CoreClrPath = coreClr,
            EmbeddedNotices = notices, EmbeddedDriverPackageSha256 = DriverPackage.PackageHash,
            DriverInstalled = false, AudioStarted = false, UserSettingsSaved = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

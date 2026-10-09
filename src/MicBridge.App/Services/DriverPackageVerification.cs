using System.IO;
using System.Security.Cryptography;
using MicBridge.App.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MicBridge.App.ViewModels;

namespace MicBridge.App.Services;

internal static class DriverPackageVerification
{
    // Validate the actual bundled package and setup controls without invoking installation or UAC.
    internal static async Task RunAsync(string reportPath)
    {
        var passed = new List<string>();
        string parent = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(parent);
        byte[] package = DriverPackage.ReadVerifiedPackage();
        Require(Platform.Sha256(package) == DriverPackage.PackageHash,
            "The embedded official ZIP is byte-identical to VB-CABLE Pack45.");
        Require(DriverPackage.ReadOriginalLicense().Contains("LICENSE AGREEMENT FOR END-USER", StringComparison.Ordinal),
            "The original VB-CABLE readme and end-user license are available offline.");
        Require(LicenseNotices.Read().Contains("Donationware", StringComparison.OrdinalIgnoreCase) &&
            LicenseNotices.Read().Contains("https://www.vb-cable.com/", StringComparison.Ordinal),
            "License notices identify VB-Audio, Donationware and the product/donation link.");
        var staging = await Task.Run(() => DriverPackage.Prepare(parent));
        string stagedDirectory = staging.DirectoryPath;
        int stagedFiles = staging.FileHashes.Count;
        try
        {
            Require(stagedFiles == 31, "All 31 original installer, INF, SYS, CAT, icon and license files are extracted.");
            staging.VerifyFiles();
            Require(staging.FileHashes["VBCABLE_Setup_x64.exe"] == DriverPackage.InstallerHash,
                "The installer matches its pinned SHA-256.");
            Authenticode.Verify(staging.InstallerPath);
            Require(true, "Windows Authenticode validates the official installer signature.");
            var launch = staging.CreateLaunchInfo();
            Require(launch.UseShellExecute && launch.Verb == "runas" && launch.FileName == staging.InstallerPath &&
                launch.WorkingDirectory == stagedDirectory && string.IsNullOrEmpty(launch.Arguments),
                "Installation uses the official UI and Windows elevation; no silent install or registry commands.");
            string readme = Path.Combine(stagedDirectory, "readme.txt");
            byte[] original = File.ReadAllBytes(readme);
            File.AppendAllText(readme, "\nverification tamper\n");
            bool rejected = false;
            try { staging.CreateLaunchInfo(); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Modified installation files are rejected before elevation.");
            File.WriteAllBytes(readme, original); staging.VerifyFiles();
        }
        finally { staging.Dispose(); }
        Require(!Directory.Exists(stagedDirectory) && staging.CleanupError is null,
            "Fresh extracted test files are removed after validation.");

        using var model = new MainViewModel(false);
        var firstUseWindow = new DriverSetupWindow(model);
        Require(DriverPackage.Supported && firstUseWindow.InstallButton.IsEnabled,
            "The setup screen offers installation when its device list is initially empty.");
        await model.RefreshAsync();
        Require(!model.HasVirtualMicrophone || !model.InstallDriverCommand.CanExecute(null),
            "Existing virtual playback and recording devices disable duplicate installation.");
        var window = new DriverSetupWindow(model);
        Require(!window.InstallButton.IsEnabled || !model.HasVirtualMicrophone,
            "The actual setup button respects existing device detection.");
        Require(window.LaterButton.IsCancel && window.InstallButton.Content?.ToString() == "安装虚拟麦克风",
            "Setup offers installation and a dismiss action.");
        var content = (FrameworkElement)window.Content; window.Content = null;
        foreach (var size in new[] { new Size(620, 550), new Size(560, 520) })
        {
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            foreach (var button in new[] { window.InstallButton, window.LaterButton })
            {
                var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= size.Width + 1 && bounds.Bottom <= size.Height + 1,
                    $"{size.Width}x{size.Height}: {button.Content} stays within the setup window.");
            }
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = passed, EmbeddedPackageBytes = package.Length,
            PackageSha256 = DriverPackage.PackageHash, InstallerSha256 = DriverPackage.InstallerHash,
            OriginalFileCount = stagedFiles, ActualVirtualMicrophoneDetected = model.HasVirtualMicrophone,
            SignatureVerified = true, ExtractedFilesCleaned = !Directory.Exists(stagedDirectory),
            InstallerInvoked = false, RegistryChangedByTest = false, AudioStarted = false, UserSettingsSaved = false },
            new JsonSerializerOptions { WriteIndented = true }));

        void Require(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException(text);
            passed.Add(text);
        }
    }
}

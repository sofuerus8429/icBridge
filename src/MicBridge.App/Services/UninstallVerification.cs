using System.IO;
using System.Security.Cryptography;
using MicBridge.App.Serialization;
using System.Windows;
using MicBridge.App.ViewModels;

namespace MicBridge.App.Services;

internal static class UninstallVerification
{
    // Check cleanup on F-drive fixtures and inspect uninstall UI; never remove an installed driver.
    internal static async Task RunAsync(string reportPath)
    {
        var passed = new List<string>();
        Require(UninstallServices.IsBundledCable("CABLE Output (VB-Audio Virtual Cable)"),
            "Driver removal identifies the ordinary bundled VB-CABLE.");
        Require(!UninstallServices.IsBundledCable("Voicemeeter Input (VB-Audio Voicemeeter VAIO)") &&
            !UninstallServices.IsBundledCable("CABLE-A Input (VB-Audio Cable A)"),
            "Other virtual drivers do not enable the ordinary VB-CABLE removal action.");
        string fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "uninstall-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            foreach (string name in new[] { "settings.json", "settings.json.tmp", "errors.log", "settings.json." + Guid.NewGuid().ToString("N") + ".tmp" }) File.WriteAllText(Path.Combine(fixture, name), "test");
            File.WriteAllText(Path.Combine(fixture, "keep-music.txt"), "user audio reference");
            File.WriteAllText(Path.Combine(fixture, "settings.json.user-draft.tmp"), "user draft");
            UninstallServices.ClearSettings(fixture);
            Require(Directory.GetFiles(fixture).Length == 2 && File.ReadAllText(Path.Combine(fixture, "keep-music.txt")) == "user audio reference" &&
                File.ReadAllText(Path.Combine(fixture, "settings.json.user-draft.tmp")) == "user draft",
                "Settings cleanup removes known files and owned staging names, while preserving music references and similarly named user drafts.");
            UninstallServices.ClearSettings(fixture);
            Require(File.Exists(Path.Combine(fixture, "keep-music.txt")), "Repeated cleanup preserves unrelated files.");
            File.Delete(Path.Combine(fixture, "keep-music.txt"));
            File.Delete(Path.Combine(fixture, "settings.json.user-draft.tmp"));
            UninstallServices.ClearSettings(fixture);
            Require(!Directory.Exists(fixture), "An empty settings fixture directory is removed without recursive deletion.");
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true); }

        string? initialSettingsHash = SettingsHash();
        using (var savingModel = new MainViewModel(true))
        {
            savingModel.SuppressSettingsSave(); savingModel.SaveSettings();
        }
        Require(SettingsHash() == initialSettingsHash, "After clearing settings, save and dispose cannot recreate the saved configuration.");

        using var model = new MainViewModel(false);
        await model.RefreshAsync();
        Require(model.UninstallCommand.CanExecute(null), "The uninstall entry is available while mixing is stopped.");
        var window = new UninstallWindow(model);
        await window.RefreshDriverAsync();
        bool cableInstalled = UninstallServices.HasVbCable();
        Require(window.DriverCheckBox.IsEnabled == (cableInstalled && DriverPackage.Supported),
            "The actual driver checkbox follows VB-CABLE detection.");
        Require(window.SettingsCheckBox.IsChecked != true, "Clearing saved settings is opt-in.");
        window.DriverCheckBox.IsChecked = false; window.SettingsCheckBox.IsChecked = false; window.UpdateAction();
        Require(!window.RemoveButton.IsEnabled, "No removal starts when neither operation is selected.");
        window.SettingsCheckBox.IsChecked = true; window.UpdateAction();
        Require(window.RemoveButton.IsEnabled, "Settings-only cleanup can be selected independently of driver removal.");
        window.SettingsCheckBox.IsChecked = false; window.DriverCheckBox.IsChecked = cableInstalled; window.UpdateAction();
        var content = (FrameworkElement)window.Content; window.Content = null;
        foreach (var size in new[] { new Size(660, 600), new Size(580, 540) })
        {
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            foreach (var button in new[] { window.CloseButton, window.RemoveButton })
            {
                var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= size.Width + 1 && bounds.Bottom <= size.Height + 1,
                    $"{size.Width}x{size.Height}: {button.Content} stays within the uninstall window.");
            }
        }
        using (var staging = await Task.Run(() => DriverPackage.Prepare(Path.GetDirectoryName(Path.GetFullPath(reportPath)))))
        {
            var launch = staging.CreateLaunchInfo();
            Require(launch.Verb == "runas" && launch.UseShellExecute && string.IsNullOrEmpty(launch.Arguments),
                "Removal opens the same signed official maintenance tool with administrator approval.");
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = passed, ActualVbCableDetected = cableInstalled,
            DriverRemoved = false, OfficialToolInvoked = false, ActualUserSettingsUnchanged = SettingsHash() == initialSettingsHash,
            FixtureFilesCleaned = !Directory.Exists(fixture), AudioStarted = false }, new JsonSerializerOptions { WriteIndented = true }));

        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            passed.Add(description);
        }
        static string? SettingsHash() => File.Exists(SettingsStore.SettingsPath) ? Platform.Sha256(File.ReadAllBytes(SettingsStore.SettingsPath)) : null;
    }
}

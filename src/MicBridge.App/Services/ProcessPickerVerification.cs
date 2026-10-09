using System.IO;
using MicBridge.App.Serialization;
using MicBridge.App.ViewModels;
using MicBridge.Audio.Devices;

namespace MicBridge.App.Services;

internal static class ProcessPickerVerification
{
    // Exercise the production view models on the WPF Dispatcher without capturing audio or saving settings.
    internal static async Task RunAsync(int processId, string reportPath)
    {
        var picker = new ProcessPickerViewModel(processId);
        await picker.RefreshAsync();
        if (!picker.Processes.Cast<ApplicationInfo>().Any(p => p.ProcessId == Platform.ProcessId && !p.CanCapture))
            throw new InvalidOperationException("The full process list must include its own disabled process entry.");
        if (!picker.Processes.Cast<ApplicationInfo>().Any(p => p.SessionId == 0))
            throw new InvalidOperationException("The full process list must include system-session processes.");
        var target = picker.Processes.Cast<ApplicationInfo>().Single(p => p.ProcessId == processId);
        var passed = new List<string> { "Live process is available in the picker.", "Full list includes system-session processes and the disabled current-process entry." };
        picker.SearchText = processId.ToString();
        Require(picker.Processes.Cast<ApplicationInfo>().Any(p => p.ProcessId == processId), "PID search includes the target.");
        picker.SearchText = $"{target.Name}.exe";
        Require(picker.Processes.Cast<ApplicationInfo>().Any(p => p.ProcessId == processId), "Executable-name search includes the target.");
        if (target.HasWindow)
        {
            picker.SearchText = target.WindowTitle;
            Require(picker.Processes.Cast<ApplicationInfo>().Any(p => p.ProcessId == processId), "Window-title search includes the target.");
        }
        picker.SearchText = "";
        picker.OnlyAudio = true;
        Require(picker.Processes.Cast<ApplicationInfo>().All(p => p.HasAudioSession), "Audio-session filter excludes processes without a session.");
        picker.OnlyAudio = false;
        picker.SearchText = processId.ToString(); picker.SelectedProcess = target;
        Require(picker.CanAccept, "The visible selection can be accepted.");
        await picker.RefreshAsync();
        Require(picker.CanAccept && picker.SelectedProcess?.ProcessId == processId,
            "Refreshing the picker preserves the same live process selection.");
        using var main = new MainViewModel(loadSettings: false);
        main.ApplicationChannel.Enabled = false; main.UseProcessSource(target);
        Require(main.CaptureMode == 1 && main.SelectedApplication?.ProcessId == processId && main.ApplicationChannel.Enabled,
            "Accepting a process enables its capture mode and mixer channel.");
        await main.RefreshAsync();
        Require(main.SelectedApplication?.ProcessId == processId && main.CaptureMode == 1,
            "Refreshing devices preserves the selected background or window process.");
        main.MixBalance = 0; Require(main.MixBalanceText == "只放音乐", "Simple mix slider can select only music.");
        main.MixBalance = 100; Require(main.MixBalanceText == "只放人声", "Simple mix slider can select only microphone.");
        main.MixBalance = 50;
        await main.ChangeSourceKindAsync(0);
        Require(main.IsLocalSource && main.CaptureMode == 0 && main.Music.Enabled && !main.ApplicationChannel.Enabled,
            "Changing to local source disables process capture and enables the local channel.");
        await main.ChangeSourceKindAsync(1);
        Require(main.IsApplicationSource && main.CaptureMode == 1 && !main.Music.Enabled && main.ApplicationChannel.Enabled,
            "Changing to process source disables the local channel and enables process capture.");
        if (target.StartTimeUtcTicks is long start)
        {
            bool rejected = false;
            try { main.UseProcessSource(target with { StartTimeUtcTicks = start - 1 }); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "A stale process identity is rejected before capture.");
        }
        picker.SearchText = Guid.NewGuid().ToString("N");
        Require(!picker.Processes.Cast<ApplicationInfo>().Any() && picker.SelectedProcess is null && !picker.CanAccept,
            "An unmatched search clears selection and disables acceptance.");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { ProcessId = processId, target.Name, target.HasWindow,
            target.HasAudioSession, Passed = passed }, new JsonSerializerOptions { WriteIndented = true }));

        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            passed.Add(description);
        }
    }
}

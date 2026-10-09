using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MicBridge.App.Services;

internal static class LicenseNotices
{
    internal static string[] EmbeddedNames => typeof(LicenseNotices).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("MicBridge.Notices.", StringComparison.Ordinal)).OrderBy(name => name).ToArray();

    internal static string Read()
    {
        var assembly = typeof(LicenseNotices).Assembly;
        var text = new StringBuilder();
        foreach (string name in EmbeddedNames)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            text.AppendLine(name.Replace("MicBridge.Notices.", "")).AppendLine(new string('─', 48))
                .AppendLine(reader.ReadToEnd()).AppendLine();
        }
        string directory = Path.Combine(AppContext.BaseDirectory, "licenses");
        text.AppendLine("VB-CABLE original readme and end-user license").AppendLine(DriverPackage.ReadOriginalLicense()).AppendLine();
        if (Directory.Exists(directory))
            foreach (string path in Directory.EnumerateFiles(directory, "*.txt"))
                text.AppendLine(Path.GetFileName(path)).AppendLine(File.ReadAllText(path)).AppendLine();
        return text.ToString();
    }

    internal static void Show(Window owner)
    {
        var body = new TextBox { Text = Read(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(24), FontSize = 12 };
        new Window { Title = "开源许可 · MicBridge", Owner = owner, Width = 800, Height = 620, MinWidth = 600, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = body }.ShowDialog();
    }
}

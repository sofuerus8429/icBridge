using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string application = Path.Combine(directory, "MicBridge.exe");
        if (!File.Exists(application))
        {
            MessageBox.Show("请把卸载入口与 MicBridge.exe 放在同一文件夹，再双击运行。也可以从音桥的高级设置打开卸载与清理。", "音桥卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        try
        {
            Process.Start(new ProcessStartInfo(application, "--uninstall") { UseShellExecute = true, WorkingDirectory = directory });
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "无法打开音桥卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

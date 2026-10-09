using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MicBridge.App.Infrastructure;

internal static class WindowBackdrop
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    public static bool Apply(Window window)
    {
        if (!Platform.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
        var source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null) return false;
        int dark = 1, rounded = 2, acrylic = 3;
        DwmSetWindowAttribute(source.Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(source.Handle, 33, ref rounded, sizeof(int));
        if (DwmSetWindowAttribute(source.Handle, 38, ref acrylic, sizeof(int)) != 0) return false;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(source.Handle, ref margins) != 0) return false;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        return true;
    }
}

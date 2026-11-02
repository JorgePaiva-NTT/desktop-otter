using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OtterDesktop;

internal static class WindowPrivacy
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static bool Apply(Window window, bool excludeFromCapture)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return Apply(handle, excludeFromCapture);
    }

    public static bool Apply(IntPtr handle, bool excludeFromCapture)
    {
        return handle != IntPtr.Zero && SetWindowDisplayAffinity(
            handle,
            excludeFromCapture ? WdaExcludeFromCapture : WdaNone);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);
}

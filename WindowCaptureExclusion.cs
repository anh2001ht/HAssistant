using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SystemAudioTranscript;

internal static class WindowCaptureExclusion
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static int? Apply(Window window) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WdaExcludeFromCapture)
            ? null
            : Marshal.GetLastWin32Error();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);
}

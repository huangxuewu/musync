using System.Runtime.InteropServices;

namespace Musync.App;

internal static class DesktopMotionPreferences
{
    public static bool AnimationsEnabled => !OperatingSystem.IsWindows()
        || !SystemParametersInfo(0x1042, 0, out var enabled, 0) || enabled;

    // SPI_GETCLIENTAREAANIMATION reads Windows' Accessibility animation setting.
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool enabled, uint flags);
}

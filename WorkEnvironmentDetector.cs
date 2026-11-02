using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.Win32;

namespace OtterDesktop;

internal readonly record struct WorkEnvironmentState(
    bool TeamsMeetingActive,
    bool TeamsSharingActive,
    bool FullscreenAppActive);

internal static class WorkEnvironmentDetector
{
    public static WorkEnvironmentState Detect(int ownProcessId)
    {
        var teamsWindows = GetTeamsWindows();
        var meeting = false;
        var sharing = false;

        foreach (var window in teamsWindows)
        {
            var title = GetWindowTitle(window).ToLowerInvariant();
            meeting |= ContainsAny(title, "meeting", "call in progress", "calling");
            sharing |= ContainsAny(title, "you're presenting", "you are presenting", "screen sharing", "stop sharing");

            if (meeting && sharing)
            {
                break;
            }

            InspectTeamsControls(window, ref meeting, ref sharing);
        }

        if (!meeting && teamsWindows.Count > 0)
        {
            meeting = IsTeamsUsingMicrophone();
        }

        return new WorkEnvironmentState(
            meeting || sharing,
            sharing,
            IsPresentationModeActive() || IsForegroundWindowFullscreen(ownProcessId));
    }

    private static List<IntPtr> GetTeamsWindows()
    {
        var result = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                var name = process.ProcessName;
                if (name.Equals("Teams", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("MSTeams", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(window);
                }
            }
            catch
            {
                // A process can close while Windows is being enumerated.
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void InspectTeamsControls(IntPtr window, ref bool meeting, ref bool sharing)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            var limit = Math.Min(buttons.Count, 500);
            for (var index = 0; index < limit; index++)
            {
                var name = (buttons[index].Current.Name ?? string.Empty).ToLowerInvariant();
                var automationId = (buttons[index].Current.AutomationId ?? string.Empty).ToLowerInvariant();

                meeting |= ContainsAny(name, "leave", "hang up", "end call") ||
                           ContainsAny(automationId, "hangup", "hang-up", "leave-call");
                sharing |= ContainsAny(name, "stop sharing", "stop presenting", "you're sharing", "you are sharing") ||
                           ContainsAny(automationId, "stop-sharing", "stop-presenting");

                if (meeting && sharing)
                {
                    return;
                }
            }
        }
        catch
        {
            // Teams UI automation can be unavailable under stricter corporate policies.
        }
    }

    private static bool IsTeamsUsingMicrophone()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var microphone = Registry.CurrentUser.OpenSubKey(path);
            return microphone is not null && FindActiveTeamsCapability(microphone, string.Empty, 0);
        }
        catch
        {
            return false;
        }
    }

    private static bool FindActiveTeamsCapability(RegistryKey key, string keyPath, int depth)
    {
        if (depth > 4)
        {
            return false;
        }

        var pathLooksLikeTeams = keyPath.Contains("teams", StringComparison.OrdinalIgnoreCase);
        if (pathLooksLikeTeams)
        {
            var start = ReadRegistryNumber(key.GetValue("LastUsedTimeStart"));
            var stop = ReadRegistryNumber(key.GetValue("LastUsedTimeStop"));
            if (start > 0 && (stop == 0 || start > stop))
            {
                return true;
            }
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            try
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey is not null && FindActiveTeamsCapability(subKey, keyPath + "\\" + subKeyName, depth + 1))
                {
                    return true;
                }
            }
            catch
            {
                // Skip registry entries that the current user cannot inspect.
            }
        }

        return false;
    }

    private static long ReadRegistryNumber(object? value) => value switch
    {
        long number => number,
        int number => number,
        string text when long.TryParse(text, out var number) => number,
        _ => 0
    };

    private static bool IsForegroundWindowFullscreen(int ownProcessId)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || IsIconic(window))
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == ownProcessId)
        {
            return false;
        }

        var className = new StringBuilder(128);
        GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            return false;
        }
        if (className.ToString().Equals("screenClass", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!GetWindowRect(window, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        const int tolerance = 3;
        return windowRect.Left <= info.Monitor.Left + tolerance &&
               windowRect.Top <= info.Monitor.Top + tolerance &&
               windowRect.Right >= info.Monitor.Right - tolerance &&
               windowRect.Bottom >= info.Monitor.Bottom - tolerance;
    }

    private static bool IsPresentationModeActive() =>
        SHQueryUserNotificationState(out var state) >= 0 && state == PresentationMode;

    private static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var title = new StringBuilder(length + 1);
        GetWindowText(window, title, title.Capacity);
        return title.ToString();
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int PresentationMode = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAudioSwitcher.Core;

/// <summary>Cheap check for the trigger app: one EnumWindows pass, no process scanning unless asked.</summary>
public static class TriggerDetector
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder sb, int max);

    public static bool IsRunning(string trigger)
    {
        trigger = trigger.Trim();
        if (trigger.Length == 0) return false;

        if (trigger.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(trigger));
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }

        var found = false;
        var sb = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var len = GetWindowTextLengthW(hwnd);
            if (len == 0) return true;
            sb.Clear();
            sb.EnsureCapacity(len + 1);
            GetWindowTextW(hwnd, sb, len + 1);
            if (sb.ToString().Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

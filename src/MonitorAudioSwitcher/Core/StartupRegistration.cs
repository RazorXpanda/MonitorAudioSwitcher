using Microsoft.Win32;

namespace MonitorAudioSwitcher.Core;

/// <summary>Per-user "run at sign-in" entry. No admin rights needed.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorAudioSwitcher";

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>True if the Run entry points somewhere other than this exe (e.g. the old Python script).</summary>
    public static bool PointsElsewhere
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.Equals(s, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }
}

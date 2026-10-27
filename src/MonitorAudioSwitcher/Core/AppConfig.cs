using System.IO;
using System.Text.Json;

namespace MonitorAudioSwitcher.Core;

public enum ProfileKind { Desk, Tv }

public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }
    public string Text { get; set; } = "";
    public bool IsEmpty => Key == 0;
}

public sealed class Profile
{
    public DisplayProfile Display { get; set; } = new();
    /// <summary>Core Audio endpoint id, e.g. "{0.0.0.00000000}.{guid}". Empty = leave audio alone.</summary>
    public string AudioDeviceId { get; set; } = "";
    public string AudioDeviceName { get; set; } = "";
    public HotkeyBinding Hotkey { get; set; } = new();
}

public sealed class AppConfig
{
    public Profile Desk { get; set; } = new();
    public Profile Tv { get; set; } = new();

    /// <summary>Window title (substring) or process name ending in .exe that means "switch to TV".</summary>
    public string Trigger { get; set; } = "Steam Big Picture Mode";
    public bool AutoSwitch { get; set; } = true;
    public int RevertDelaySeconds { get; set; } = 3;
    public bool RestoreDeskOnShutdown { get; set; } = true;
    public bool RestoreDeskOnLaunch { get; set; } = true;

    public Profile Get(ProfileKind kind) => kind == ProfileKind.Desk ? Desk : Tv;

    // ------------------------------------------------------------ persistence

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorAudioSwitcher");

    private static string FilePath => Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object SaveGate = new();

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Write($"Config could not be read, starting fresh: {ex.Message}");
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { /* best effort */ }
            return new AppConfig();
        }

        var fresh = new AppConfig();
        fresh.ImportLegacy();
        return fresh;
    }

    /// <summary>
    /// Picks up audio devices and trigger from the old Python version's switcher_config.json, if it sits
    /// next to this exe or a few folders above it. Its display configs are MultiMonitorTool files and
    /// can't be reused, so displays still need one capture each.
    /// </summary>
    private void ImportLegacy()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
        {
            var legacy = Path.Combine(dir.FullName, "switcher_config.json");
            if (!File.Exists(legacy)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                var root = doc.RootElement;
                string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

                Desk.AudioDeviceId = Str("audio_a_guid");
                Desk.AudioDeviceName = Str("audio_a");
                Tv.AudioDeviceId = Str("audio_b_guid");
                Tv.AudioDeviceName = Str("audio_b");
                if (Str("target_proc") is { Length: > 0 } trigger) Trigger = trigger;
                Log.Write($"Imported audio settings from {legacy}.");
            }
            catch (Exception ex)
            {
                Log.Write($"Could not import {legacy}: {ex.Message}");
            }
            return;
        }
    }

    public void Save()
    {
        lock (SaveGate)
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, true);
        }
    }
}

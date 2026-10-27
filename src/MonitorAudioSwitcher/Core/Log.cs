using System.IO;

namespace MonitorAudioSwitcher.Core;

public sealed record LogEntry(DateTime Time, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppConfig.Folder, "switcher.log");

    public static event Action<LogEntry>? Written;

    public static void Write(string message)
    {
        var entry = new LogEntry(DateTime.Now, message);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppConfig.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{entry.Time:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never break switching.
            }
        }
        Written?.Invoke(entry);
    }

    public static string LogFile => FilePath;
}

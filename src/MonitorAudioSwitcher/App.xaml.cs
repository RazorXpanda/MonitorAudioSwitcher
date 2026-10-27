using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MonitorAudioSwitcher.Core;
using Forms = System.Windows.Forms;

namespace MonitorAudioSwitcher;

public partial class App : Application
{
    private const string MutexName = @"Local\MonitorAudioSwitcher.Instance";
    private const string ShowEventName = @"Local\MonitorAudioSwitcher.Show";

    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);
    [DllImport("user32.dll")] private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private HwndSource? _host;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _autoItem;
    private MainWindow? _window;
    private bool _trayHintShown;

    public AppConfig Config { get; private set; } = null!;
    public SwitcherEngine Engine { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    public event Action? DisplaysChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running (probably in the tray) - ask it to show its window instead.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"Unexpected error: {ex.Exception.Message}");
            ex.Handled = true;
        };

        // Ask Windows to notify us before most other apps at shutdown, so the desk layout is restored early.
        SetProcessShutdownParameters(0x3FF, 0);

        Config = AppConfig.Load();
        Engine = new SwitcherEngine(Config);

        if (StartupRegistration.IsEnabled && StartupRegistration.PointsElsewhere)
        {
            StartupRegistration.Set(true);
            Log.Write("Updated the Windows startup entry to point at this app.");
        }

        CreateHostWindow();
        CreateTray();
        RegisterHotkeys();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true }.Start();

        var autostart = e.Args.Any(a => a is "--autostart" or "-a" or "--minimized");
        Log.Write(autostart ? "Started with Windows." : "Started.");
        Engine.Start();

        if (!autostart) ShowMainWindow();
    }

    // ------------------------------------------------------------------ window / tray

    public void ShowMainWindow()
    {
        _window ??= new MainWindow();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void OnMainWindowHidden()
    {
        if (_trayHintShown || _tray == null) return;
        _trayHintShown = true;
        _tray.ShowBalloonTip(3000, "Still running", "Monitor & Audio Switcher keeps watching from the tray.", Forms.ToolTipIcon.None);
    }

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Switch to Desk now", null, (_, _) => _ = Engine.ApplyAsync(ProfileKind.Desk, "tray"));
        menu.Items.Add("Switch to TV now", null, (_, _) => _ = Engine.ApplyAsync(ProfileKind.Tv, "tray"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _autoItem = new Forms.ToolStripMenuItem("Auto-switch", null, (_, _) => SetAutoSwitch(!Config.AutoSwitch))
        {
            Checked = Config.AutoSwitch,
        };
        menu.Items.Add(_autoItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Monitor & Audio Switcher",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowMainWindow();
        };
    }

    public void SetAutoSwitch(bool enabled)
    {
        Config.AutoSwitch = enabled;
        Config.Save();
        if (enabled) Engine.ResetDetection();
        if (_autoItem != null) _autoItem.Checked = enabled;
        Log.Write(enabled ? "Auto-switch enabled." : "Auto-switch paused.");
        _window?.RefreshAll();
    }

    public bool IsExiting { get; private set; }

    public void ExitApp()
    {
        IsExiting = true;
        Engine.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        UnregisterHotKey(_host?.Handle ?? IntPtr.Zero, 1);
        UnregisterHotKey(_host?.Handle ?? IntPtr.Zero, 2);
        Shutdown();
    }

    // ------------------------------------------------------------------ host window

    /// <summary>
    /// Hidden top-level window (not message-only: those don't get WM_QUERYENDSESSION) that owns hotkeys
    /// and receives shutdown / display-change broadcasts even while the UI is closed.
    /// </summary>
    private void CreateHostWindow()
    {
        _host = new HwndSource(new HwndSourceParameters("MonitorAudioSwitcherHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _host.AddHook(HostProc);
    }

    private IntPtr HostProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_QUERYENDSESSION:
                ShutdownBlockReasonCreate(hwnd, "Switching the display back to your desk monitor…");
                try
                {
                    Engine.PrepareForShutdown();
                }
                finally
                {
                    ShutdownBlockReasonDestroy(hwnd);
                }
                handled = true;
                return 1;

            case WM_ENDSESSION:
                if (wParam == IntPtr.Zero)
                {
                    Log.Write("Shutdown was cancelled.");
                    Engine.ShutdownCancelled();
                }
                else
                {
                    // Normally already done in WM_QUERYENDSESSION; this covers shutdowns that skip it.
                    Engine.PrepareForShutdown();
                    if (_tray != null) _tray.Visible = false;
                }
                break;

            case WM_DISPLAYCHANGE:
                DisplaysChanged?.Invoke();
                break;

            case WM_HOTKEY:
                var kind = wParam.ToInt32() == 1 ? ProfileKind.Desk : ProfileKind.Tv;
                _ = Engine.ApplyAsync(kind, "hotkey");
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Returns a message for each hotkey that could not be registered.</summary>
    public List<string> RegisterHotkeys()
    {
        var errors = new List<string>();
        if (_host == null) return errors;

        foreach (var (id, kind) in new[] { (1, ProfileKind.Desk), (2, ProfileKind.Tv) })
        {
            UnregisterHotKey(_host.Handle, id);
            var hk = Config.Get(kind).Hotkey;
            if (hk.IsEmpty) continue;
            if (!RegisterHotKey(_host.Handle, id, hk.Modifiers | MOD_NOREPEAT, hk.Key))
            {
                var msg = $"Hotkey {hk.Text} is already used by another app.";
                errors.Add(msg);
                Log.Write(msg);
            }
        }
        return errors;
    }
}

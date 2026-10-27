using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MonitorAudioSwitcher.Core;
using Wpf.Ui.Controls;

namespace MonitorAudioSwitcher;

public partial class MainWindow : FluentWindow
{
    private static readonly int[] Delays = [0, 3, 5, 10, 30];

    private readonly ObservableCollection<LogEntry> _activity = new();
    private readonly DispatcherTimer _footerReset = new() { Interval = TimeSpan.FromSeconds(6) };
    private string _layoutName = "";

    private static App TheApp => App.Current;
    private static AppConfig Config => TheApp.Config;

    public MainWindow()
    {
        InitializeComponent();

        DeskCard.Init(ProfileKind.Desk);
        TvCard.Init(ProfileKind.Tv);
        DeskCard.StatusMessage += ShowFooter;
        TvCard.StatusMessage += ShowFooter;

        TriggerBox.Text = Config.Trigger;
        DelayCombo.ItemsSource = Delays.Select(d => d == 0 ? "Immediately" : $"{d} seconds").ToList();
        DelayCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Delays, Config.RevertDelaySeconds));
        if (!Delays.Contains(Config.RevertDelaySeconds)) DelayCombo.SelectedIndex = 1;

        ActivityList.ItemsSource = _activity;
        Log.Written += OnLogWritten;
        TheApp.Engine.StateChanged += OnEngineStateChanged;
        TheApp.DisplaysChanged += OnDisplaysChanged;

        _footerReset.Tick += (_, _) =>
        {
            _footerReset.Stop();
            FooterText.Text = "Closing this window keeps the switcher running in the tray.";
        };

        RefreshAll();
    }

    // ------------------------------------------------------------ refresh

    public void RefreshAll()
    {
        AutoToggle.IsChecked = Config.AutoSwitch;
        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        ShutdownToggle.IsChecked = Config.RestoreDeskOnShutdown;
        LaunchToggle.IsChecked = Config.RestoreDeskOnLaunch;
        DeskCard.SetTrigger(Config.Trigger);
        TvCard.SetTrigger(Config.Trigger);
        DeskCard.RefreshDisplays();
        TvCard.RefreshDisplays();
        RefreshSystem();
        RefreshStatus();
    }

    private void RefreshSystem()
    {
        try
        {
            var snap = DisplayManager.GetSnapshot();
            CurrentMonitors.ItemsSource = snap.Monitors;
            _layoutName = TheApp.Engine.DescribeCurrentLayout(snap);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read displays: {ex.Message}");
        }

        try
        {
            var devices = AudioManager.GetPlaybackDevices();
            CurrentAudio.Text = devices.FirstOrDefault(d => d.IsDefault)?.Name ?? "None";
            DeskCard.RefreshAudio(devices);
            TvCard.RefreshAudio(devices);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read audio devices: {ex.Message}");
        }

        DeskCard.SetActive(_layoutName == "Desk");
        TvCard.SetActive(_layoutName == "TV");
    }

    private void RefreshStatus()
    {
        var engine = TheApp.Engine;
        BusyRing.Visibility = engine.IsApplying ? Visibility.Visible : Visibility.Collapsed;
        DeskCard.SetBusy(engine.IsApplying);
        TvCard.SetBusy(engine.IsApplying);

        StatusTitle.Text = engine.IsApplying ? "Switching…" : _layoutName switch
        {
            "Desk" => "Desk setup active",
            "TV" => "TV setup active",
            _ => "Custom display layout",
        };
        StatusDot.Fill = _layoutName switch
        {
            "Desk" => (Brush)FindResource("DeskAccent"),
            "TV" => (Brush)FindResource("TvAccent"),
            _ => Brushes.Gray,
        };

        var watching = Config.AutoSwitch
            ? $"Watching for '{Config.Trigger}' – {(engine.TriggerRunning ? "open" : "not running")}"
            : "Auto-switch is paused";
        StatusDetail.Text = $"{watching}  ·  {engine.LastAction}";
    }

    private void OnEngineStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!TheApp.Engine.IsApplying) RefreshSystem();
        RefreshStatus();
    });

    private void OnDisplaysChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (TheApp.Engine.IsApplying) return;
        RefreshSystem();
        RefreshStatus();
    });

    private void OnLogWritten(LogEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        _activity.Insert(0, entry);
        while (_activity.Count > 60) _activity.RemoveAt(_activity.Count - 1);
    });

    private void ShowFooter(string text)
    {
        FooterText.Text = text;
        _footerReset.Stop();
        _footerReset.Start();
    }

    // ------------------------------------------------------------ handlers

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshSystem();
        RefreshStatus();
    }

    private void AutoToggle_Click(object sender, RoutedEventArgs e) => TheApp.SetAutoSwitch(AutoToggle.IsChecked == true);

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.Set(StartupToggle.IsChecked == true);
            Log.Write(StartupToggle.IsChecked == true ? "Will run at Windows sign-in." : "Removed from Windows startup.");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not change startup setting: {ex.Message}");
            StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        }
    }

    private void ShutdownToggle_Click(object sender, RoutedEventArgs e)
    {
        Config.RestoreDeskOnShutdown = ShutdownToggle.IsChecked == true;
        Config.Save();
    }

    private void LaunchToggle_Click(object sender, RoutedEventArgs e)
    {
        Config.RestoreDeskOnLaunch = LaunchToggle.IsChecked == true;
        Config.Save();
    }

    private void DelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DelayCombo.SelectedIndex < 0) return;
        Config.RevertDelaySeconds = Delays[DelayCombo.SelectedIndex];
        Config.Save();
    }

    private void TriggerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveTrigger();
    }

    private void TriggerBox_LostFocus(object sender, RoutedEventArgs e) => SaveTrigger();

    private void SaveTrigger()
    {
        var value = TriggerBox.Text.Trim();
        if (value.Length == 0) value = "Steam Big Picture Mode";
        if (value == Config.Trigger) return;
        Config.Trigger = value;
        TriggerBox.Text = value;
        Config.Save();
        TheApp.Engine.ResetDetection();
        Log.Write($"Trigger changed to '{value}'.");
        RefreshAll();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Log.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowFooter($"Could not open log: {ex.Message}");
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => TheApp.ExitApp();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (TheApp.IsExiting) return;
        // Closing the window just hides it; the switcher keeps running in the tray.
        e.Cancel = true;
        Hide();
        TheApp.OnMainWindowHidden();
    }
}

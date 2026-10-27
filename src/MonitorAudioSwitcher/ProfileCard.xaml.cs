using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorAudioSwitcher.Core;
using Wpf.Ui.Controls;

namespace MonitorAudioSwitcher;

public partial class ProfileCard : UserControl
{
    private static readonly AudioDevice NoChange = new("", "Don't change audio", false);

    private ProfileKind _kind;
    private bool _loadingAudio;

    public event Action<string>? StatusMessage;

    public ProfileCard() => InitializeComponent();

    private static App TheApp => App.Current;
    private Profile Profile => TheApp.Config.Get(_kind);
    private string Label => _kind == ProfileKind.Desk ? "Desk" : "TV";

    public void Init(ProfileKind kind)
    {
        _kind = kind;
        var accent = (Brush)FindResource(kind == ProfileKind.Desk ? "DeskAccent" : "TvAccent");
        IconBadge.Background = accent;
        HeaderIcon.Symbol = kind == ProfileKind.Desk ? SymbolRegular.Desktop24 : SymbolRegular.Tv24;
        TitleText.Text = kind == ProfileKind.Desk ? "Desk setup" : "TV setup";
        ApplyButton.Content = $"Switch to {Label} now";
        RefreshDisplays();
        RefreshHotkey();
    }

    public void SetTrigger(string trigger) =>
        SubtitleText.Text = _kind == ProfileKind.Desk
            ? $"When '{trigger}' is closed"
            : $"When '{trigger}' is open";

    public void SetActive(bool active) => ActivePill.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

    public void SetBusy(bool busy) => ApplyButton.IsEnabled = !busy;

    // ------------------------------------------------------------ displays

    public void RefreshDisplays()
    {
        var d = Profile.Display;
        MonitorList.ItemsSource = d.Monitors;
        if (d.IsEmpty)
        {
            NoDisplayText.Visibility = Visibility.Visible;
            NoDisplayText.Text = _kind == ProfileKind.Desk
                ? "Not captured yet. Set Windows up the way you want it at your desk (monitor on, TV off, correct refresh rate), then press Capture."
                : "Not captured yet. Switch Windows to TV only (Win+P → Second screen only, check the TV's refresh rate), then press Capture.";
            CapturedText.Text = "";
        }
        else
        {
            NoDisplayText.Visibility = Visibility.Collapsed;
            CapturedText.Text = $"Captured {d.CapturedAt:MMM d, HH:mm}";
        }
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Profile.Display = DisplayManager.Capture();
            TheApp.Config.Save();
            RefreshDisplays();
            var summary = string.Join(", ", Profile.Display.Monitors.Select(m => $"{m.DisplayName} {m.RefreshHz:0.##} Hz"));
            Log.Write($"Captured {Label} layout: {summary}");
            StatusMessage?.Invoke($"{Label} layout saved.");
        }
        catch (Exception ex)
        {
            Log.Write($"Capture failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ audio

    public void RefreshAudio(List<AudioDevice> devices)
    {
        _loadingAudio = true;
        var items = new List<AudioDevice> { NoChange };
        items.AddRange(devices);

        // Keep the saved device selectable even while it is unplugged (e.g. TV off).
        if (!string.IsNullOrEmpty(Profile.AudioDeviceId) && devices.All(d => d.Id != Profile.AudioDeviceId))
            items.Add(new AudioDevice(Profile.AudioDeviceId, $"{Profile.AudioDeviceName} (not connected)", false));

        AudioCombo.ItemsSource = items;
        AudioCombo.SelectedItem = items.FirstOrDefault(i => i.Id == Profile.AudioDeviceId) ?? NoChange;
        _loadingAudio = false;
    }

    private void AudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudio || AudioCombo.SelectedItem is not AudioDevice dev) return;
        Profile.AudioDeviceId = dev.Id;
        Profile.AudioDeviceName = dev.Id == "" ? "" : dev.Name.Replace(" (not connected)", "");
        TheApp.Config.Save();
    }

    // ------------------------------------------------------------ hotkey

    private void RefreshHotkey() => HotkeyBox.Text = Profile.Hotkey.Text;

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab)
            return;
        if (key is Key.Escape or Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            SaveHotkey(new HotkeyBinding());
            return;
        }

        var mods = Keyboard.Modifiers;
        var isFKey = key >= Key.F1 && key <= Key.F24;
        if (mods == ModifierKeys.None && !isFKey)
        {
            StatusMessage?.Invoke("Use at least one modifier (Ctrl / Alt / Shift / Win), or an F-key.");
            return;
        }

        uint m = 0;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) { m |= 0x2; parts.Add("Ctrl"); }
        if (mods.HasFlag(ModifierKeys.Alt)) { m |= 0x1; parts.Add("Alt"); }
        if (mods.HasFlag(ModifierKeys.Shift)) { m |= 0x4; parts.Add("Shift"); }
        if (mods.HasFlag(ModifierKeys.Windows)) { m |= 0x8; parts.Add("Win"); }
        parts.Add(new KeyConverter().ConvertToString(key) ?? key.ToString());

        SaveHotkey(new HotkeyBinding
        {
            Modifiers = m,
            Key = (uint)KeyInterop.VirtualKeyFromKey(key),
            Text = string.Join(" + ", parts),
        });
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => SaveHotkey(new HotkeyBinding());

    private void SaveHotkey(HotkeyBinding hk)
    {
        Profile.Hotkey = hk;
        TheApp.Config.Save();
        RefreshHotkey();
        var errors = TheApp.RegisterHotkeys();
        StatusMessage?.Invoke(errors.Count > 0 ? errors[0] : hk.IsEmpty ? $"{Label} hotkey cleared." : $"{Label} hotkey set to {hk.Text}.");
        Keyboard.ClearFocus();
    }

    // ------------------------------------------------------------ apply

    private void Apply_Click(object sender, RoutedEventArgs e) => _ = TheApp.Engine.ApplyAsync(_kind, "manual");
}

namespace MonitorAudioSwitcher.Core;

/// <summary>
/// Watches for the trigger app and applies profiles. Switching is edge-triggered: it reacts when the trigger
/// opens or closes, so a manual switch (button/hotkey/tray) is never fought by the watcher.
/// </summary>
public sealed class SwitcherEngine : IDisposable
{
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _watcher;

    private bool? _triggerSeen;      // null = not evaluated yet (first pass after start)
    private DateTime? _closedSince;
    private volatile bool _suspended; // set during Windows shutdown so we never switch back to the TV

    public event Action? StateChanged;

    public bool TriggerRunning { get; private set; }
    public bool IsApplying { get; private set; }
    public string LastAction { get; private set; } = "Idle";

    public SwitcherEngine(AppConfig config) => _config = config;

    public void Start()
    {
        _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "TriggerWatcher" };
        _watcher.Start();
    }

    /// <summary>Re-evaluate from scratch, e.g. after the auto-switch toggle is turned on.</summary>
    public void ResetDetection()
    {
        _triggerSeen = null;
        _closedSince = null;
    }

    private void WatchLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Write($"Watcher error: {ex.Message}");
            }
            _cts.Token.WaitHandle.WaitOne(1000);
        }
    }

    private void Tick()
    {
        var running = TriggerDetector.IsRunning(_config.Trigger);
        if (running != TriggerRunning)
        {
            TriggerRunning = running;
            StateChanged?.Invoke();
        }

        if (_suspended || !_config.AutoSwitch) return;

        if (_triggerSeen == null)
        {
            // First look after launch / re-enable. If the trigger is not running we are at the desk,
            // which also repairs a PC that was shut down from Big Picture while on the TV.
            _triggerSeen = running;
            if (running)
            {
                Log.Write($"'{_config.Trigger}' is already open.");
                Apply(ProfileKind.Tv, "trigger already open");
            }
            else if (_config.RestoreDeskOnLaunch)
            {
                Apply(ProfileKind.Desk, "startup check", onlyIfDifferent: true);
            }
            return;
        }

        if (running)
        {
            _closedSince = null;
            if (_triggerSeen == false)
            {
                _triggerSeen = true;
                Log.Write($"Detected '{_config.Trigger}'.");
                Apply(ProfileKind.Tv, "trigger opened");
            }
        }
        else if (_triggerSeen == true)
        {
            // Debounce: Big Picture briefly drops its window while switching displays / restarting.
            _closedSince ??= DateTime.Now;
            if (DateTime.Now - _closedSince.Value >= TimeSpan.FromSeconds(Math.Max(0, _config.RevertDelaySeconds)))
            {
                _triggerSeen = false;
                _closedSince = null;
                Log.Write($"'{_config.Trigger}' closed.");
                Apply(ProfileKind.Desk, "trigger closed");
            }
        }
    }

    public Task ApplyAsync(ProfileKind kind, string reason) => Task.Run(() => Apply(kind, reason));

    /// <summary>Applies a profile. Serialized: concurrent requests wait for the one in progress.</summary>
    public bool Apply(ProfileKind kind, string reason, bool onlyIfDifferent = false, int lockTimeoutMs = 30_000)
    {
        if (!_applyLock.Wait(lockTimeoutMs)) return false;
        IsApplying = true;
        StateChanged?.Invoke();
        var label = kind == ProfileKind.Desk ? "Desk" : "TV";
        try
        {
            var profile = _config.Get(kind);
            if (profile.Display.IsEmpty)
            {
                Log.Write($"{label} layout isn't captured yet, so there is nothing to switch to ({reason}).");
                LastAction = $"{label} layout not captured yet";
                return false;
            }

            if (onlyIfDifferent && DisplayManager.Matches(DisplayManager.GetSnapshot(), profile.Display))
            {
                LastAction = $"{label} layout already active";
            }
            else
            {
                Log.Write($"Switching to {label} displays ({reason})…");
                DisplayManager.Apply(profile.Display, Log.Write);
                Log.Write($"{label} displays active.");
                LastAction = $"Switched to {label} at {DateTime.Now:HH:mm:ss}";
            }

            if (!string.IsNullOrEmpty(profile.AudioDeviceId)) SwitchAudio(profile);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Switching to {label} failed: {ex.Message}");
            LastAction = $"{label} failed – see activity log";
            return false;
        }
        finally
        {
            IsApplying = false;
            _applyLock.Release();
            StateChanged?.Invoke();
        }
    }

    private static void SwitchAudio(Profile profile)
    {
        if (AudioManager.GetDefaultId() == profile.AudioDeviceId) return;

        // HDMI audio endpoints only appear a few seconds after the TV's display path comes up.
        var deadline = DateTime.Now.AddSeconds(12);
        while (!AudioManager.IsAvailable(profile.AudioDeviceId))
        {
            if (DateTime.Now > deadline)
            {
                Log.Write($"Audio device '{profile.AudioDeviceName}' did not show up; audio left unchanged.");
                return;
            }
            Thread.Sleep(500);
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                AudioManager.SetDefault(profile.AudioDeviceId);
                if (AudioManager.GetDefaultId() == profile.AudioDeviceId)
                {
                    Log.Write($"Audio → {profile.AudioDeviceName}");
                    return;
                }
            }
            catch (Exception ex) when (attempt < 4)
            {
                Log.Write($"Audio switch retry ({ex.Message})");
            }
            Thread.Sleep(600);
        }
        Log.Write($"Could not make '{profile.AudioDeviceName}' the default audio device.");
    }

    /// <summary>
    /// Called from WM_QUERYENDSESSION. Puts the desk layout back so the next boot (and the sign-in screen)
    /// shows up on the desk monitor instead of the TV in the other room.
    /// </summary>
    public void PrepareForShutdown()
    {
        if (_suspended) return; // already handled for this shutdown
        _suspended = true;
        if (!_config.RestoreDeskOnShutdown || _config.Desk.Display.IsEmpty) return;
        try
        {
            if (DisplayManager.Matches(DisplayManager.GetSnapshot(), _config.Desk.Display)) return;
        }
        catch
        {
            // fall through and try to apply anyway
        }
        Log.Write("Windows is shutting down – restoring Desk layout for next boot.");
        Apply(ProfileKind.Desk, "shutdown", lockTimeoutMs: 4000);
    }

    public void ShutdownCancelled()
    {
        _suspended = false;
        ResetDetection();
    }

    /// <summary>Which saved profile the current display layout matches, if any.</summary>
    public string DescribeCurrentLayout(DisplaySnapshot snap)
    {
        if (!_config.Desk.Display.IsEmpty && DisplayManager.Matches(snap, _config.Desk.Display)) return "Desk";
        if (!_config.Tv.Display.IsEmpty && DisplayManager.Matches(snap, _config.Tv.Display)) return "TV";
        return "Custom";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Join(2000);
    }
}

using System.Runtime.InteropServices;
using static MonitorAudioSwitcher.Native.DisplayNative;

namespace MonitorAudioSwitcher.Core;

public sealed class MonitorEntry
{
    public string Name { get; set; } = "";
    public string DevicePath { get; set; } = "";
    public ushort EdidManufacturer { get; set; }
    public ushort EdidProduct { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double RefreshHz { get; set; }
    public bool Primary { get; set; }
    public bool Active { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Unknown display" : Name;

    public string ModeText => Active
        ? $"{Width} × {Height} @ {RefreshHz:0.##} Hz"
        : "Off";

    public bool IsSameMonitor(MonitorEntry other)
    {
        if (!string.IsNullOrEmpty(DevicePath) &&
            string.Equals(DevicePath, other.DevicePath, StringComparison.OrdinalIgnoreCase))
            return true;
        // Same physical model plugged into a different port: device path changes, EDID ids don't.
        return EdidManufacturer != 0 && EdidManufacturer == other.EdidManufacturer &&
               EdidProduct == other.EdidProduct &&
               string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DisplayProfile
{
    public DateTime CapturedAt { get; set; }
    public List<MonitorEntry> Monitors { get; set; } = new();
    /// <summary>Raw CCD path/mode arrays captured from Windows, replayed verbatim on apply.</summary>
    public string RawPaths { get; set; } = "";
    public string RawModes { get; set; } = "";
    /// <summary>Adapter LUID (changes every boot) at capture time -> stable adapter device path.</summary>
    public Dictionary<string, string> AdapterPaths { get; set; } = new();

    public bool IsEmpty => Monitors.Count == 0 || string.IsNullOrEmpty(RawPaths);
}

public sealed class DisplaySnapshot
{
    public List<MonitorEntry> Monitors { get; init; } = new();
    public IEnumerable<MonitorEntry> ActiveMonitors => Monitors.Where(m => m.Active);
}

public static class DisplayManager
{
    private static readonly object Gate = new();

    // ---------------------------------------------------------------- query

    private static (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) Query(uint flags)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rc = GetDisplayConfigBufferSizes(flags, out var numPaths, out var numModes);
            if (rc != ERROR_SUCCESS) throw new DisplayException($"GetDisplayConfigBufferSizes failed ({rc})");

            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            rc = QueryDisplayConfig(flags, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc == ERROR_INSUFFICIENT_BUFFER) continue; // topology changed between the two calls
            if (rc != ERROR_SUCCESS) throw new DisplayException($"QueryDisplayConfig failed ({rc})");

            return (paths[..(int)numPaths], modes[..(int)numModes]);
        }
        throw new DisplayException("QueryDisplayConfig kept changing size");
    }

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetName(LUID adapter, uint targetId)
    {
        var req = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header =
            {
                type = DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapter,
                id = targetId,
            },
        };
        DisplayConfigGetDeviceInfo(ref req);
        return req;
    }

    private static string GetSourceGdiName(LUID adapter, uint sourceId)
    {
        var req = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header =
            {
                type = DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapter,
                id = sourceId,
            },
        };
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.viewGdiDeviceName : "";
    }

    private static string GetAdapterPath(LUID adapter)
    {
        var req = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header =
            {
                type = DEVICE_INFO_GET_ADAPTER_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>(),
                adapterId = adapter,
            },
        };
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.adapterDevicePath : "";
    }

    private static MonitorEntry DescribeTarget(LUID adapter, uint targetId)
    {
        var t = GetTargetName(adapter, targetId);
        var name = t.monitorFriendlyDeviceName;
        if (string.IsNullOrWhiteSpace(name))
            name = t.outputTechnology switch
            {
                0x80000000 => "Built-in display",
                5 or 10 or 11 => "HDMI display",
                _ => "Display",
            };
        return new MonitorEntry
        {
            Name = name,
            DevicePath = t.monitorDevicePath ?? "",
            EdidManufacturer = t.edidManufactureId,
            EdidProduct = t.edidProductCodeId,
        };
    }

    private static List<MonitorEntry> DescribeActive(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var list = new List<MonitorEntry>();
        foreach (var p in paths)
        {
            if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            var m = DescribeTarget(p.targetInfo.adapterId, p.targetInfo.id);
            m.Active = true;

            var srcIdx = p.sourceInfo.modeInfoIdx;
            if (srcIdx < modes.Length && modes[srcIdx].infoType == MODE_INFO_TYPE_SOURCE)
            {
                var sm = modes[srcIdx].sourceMode;
                m.Width = (int)sm.width;
                m.Height = (int)sm.height;
                m.X = sm.position.x;
                m.Y = sm.position.y;
                m.Primary = sm.position.x == 0 && sm.position.y == 0;
            }

            var tgtIdx = p.targetInfo.modeInfoIdx;
            m.RefreshHz = tgtIdx < modes.Length && modes[tgtIdx].infoType == MODE_INFO_TYPE_TARGET
                ? modes[tgtIdx].targetVideoSignalInfo.vSyncFreq.Value
                : p.targetInfo.refreshRate.Value;
            m.RefreshHz = Math.Round(m.RefreshHz, 2);
            list.Add(m);
        }
        return list;
    }

    /// <summary>All connected monitors, with mode info for the ones currently on.</summary>
    public static DisplaySnapshot GetSnapshot()
    {
        lock (Gate)
        {
            var (activePaths, activeModes) = Query(QDC_ONLY_ACTIVE_PATHS);
            var result = DescribeActive(activePaths, activeModes);

            var (allPaths, _) = Query(QDC_ALL_PATHS);
            var seen = new HashSet<(LUID, uint)>();
            foreach (var p in allPaths)
            {
                if (p.targetInfo.targetAvailable == 0) continue;
                if (!seen.Add((p.targetInfo.adapterId, p.targetInfo.id))) continue;
                var m = DescribeTarget(p.targetInfo.adapterId, p.targetInfo.id);
                if (string.IsNullOrEmpty(m.DevicePath)) continue;
                if (result.Any(r => r.IsSameMonitor(m))) continue;
                result.Add(m);
            }

            return new DisplaySnapshot
            {
                Monitors = result.OrderByDescending(m => m.Active).ThenByDescending(m => m.Primary).ThenBy(m => m.X).ToList(),
            };
        }
    }

    // ---------------------------------------------------------------- capture

    public static DisplayProfile Capture()
    {
        lock (Gate)
        {
            var (paths, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
            var profile = new DisplayProfile
            {
                CapturedAt = DateTime.Now,
                Monitors = DescribeActive(paths, modes),
                RawPaths = Convert.ToBase64String(MemoryMarshal.AsBytes(paths.AsSpan()).ToArray()),
                RawModes = Convert.ToBase64String(MemoryMarshal.AsBytes(modes.AsSpan()).ToArray()),
            };

            foreach (var luid in paths.SelectMany(p => new[] { p.sourceInfo.adapterId, p.targetInfo.adapterId })
                         .Concat(modes.Select(m => m.adapterId)).Distinct())
                profile.AdapterPaths[luid.ToString()] = GetAdapterPath(luid);

            return profile;
        }
    }

    // ---------------------------------------------------------------- compare

    public static bool Matches(DisplaySnapshot snapshot, DisplayProfile profile)
    {
        var active = snapshot.ActiveMonitors.ToList();
        if (active.Count != profile.Monitors.Count) return false;
        foreach (var want in profile.Monitors)
        {
            var have = active.FirstOrDefault(a => a.IsSameMonitor(want));
            if (have == null) return false;
            if (!ModeMatches(have, want)) return false;
        }
        return true;
    }

    private static bool ModeMatches(MonitorEntry have, MonitorEntry want) =>
        have.Width == want.Width && have.Height == want.Height &&
        have.X == want.X && have.Y == want.Y &&
        Math.Abs(have.RefreshHz - want.RefreshHz) < 0.6;

    // ---------------------------------------------------------------- apply

    public static void Apply(DisplayProfile profile, Action<string> log)
    {
        if (profile.IsEmpty) throw new DisplayException("This profile has no captured display layout yet.");

        lock (Gate)
        {
            var applied = TryExactReplay(profile, log) || TryRebuildTopology(profile, log);
            if (!applied)
                throw new DisplayException("Windows rejected the display layout. Is the monitor/TV connected and powered?");

            // Windows sometimes takes a moment before the new topology is reported.
            for (var i = 0; i < 10; i++)
            {
                var snap = GetSnapshot();
                if (Matches(snap, profile)) return;
                if (profile.Monitors.All(w => snap.ActiveMonitors.Any(a => a.IsSameMonitor(w))))
                {
                    // Right monitors are on but a mode drifted (usually refresh rate) - correct it.
                    FixModes(profile, log);
                    if (Matches(GetSnapshot(), profile)) return;
                }
                Thread.Sleep(300);
            }
            log("Display layout applied, but Windows reports a slightly different mode than captured.");
        }
    }

    /// <summary>Asks Windows whether the saved layout could be applied right now, without changing anything.</summary>
    public static bool Validate(DisplayProfile profile, Action<string> log)
    {
        lock (Gate) return !profile.IsEmpty && TryExactReplay(profile, log, validateOnly: true);
    }

    private static T[] FromBase64<T>(string data) where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(Convert.FromBase64String(data)).ToArray();

    private static Dictionary<string, LUID> CurrentAdapters(DISPLAYCONFIG_PATH_INFO[] allPaths)
    {
        var map = new Dictionary<string, LUID>(StringComparer.OrdinalIgnoreCase);
        foreach (var luid in allPaths.Select(p => p.targetInfo.adapterId).Concat(allPaths.Select(p => p.sourceInfo.adapterId)).Distinct())
        {
            var path = GetAdapterPath(luid);
            if (!string.IsNullOrEmpty(path)) map[path] = luid;
        }
        return map;
    }

    /// <summary>
    /// Replays the captured path+mode arrays exactly. This restores the precise timing the monitor was
    /// running at (e.g. 165 Hz), not whatever Windows considers the default mode.
    /// </summary>
    private static bool TryExactReplay(DisplayProfile profile, Action<string> log, bool validateOnly = false)
    {
        DISPLAYCONFIG_PATH_INFO[] paths;
        DISPLAYCONFIG_MODE_INFO[] modes;
        try
        {
            paths = FromBase64<DISPLAYCONFIG_PATH_INFO>(profile.RawPaths);
            modes = FromBase64<DISPLAYCONFIG_MODE_INFO>(profile.RawModes);
        }
        catch (Exception ex)
        {
            log($"Saved layout is unreadable ({ex.Message}); rebuilding from monitor list.");
            return false;
        }

        var (allPaths, _) = Query(QDC_ALL_PATHS);
        var adapters = CurrentAdapters(allPaths);

        // Adapter LUIDs are regenerated every boot, so translate them via the adapter's device path.
        LUID? Remap(LUID old)
        {
            if (profile.AdapterPaths.TryGetValue(old.ToString(), out var devPath) &&
                adapters.TryGetValue(devPath, out var now))
                return now;
            return adapters.Count == 1 ? adapters.Values.First() : null;
        }

        for (var i = 0; i < paths.Length; i++)
        {
            var s = Remap(paths[i].sourceInfo.adapterId);
            var t = Remap(paths[i].targetInfo.adapterId);
            if (s == null || t == null)
            {
                log("Graphics adapter changed since capture; rebuilding layout.");
                return false;
            }
            paths[i].sourceInfo.adapterId = s.Value;
            paths[i].targetInfo.adapterId = t.Value;

            // Make sure the same monitor is still on that port before trusting the raw replay.
            var expected = profile.Monitors.ElementAtOrDefault(i);
            var actual = DescribeTarget(t.Value, paths[i].targetInfo.id);
            if (expected != null && !expected.IsSameMonitor(actual))
            {
                log($"'{expected.DisplayName}' is on a different port than when captured; rebuilding layout.");
                return false;
            }
        }
        for (var i = 0; i < modes.Length; i++)
        {
            var a = Remap(modes[i].adapterId);
            if (a == null) return false;
            modes[i].adapterId = a.Value;
        }

        var rc = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            validateOnly
                ? SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES
                : SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
        if (rc == ERROR_SUCCESS) return true;

        log($"Exact layout replay failed (error {rc}); rebuilding layout.");
        return false;
    }

    /// <summary>
    /// Fallback: pick paths for the wanted monitors from everything Windows can currently drive, let
    /// Windows choose modes, then FixModes() corrects resolution/refresh/position afterwards.
    /// </summary>
    private static bool TryRebuildTopology(DisplayProfile profile, Action<string> log)
    {
        var (allPaths, _) = Query(QDC_ALL_PATHS);
        var chosen = new List<DISPLAYCONFIG_PATH_INFO>();
        var usedSources = new HashSet<(LUID, uint)>();
        var usedTargets = new HashSet<(LUID, uint)>();

        // Primary first so it gets the lowest source id.
        foreach (var want in profile.Monitors.OrderByDescending(m => m.Primary))
        {
            var found = false;
            foreach (var p in allPaths)
            {
                if (p.targetInfo.targetAvailable == 0) continue;
                var tKey = (p.targetInfo.adapterId, p.targetInfo.id);
                var sKey = (p.sourceInfo.adapterId, p.sourceInfo.id);
                if (usedTargets.Contains(tKey) || usedSources.Contains(sKey)) continue;
                if (!want.IsSameMonitor(DescribeTarget(p.targetInfo.adapterId, p.targetInfo.id))) continue;

                var path = p;
                path.flags = DISPLAYCONFIG_PATH_ACTIVE;
                path.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                path.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                chosen.Add(path);
                usedTargets.Add(tKey);
                usedSources.Add(sKey);
                found = true;
                break;
            }
            if (!found) log($"'{want.DisplayName}' isn't connected or isn't responding right now.");
        }

        if (chosen.Count == 0) return false;

        var arr = chosen.ToArray();
        var rc = SetDisplayConfig((uint)arr.Length, arr, 0, null,
            SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
        if (rc != ERROR_SUCCESS)
            rc = SetDisplayConfig((uint)arr.Length, arr, 0, null,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        if (rc != ERROR_SUCCESS)
        {
            log($"Windows refused the rebuilt layout (error {rc}).");
            return false;
        }

        Thread.Sleep(500);
        FixModes(profile, log);
        return true;
    }

    /// <summary>Forces resolution, refresh rate and position of each monitor to the captured values.</summary>
    private static void FixModes(DisplayProfile profile, Action<string> log)
    {
        // Every path from an active-only query is active, so snap[i] describes paths[i].
        var (paths, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        var snap = DescribeActive(paths, modes);
        var anyChange = false;

        foreach (var want in profile.Monitors)
        {
            var idx = snap.FindIndex(a => a.IsSameMonitor(want));
            if (idx < 0 || ModeMatches(snap[idx], want)) continue;

            var p = paths[idx];
            var gdi = GetSourceGdiName(p.sourceInfo.adapterId, p.sourceInfo.id);
            if (string.IsNullOrEmpty(gdi)) continue;

            var best = FindMode(gdi, want);
            if (best == null)
            {
                log($"No {want.Width}×{want.Height} @ {want.RefreshHz:0.##} Hz mode available on '{want.DisplayName}'.");
                continue;
            }

            var dm = best.Value;
            dm.dmPositionX = want.X;
            dm.dmPositionY = want.Y;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION;
            var flags = CDS_UPDATEREGISTRY | CDS_NORESET | (want.Primary ? CDS_SET_PRIMARY : 0);
            var rc = ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (rc == DISP_CHANGE_SUCCESSFUL)
            {
                anyChange = true;
                log($"Set '{want.DisplayName}' to {dm.dmPelsWidth}×{dm.dmPelsHeight} @ {dm.dmDisplayFrequency} Hz.");
            }
            else log($"Could not set mode on '{want.DisplayName}' (code {rc}).");
        }

        if (anyChange) ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    private static DEVMODE? FindMode(string gdiName, MonitorEntry want)
    {
        DEVMODE? best = null;
        var bestDiff = double.MaxValue;
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        for (var i = 0; EnumDisplaySettingsW(gdiName, i, ref dm); i++)
        {
            if (dm.dmPelsWidth != want.Width || dm.dmPelsHeight != want.Height || dm.dmBitsPerPel < 32) continue;
            var diff = Math.Abs(dm.dmDisplayFrequency - want.RefreshHz);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = dm;
            }
            dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        }
        return best;
    }
}

public sealed class DisplayException(string message) : Exception(message);

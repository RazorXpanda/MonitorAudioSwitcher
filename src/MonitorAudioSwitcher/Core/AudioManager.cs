using System.Runtime.InteropServices;

namespace MonitorAudioSwitcher.Core;

public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;
}

/// <summary>Playback device enumeration and default-device switching via Core Audio COM APIs.</summary>
public static class AudioManager
{
    private const int DEVICE_STATE_ACTIVE = 0x1;
    private const int STGM_READ = 0;

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    // PKEY_Device_FriendlyName, e.g. "Speakers (EDIFIER MG250)"
    private static PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(16)] private IntPtr _pad;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    // Undocumented but stable since Windows 7; used by the Sound control panel itself.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClientComObject;

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, int def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, int def, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility(string id, int visible);
    }

    private static string ReadName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(STGM_READ, out var store) != 0) return "";
        try
        {
            if (store.GetValue(ref PKEY_Device_FriendlyName, out var pv) != 0) return "";
            try
            {
                return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.pointerValue) ?? "" : "";
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    public static string? GetDefaultId()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev) != 0) return null;
            try
            {
                return dev.GetId(out var id) == 0 ? id : null;
            }
            finally
            {
                Marshal.ReleaseComObject(dev);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(en);
        }
    }

    public static List<AudioDevice> GetPlaybackDevices()
    {
        var result = new List<AudioDevice>();
        var defaultId = GetDefaultId();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (en.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var col) != 0) return result;
            try
            {
                col.GetCount(out var count);
                for (var i = 0; i < count; i++)
                {
                    if (col.Item(i, out var dev) != 0) continue;
                    try
                    {
                        if (dev.GetId(out var id) != 0) continue;
                        result.Add(new AudioDevice(id, ReadName(dev), id == defaultId));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(col);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(en);
        }
        return result.OrderBy(d => d.Name).ToList();
    }

    public static bool IsAvailable(string id) => GetPlaybackDevices().Any(d => d.Id == id);

    public static void SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClientComObject();
        try
        {
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                var hr = policy.SetDefaultEndpoint(id, role);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }
}

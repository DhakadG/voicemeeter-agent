using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VoicemeeterAgent;

/// <summary>
/// Thin P/Invoke wrapper around VoicemeeterRemote64.dll.
/// Resolves the DLL path from the Windows registry at startup.
/// </summary>
internal sealed class VoicemeeterApi : IDisposable
{
    // ─── Registry / DLL resolution ───────────────────────────────────────────

    private const string RegKey32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}";
    private const string RegKey64 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}";
    private const string DllName64 = "VoicemeeterRemote64.dll";

    private static string? _resolvedDllPath;
    private bool _disposed;

    /// <summary>Finds the Voicemeeter install folder from the registry and returns the full DLL path,
    /// or null if not found.</summary>
    public static string? FindDllPath()
    {
        foreach (var key in new[] { RegKey64, RegKey32 })
        {
            using var hk = Registry.LocalMachine.OpenSubKey(key);
            if (hk?.GetValue("UninstallString") is string uninstall)
            {
                // UninstallString looks like: "C:\Program Files (x86)\VB\Voicemeeter\uninstall.exe"
                var dir = Path.GetDirectoryName(uninstall.Trim('"'));
                if (dir != null)
                {
                    var dll = Path.Combine(dir, DllName64);
                    if (File.Exists(dll)) return dll;
                }
            }

            // Fallback: check for "INSTDIR" value
            using var hk2 = Registry.LocalMachine.OpenSubKey(key);
            if (hk2?.GetValue("INSTDIR") is string instDir)
            {
                var dll = Path.Combine(instDir, DllName64);
                if (File.Exists(dll)) return dll;
            }
        }

        // Last resort: well-known default paths
        foreach (var candidate in new[]
        {
            @"C:\Program Files (x86)\VB\Voicemeeter\" + DllName64,
            @"C:\Program Files\VB\Voicemeeter\" + DllName64,
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    // ─── DLL imports ─────────────────────────────────────────────────────────

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_Login", CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_Login();

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_Logout", CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_Logout();

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_RunVoicemeeter", CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_RunVoicemeeter(int vType);

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_IsParametersDirty", CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_IsParametersDirty();

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_GetParameterFloat",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int VBVMR_GetParameterFloat([MarshalAs(UnmanagedType.LPStr)] string paramName, out float value);

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_SetParameterFloat",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int VBVMR_SetParameterFloat([MarshalAs(UnmanagedType.LPStr)] string paramName, float value);

    // ─── Constructor / DLL loader ─────────────────────────────────────────────

    /// <summary>
    /// Loads the DLL from <paramref name="dllPath"/> so that the DllImport pinvokes resolve correctly.
    /// Must be called before any other method.
    /// </summary>
    public static void PreloadDll(string dllPath)
    {
        _resolvedDllPath = dllPath;
        NativeLibrary.SetDllImportResolver(typeof(VoicemeeterApi).Assembly, (name, _, _) =>
        {
            if (name == "VoicemeeterRemote64.dll" && _resolvedDllPath != null)
                return NativeLibrary.Load(_resolvedDllPath);
            return IntPtr.Zero;
        });
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Logs in to the Voicemeeter Remote service.
    /// Returns 0 = OK and running; 1 = OK but Voicemeeter not yet running (caller should start it);
    /// negative = error.
    /// </summary>
    public int Login() => VBVMR_Login();

    /// <summary>Logs out. Must be called exactly once before the process exits.</summary>
    public void Logout() => VBVMR_Logout();

    /// <summary>
    /// Launches Voicemeeter. vType: 1 = Voicemeeter, 2 = Banana, 3 = Potato.
    /// </summary>
    public int RunVoicemeeter(int vType = 2) => VBVMR_RunVoicemeeter(vType);

    /// <summary>
    /// Returns 1 if any Voicemeeter parameter changed since the last call.
    /// Returns negative if the connection dropped.
    /// </summary>
    public int IsParametersDirty() => VBVMR_IsParametersDirty();

    /// <summary>Sets a named float parameter. Returns 0 on success, negative on error.</summary>
    public int SetParameterFloat(string name, float value) => VBVMR_SetParameterFloat(name, value);

    /// <summary>Gets a named float parameter. Returns 0 on success, negative on error.</summary>
    public int GetParameterFloat(string name, out float value) => VBVMR_GetParameterFloat(name, out value);

    // ─── Convenience helpers ─────────────────────────────────────────────────

    /// <summary>Sets Strip[<paramref name="stripIndex"/>].Gain in dB (clamped -60..+12).</summary>
    public bool SetStripGain(int stripIndex, float dB)
    {
        dB = Math.Clamp(dB, -60f, 12f);
        return SetParameterFloat($"Strip[{stripIndex}].Gain", dB) == 0;
    }

    /// <summary>Sets Strip[<paramref name="stripIndex"/>].Mute (1 = muted, 0 = unmuted).</summary>
    public bool SetStripMute(int stripIndex, bool muted)
        => SetParameterFloat($"Strip[{stripIndex}].Mute", muted ? 1f : 0f) == 0;

    /// <summary>Gets Strip[<paramref name="stripIndex"/>].Gain in dB. Returns true on success.</summary>
    public bool GetStripGain(int stripIndex, out float dB)
        => GetParameterFloat($"Strip[{stripIndex}].Gain", out dB) == 0;

    /// <summary>Gets Strip[<paramref name="stripIndex"/>].Mute state. Returns true on success.</summary>
    public bool GetStripMute(int stripIndex, out bool muted)
    {
        if (GetParameterFloat($"Strip[{stripIndex}].Mute", out float val) == 0)
        {
            muted = val >= 0.5f;
            return true;
        }
        muted = false;
        return false;
    }

    // ─── Device enumeration / bus device selection ───────────────────────────

    /// <summary>Voicemeeter device type codes (from VoicemeeterRemote.h).</summary>
    public const int DevTypeMme = 1;
    public const int DevTypeWdm = 3;
    public const int DevTypeKs = 4;
    public const int DevTypeAsio = 5;

    /// <summary>Buffer size Voicemeeter expects for string parameters and device names.</summary>
    private const int StrBufSize = 512;

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_GetParameterStringA",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int VBVMR_GetParameterStringA([MarshalAs(UnmanagedType.LPStr)] string paramName, byte[] value);

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_SetParameterStringA",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int VBVMR_SetParameterStringA([MarshalAs(UnmanagedType.LPStr)] string paramName,
        [MarshalAs(UnmanagedType.LPStr)] string value);

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_Output_GetDeviceNumber",
        CallingConvention = CallingConvention.StdCall)]
    private static extern int VBVMR_Output_GetDeviceNumber();

    [DllImport("VoicemeeterRemote64.dll", EntryPoint = "VBVMR_Output_GetDeviceDescA",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int VBVMR_Output_GetDeviceDescA(int index, out int nType, byte[] name, byte[] hardwareId);

    /// <summary>An output device Voicemeeter can currently see.</summary>
    public sealed record AudioDevice(int Type, string Name)
    {
        /// <summary>Label shown in the tray menu, e.g. "WDM: Speakers (Realtek)".</summary>
        public string Display => $"{TypeKey(Type).ToUpperInvariant()}: {Name}";
    }

    /// <summary>Maps a device type code to the parameter suffix used to select it.</summary>
    public static string TypeKey(int type) => type switch
    {
        DevTypeMme => "mme",
        DevTypeKs => "ks",
        DevTypeAsio => "asio",
        _ => "wdm",
    };

    private static string ReadCString(byte[] buf)
    {
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return System.Text.Encoding.Default.GetString(buf, 0, len);
    }

    /// <summary>Gets a named string parameter, or null on error.</summary>
    public string? GetParameterString(string name)
    {
        var buf = new byte[StrBufSize];
        return VBVMR_GetParameterStringA(name, buf) == 0 ? ReadCString(buf) : null;
    }

    /// <summary>Sets a named string parameter. Returns true on success.</summary>
    public bool SetParameterString(string name, string value)
        => VBVMR_SetParameterStringA(name, value) == 0;

    /// <summary>
    /// Enumerates the output devices Voicemeeter currently sees. A device that is powered
    /// off or unplugged drops out of this list, which is what the watchdog keys on.
    /// </summary>
    public List<AudioDevice> ListOutputDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            int n = VBVMR_Output_GetDeviceNumber();
            for (int i = 0; i < n; i++)
            {
                var name = new byte[StrBufSize];
                var hwid = new byte[StrBufSize];
                if (VBVMR_Output_GetDeviceDescA(i, out int type, name, hwid) != 0) continue;
                var text = ReadCString(name);
                if (text.Length > 0) list.Add(new AudioDevice(type, text));
            }
        }
        catch { /* Voicemeeter not running */ }
        return list;
    }

    /// <summary>Gets the device name currently attached to a physical bus, or null/empty if none.</summary>
    public string? GetBusDeviceName(int bus) => GetParameterString($"Bus[{bus}].device.name");

    /// <summary>Attaches an output device to a physical bus.</summary>
    public bool SetBusDevice(int bus, int type, string name)
        => SetParameterString($"Bus[{bus}].device.{TypeKey(type)}", name);

    /// <summary>Detaches whatever device is on a physical bus (empty string = unselect).</summary>
    public bool ClearBusDevice(int bus, int type)
        => SetParameterString($"Bus[{bus}].device.{TypeKey(type)}", "");

    // ─── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try { VBVMR_Logout(); } catch { /* suppress */ }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoicemeeterAgent;

/// <summary>
/// A saved binding between a Voicemeeter physical bus (A1/A2/A3) and an output device.
/// The watchdog attaches the device when it is available and detaches it when it is not,
/// which stops Voicemeeter from re-probing a dead device every few seconds.
/// </summary>
internal sealed class BusDeviceBinding
{
    /// <summary>Zero-based bus index. Banana: 0 = A1, 1 = A2, 2 = A3.</summary>
    public int Bus { get; set; }

    /// <summary>Voicemeeter device type (see VoicemeeterApi.DevType*). 0 = unassigned.</summary>
    public int Type { get; set; }

    /// <summary>Exact device name as reported by Voicemeeter's output enumeration.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// User settings persisted to %APPDATA%\VoicemeeterAgent\config.json.
/// </summary>
internal sealed class AppConfig
{
    /// <summary>
    /// Gain in dB applied at Windows volume 0%. The volume curve interpolates between this
    /// and 0 dB at 100%. Tune it if the usable range of the slider feels too short or too long.
    /// </summary>
    public float MinGainDb { get; set; } = -60f;

    /// <summary>Whether the bus-device watchdog runs.</summary>
    public bool DeviceWatchdogEnabled { get; set; }

    /// <summary>How often the watchdog checks the output device list (ms).</summary>
    public int DeviceWatchdogIntervalMs { get; set; } = 5_000;

    /// <summary>Bus-to-device bindings managed by the watchdog.</summary>
    public List<BusDeviceBinding> Buses { get; set; } = new();

    // ─── Persistence ─────────────────────────────────────────────────────────

    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoicemeeterAgent", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path)) ?? new AppConfig();
        }
        catch { /* corrupt or unreadable — fall back to defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* best effort */ }
    }

    /// <summary>Returns the binding for <paramref name="bus"/>, creating it if absent.</summary>
    public BusDeviceBinding BindingFor(int bus)
    {
        var b = Buses.FirstOrDefault(x => x.Bus == bus);
        if (b == null)
        {
            b = new BusDeviceBinding { Bus = bus };
            Buses.Add(b);
        }
        return b;
    }
}

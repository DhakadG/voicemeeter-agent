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

    /// <summary>Shape of the volume curve. Switchable from the tray menu.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VolumeProfile Profile { get; set; } = VolumeProfile.Knee;

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
                return Validate(JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path)) ?? new AppConfig());
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

    /// <summary>
    /// Clamps hand-edited values into a range the curves can handle. A floor of 0 dB divides by
    /// zero in the LinearDb inverse, and one of -20 dB does the same in the Knee inverse; either
    /// produces NaN, which NAudio rejects on the poll thread and takes the process down.
    /// </summary>
    private static AppConfig Validate(AppConfig config)
    {
        config.MinGainDb = float.IsFinite(config.MinGainDb)
            ? Math.Clamp(config.MinGainDb, -96f, -24f)
            : -60f;
        config.DeviceWatchdogIntervalMs = Math.Max(config.DeviceWatchdogIntervalMs, 500);
        return config;
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

/// <summary>
/// Shape of the Windows volume scalar to Voicemeeter gain mapping.
/// All profiles hit 0 dB at 100% and <see cref="AppConfig.MinGainDb"/> at 0%; they differ in
/// how the dB range is spread across the slider.
/// </summary>
internal enum VolumeProfile
{
    /// <summary>Even dB per step: 0% = floor, 50% = half the floor, 100% = 0 dB. The v1.0 curve.</summary>
    LinearDb,

    /// <summary>Slider position is the amplitude ratio: 50% = -6 dB, 25% = -12 dB. The v1.1 curve.</summary>
    Amplitude,

    /// <summary>Amplitude squared: 50% = -12 dB. Sits between LinearDb and Amplitude.</summary>
    Gamma2,

    /// <summary>
    /// Two straight segments with a knee at 50% / -20 dB. The lower half covers the quiet range
    /// quickly, the upper half gets twice the resolution of LinearDb where the volume is loud.
    /// </summary>
    Knee,
}

/// <summary>
/// Windows volume scalar to Voicemeeter gain conversions. Every profile is invertible, because
/// the agent syncs in both directions and a lossy round trip would make the two ends drift.
/// </summary>
internal static class VolumeCurve
{
    /// <summary>Slider position of the Knee profile's break point.</summary>
    private const float KneeScalar = 0.5f;

    /// <summary>Gain in dB at the Knee profile's break point.</summary>
    private const float KneeDb = -20f;

    /// <summary>Converts a Windows volume scalar [0..1] to gain in dB, clamped to [minDb, 0].</summary>
    public static float ToDb(VolumeProfile profile, float minDb, float scalar)
    {
        scalar = Math.Clamp(scalar, 0f, 1f);
        if (scalar <= 0f) return minDb;

        float dB = profile switch
        {
            VolumeProfile.LinearDb => minDb * (1f - scalar),
            VolumeProfile.Amplitude => 20f * MathF.Log10(scalar),
            VolumeProfile.Gamma2 => 40f * MathF.Log10(scalar),
            VolumeProfile.Knee => scalar <= KneeScalar
                ? minDb + (KneeDb - minDb) * (scalar / KneeScalar)
                : KneeDb * (1f - (scalar - KneeScalar) / (1f - KneeScalar)),
            _ => minDb * (1f - scalar),
        };

        return Math.Clamp(dB, minDb, 0f);
    }

    /// <summary>Converts gain in dB back to a Windows volume scalar [0..1]. Inverse of <see cref="ToDb"/>.</summary>
    public static float ToScalar(VolumeProfile profile, float minDb, float dB)
    {
        dB = Math.Clamp(dB, minDb, 0f);

        // At or below the floor the slider is at zero. Without this the log profiles land on a
        // non-zero scalar (Gamma2 at a -40 dB floor gives 0.1), so pulling the Voicemeeter fader
        // all the way down would leave Windows sitting at 10%% instead of silent.
        if (dB <= minDb) return 0f;

        float scalar = profile switch
        {
            VolumeProfile.LinearDb => 1f - dB / minDb,
            VolumeProfile.Amplitude => MathF.Pow(10f, dB / 20f),
            VolumeProfile.Gamma2 => MathF.Pow(10f, dB / 40f),
            VolumeProfile.Knee => dB <= KneeDb
                ? (dB - minDb) / (KneeDb - minDb) * KneeScalar
                : KneeScalar + (1f - dB / KneeDb) * (1f - KneeScalar),
            _ => 1f - dB / minDb,
        };

        return Math.Clamp(scalar, 0f, 1f);
    }

    /// <summary>
    /// Checks that every profile is anchored at both ends and survives a scalar round trip.
    /// Run with "VoicemeeterAgent.exe --selftest"; the process exit code is 0 on success.
    /// </summary>
    public static bool SelfTest()
    {
        bool ok = true;

        foreach (float minDb in new[] { -40f, -50f, -60f, -96f })
        foreach (VolumeProfile profile in Enum.GetValues<VolumeProfile>())
        {
            ok &= MathF.Abs(ToDb(profile, minDb, 1f) - 0f) < 0.01f;
            ok &= MathF.Abs(ToDb(profile, minDb, 0f) - minDb) < 0.01f;

            // Round trip. A scalar that maps onto the floor is clamped and cannot come back,
            // so only check the part of the slider that stays above it.
            for (float s = 0.05f; s <= 1f; s += 0.05f)
            {
                float dB = ToDb(profile, minDb, s);
                if (dB <= minDb + 0.01f) continue;
                ok &= MathF.Abs(ToScalar(profile, minDb, dB) - s) < 0.005f;
            }

            // The floor must map back to a silent slider, not to a non-zero scalar.
            ok &= ToScalar(profile, minDb, minDb) == 0f;

            // Monotonic: louder slider must never mean lower gain.
            float previous = float.NegativeInfinity;
            for (float s = 0f; s <= 1f; s += 0.01f)
            {
                float dB = ToDb(profile, minDb, s);
                ok &= dB >= previous - 0.001f;
                previous = dB;
            }
        }

        return ok;
    }
}

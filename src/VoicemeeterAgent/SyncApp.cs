namespace VoicemeeterAgent;

/// <summary>
/// Core sync logic: ties <see cref="WindowsVolumeWatcher"/> to <see cref="VoicemeeterApi"/>.
/// Handles initial sync, reconnect, and a keep-alive poll timer.
/// </summary>
internal sealed class SyncApp : IDisposable
{
    // ─── Configuration ───────────────────────────────────────────────────────

    /// <summary>Voicemeeter Banana virtual VAIO B1 input strip index (zero-based).</summary>
    private const int TargetStrip = 3;

    /// <summary>Normal poll interval (ms).</summary>
    private const int PollIntervalMs = 500;

    /// <summary>Fast poll interval used briefly after any volume change (ms).</summary>
    private const int BurstPollMs = 50;

    /// <summary>How long (ms) to stay in fast-poll burst mode after a change.</summary>
    private const int BurstDurationMs = 1_500;

    /// <summary>Time between reconnect attempts when Voicemeeter is not running (ms).</summary>
    private const int ReconnectIntervalMs = 5_000;

    // ─── Fields ──────────────────────────────────────────────────────────────

    private readonly VoicemeeterApi _api;
    private readonly WindowsVolumeWatcher _watcher;
    private readonly AppConfig _config;
    private readonly System.Threading.Timer _pollTimer;

    private bool _connected;
    private bool _wasConnected;
    private bool _loggedIn;
    private long _lastReconnectTick;
    private bool _disposed;

    /// <summary>Guards _lastSentGainDb/_lastSentMute which are read/written on two threads.</summary>
    private readonly object _syncLock = new();

    /// <summary>
    /// The last gain (dB) value WE pushed to Voicemeeter.
    /// Only access under <see cref="_syncLock"/>.
    /// </summary>
    private float _lastSentGainDb = float.NaN;

    /// <summary>The last mute state WE pushed to Voicemeeter. Only access under _syncLock.</summary>
    private bool _lastSentMute;

    /// <summary>
    /// TickCount64 of the last time we wrote volume TO Windows (from Voicemeeter).
    /// Suppresses the Windows notification echo for <see cref="SuppressMs"/>.
    /// </summary>
    private long _lastWindowsWriteTime;

    /// <summary>TickCount64 when burst-poll mode should end. 0 = not in burst.</summary>
    private long _burstEndTime;

    /// <summary>
    /// How many ms after writing to Windows we suppress incoming Windows notifications.
    /// </summary>
    private const long SuppressMs = 150;

    /// <summary>dB tolerance when comparing what we sent vs what VM reports back.</summary>
    private const float GainEpsilon = 0.15f;

    /// <summary>
    /// Windows volume scalar tolerance (~0.8%). Don't write to Windows if the computed
    /// target is already within this of the current value — prevents the OSD snap.
    /// </summary>
    private const float ScalarEpsilon = 0.008f;

    // ─── Events ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fired on the thread-pool whenever the Voicemeeter connection state changes.
    /// true = just connected, false = just disconnected.
    /// </summary>
    public event Action<bool>? ConnectionStateChanged;

    // ─── Construction ────────────────────────────────────────────────────────

    public SyncApp(VoicemeeterApi api, WindowsVolumeWatcher watcher, AppConfig config)
    {
        _api = api;
        _watcher = watcher;
        _config = config;
        _pollTimer = new System.Threading.Timer(OnPollTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    // ─── Public entry point ──────────────────────────────────────────────────

    public void Start()
    {
        // Login is called ONCE per process lifetime.
        // From the Voicemeeter API spec:
        //   "Login works regardless of whether Voicemeeter is running."
        //   "Voicemeeter can be restarted during an active login session."
        // Use IsParametersDirty() >= 0 to detect whether VM is currently running.
        // Calling Login() a second time returns an error and breaks reconnection.
        try
        {
            int rep = _api.Login();
            _loggedIn = rep >= 0; // 0 = VM running, 1 = VM not yet running; both valid
            if (rep == 0) TryActivate(); // VM already up — do initial sync immediately
            // rep == 1: VM not running yet — polling loop detects when it starts
        }
        catch { /* DLL load error — _loggedIn stays false */ }

        // Subscribe AFTER initial sync so we don't double-apply the startup state
        _watcher.VolumeChanged += OnVolumeChanged;
        // One-shot timer; re-armed manually in OnPollTick so we can vary the interval
        _pollTimer.Change(PollIntervalMs, Timeout.Infinite);
    }

    // ─── Volume event (Windows → Voicemeeter) ───────────────────────────────

    private void OnVolumeChanged(float scalar, bool muted)
    {
        if (!_connected) return;

        // Suppress echoed notifications caused by our own Windows writes
        if (Environment.TickCount64 - _lastWindowsWriteTime < SuppressMs) return;

        float dB = ScalarToDb(scalar);

        // Thread-safe: remember what we're sending so the poll can recognise our own echo
        lock (_syncLock) { _lastSentGainDb = dB; _lastSentMute = muted; }

        // Activate burst-poll mode so the VM echo is consumed quickly
        _burstEndTime = Environment.TickCount64 + BurstDurationMs;

        _api.SetStripGain(TargetStrip, dB);
        _api.SetStripMute(TargetStrip, muted);
    }

    // ─── Poll / keep-alive + Voicemeeter → Windows sync ─────────────────────

    private void OnPollTick(object? _)
    {
        if (_connected)
        {
            // IsParametersDirty: -1 = disconnected, 0 = nothing changed, 1 = something changed
            int dirty = _api.IsParametersDirty();
            if (dirty < 0)
            {
                _connected = false;
                if (_wasConnected) { _wasConnected = false; ConnectionStateChanged?.Invoke(false); }
            }
            else if (dirty > 0)
            {
                SyncVoicemeeterToWindows();
            }

            CheckDevices();
        }
        else
        {
            // When disconnected, poll IsParametersDirty() to detect when VM starts/restarts.
            // DO NOT call Login() again — it was called once in Start() and the session
            // persists across VM restarts automatically per the Voicemeeter API spec.
            if (_loggedIn)
            {
                long now = Environment.TickCount64;
                if (now - _lastReconnectTick >= ReconnectIntervalMs)
                {
                    _lastReconnectTick = now;
                    try { if (_api.IsParametersDirty() >= 0) TryActivate(); }
                    catch { /* VM still not responding */ }
                }
            }
        }

        // Re-arm as one-shot: burst speed right after a change, normal speed otherwise
        int nextMs = Environment.TickCount64 < _burstEndTime ? BurstPollMs : PollIntervalMs;
        if (!_disposed) _pollTimer.Change(nextMs, Timeout.Infinite);
    }

    // ─── Voicemeeter → Windows sync ──────────────────────────────────────────

    private void SyncVoicemeeterToWindows()
    {
        if (!_api.GetStripGain(TargetStrip, out float dB) ||
            !_api.GetStripMute(TargetStrip, out bool muted))
            return;

        // Thread-safe read of what we last sent
        float sentGain;
        bool sentMute;
        lock (_syncLock) { sentGain = _lastSentGainDb; sentMute = _lastSentMute; }

        // Echo filter: VM is reporting what we last sent — our own push echoing, ignore it
        if (!float.IsNaN(sentGain) &&
            MathF.Abs(dB - sentGain) < GainEpsilon &&
            muted == sentMute)
            return;

        // Value guard: don't write to Windows if the value wouldn't actually change.
        // This is the primary defence against the OSD snap — even if echo detection
        // misses for any reason, a no-op write is blocked here.
        float targetScalar = DbToScalar(dB);
        if (MathF.Abs(targetScalar - _watcher.CurrentVolume) < ScalarEpsilon &&
            muted == _watcher.CurrentMuted)
            return;

        // Genuine Voicemeeter fader change — sync back to Windows
        lock (_syncLock) { _lastSentGainDb = dB; _lastSentMute = muted; }
        _lastWindowsWriteTime = Environment.TickCount64;
        _watcher.SetVolume(targetScalar);
        _watcher.SetMute(muted);
    }

    // ─── Activate / initial sync ─────────────────────────────────────────────

    /// <summary>
    /// Called when we know Voicemeeter is running (Login returned 0, or IsParametersDirty
    /// returned >= 0 after a period of disconnection). Sets connected state and syncs volume.
    /// Does NOT call Login() — that is called exactly once in <see cref="Start"/>.
    /// </summary>
    private void TryActivate()
    {
        try
        {
            // Flush the dirty flag so the next poll gets a clean read
            _api.IsParametersDirty();

            _connected = true;
            if (!_wasConnected) { _wasConnected = true; ConnectionStateChanged?.Invoke(true); }

            // Push current Windows volume to Voicemeeter immediately
            ApplyCurrentVolume();
        }
        catch
        {
            _connected = false;
        }
    }

    private void ApplyCurrentVolume()
    {
        float dB = ScalarToDb(_watcher.CurrentVolume);
        lock (_syncLock) { _lastSentGainDb = dB; _lastSentMute = _watcher.CurrentMuted; }
        _api.SetStripGain(TargetStrip, dB);
        _api.SetStripMute(TargetStrip, _watcher.CurrentMuted);
    }

    // ─── Bus device watchdog ─────────────────────────────────────────────────

    /// <summary>TickCount64 of the last device-list check.</summary>
    private long _lastDeviceCheckTick;

    /// <summary>
    /// Consecutive polls each bound device has been visible for.
    /// Attaching needs two in a row so we don't grab a device that is still enumerating
    /// while it powers up. Keyed by bus index.
    /// </summary>
    private readonly Dictionary<int, int> _deviceSeenCount = new();

    /// <summary>Output devices Voicemeeter can currently see. Empty when not connected.</summary>
    public List<VoicemeeterApi.AudioDevice> ListOutputDevices() => _api.ListOutputDevices();

    /// <summary>Settings shared with the tray menu. Caller must call Save() after changes.</summary>
    public AppConfig Config => _config;

    /// <summary>
    /// Applies a binding immediately: attach if the device is present, detach otherwise.
    /// Called when the user picks a device from the tray menu.
    /// </summary>
    public void ApplyBinding(BusDeviceBinding binding)
    {
        if (!_connected) return;
        _deviceSeenCount.Remove(binding.Bus);

        if (string.IsNullOrEmpty(binding.Name))
        {
            _api.ClearBusDevice(binding.Bus, binding.Type);
            return;
        }

        if (_api.ListOutputDevices().Any(d => d.Type == binding.Type && d.Name == binding.Name))
            _api.SetBusDevice(binding.Bus, binding.Type, binding.Name);
    }

    /// <summary>
    /// Keeps each bound bus attached to its device only while that device exists.
    /// A monitor that loses power drops out of Voicemeeter's output device list; leaving it
    /// selected makes Voicemeeter re-probe it every few seconds, which clips audio on the
    /// other buses. Detaching stops the re-probing; re-attaching happens on its own once the
    /// device comes back.
    /// </summary>
    private void CheckDevices()
    {
        if (!_config.DeviceWatchdogEnabled || _config.Buses.Count == 0) return;

        long now = Environment.TickCount64;
        if (now - _lastDeviceCheckTick < _config.DeviceWatchdogIntervalMs) return;
        _lastDeviceCheckTick = now;

        var present = _api.ListOutputDevices();
        // An empty list means Voicemeeter has not enumerated yet — don't tear anything down.
        if (present.Count == 0) return;

        foreach (var b in _config.Buses)
        {
            if (string.IsNullOrEmpty(b.Name)) continue;

            bool available = present.Any(d => d.Type == b.Type && d.Name == b.Name);
            bool attached = !string.IsNullOrWhiteSpace(_api.GetBusDeviceName(b.Bus));

            if (!available)
            {
                _deviceSeenCount[b.Bus] = 0;
                if (attached) _api.ClearBusDevice(b.Bus, b.Type);
                continue;
            }

            if (attached)
            {
                _deviceSeenCount[b.Bus] = 2;
                continue;
            }

            int seen = _deviceSeenCount.GetValueOrDefault(b.Bus) + 1;
            _deviceSeenCount[b.Bus] = seen;
            if (seen >= 2) _api.SetBusDevice(b.Bus, b.Type, b.Name);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Converts a Windows volume scalar [0..1] to Voicemeeter gain in dB.</summary>
    private float ScalarToDb(float scalar)
        => VolumeCurve.ToDb(_config.Profile, _config.MinGainDb, scalar);

    /// <summary>Converts a Voicemeeter gain in dB back to a Windows scalar [0..1].</summary>
    private float DbToScalar(float dB)
        => VolumeCurve.ToScalar(_config.Profile, _config.MinGainDb, dB);

    /// <summary>Forces an immediate re-sync of current Windows volume to Voicemeeter.</summary>
    public void SyncNow()
    {
        if (_connected) ApplyCurrentVolume();
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _watcher.VolumeChanged -= OnVolumeChanged;
            _pollTimer.Dispose();
        }
    }
}

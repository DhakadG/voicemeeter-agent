using NAudio.CoreAudioApi;

namespace VoicemeeterAgent;

/// <summary>
/// Watches the Windows default playback device for master volume and mute changes
/// and raises <see cref="VolumeChanged"/> on any thread.
/// </summary>
internal sealed class WindowsVolumeWatcher : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private MMDevice? _device;
    private bool _disposed;

    /// <summary>
    /// Raised when Windows master volume or mute state changes.
    /// <c>scalar</c> is in [0, 1]; <c>muted</c> reflects the mute button.
    /// </summary>
    public event Action<float, bool>? VolumeChanged;

    public float CurrentVolume { get; private set; }
    public bool CurrentMuted { get; private set; }

    public WindowsVolumeWatcher()
    {
        _enumerator = new MMDeviceEnumerator();
        AttachDevice();
    }

    private void AttachDevice()
    {
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device.Dispose();
            }

            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            // Snapshot current state before subscribing
            CurrentVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            CurrentMuted = _device.AudioEndpointVolume.Mute;

            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        }
        catch
        {
            // Default device not available; will retry via poll
            _device = null;
        }
    }

    // ─── NAudio volume notification ──────────────────────────────────────────

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_disposed) return;
        CurrentVolume = data.MasterVolume;
        CurrentMuted = data.Muted;
        VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
    }

    /// <summary>
    /// Re-attaches to the current default device (call when the default device changes).
    /// </summary>
    public void Reattach() => AttachDevice();

    /// <summary>Sets Windows master volume scalar [0..1]. Will trigger <see cref="VolumeChanged"/> as normal.</summary>
    public void SetVolume(float scalar)
    {
        if (_device == null) return;
        _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
    }

    /// <summary>Sets Windows master mute state. Will trigger <see cref="VolumeChanged"/> as normal.</summary>
    public void SetMute(bool muted)
    {
        if (_device == null) return;
        _device.AudioEndpointVolume.Mute = muted;
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                if (_device != null)
                    _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            }
            catch { }
            _device?.Dispose();
            _enumerator.Dispose();
        }
    }
}

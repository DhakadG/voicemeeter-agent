using System.Globalization;
using System.IO.Pipes;

namespace VoicemeeterAgent;

/// <summary>
/// Client for the Edifier MR5 desktop app's local pipe (<c>\\.\pipe\mr5-volume</c>).
/// Lets "hardware volume" mode drive the speaker's own volume instead of a Voicemeeter fader.
/// Line protocol (UTF-8):
///   agent → app: <c>set 0.42</c> (Windows scalar), <c>get</c>
///   app → agent: <c>vol 16 30</c> (speaker step / max), <c>link up</c> / <c>link down</c>
/// The pipe is optional: when the app is not running the agent behaves exactly as before.
/// </summary>
internal sealed class Mr5Bridge : IDisposable
{
    private const string PipeName = "mr5-volume";
    private const int RetryMs = 5_000;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();
    private StreamWriter? _out;
    private bool _pipeUp, _linkUp;

    /// <summary>True when the app is running and the speaker is reachable.</summary>
    public bool Active => _pipeUp && _linkUp;

    /// <summary>Raised on a background thread when <see cref="Active"/> changes.</summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>Raised on a background thread with the speaker's (step, max) when it changes.</summary>
    public event Action<int, int>? VolumeReported;

    public Mr5Bridge() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void SendVolume(float scalar) =>
        Send(string.Create(CultureInfo.InvariantCulture, $"set {Math.Clamp(scalar, 0f, 1f):0.####}"));

    private void Send(string line)
    {
        lock (_writeLock)
        {
            // A failed write means the app is gone: go inactive now so the fader fallback kicks in
            // before the read loop notices, instead of pinning Voicemeeter at unity meanwhile.
            try { _out?.WriteLine(line); }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                _out = null;
                Set(pipeUp: false, linkUp: false);
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(2_000, ct);
                using var reader = new StreamReader(pipe);
                lock (_writeLock) _out = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
                Set(pipeUp: true, linkUp: _linkUp);
                Send("get");

                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    var p = line.Trim().Split(' ');
                    if (p is ["link", var state]) Set(true, state == "up");
                    else if (p is ["vol", var cur, var max] &&
                             int.TryParse(cur, out int c) && int.TryParse(max, out int m) && m > 0)
                        VolumeReported?.Invoke(c, m);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* app not running (timeout) or pipe broke */ }

            lock (_writeLock) _out = null;
            Set(pipeUp: false, linkUp: false);
            try { await Task.Delay(RetryMs, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Set(bool pipeUp, bool linkUp)
    {
        bool was = Active;
        (_pipeUp, _linkUp) = (pipeUp, linkUp);
        if (Active != was) ActiveChanged?.Invoke(Active);
    }

    public void Dispose() => _cts.Cancel();
}

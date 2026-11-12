using PhoneAudioConsole.Audio;
using SonicRoute.AndroidAudio;

namespace PhoneAudioConsole.Platform;

/// <summary>Live view of one per-application stream, for the settings UI.</summary>
internal sealed record AppStreamView(string Package, string Name, int Volume, bool Muted, PhoneCaptureState State, string? Error);

/// <summary>
/// Owns one capture stream per playing phone app: starts a stream when an app begins playing,
/// retires it a few seconds after it stops, and keeps an independent volume per app.
/// </summary>
internal sealed class PerAppAudioManager : IAsyncDisposable
{
    private static readonly TimeSpan RetireAfter = TimeSpan.FromSeconds(4);

    private readonly AdbClient _adb;
    private readonly Func<string?> _outputDeviceProvider;
    private readonly Func<bool> _mutedProvider;
    private readonly Action<string> _log;
    private readonly Func<string, int> _volumeProvider;
    private readonly Action<string, int> _volumeSaver;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public event Action? Changed;

    private sealed class Entry
    {
        public required AndroidApp App { get; init; }
        public required PhoneCaptureSession Session { get; init; }
        public long LastSeenTicks { get; set; }
    }

    public PerAppAudioManager(AdbClient adb, Func<string?> outputDeviceProvider, Func<bool> mutedProvider, Func<string, int> volumeProvider, Action<string, int> volumeSaver, Action<string> log)
    {
        _adb = adb;
        _outputDeviceProvider = outputDeviceProvider;
        _mutedProvider = mutedProvider;
        _volumeProvider = volumeProvider;
        _volumeSaver = volumeSaver;
        _log = log;
    }

    public IReadOnlyList<AppStreamView> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values
                .Select(e => new AppStreamView(e.App.Package, e.App.Name, e.Session.Volume, e.Session.Muted, e.Session.State, e.Session.Error))
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public void SetVolume(string package, int volume) => SetVolume(package, volume, save: true);

    public bool HasAny
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0;
            }
        }
    }

    public void SetAllMuted(bool muted)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Session.Muted = muted;
            }
        }
    }

    public void AdjustAllVolume(int delta)
    {
        List<(string Package, int Volume)> updates = new();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                int volume = Math.Clamp(entry.Session.Volume + delta, 0, 100);
                entry.Session.Volume = volume;
                updates.Add((entry.App.Package, volume));
            }
        }

        foreach (var (package, volume) in updates)
        {
            _volumeSaver(package, volume);
        }
    }

    private void SetVolume(string package, int volume, bool save)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(package, out entry);
        }

        if (entry == null)
        {
            return;
        }

        entry.Session.Volume = volume;
        if (save)
        {
            _volumeSaver(package, volume);
        }
    }

    public async Task SyncAsync(IReadOnlyList<AndroidApp> playing, AndroidDevice device, CancellationToken token)
    {
        if (_disposed)
        {
            return;
        }

        long now = Environment.TickCount64;
        var playingSet = new HashSet<string>(playing.Select(a => a.Package), StringComparer.Ordinal);
        var toStop = new List<Entry>();

        lock (_gate)
        {
            foreach (var app in playing)
            {
                if (_entries.TryGetValue(app.Package, out var existing))
                {
                    existing.LastSeenTicks = now;
                    continue;
                }

                var session = new PhoneCaptureSession(
                    () => new ScrcpyAudioTransport(_adb, CaptureScope.Application, app.Package, keepPhoneAudio: false, _log),
                    app, _outputDeviceProvider, _log)
                {
                    Volume = _volumeProvider(app.Package),
                    Muted = _mutedProvider()
                };
                session.Changed += RaiseChanged;
                var entry = new Entry { App = app, Session = session, LastSeenTicks = now };
                _entries[app.Package] = entry;
                _ = StartEntryAsync(entry, device, token);
            }

            foreach (var kv in _entries.ToList())
            {
                if (!playingSet.Contains(kv.Key) && now - kv.Value.LastSeenTicks > RetireAfter.TotalMilliseconds)
                {
                    _entries.Remove(kv.Key);
                    toStop.Add(kv.Value);
                }
            }
        }

        RaiseChanged();

        foreach (var entry in toStop)
        {
            await StopEntryAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task StartEntryAsync(Entry entry, AndroidDevice device, CancellationToken token)
    {
        try
        {
            await entry.Session.StartAsync(device, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"Capture failed ({entry.App.Name}): {ex.Message}");
        }
    }

    private async Task StopEntryAsync(Entry entry)
    {
        entry.Session.Changed -= RaiseChanged;
        try
        {
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"Retire failed ({entry.App.Name}): {ex.Message}");
        }

        RaiseChanged();
    }

    public async Task StopAllAsync()
    {
        List<Entry> entries;
        lock (_gate)
        {
            entries = _entries.Values.ToList();
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Session.Changed -= RaiseChanged;
        }

        foreach (var entry in entries)
        {
            try
            {
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"Stop failed ({entry.App.Name}): {ex.Message}");
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
    }
}

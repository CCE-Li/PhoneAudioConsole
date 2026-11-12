using SonicRoute.AndroidAudio;

namespace PhoneAudioConsole.Audio;

public enum PhoneCaptureState
{
    Idle,
    Starting,
    Receiving,
    Failed
}

/// <summary>
/// One capture session: transport + Opus decode + PCM source + WASAPI output. The transport is
/// supplied by the owner so the same session serves whole-device and per-application streams.
/// Volume/mute are applied to the PC-side gain only; the phone is left untouched here.
/// </summary>
public sealed class PhoneCaptureSession : IAsyncDisposable
{
    private readonly Func<IAudioTransport> _transportFactory;
    private readonly AndroidApp _app;
    private readonly Func<string?> _outputDeviceProvider;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _run;
    private IAudioTransport? _transport;
    private IPcmPlayer? _player;
    private AndroidPcmSource? _pcm;
    private Task? _receive;
    private int _volume = 100;
    private bool _muted;
    private bool _disposed;

    public PhoneCaptureState State { get; private set; } = PhoneCaptureState.Idle;
    public AndroidDevice? Device { get; private set; }
    public string? Error { get; private set; }

    public event Action? Changed;

    public PhoneCaptureSession(Func<IAudioTransport> transportFactory, AndroidApp app, Func<string?>? outputDeviceProvider = null, Action<string>? log = null)
    {
        _transportFactory = transportFactory;
        _app = app;
        _outputDeviceProvider = outputDeviceProvider ?? (() => null);
        _log = log ?? (_ => { });
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _transport != null;
            }
        }
    }

    public int Volume
    {
        get => Volatile.Read(ref _volume);
        set
        {
            Volatile.Write(ref _volume, Math.Clamp(value, 0, 100));
            ApplyGain();
        }
    }

    public bool Muted
    {
        get => Volatile.Read(ref _muted);
        set
        {
            Volatile.Write(ref _muted, value);
            ApplyGain();
        }
    }

    public async Task StartAsync(AndroidDevice device, CancellationToken token)
    {
        await StopAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var run = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_gate)
        {
            _run = run;
        }

        var runToken = run.Token;
        SetState(PhoneCaptureState.Starting, device, null);

        try
        {
            var transport = _transportFactory();
            Stream stream = await transport.ConnectAsync(device, _app, keepPhoneAudio: true, runToken).ConfigureAwait(false);

            var pcm = new AndroidPcmSource { Volume = Volatile.Read(ref _muted) ? 0f : Volatile.Read(ref _volume) / 100f };
            var player = new WasapiPcmPlayer(pcm, _outputDeviceProvider());
            lock (_gate)
            {
                _transport = transport;
                _pcm = pcm;
                _player = player;
            }

            player.Failed += OnPlayerFailed;
            player.Play();

            var receiver = new ScrcpyOpusReceiver();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var receive = receiver.ReceiveAsync(stream, pcm, () =>
            {
                SetState(PhoneCaptureState.Receiving, device, null);
                first.TrySetResult();
            }, runToken);
            lock (_gate)
            {
                _receive = receive;
            }

            _ = ObserveAsync(receive);

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            await Task.WhenAny(first.Task, receive).WaitAsync(startup.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopAsync().ConfigureAwait(false);
            SetState(PhoneCaptureState.Failed, device, ex.Message);
            _log($"Start failed ({_app.Name}): {ex.Message}");
            throw;
        }
    }

    private async Task ObserveAsync(Task receive)
    {
        string message;
        try
        {
            await receive.ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        await StopAsync().ConfigureAwait(false);
        SetState(PhoneCaptureState.Failed, Device, message);
        _log($"Receive ended ({_app.Name}): {message}");
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? run;
        IAudioTransport? transport;
        IPcmPlayer? player;
        Task? receive;
        lock (_gate)
        {
            run = _run;
            _run = null;
            transport = _transport;
            _transport = null;
            player = _player;
            _player = null;
            _pcm = null;
            receive = _receive;
            _receive = null;
        }

        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException) { }

        if (receive != null)
        {
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch { /* session teardown: the receive loop's own error is reported elsewhere */ }
        }

        if (player != null)
        {
            try
            {
                player.Dispose();
            }
            catch (Exception ex)
            {
                _log("Player cleanup: " + ex.Message);
            }
        }

        if (transport != null)
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log("Transport cleanup: " + ex.Message);
            }
        }

        run?.Dispose();
        SetState(PhoneCaptureState.Idle, null, null);
    }

    private void OnPlayerFailed(Exception error)
    {
        _log($"Audio output ({_app.Name}): {error.Message}");
        _ = Task.Run(async () =>
        {
            await StopAsync().ConfigureAwait(false);
            SetState(PhoneCaptureState.Failed, Device, error.Message);
        });
    }

    private void ApplyGain()
    {
        AndroidPcmSource? pcm;
        lock (_gate)
        {
            pcm = _pcm;
        }

        if (pcm != null)
        {
            pcm.Volume = Volatile.Read(ref _muted) ? 0f : Volatile.Read(ref _volume) / 100f;
        }
    }

    private void SetState(PhoneCaptureState state, AndroidDevice? device, string? error)
    {
        State = state;
        if (device != null)
        {
            Device = device;
        }

        Error = error;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}

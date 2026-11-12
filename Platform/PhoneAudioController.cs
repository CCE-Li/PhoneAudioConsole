using System.IO;
using System.Text.RegularExpressions;
using PhoneAudioConsole.Audio;
using SonicRoute.AndroidAudio;

namespace PhoneAudioConsole.Platform;

/// <summary>How the phone connects to the PC.</summary>
public enum ConnectionMode
{
    /// <summary>Cable + USB debugging. Supports per-app capture.</summary>
    Usb,

    /// <summary>ADB over TCP on the same LAN. Supports per-app capture.</summary>
    Wifi,

    /// <summary>PC acts as a Bluetooth A2DP sink. No cable, no debugging, but whole-device only.</summary>
    Bluetooth
}

/// <summary>
/// Watches for a phone and drives capture. In <see cref="CaptureScope.Application"/> it detects
/// the app currently playing and takes over just that app; in <see cref="CaptureScope.Device"/> it
/// captures the whole output. Either way the phone is silenced on takeover, which persists after
/// unplug (device-side media volume stays at 0).
/// </summary>
public sealed class PhoneAudioController : IAsyncDisposable
{
    private static readonly AndroidApp DeviceApp = new("device", "整机输出", 0, "media");

    private const int MinSdkApplication = 33; // Android 13 for per-app playback capture.
    private const int BootstrapIntervalMs = 30_000;

    private readonly AppSettings _settings;
    private readonly AdbClient _adb;
    private readonly PhoneCaptureSession _deviceSession;
    private readonly PerAppAudioManager _perApp;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _sdkBySerial = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sweptSerials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);
    private readonly ExplorerWindowSuppressor _explorerSuppressor;
    private readonly BluetoothSink _bluetooth;
    private readonly ScrcpyMirror _mirror;
    private readonly string _logPath;

    private long _lastBootstrapTicks;
    private int _bluetoothVolume = 100;
    private string? _targetSerial;

    private Task? _watch;
    private ConnectionMode _connection;
    private CaptureScope _scope;
    private Dictionary<string, AndroidApp>? _apps;
    private string? _appsSerial;
    private string? _mutedSerial;
    private string _status = "等待手机接入";
    private bool _disposed;

    public event Action? Changed;

    public PhoneAudioController(AppSettings settings)
    {
        _settings = settings;
        _connection = settings.ConnectionMode?.ToLowerInvariant() switch
        {
            "wifi" => ConnectionMode.Wifi,
            "bluetooth" => ConnectionMode.Bluetooth,
            _ => ConnectionMode.Usb
        };
        _scope = string.Equals(settings.CaptureScope, "device", StringComparison.OrdinalIgnoreCase)
            ? CaptureScope.Device
            : CaptureScope.Application;

        var bundle = new AndroidRuntimeBundle(Path.Combine(AppSettings.DirectoryPath, "PhoneRuntime"));
        _adb = new AdbClient(bundle);
        _logPath = Path.Combine(AppSettings.DirectoryPath, "logs", "console.log");
        _explorerSuppressor = new ExplorerWindowSuppressor(Log);
        _bluetooth = new BluetoothSink(Log);
        _bluetooth.Changed += RaiseChanged;
        _bluetoothVolume = settings.PhoneVolume;
        _mirror = new ScrcpyMirror(_adb, Log);

        _deviceSession = new PhoneCaptureSession(
            () => new ScrcpyAudioTransport(_adb, CaptureScope.Device, log: Log),
            DeviceApp, () => _settings.SelectedOutputDeviceId, Log)
        {
            Volume = settings.PhoneVolume
        };
        _deviceSession.Changed += RaiseChanged;

        _perApp = new PerAppAudioManager(
            _adb,
            () => _settings.SelectedOutputDeviceId,
            () => MutedAll,
            package => _settings.PhoneAppVolumes.TryGetValue(package, out int v) ? v : 100,
            (package, volume) =>
            {
                _settings.PhoneAppVolumes[package] = volume;
                _settings.Save();
            },
            Log);
        _perApp.Changed += RaiseChanged;
    }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public ConnectionMode Connection
    {
        get => _connection;
        set
        {
            if (_connection == value)
            {
                return;
            }

            _connection = value;
            _settings.ConnectionMode = value switch
            {
                ConnectionMode.Wifi => "wifi",
                ConnectionMode.Bluetooth => "bluetooth",
                _ => "usb"
            };
            _settings.Save();

            if (value == ConnectionMode.Bluetooth)
            {
                // Bluetooth renders to the default endpoint, so its volume is that endpoint's.
                int current = AudioDeviceService.GetDefaultRenderVolume();
                _bluetoothVolume = current >= 0 ? current : _settings.PhoneVolume;
            }
            else
            {
                _ = _bluetooth.DisconnectAsync();
            }

            _ = Task.Run(TeardownAsync);
            _mirror.Stop();
            Changed?.Invoke();
        }
    }

    public CaptureScope Scope
    {
        get => _scope;
        set
        {
            if (_scope == value)
            {
                return;
            }

            _scope = value;
            _settings.CaptureScope = value == CaptureScope.Device ? "device" : "app";
            _settings.Save();
            _ = Task.Run(TeardownAsync);
            Changed?.Invoke();
        }
    }

    public string? BluetoothDeviceId
    {
        get => _settings.BluetoothDeviceId;
        set
        {
            if (string.Equals(_settings.BluetoothDeviceId, value, StringComparison.Ordinal))
            {
                return;
            }

            _settings.BluetoothDeviceId = value;
            _settings.Save();
            Changed?.Invoke();
        }
    }

    public static Task<IReadOnlyList<(string Id, string Name)>> ListBluetoothDevicesAsync() => BluetoothSink.ListDevicesAsync();

    public bool AutoTakeover
    {
        get => _settings.AutoTakeover;
        set
        {
            if (_settings.AutoTakeover == value)
            {
                return;
            }

            _settings.AutoTakeover = value;
            _settings.Save();
            Changed?.Invoke();
        }
    }

    public bool MuteOnTakeover
    {
        get => _settings.MuteOnTakeover;
        set
        {
            if (_settings.MuteOnTakeover == value)
            {
                return;
            }

            _settings.MuteOnTakeover = value;
            _settings.Save();
            Changed?.Invoke();
        }
    }

    public bool SuppressExplorerWindow
    {
        get => _settings.SuppressExplorerWindow;
        set
        {
            if (_settings.SuppressExplorerWindow == value)
            {
                return;
            }

            _settings.SuppressExplorerWindow = value;
            _settings.Save();
            Changed?.Invoke();
        }
    }

    public string? OutputDeviceId
    {
        get => _settings.SelectedOutputDeviceId;
        set
        {
            if (string.Equals(_settings.SelectedOutputDeviceId, value, StringComparison.Ordinal))
            {
                return;
            }

            _settings.SelectedOutputDeviceId = value;
            _settings.Save();
            // Restart active streams so they move to the new endpoint.
            _ = Task.Run(TeardownAsync);
            Changed?.Invoke();
        }
    }

    public int DeviceVolume
    {
        get => _connection == ConnectionMode.Bluetooth ? _bluetoothVolume : _deviceSession.Volume;
        set
        {
            if (_connection == ConnectionMode.Bluetooth)
            {
                _bluetoothVolume = Math.Clamp(value, 0, 100);
                AudioDeviceService.SetDefaultRenderVolume(_bluetoothVolume);
                _settings.PhoneVolume = _bluetoothVolume;
                _settings.Save();
                Changed?.Invoke();
                return;
            }

            _deviceSession.Volume = value;
            _settings.PhoneVolume = value;
            _settings.Save();
            Changed?.Invoke();
        }
    }

    internal IReadOnlyList<AppStreamView> AppStreams => _perApp.Snapshot();

    public void SetAppVolume(string package, int volume)
    {
        _perApp.SetVolume(package, volume);
        Changed?.Invoke();
    }

    public bool MutedAll { get; private set; }

    public void ToggleMuteAll()
    {
        MutedAll = !MutedAll;
        _deviceSession.Muted = MutedAll;
        _perApp.SetAllMuted(MutedAll);
        Changed?.Invoke();
    }

    /// <summary>Adjusts every active stream (device and per-app) by the same delta.</summary>
    public void AdjustVolumeAll(int delta)
    {
        if (_deviceSession.IsActive)
        {
            DeviceVolume = Math.Clamp(DeviceVolume + delta, 0, 100);
        }

        _perApp.AdjustAllVolume(delta);
        Changed?.Invoke();
    }

    public void ToggleMirror()
    {
        if (_mirror.IsRunning)
        {
            _mirror.Stop();
            SetStatus("已关闭息屏控制");
            Changed?.Invoke();
            return;
        }

        string? serial = _targetSerial;
        if (serial is null || _connection == ConnectionMode.Bluetooth)
        {
            SetStatus("息屏控制需要先连接手机（USB/WiFi）");
            return;
        }

        try
        {
            _mirror.Start(serial);
            SetStatus("已开启息屏控制（scrcpy 窗口）");
        }
        catch (Exception ex)
        {
            SetStatus("息屏控制启动失败：" + ex.Message);
        }

        Changed?.Invoke();
    }

    public bool IsMirroring => _mirror.IsRunning;

    public bool CanMirror => _targetSerial is not null && _connection != ConnectionMode.Bluetooth;

    public void LogMessage(string line) => Log(line);

    public void Start()
    {
        var outputs = AudioDeviceService.ListRenderDevices();
        Log($"render devices: {(outputs.Count == 0 ? "(none)" : string.Join(" | ", outputs.Select(d => d.Name)))}");
        _watch ??= Task.Run(() => WatchAsync(_lifetime.Token));
    }

    private async Task WatchAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus("ADB 不可用：" + ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        if (_connection == ConnectionMode.Bluetooth)
        {
            await PollBluetoothAsync(token).ConfigureAwait(false);
            return;
        }

        if (!_settings.AutoTakeover)
        {
            await TeardownAsync().ConfigureAwait(false);
            SetStatus("自动接管已关闭");
            return;
        }

        string text = await _adb.RunAsync(token, "devices", "-l").ConfigureAwait(false);
        var devices = AdbClient.ParseDevices(text);
        TrackArrivals(text, devices);
        var usb = devices.FirstOrDefault(d => d.State == "device" && !d.IsWifi);
        var wifi = devices.FirstOrDefault(d => d.State == "device" && d.IsWifi);

        AndroidDevice? target;
        if (_connection == ConnectionMode.Wifi)
        {
            target = wifi;
            if (target == null && usb != null)
            {
                // No wireless endpoint yet: enable ADB-over-TCP over the cable.
                if (await TryBootstrapWifiAsync(usb, token).ConfigureAwait(false))
                {
                    SetStatus("正在启用 WiFi 调试…");
                    return;
                }

                await TeardownAsync().ConfigureAwait(false);
                SetStatus("WiFi 模式：等待无线连接（已尝试通过 USB 启用无线调试）");
                return;
            }
        }
        else
        {
            target = usb;
        }

        if (target == null)
        {
            _targetSerial = null;
            await TeardownAsync().ConfigureAwait(false);
            SetStatus(devices.Any(d => d.State == "unauthorized") ? "手机待授权（请在手机上允许 USB 调试）"
                : devices.Any(d => d.State == "offline") ? "手机离线（请重新插拔或重新配对）"
                : _connection == ConnectionMode.Wifi ? "WiFi 模式：未发现设备（先插一次 USB 可启用无线调试）"
                : "USB 模式：未检测到 USB 设备（请检查数据线与 USB 调试授权）");
            return;
        }

        _targetSerial = target.Serial;
        await SweepStaleArtifactsAsync(target, token).ConfigureAwait(false);

        if (_scope == CaptureScope.Device)
        {
            await PollDeviceModeAsync(target, token, null).ConfigureAwait(false);
            return;
        }

        int sdk;
        try
        {
            sdk = await GetSdkAsync(target, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus("读取 Android 版本失败：" + ex.Message);
            return;
        }

        if (sdk > 0 && sdk < MinSdkApplication)
        {
            // Per-app capture needs Android 13+; fall back to whole-device takeover on older phones.
            await PollDeviceModeAsync(target, token, "手机需 Android 13+ 才能按应用，已回退整机").ConfigureAwait(false);
            return;
        }

        await PollAppModeAsync(target, token).ConfigureAwait(false);
    }

    private async Task PollBluetoothAsync(CancellationToken token)
    {
        // Bluetooth and ADB are mutually exclusive: ADB audio would double the Bluetooth audio.
        if (_deviceSession.IsActive || _perApp.HasAny)
        {
            await TeardownAsync().ConfigureAwait(false);
        }

        await _bluetooth.StepAsync(_settings.BluetoothDeviceId, token).ConfigureAwait(false);

        string name = _bluetooth.DeviceName ?? "手机";
        SetStatus(_bluetooth.State switch
        {
            BluetoothSinkState.Flowing => $"蓝牙：正在播放（{name}）",
            BluetoothSinkState.Ready => $"蓝牙已就绪（{name}），等待手机播放",
            BluetoothSinkState.Connecting => $"蓝牙：正在连接 {name}…",
            BluetoothSinkState.Failed => $"蓝牙失败：{_bluetooth.Detail}",
            _ => _bluetooth.Detail ?? "蓝牙：等待已配对的手机"
        });
    }

    private async Task<int> GetSdkAsync(AndroidDevice device, CancellationToken token)
    {
        if (_sdkBySerial.TryGetValue(device.Serial, out int cached))
        {
            return cached;
        }

        string text = await _adb.RunAsync(token, "-s", device.Serial, "shell", "getprop", "ro.build.version.sdk").ConfigureAwait(false);
        int sdk = int.TryParse(text.Trim(), out int value) ? value : 0;
        _sdkBySerial[device.Serial] = sdk;
        return sdk;
    }

    /// <summary>Arms Explorer-window suppression each time a USB phone (re)appears.</summary>
    private void TrackArrivals(string rawText, IReadOnlyList<AndroidDevice> devices)
    {
        long now = Environment.TickCount64;
        foreach (var device in devices)
        {
            if (device.State != "device" || device.IsWifi)
            {
                continue;
            }

            // Treat as an arrival if unseen for a while, so a fast unplug/replug still re-arms.
            bool arrived = !_lastSeen.TryGetValue(device.Serial, out long last) || now - last > 4000;
            _lastSeen[device.Serial] = now;
            if (arrived && _settings.SuppressExplorerWindow)
            {
                var hints = new List<string>(HintsFor(device, rawText));
                hints.AddRange(PortableDeviceName.Resolve(device.Serial));
                _explorerSuppressor.Arm(hints);
            }
        }
    }

    /// <summary>Candidate names a Windows window for this phone might carry in its title.</summary>
    private static IEnumerable<string> HintsFor(AndroidDevice device, string rawText)
    {
        yield return device.Name;
        foreach (string line in rawText.Split('\n'))
        {
            if (!line.TrimStart().StartsWith(device.Serial, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int index = token.IndexOf(':');
                if (index <= 0)
                {
                    continue;
                }

                if (token[..index] is "model" or "product" or "device")
                {
                    yield return token[(index + 1)..].Replace('_', ' ');
                }
            }
        }
    }

    /// <summary>Removes temp server jars left on the phone by a previous crash. Runs once per device.</summary>
    private async Task SweepStaleArtifactsAsync(AndroidDevice device, CancellationToken token)
    {
        if (!_sweptSerials.Add(device.Serial))
        {
            return;
        }

        try
        {
            await _adb.RunAsync(token, "-s", device.Serial, "shell", "rm", "-f", "/data/local/tmp/phonaudio_*.jar").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("Sweep stale jars: " + ex.Message);
        }
    }

    /// <summary>
    /// Enables ADB-over-TCP on the phone and connects, so it survives unplugging. Throttled, and
    /// returns true only when it actually switched. The IP must be read before <c>tcpip</c>, because
    /// that command restarts adbd and drops the USB link.
    /// </summary>
    private async Task<bool> TryBootstrapWifiAsync(AndroidDevice device, CancellationToken token)
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastBootstrapTicks) < BootstrapIntervalMs)
        {
            return false;
        }

        Volatile.Write(ref _lastBootstrapTicks, now);

        string? ip;
        try
        {
            ip = await GetDeviceIpAsync(device.Serial, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("WiFi bootstrap: IP read failed: " + ex.Message);
            return false;
        }

        if (ip == null)
        {
            Log("WiFi bootstrap: no device IP (phone not on WiFi?)");
            return false;
        }

        try
        {
            // adbd restarts in TCP mode; this may report a spurious failure as the USB link drops.
            await _adb.RunAsync(token, "-s", device.Serial, "tcpip", "5555").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("WiFi tcpip: " + ex.Message);
        }

        try
        {
            string result = await _adb.RunAsync(token, "connect", ip + ":5555").ConfigureAwait(false);
            Log($"WiFi bootstrap: {ip}:5555 -> {result.Trim()}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("WiFi connect failed: " + ex.Message);
        }

        return true;
    }

    private async Task<string?> GetDeviceIpAsync(string serial, CancellationToken token)
    {
        string route = await _adb.RunAsync(token, "-s", serial, "shell", "ip", "route").ConfigureAwait(false);
        foreach (string line in route.Split('\n'))
        {
            if (!line.Contains("wlan", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = Regex.Match(line, @"src\s+(\d{1,3}(?:\.\d{1,3}){3})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        var fallback = Regex.Match(route, @"(\d{1,3}(?:\.\d{1,3}){3})");
        return fallback.Success && fallback.Groups[1].Value != "127.0.0.1" ? fallback.Groups[1].Value : null;
    }

    private async Task PollDeviceModeAsync(AndroidDevice device, CancellationToken token, string? note)
    {
        await _perApp.StopAllAsync().ConfigureAwait(false);

        string suffix = note == null ? "（整机）" : $"（整机）· {note}";
        if (_deviceSession.IsActive || _deviceSession.State == PhoneCaptureState.Starting)
        {
            SetStatus($"已接管 {device.Name}{suffix}");
            return;
        }

        SetStatus($"正在接管 {device.Name}…");
        try
        {
            await _deviceSession.StartAsync(device, token).ConfigureAwait(false);
            await MuteIfRequestedAsync(device, token).ConfigureAwait(false);
            SetStatus($"已接管 {device.Name}{suffix}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus($"接管失败：{ex.Message}");
        }
    }

    private async Task PollAppModeAsync(AndroidDevice device, CancellationToken token)
    {
        if (_deviceSession.IsActive)
        {
            await _deviceSession.StopAsync().ConfigureAwait(false);
        }

        var apps = _apps;
        if (apps == null || _appsSerial != device.Serial)
        {
            try
            {
                apps = (await _adb.AppsAsync(device, token).ConfigureAwait(false)).ToDictionary(a => a.Package, StringComparer.Ordinal);
                _apps = apps;
                _appsSerial = device.Serial;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _apps = null;
                _appsSerial = null;
                SetStatus("读取手机应用列表失败：" + ex.Message);
                return;
            }
        }

        IReadOnlyList<string> playing;
        try
        {
            playing = await PhoneAppWatcher.GetPlayingPackagesAsync(_adb, device, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus("读取播放状态失败：" + ex.Message);
            return;
        }

        var known = new List<AndroidApp>();
        foreach (string package in playing)
        {
            if (apps.TryGetValue(package, out var app))
            {
                known.Add(app);
            }
        }

        await _perApp.SyncAsync(known, device, token).ConfigureAwait(false);

        var snapshot = _perApp.Snapshot();
        var receiving = snapshot.Where(v => v.State is PhoneCaptureState.Receiving or PhoneCaptureState.Starting).ToList();
        var failed = snapshot.FirstOrDefault(v => v.State == PhoneCaptureState.Failed);

        if (receiving.Count > 0)
        {
            await MuteIfRequestedAsync(device, token).ConfigureAwait(false);
            string names = string.Join("、", receiving.Select(a => a.Name).Take(3));
            SetStatus($"已接管 {receiving.Count} 个应用：{names}");
        }
        else if (failed != null)
        {
            SetStatus($"按应用接管失败（{failed.Name}）：{failed.Error}");
        }
        else
        {
            SetStatus($"{device.Name} 已连接，等待应用播放");
        }
    }

    private async Task MuteIfRequestedAsync(AndroidDevice device, CancellationToken token)
    {
        if (!_settings.MuteOnTakeover || _mutedSerial == device.Serial)
        {
            return;
        }

        _mutedSerial = device.Serial;
        try
        {
            await _adb.RunAsync(token, "-s", device.Serial, "shell", "cmd", "media_session", "volume", "--stream", "3", "--set", "0").ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Older builds may not expose `cmd media_session volume`; fall back to the media helper.
        }

        try
        {
            await _adb.RunAsync(token, "-s", device.Serial, "shell", "media", "volume", "--stream", "3", "--set", "0").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("Mute failed: " + ex.Message);
        }
    }

    private async Task TeardownAsync()
    {
        await _deviceSession.StopAsync().ConfigureAwait(false);
        await _perApp.StopAllAsync().ConfigureAwait(false);
        _apps = null;
        _appsSerial = null;
        _mutedSerial = null;
    }

    private void SetStatus(string status)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status != status;
            _status = status;
        }

        if (changed)
        {
            Log("status: " + status);
            Changed?.Invoke();
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best-effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_watch != null)
        {
            try
            {
                await _watch.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        await TeardownAsync().ConfigureAwait(false);
        _mirror.Dispose();
        await _deviceSession.DisposeAsync().ConfigureAwait(false);
        await _perApp.DisposeAsync().ConfigureAwait(false);
        await _bluetooth.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

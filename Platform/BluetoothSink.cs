using PhoneAudioConsole.Audio;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace PhoneAudioConsole.Platform;

public enum BluetoothSinkState
{
    Idle,
    Connecting,
    Ready,
    Flowing,
    Failed
}

/// <summary>
/// PC as a Bluetooth A2DP sink: pairs with the phone's audio and renders it to the default output
/// device. Models BlueAudioSink's proven sequence — create, arm (StartAsync), open, and on failure
/// reuse the connection object (only a cancelled open forces a rebuild). Whole-device only; A2DP is
/// a single mixed stream, so per-app volume is impossible here.
/// </summary>
internal sealed class BluetoothSink : IAsyncDisposable
{
    private const float FlowPeakThreshold = 0.01f;
    private static readonly TimeSpan ArmTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(4);

    private readonly Action<string> _log;
    private readonly object _gate = new();

    private AudioPlaybackConnection? _connection;
    private string? _armedId;
    private DateTime _retryAt = DateTime.MinValue;
    private bool _disposed;

    public BluetoothSinkState State { get; private set; } = BluetoothSinkState.Idle;
    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public string? Detail { get; private set; }

    public event Action? Changed;

    public BluetoothSink(Action<string>? log = null) => _log = log ?? (_ => { });

    public static async Task<IReadOnlyList<(string Id, string Name)>> ListDevicesAsync()
    {
        var list = new List<(string, string)>();
        try
        {
            DeviceInformationCollection found = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
            foreach (DeviceInformation info in found)
            {
                list.Add((info.Id, info.Name));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Enumeration can fail on machines without a usable Bluetooth stack.
        }

        return list;
    }

    /// <summary>Called each poll cycle while Bluetooth mode is selected.</summary>
    public async Task StepAsync(string? preferredId, CancellationToken token)
    {
        if (_disposed)
        {
            return;
        }

        if (State is BluetoothSinkState.Ready or BluetoothSinkState.Flowing)
        {
            UpdateFlow();
            return;
        }

        if (DateTime.UtcNow < _retryAt)
        {
            return;
        }

        var devices = await ListDevicesAsync();
        if (devices.Count == 0)
        {
            SetState(BluetoothSinkState.Idle, null, null, "未发现已配对的手机（请先在 Windows 蓝牙设置中配对）");
            return;
        }

        var target = devices.FirstOrDefault(d => string.Equals(d.Id, preferredId, StringComparison.OrdinalIgnoreCase));
        if (target.Id is null)
        {
            target = devices[0];
        }

        DeviceId = target.Id;
        DeviceName = target.Name;
        await ConnectAsync(target.Id, target.Name, token).ConfigureAwait(false);
    }

    private async Task ConnectAsync(string deviceId, string name, CancellationToken token)
    {
        SetState(BluetoothSinkState.Connecting, null, name, "正在连接…");

        AudioPlaybackConnection? connection = _connection;
        bool freshlyCreated = false;
        try
        {
            if (connection is null || _armedId != deviceId)
            {
                connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
                if (connection is null)
                {
                    Fail("该设备不支持蓝牙音频接收", name);
                    return;
                }

                _connection = connection;
                _armedId = deviceId;
                freshlyCreated = true;
            }

            if (freshlyCreated)
            {
                Task arm = connection.StartAsync().AsTask();
                if (await Task.WhenAny(arm, Task.Delay(ArmTimeout, token)).ConfigureAwait(false) != arm)
                {
                    await ReleaseAsync("arm timeout").ConfigureAwait(false);
                    Fail("蓝牙栈无响应，请重新开关电脑蓝牙", name);
                    return;
                }

                await arm.ConfigureAwait(false);
            }

            AudioPlaybackConnectionOpenResultStatus status;
            var open = connection.OpenAsync();
            Task<AudioPlaybackConnectionOpenResult> openTask = open.AsTask();
            if (await Task.WhenAny(openTask, Task.Delay(OpenTimeout, token)).ConfigureAwait(false) != openTask)
            {
                // A cancelled OpenAsync poisons the object; drop it so the next try rebuilds.
                try
                {
                    open.Cancel();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }

                await ReleaseAsync("open timeout").ConfigureAwait(false);
                Fail("连接超时（手机可能未响应）", name);
                return;
            }

            status = (await openTask.ConfigureAwait(false)).Status;
            if (status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                // Non-timeout failure: keep the object and retry later.
                Fail($"打开失败（{status}）", name);
                return;
            }

            await WaitForOpenedAsync(connection, token).ConfigureAwait(false);
            if (connection.State != AudioPlaybackConnectionState.Opened)
            {
                Fail("连接未能保持打开状态", name);
                return;
            }

            SetState(BluetoothSinkState.Ready, null, name, "已就绪，等待手机发送音频");
            _log($"Bluetooth ready: {name}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ReleaseAsync("exception").ConfigureAwait(false);
            Fail(ex.Message, name);
        }
    }

    private static async Task WaitForOpenedAsync(AudioPlaybackConnection connection, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (connection.State == AudioPlaybackConnectionState.Opened)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return;
            }

            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private void UpdateFlow()
    {
        bool flowing = AudioDeviceService.GetDefaultRenderPeak() > FlowPeakThreshold;
        var next = flowing ? BluetoothSinkState.Flowing : BluetoothSinkState.Ready;
        if (next != State)
        {
            SetState(next, null, DeviceName, flowing ? "音频传输中" : "已就绪，等待手机发送音频");
        }
    }

    private void Fail(string detail, string? name)
    {
        _retryAt = DateTime.UtcNow + RetryDelay;
        _log($"Bluetooth: {detail}");
        SetState(BluetoothSinkState.Failed, null, name, detail);
    }

    public async Task DisconnectAsync()
    {
        await ReleaseAsync("disconnect").ConfigureAwait(false);
        SetState(BluetoothSinkState.Idle, null, null, "未连接");
    }

    private async Task ReleaseAsync(string reason)
    {
        AudioPlaybackConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
            _armedId = null;
        }

        if (connection is null)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    connection.Dispose();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }
            }).ConfigureAwait(false);
            _log($"Bluetooth released ({reason})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log("Bluetooth release failed: " + ex.Message);
        }
    }

    private void SetState(BluetoothSinkState state, string? deviceId, string? deviceName, string? detail)
    {
        State = state;
        if (deviceId is not null)
        {
            DeviceId = deviceId;
        }

        if (deviceName is not null)
        {
            DeviceName = deviceName;
        }

        Detail = detail;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await ReleaseAsync("dispose").ConfigureAwait(false);
    }
}

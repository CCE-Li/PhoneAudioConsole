using System.Diagnostics;
using System.Net.Sockets;
using SonicRoute.AndroidAudio;

namespace PhoneAudioConsole.Audio;

/// <summary>Which slice of phone audio a transport captures.</summary>
public enum CaptureScope
{
    /// <summary>Whole output via REMOTE_SUBMIX; the phone's own playback goes silent.</summary>
    Device,

    /// <summary>One application's playback (Android 13+); only that app's local playback is muted.</summary>
    Application
}

/// <summary>
/// ADB audio transport. Pushes the patched scrcpy server and runs it via app_process with either
/// whole-device (<c>audio_source=output</c>) or per-application (<c>audio_source=playback</c> +
/// <c>audio_target_packages</c> + <c>audio_mix_mode=uid</c>) capture. Nothing is installed on the phone.
/// </summary>
public sealed class ScrcpyAudioTransport : IAudioTransport
{
    private const string ServerVersion = "3.3.4";
    private const int MinSdkDevice = 30;      // Android 11: output (REMOTE_SUBMIX) capture.
    private const int MinSdkApplication = 33; // Android 13: per-app playback capture.

    private readonly AdbClient _adb;
    private readonly CaptureScope _scope;
    private readonly string? _package;
    private readonly bool _keepPhoneAudio;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _logLifetime = new();
    private readonly string _scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");

    private string? _serial;
    private string? _remote;
    private int _port;
    private int _serverPid;
    private Process? _server;
    private TcpClient? _socket;
    private Task? _logs;
    private bool _disposed;

    public ScrcpyAudioTransport(AdbClient adb, CaptureScope scope, string? package = null, bool keepPhoneAudio = false, Action<string>? log = null)
    {
        if (scope == CaptureScope.Application)
        {
            if (string.IsNullOrEmpty(package))
            {
                throw new ArgumentException("Per-application capture requires a package name.", nameof(package));
            }

            AdbClient.ValidatePackage(package);
        }

        _adb = adb;
        _scope = scope;
        _package = package;
        _keepPhoneAudio = keepPhoneAudio;
        _log = log ?? (_ => { });
    }

    public async Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
    {
        _serial = device.Serial;
        await _adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        _adb.ValidateServer();

        int minSdk = _scope == CaptureScope.Application ? MinSdkApplication : MinSdkDevice;
        string sdkText = await _adb.RunAsync(token, "-s", _serial, "shell", "getprop", "ro.build.version.sdk").ConfigureAwait(false);
        if (!int.TryParse(sdkText.Trim(), out int sdk) || sdk < minSdk)
        {
            throw new NotSupportedException(_scope == CaptureScope.Application
                ? "按应用捕获需要 Android 13 或更高版本。"
                : "输出音频捕获需要 Android 11 或更高版本。");
        }

        if (!File.Exists(_adb.ServerFile))
        {
            throw new FileNotFoundException("缺少手机音频组件 (scrcpy-server)。", _adb.ServerFile);
        }

        _remote = "/data/local/tmp/phonaudio_" + _scid + ".jar";
        await _adb.RunAsync(token, "-s", _serial, "push", _adb.ServerFile, _remote).ConfigureAwait(false);

        string forward = await _adb.RunAsync(token, "-s", _serial, "forward", "tcp:0", "localabstract:scrcpy_" + _scid).ConfigureAwait(false);
        if (!int.TryParse(forward.Trim(), out _port) || _port is < 1 or > 65535)
        {
            throw new IOException("ADB 端口转发失败。");
        }

        _server = _adb.Start("-s", _serial, "shell", BuildCommand());
        _logs = Task.WhenAll(ReadLogAsync(_server.StandardOutput), ReadLogAsync(_server.StandardError));

        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            connect.Token.ThrowIfCancellationRequested();
            var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync("127.0.0.1", _port, connect.Token).ConfigureAwait(false);
                byte[] dummy = new byte[1];
                await socket.GetStream().ReadExactlyAsync(dummy, connect.Token).ConfigureAwait(false);
                if (dummy[0] != 0)
                {
                    throw new InvalidDataException("scrcpy 握手失败。");
                }

                _socket = socket;
                return socket.GetStream();
            }
            catch (Exception ex) when (ex is SocketException or EndOfStreamException)
            {
                socket.Dispose();
                if (_server.HasExited)
                {
                    throw new IOException("手机音频服务已退出；请检查调试授权、Android 版本与目标应用。");
                }

                await Task.Delay(100, connect.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private string BuildCommand()
    {
        string source = _scope == CaptureScope.Application
            ? "audio_source=playback audio_dup=false audio_target_packages=" + _package + " audio_mix_mode=uid"
            : "audio_source=output";
        return "echo SR_PID:$$; CLASSPATH=" + _remote + " exec app_process / com.genymobile.scrcpy.Server " + ServerVersion
            + $" scid={_scid} log_level=info video=false audio=true control=false audio_codec=opus {source} "
            + "tunnel_forward=true send_device_meta=false send_dummy_byte=true cleanup=false";
    }

    private async Task ReadLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_logLifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("SR_PID:", StringComparison.Ordinal) && int.TryParse(line[7..].Trim(), out int pid))
                {
                    Volatile.Write(ref _serverPid, pid);
                }
                else
                {
                    _log(line.Length > 2048 ? line[..2048] : line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _socket?.Dispose();
        _socket = null;

        if (_server != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _server.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                int pid = Volatile.Read(ref _serverPid);
                if (pid > 0 && _serial != null)
                {
                    try
                    {
                        string cmdline = await _adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "cat", $"/proc/{pid}/cmdline").ConfigureAwait(false);
                        if (cmdline.Contains("com.genymobile.scrcpy.Server", StringComparison.Ordinal)
                            && cmdline.Contains("scid=" + _scid, StringComparison.Ordinal))
                        {
                            await _adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "kill", "-TERM", pid.ToString()).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log("Owned server cleanup: " + ex.Message);
                    }
                }

                try
                {
                    if (!_server.HasExited)
                    {
                        _server.Kill(true);
                    }
                }
                catch (InvalidOperationException) { }
            }

            _logLifetime.Cancel();
            if (_logs != null)
            {
                try
                {
                    await _logs.ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { }
            }

            _server.Dispose();
            _server = null;
        }

        if (_serial != null)
        {
            if (_port != 0)
            {
                try
                {
                    await _adb.RunAsync(cleanup.Token, "-s", _serial, "forward", "--remove", "tcp:" + _port).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log("Forward cleanup: " + ex.Message);
                }
            }

            if (_remote != null)
            {
                try
                {
                    await _adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "rm", "-f", _remote).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log("Temporary server cleanup: " + ex.Message);
                }
            }
        }

        _logLifetime.Dispose();
    }
}

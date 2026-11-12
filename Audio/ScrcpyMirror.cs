using System.Diagnostics;
using System.IO;
using SonicRoute.AndroidAudio;

namespace PhoneAudioConsole.Audio;

/// <summary>
/// Launches the bundled scrcpy client to mirror and control the phone from the PC. With
/// <c>--turn-screen-off</c> the phone's own display is off while the PC window stays interactive,
/// i.e. you control the phone while its screen is dark.
/// </summary>
public sealed class ScrcpyMirror : IDisposable
{
    private readonly AdbClient _adb;
    private readonly Action<string>? _log;
    private Process? _process;
    private bool _disposed;

    public ScrcpyMirror(AdbClient adb, Action<string>? log = null)
    {
        _adb = adb;
        _log = log;
    }

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void Start(string serial, bool turnScreenOff = true, string bitRate = "16M")
    {
        if (IsRunning)
        {
            return;
        }

        _adb.PrepareRuntime("video");
        _adb.PrepareRuntime("control");

        string directory = Path.Combine(_adb.RuntimeDirectory, "VirtualDisplay");
        string exe = Path.Combine(directory, "scrcpy.exe");
        string server = Path.Combine(directory, "scrcpy-server-video");
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("缺少 scrcpy 组件。", exe);
        }

        if (!File.Exists(server))
        {
            throw new FileNotFoundException("缺少 scrcpy 服务端。", server);
        }

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        foreach (string argument in new[] { "-s", serial, "--no-audio", "--stay-awake", "-b", bitRate })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (turnScreenOff)
        {
            startInfo.ArgumentList.Add("--turn-screen-off");
        }

        startInfo.Environment["SCRCPY_SERVER_PATH"] = server;
        startInfo.Environment["ADB"] = _adb.Executable;

        _process = Process.Start(startInfo) ?? throw new IOException("无法启动 scrcpy。");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _log?.Invoke("scrcpy exited");
        try
        {
            // Slightly above normal so encoder/render hiccups from other apps don't cause stutter.
            _process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Priority is best-effort.
        }

        _log?.Invoke($"scrcpy started for {serial} (screen {(turnScreenOff ? "off" : "on")}, bitrate {bitRate})");
    }

    public void Stop()
    {
        Process? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        process.Dispose();
        _log?.Invoke("scrcpy stopped");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace PhoneAudioConsole.Platform;

/// <summary>
/// Windows mounts an Android phone as an MTP portable device on plug-in, and something (Explorer
/// folder window or an AutoPlay dialog) may appear. We cannot stop the mount, so after a recognized
/// phone arrives we scan top-level windows for a short while: matching File Explorer windows are
/// closed, and every newly seen window is logged so the real culprit can be identified.
/// </summary>
internal sealed class ExplorerWindowSuppressor
{
    private const int WM_CLOSE = 0x0010;
    private const string ExplorerClass = "CabinetWClass";
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly List<string> _hints = new();
    private DateTime _until = DateTime.MinValue;
    private bool _running;

    public ExplorerWindowSuppressor(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>Called when a phone arrives; watches for and closes matching windows briefly.</summary>
    public void Arm(IEnumerable<string> hints)
    {
        lock (_gate)
        {
            _hints.Clear();
            foreach (string hint in hints)
            {
                string trimmed = hint?.Trim() ?? string.Empty;
                if (trimmed.Length >= 3 && !_hints.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    _hints.Add(trimmed);
                }
            }

            if (_hints.Count == 0)
            {
                return;
            }

            _until = DateTime.UtcNow + Window;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        Log($"watch armed, hints=[{string.Join(", ", _hints)}]");
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                List<string> hints;
                lock (_gate)
                {
                    if (DateTime.UtcNow >= _until)
                    {
                        return;
                    }

                    hints = _hints.ToList();
                }

                Scan(hints);
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private void Scan(List<string> hints)
    {
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            if (GetText(hwnd, className: true) != ExplorerClass)
            {
                return true;
            }

            string title = GetText(hwnd, className: false);
            if (title.Length > 0 && hints.Any(h => title.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                Log($"closing Explorer window: {title}");
                PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);
    }

    private static string GetText(IntPtr hwnd, bool className)
    {
        var buffer = new StringBuilder(className ? 128 : 512);
        int length = className
            ? GetClassNameW(hwnd, buffer, buffer.Capacity)
            : GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return length == 0 ? string.Empty : buffer.ToString();
    }

    private void Log(string line)
    {
        lock (_gate)
        {
            _log(line);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

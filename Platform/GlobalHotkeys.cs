using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PhoneAudioConsole.Platform;

/// <summary>
/// Global hotkeys hosted on a message-only window. Defaults: Ctrl+Alt+M mutes all streams,
/// Ctrl+Alt+Up/Down adjusts every active stream's volume.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_UP = 0x26;
    private const uint VK_DOWN = 0x28;
    private const uint VK_M = 0x4D;

    private readonly HwndSource _source;
    private readonly List<(int Id, Action Handler)> _bindings = new();
    private readonly Action<string>? _log;
    private int _nextId = 0x4200;
    private bool _disposed;

    public GlobalHotkeys(Action<string>? log = null)
    {
        _log = log;
        var parameters = new HwndSourceParameters("PhoneAudioConsole.Hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        parameters.ParentWindow = new IntPtr(-3); // HWND_MESSAGE
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public void RegisterDefaults(PhoneAudioController controller)
    {
        Register(MOD_CONTROL | MOD_ALT, VK_M, controller.ToggleMuteAll, "Ctrl+Alt+M 全部静音");
        Register(MOD_CONTROL | MOD_ALT, VK_UP, () => controller.AdjustVolumeAll(5), "Ctrl+Alt+Up 音量+");
        Register(MOD_CONTROL | MOD_ALT, VK_DOWN, () => controller.AdjustVolumeAll(-5), "Ctrl+Alt+Down 音量-");
    }

    private void Register(uint modifiers, uint key, Action handler, string name)
    {
        int id = _nextId++;
        if (RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, key))
        {
            _bindings.Add((id, handler));
        }
        else
        {
            _log?.Invoke($"hotkey busy: {name}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            foreach (var (bindingId, handler) in _bindings)
            {
                if (bindingId == id)
                {
                    handler();
                    handled = true;
                    break;
                }
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (id, _) in _bindings)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _bindings.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

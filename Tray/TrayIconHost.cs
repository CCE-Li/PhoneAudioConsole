using System.Drawing;
using System.Windows;
using PhoneAudioConsole.Platform;
using PhoneAudioConsole.Settings;
using Forms = System.Windows.Forms;

namespace PhoneAudioConsole.Tray;

/// <summary>Owns the tray icon, its menu and the lazily-created settings window.</summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly PhoneAudioController _controller;
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon? _customIcon;
    private readonly Forms.ToolStripMenuItem _autoStartItem;
    private readonly Forms.ToolStripMenuItem _autoTakeoverItem;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _muteItem;
    private readonly Forms.ToolStripMenuItem _mirrorItem;
    private SettingsWindow? _window;

    public event Action? RequestExit;

    public TrayIconHost(AppSettings settings, PhoneAudioController controller)
    {
        _settings = settings;
        _controller = controller;

        _statusItem = new Forms.ToolStripMenuItem("等待手机接入") { Enabled = false };

        _autoTakeoverItem = new Forms.ToolStripMenuItem("插上 USB 自动接管", null, OnToggleAutoTakeover)
        {
            CheckOnClick = true,
            Checked = controller.AutoTakeover
        };

        _autoStartItem = new Forms.ToolStripMenuItem("开机自启", null, OnToggleAutoStart)
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled()
        };

        _muteItem = new Forms.ToolStripMenuItem("全部静音 (Ctrl+Alt+M)", null, (_, _) => _controller.ToggleMuteAll())
        {
            CheckOnClick = true,
            Checked = controller.MutedAll
        };

        _mirrorItem = new Forms.ToolStripMenuItem("息屏控制手机 (scrcpy)", null, (_, _) => _controller.ToggleMirror())
        {
            CheckOnClick = true,
            Checked = controller.IsMirroring
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("打开设置", null, (_, _) => ShowSettings()));
        menu.Items.Add(_muteItem);
        menu.Items.Add(_mirrorItem);
        menu.Items.Add(_autoTakeoverItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => RequestExit?.Invoke()));

        _customIcon = AppIcons.Load(Forms.SystemInformation.SmallIconSize);
        _icon = new Forms.NotifyIcon
        {
            Icon = _customIcon ?? SystemIcons.Application,
            Text = "手机音频控制台",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowSettings();

        _controller.Changed += OnControllerChanged;
        UpdateStatus();
    }

    private void OnControllerChanged() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateStatus);

    private void UpdateStatus()
    {
        string status = _controller.Status;
        _statusItem.Text = status;
        _icon.Text = status.Length > 60 ? status[..60] : status;
        _muteItem.Checked = _controller.MutedAll;
        _mirrorItem.Checked = _controller.IsMirroring;
        _mirrorItem.Enabled = _controller.IsMirroring || _controller.CanMirror;
    }

    private void OnToggleAutoTakeover(object? sender, EventArgs e)
    {
        _controller.AutoTakeover = _autoTakeoverItem.Checked;
        _settings.AutoTakeover = _autoTakeoverItem.Checked;
        _settings.Save();
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        AutoStart.SetEnabled(_autoStartItem.Checked);
        _settings.AutoStart = _autoStartItem.Checked;
        _settings.Save();
    }

    public void ShowSettings()
    {
        _window ??= new SettingsWindow(_settings, _controller);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void Dispose()
    {
        _controller.Changed -= OnControllerChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _customIcon?.Dispose();
        _window?.CloseForExit();
    }
}

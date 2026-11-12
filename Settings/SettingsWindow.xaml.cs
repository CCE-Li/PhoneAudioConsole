using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PhoneAudioConsole.Audio;
using PhoneAudioConsole.Platform;

namespace PhoneAudioConsole.Settings;

public partial class SettingsWindow : Window
{
    private sealed record OutputOption(string? Id, string Name);

    private sealed record BluetoothOption(string? Id, string Name);

    private readonly AppSettings _settings;
    private readonly PhoneAudioController _controller;
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _rows = new(StringComparer.Ordinal);
    private bool _allowClose;
    private bool _updating;

    public SettingsWindow(AppSettings settings, PhoneAudioController controller)
    {
        InitializeComponent();
        _settings = settings;
        _controller = controller;
        Icon = AppIcons.WindowIcon();

        _updating = true;
        ConnUsbRadio.IsChecked = controller.Connection == ConnectionMode.Usb;
        ConnWifiRadio.IsChecked = controller.Connection == ConnectionMode.Wifi;
        ConnBluetoothRadio.IsChecked = controller.Connection == ConnectionMode.Bluetooth;
        ScopeAppRadio.IsChecked = controller.Scope == CaptureScope.Application;
        ScopeDeviceRadio.IsChecked = controller.Scope == CaptureScope.Device;
        AutoTakeoverCheck.IsChecked = controller.AutoTakeover;
        MuteOnTakeoverCheck.IsChecked = controller.MuteOnTakeover;
        SuppressExplorerCheck.IsChecked = controller.SuppressExplorerWindow;
        DeviceVolumeSlider.Value = controller.DeviceVolume;
        DeviceVolumeText.Text = $"{controller.DeviceVolume}%";
        AutoStartCheck.IsChecked = AutoStart.IsEnabled();
        _updating = false;
        UpdateScopeAvailability();

        RefreshOutputDevices();
        Activated += (_, _) =>
        {
            RefreshOutputDevices();
            if (_controller.Connection == ConnectionMode.Bluetooth)
            {
                RefreshBluetoothDevices();
            }
        };

        if (controller.Connection == ConnectionMode.Bluetooth)
        {
            RefreshBluetoothDevices();
        }

        UpdateFromController();
        _controller.Changed += OnControllerChanged;
    }

    private void RefreshOutputDevices()
    {
        var options = new List<OutputOption> { new(null, "系统默认") };
        options.AddRange(AudioDeviceService.ListRenderDevices().Select(d => new OutputOption(d.Id, d.Name)));

        _updating = true;
        OutputDeviceCombo.ItemsSource = options;
        OutputDeviceCombo.DisplayMemberPath = nameof(OutputOption.Name);
        OutputDeviceCombo.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Id, _controller.OutputDeviceId, StringComparison.Ordinal)) ?? options[0];
        _updating = false;
    }

    private void OutputDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || OutputDeviceCombo.SelectedItem is not OutputOption option)
        {
            return;
        }

        _controller.OutputDeviceId = option.Id;
    }

    private async void RefreshBluetoothDevices()
    {
        try
        {
            var devices = await PhoneAudioController.ListBluetoothDevicesAsync();
            var options = new List<BluetoothOption> { new(null, "自动（第一个可用设备）") };
            options.AddRange(devices.Select(d => new BluetoothOption(d.Id, d.Name)));

            _updating = true;
            BluetoothDeviceCombo.ItemsSource = options;
            BluetoothDeviceCombo.DisplayMemberPath = nameof(BluetoothOption.Name);
            BluetoothDeviceCombo.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Id, _controller.BluetoothDeviceId, StringComparison.Ordinal)) ?? options[0];
            _updating = false;
        }
        catch (Exception ex)
        {
            _controller.LogMessage("Bluetooth list failed: " + ex.Message);
        }
    }

    private void BluetoothDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || BluetoothDeviceCombo.SelectedItem is not BluetoothOption option)
        {
            return;
        }

        _controller.BluetoothDeviceId = option.Id;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing merely hides the window; the process keeps running in the tray.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnControllerChanged() => Dispatcher.BeginInvoke(UpdateFromController);

    private void UpdateFromController()
    {
        StatusText.Text = _controller.Status;

        bool mirroring = _controller.IsMirroring;
        MirrorButton.Content = mirroring ? "关闭息屏控制（scrcpy）" : "开启息屏控制（scrcpy）";
        MirrorButton.IsEnabled = mirroring || _controller.CanMirror;
        MirrorKeepScreenCheck.IsChecked = _controller.MirrorKeepScreenOn;

        _updating = true;
        DeviceVolumeSlider.Value = _controller.DeviceVolume;
        DeviceVolumeText.Text = $"{_controller.DeviceVolume}%";
        _updating = false;

        RebuildAppList();
    }

    private void RebuildAppList()
    {
        var views = _controller.AppStreams;
        var packages = views.Select(v => v.Package).ToHashSet(StringComparer.Ordinal);

        foreach (string stale in _rows.Keys.Where(p => !packages.Contains(p)).ToList())
        {
            AppListPanel.Children.Remove(_rows[stale].Slider.Parent as UIElement ?? _rows[stale].Slider);
            _rows.Remove(stale);
        }

        AppListEmpty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var view in views)
        {
            if (!_rows.TryGetValue(view.Package, out var row))
            {
                row = CreateRow(view.Package);
                _rows[view.Package] = row;
                AppListPanel.Children.Add((UIElement)row.Slider.Parent);
            }

            _updating = true;
            row.Slider.Value = view.Volume;
            row.Value.Text = $"{view.Volume}%";
            _updating = false;
        }
    }

    private (Slider Slider, TextBlock Value) CreateRow(string package)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = _controller.AppStreams.FirstOrDefault(v => v.Package == package)?.Name ?? package,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(name, 0);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = package
        };
        slider.ValueChanged += AppVolume_Changed;
        Grid.SetColumn(slider, 1);

        var value = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 2);

        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return (slider, value);
    }

    private void AppVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || sender is not Slider { Tag: string package })
        {
            return;
        }

        int volume = (int)Math.Round(e.NewValue);
        if (_rows.TryGetValue(package, out var row))
        {
            row.Value.Text = $"{volume}%";
        }

        _controller.SetAppVolume(package, volume);
    }

    private void Connection_Click(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _controller.Connection = ConnBluetoothRadio.IsChecked == true
            ? ConnectionMode.Bluetooth
            : ConnWifiRadio.IsChecked == true ? ConnectionMode.Wifi : ConnectionMode.Usb;

        UpdateScopeAvailability();
        if (_controller.Connection == ConnectionMode.Bluetooth)
        {
            RefreshBluetoothDevices();
        }
    }

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _controller.Scope = ScopeDeviceRadio.IsChecked == true ? CaptureScope.Device : CaptureScope.Application;
    }

    private void Mirror_Click(object sender, RoutedEventArgs e) => _controller.ToggleMirror();

    private void MirrorKeepScreen_Click(object sender, RoutedEventArgs e) =>
        _controller.MirrorKeepScreenOn = MirrorKeepScreenCheck.IsChecked == true;

    private void UpdateScopeAvailability()
    {
        bool enabled = _controller.Connection != ConnectionMode.Bluetooth;
        ScopeAppRadio.IsEnabled = enabled;
        ScopeDeviceRadio.IsEnabled = enabled;
    }

    private void AutoTakeover_Click(object sender, RoutedEventArgs e)
    {
        _controller.AutoTakeover = AutoTakeoverCheck.IsChecked == true;
    }

    private void MuteOnTakeover_Click(object sender, RoutedEventArgs e)
    {
        _controller.MuteOnTakeover = MuteOnTakeoverCheck.IsChecked == true;
    }

    private void SuppressExplorer_Click(object sender, RoutedEventArgs e)
    {
        _controller.SuppressExplorerWindow = SuppressExplorerCheck.IsChecked == true;
    }

    private void DeviceVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        int volume = (int)Math.Round(e.NewValue);
        DeviceVolumeText.Text = $"{volume}%";
        _controller.DeviceVolume = volume;
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = AutoStartCheck.IsChecked == true;
        AutoStart.SetEnabled(enabled);
        _settings.AutoStart = enabled;
        _settings.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    public void CloseForExit()
    {
        _controller.Changed -= OnControllerChanged;
        _allowClose = true;
        Close();
    }
}

using System.Windows;
using PhoneAudioConsole.Platform;
using PhoneAudioConsole.Settings;
using PhoneAudioConsole.Tray;

namespace PhoneAudioConsole;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private TrayIconHost? _tray;
    private PhoneAudioController? _controller;
    private GlobalHotkeys? _hotkeys;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstance("PhoneAudioConsole.SingleInstance");
        if (!_instance.IsFirst)
        {
            // A previous instance already owns the tray; ask it to surface the settings window.
            _instance.SignalExisting();
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();

        _controller = new PhoneAudioController(_settings);
        _controller.Start();

        _tray = new TrayIconHost(_settings, _controller);
        _instance.ShowRequested += () => Dispatcher.Invoke(() => _tray?.ShowSettings());
        _tray.RequestExit += () => Dispatcher.Invoke(Shutdown);

        _hotkeys = new GlobalHotkeys();
        _hotkeys.RegisterDefaults(_controller);

        // No visible window at startup: the app is tray-only.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();

        try
        {
            // Bounded shutdown so a stuck ADB call cannot hang process exit.
            _controller?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Exit must not throw.
        }

        _instance?.Dispose();
        _settings.Save();
        base.OnExit(e);
    }
}

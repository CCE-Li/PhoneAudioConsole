using System.IO;
using System.Text.Json;

namespace PhoneAudioConsole.Platform;

/// <summary>Persisted user preferences, stored per-user under %LocalAppData%\PhoneAudioConsole.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool AutoStart { get; set; }

    public string? SelectedOutputDeviceId { get; set; }

    public bool CloseToTray { get; set; } = true;

    public bool AutoTakeover { get; set; } = true;

    public bool MuteOnTakeover { get; set; } = true;

    public int PhoneVolume { get; set; } = 100;

    public string CaptureMode { get; set; } = "app";

    /// <summary>"usb" | "wifi" | "bluetooth".</summary>
    public string ConnectionMode { get; set; } = "usb";

    /// <summary>"app" (per-app, falls back to whole-device below Android 13) | "device".</summary>
    public string CaptureScope { get; set; } = "app";

    public string? BluetoothDeviceId { get; set; }

    public bool WifiEnabled { get; set; }

    public bool SuppressExplorerWindow { get; set; } = true;

    /// <summary>When true, scrcpy mirrors with the phone screen left on (smoother than screen-off).</summary>
    public bool MirrorKeepScreenOn { get; set; }

    public Dictionary<string, int> PhoneAppVolumes { get; set; } = new();

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhoneAudioConsole");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                loaded.MigrateLegacy();
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings fall back to defaults rather than blocking startup.
        }

        return new AppSettings();
    }

    /// <summary>Maps the old single "CaptureMode" key onto the new connection + scope pair.</summary>
    private void MigrateLegacy()
    {
        if (string.IsNullOrEmpty(CaptureMode))
        {
            return;
        }

        if (ConnectionMode == "usb" && CaptureScope == "app")
        {
            switch (CaptureMode.ToLowerInvariant())
            {
                case "bluetooth":
                    ConnectionMode = "bluetooth";
                    break;
                case "device":
                    CaptureScope = "device";
                    break;
            }
        }

        CaptureMode = string.Empty;
        WifiEnabled = false;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings persistence is best-effort; never crash the tray process over it.
        }
    }
}

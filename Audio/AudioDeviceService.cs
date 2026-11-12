using NAudio.CoreAudioApi;

namespace PhoneAudioConsole.Audio;

/// <summary>Enumerates Windows render endpoints for the output-device picker.</summary>
public static class AudioDeviceService
{
    public static IReadOnlyList<(string Id, string Name)> ListRenderDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    list.Add((device.ID, device.FriendlyName));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // Enumeration fails if the audio service is unavailable; return whatever we collected.
        }

        return list;
    }

    /// <summary>Bluetooth audio renders to the default output, so its volume is that endpoint's.</summary>
    public static int GetDefaultRenderVolume()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }
        catch
        {
            return -1;
        }
    }

    public static void SetDefaultRenderVolume(int percent)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        }
        catch
        {
            // No default endpoint or no permission; ignore.
        }
    }

    /// <summary>Peak level of the default output, used to tell "linked" from "audio actually flowing".</summary>
    public static float GetDefaultRenderPeak()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioMeterInformation.MasterPeakValue;
        }
        catch
        {
            return 0f;
        }
    }
}

using System.Management;
using System.Text.RegularExpressions;

namespace PhoneAudioConsole.Platform;

/// <summary>
/// The Windows friendly name of a portable (MTP/WPD) device — e.g. "iQOO Neo3" — is what File
/// Explorer shows in its window title, and it differs from the adb model name. Resolve it by tying
/// the adb serial to the USB device (its DeviceID contains the serial), then finding the WPD device
/// with the same VID/PID.
/// </summary>
internal static class PortableDeviceName
{
    public static IReadOnlyList<string> Resolve(string serial)
    {
        var names = new List<string>();
        try
        {
            var entities = new List<(string Class, string Name, string Id)>();
            using (var searcher = new ManagementObjectSearcher("SELECT PNPClass, Name, DeviceID FROM Win32_PnPEntity"))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        if (item["DeviceID"] is string id && item["Name"] is string name
                            && id.Length > 0 && name.Length > 0)
                        {
                            entities.Add((item["PNPClass"] as string ?? string.Empty, name, id));
                        }
                    }
                }
            }

            Match vidPid = Regex.Match(
                entities.FirstOrDefault(e => e.Id.Contains(serial, StringComparison.OrdinalIgnoreCase)).Id ?? string.Empty,
                @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}",
                RegexOptions.IgnoreCase);

            if (vidPid.Success)
            {
                foreach (var entity in entities)
                {
                    if (entity.Class == "WPD" && entity.Id.Contains(vidPid.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(entity.Name);
                    }
                }
            }

            if (names.Count == 0)
            {
                names.AddRange(entities.Where(e => e.Class == "WPD").Select(e => e.Name));
            }
        }
        catch
        {
            // WMI may be unavailable; the caller keeps its other name hints.
        }

        return names;
    }
}

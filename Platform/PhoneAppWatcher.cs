using SonicRoute.AndroidAudio;

namespace PhoneAudioConsole.Platform;

/// <summary>
/// Reads which phone apps are currently playing audio, by parsing <c>dumpsys media_session</c>.
/// Sessions list a <c>package=</c> line followed by a <c>state=PlaybackState {state=N, ...}</c> line
/// where N == 3 means PLAYING. ROMs differ: AOSP prints <c>state=3(PLAYING)</c>, some (e.g. vivo)
/// print <c>state=3,</c> — both are handled by reading the integer after the state key.
/// </summary>
internal static class PhoneAppWatcher
{
    private const string StateKey = "state=PlaybackState {state=";

    public static async Task<IReadOnlyList<string>> GetPlayingPackagesAsync(AdbClient adb, AndroidDevice device, CancellationToken token)
    {
        string text = await adb.RunAsync(token, "-s", device.Serial, "shell", "dumpsys", "media_session").ConfigureAwait(false);

        var playing = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            string? package = ParsePackage(line);
            if (package != null)
            {
                current = package;
            }

            if (current != null && IsPlaying(line))
            {
                playing.Add(current);
            }
        }

        return playing.ToList();
    }

    private static string? ParsePackage(string line)
    {
        int index = line.IndexOf("package=", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        string rest = line[(index + "package=".Length)..];
        int end = rest.IndexOfAny([' ', '\t', ')', ',', '}', '/']);
        string package = (end < 0 ? rest : rest[..end]).Trim();
        return package.Length > 0 && package.Contains('.', StringComparison.Ordinal) ? package : null;
    }

    private static bool IsPlaying(string line)
    {
        int index = line.IndexOf(StateKey, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        int start = index + StateKey.Length;
        int end = start;
        while (end < line.Length && char.IsAsciiDigit(line[end]))
        {
            end++;
        }

        return end > start && int.TryParse(line.AsSpan(start, end - start), out int state) && state == 3;
    }
}

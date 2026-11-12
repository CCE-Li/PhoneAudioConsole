using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneAudioConsole.Platform;

/// <summary>Loads the embedded application icon (generated from the root icon.png by tools/make-icon.ps1).</summary>
internal static class AppIcons
{
    private const string Resource = "PhoneAudioConsole.Assets.app.ico";

    public static Icon? Load(System.Drawing.Size size)
    {
        try
        {
            using Stream? stream = typeof(AppIcons).Assembly.GetManifestResourceStream(Resource);
            return stream is null ? null : new Icon(stream, size);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>Window icon (ImageSource) for WPF windows, sized for the taskbar/alt-tab.</summary>
    public static ImageSource? WindowIcon()
    {
        using Icon? icon = Load(new System.Drawing.Size(32, 32));
        if (icon is null)
        {
            return null;
        }

        ImageSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}

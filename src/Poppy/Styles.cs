using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Poppy;

static class Styles
{
    static Style Get(string key) => (Style)Application.Current.Resources[key];

    public static Style Primary => Get("PrimaryButton");
    public static Style Ghost => Get("GhostButton");
    public static Style Toggle => Get("ToolToggle");
    public static Style Icon => Get("IconButton");
}

/// <summary>The Poppy flower, loaded from the embedded PNG.</summary>
static class Logo
{
    static ImageSource? small;

    public static ImageSource Small => small ??= Load();

    static ImageSource Load()
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri("pack://application:,,,/Assets/Poppy-256.png");
        img.DecodePixelWidth = 128;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }
}

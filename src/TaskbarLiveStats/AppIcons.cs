using System.Reflection;

namespace TaskbarLiveStats;

/// <summary>
/// Loads branded icons/images embedded from Assets/.
/// Kept alive for the process lifetime so NotifyIcon / forms are not left with disposed handles.
/// </summary>
internal static class AppIcons
{
    private static readonly Lazy<Icon> AppIconLazy = new(LoadAppIcon);
    private static readonly Lazy<Image> LogoLazy = new(() => LoadPng("TaskbarLiveStats.Assets.app.png"));
    private static readonly Lazy<Image> LogoOnDarkLazy = new(() => LoadPng("TaskbarLiveStats.Assets.app-on-dark.png"));

    public static Icon AppIcon => AppIconLazy.Value;
    public static Image Logo => LogoLazy.Value;
    public static Image LogoOnDark => LogoOnDarkLazy.Value;

    private static Icon LoadAppIcon()
    {
        using var stream = OpenRequired("TaskbarLiveStats.Assets.app.ico");
        // Clone so the stream can be closed; Icon(stream) keeps a reference otherwise.
        using var temp = new Icon(stream);
        return (Icon)temp.Clone();
    }

    private static Image LoadPng(string resourceName)
    {
        using var stream = OpenRequired(resourceName);
        using var temp = Image.FromStream(stream);
        return new Bitmap(temp);
    }

    private static Stream OpenRequired(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            var available = string.Join(", ", asm.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"Missing embedded resource '{resourceName}'. Available: {available}");
        }

        return stream;
    }
}

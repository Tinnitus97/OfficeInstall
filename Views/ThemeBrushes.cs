using Avalonia;
using Avalonia.Media;

namespace OfficeInstall.Views;

/// <summary>
/// Kleiner Helfer, um die Farben aus App.axaml auch in Code-behind-Dialogen
/// zu verwenden. Damit folgen die Dialoge dem Hell-/Dunkelmodus statt feste
/// Farbwerte zu benutzen.
/// </summary>
internal static class ThemeBrushes
{
    public static IBrush Get(string key, string fallbackHex)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    public static IBrush Base    => Get("BaseBrush", "#1E1E2E");
    public static IBrush Crust   => Get("CrustBrush", "#11111B");
    public static IBrush Surface0=> Get("Surface0Brush", "#313244");
    public static IBrush Surface1=> Get("Surface1Brush", "#45475A");
    public static IBrush Text    => Get("TextBrush", "#CDD6F4");
    public static IBrush Subtext => Get("SubtextBrush", "#A6ADC8");
    public static IBrush Blue    => Get("BlueBrush", "#89B4FA");
    public static IBrush Green   => Get("GreenBrush", "#A6E3A1");
}

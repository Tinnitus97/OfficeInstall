using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OfficeInstall.Services;

namespace OfficeInstall.Views;

/// <summary>
/// Kleiner Ersatz fuer System.Windows.MessageBox in der Optik der Anwendung.
/// Wird immer vom UI-Thread aus aufgerufen.
/// </summary>
public static class MessageBox
{
    /// <summary>Eine Schaltflaeche fuer <see cref="ShowChoice"/>.</summary>
    public sealed record Choice(string Label, bool Primary = false, bool Danger = false);

    public static async Task ShowInfo(Window owner, string title, string message)
        => await Show(owner, title, message, new[] { new Choice("OK", Primary: true) }, okStyle: true);

    public static async Task<bool> ShowYesNo(Window owner, string title, string message)
        => await Show(owner, title, message,
                      new[] { new Choice(Loc.Tr("Nein", "No")), new Choice(Loc.Tr("Ja", "Yes"), Primary: true) },
                      okStyle: false) == 1;

    /// <summary>
    /// Frage mit beliebig vielen Antworten, von links nach rechts in der
    /// angegebenen Reihenfolge. Gibt den Index der gewaehlten zurueck - oder
    /// -1, wenn das Fenster ueber das X geschlossen wurde.
    ///
    /// Etwas breiter als die uebrigen Dialoge: Solche Fragen tragen meist eine
    /// Aufzaehlung, deren Zeilen sonst mitten im Satz umbrechen.
    /// </summary>
    public static Task<int> ShowChoice(Window owner, string title, string message, params Choice[] choices)
        => Show(owner, title, message, choices, okStyle: false, width: 540);

    private static async Task<int> Show(Window owner, string title, string message,
                                        Choice[] choices, bool okStyle, double width = 460)
    {
        var tcs = new TaskCompletionSource<int>();

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.Text,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var dialog = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ThemeBrushes.Base
        };

        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var choice = choices[i];

            // Hinweis-Dialoge behalten ihr blaues OK; bei Fragen ist die
            // Hauptantwort gruen, eine zerstoererische rot.
            var (bg, fg) = choice switch
            {
                { Danger: true }               => (ThemeBrushes.Red, ThemeBrushes.Crust),
                { Primary: true } when okStyle => (ThemeBrushes.Blue, ThemeBrushes.Crust),
                { Primary: true }              => (ThemeBrushes.Green, ThemeBrushes.Crust),
                _                              => (ThemeBrushes.Surface0, ThemeBrushes.Text)
            };

            var button = MakeButton(choice.Label, bg, fg);
            button.Click += (_, _) => { tcs.TrySetResult(index); dialog.Close(); };
            buttons.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { text, buttons }
        };

        dialog.Closed += (_, _) => tcs.TrySetResult(-1);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    private static Button MakeButton(string content, IBrush bg, IBrush fg) => new()
    {
        Content = content,
        Width = 90,
        Height = 32,
        Background = bg,
        Foreground = fg,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(5)
    };
}

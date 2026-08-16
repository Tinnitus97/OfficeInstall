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
    public static async Task ShowInfo(Window owner, string title, string message)
        => await Show(owner, title, message, yesNo: false);

    public static async Task<bool> ShowYesNo(Window owner, string title, string message)
        => await Show(owner, title, message, yesNo: true);

    private static async Task<bool> Show(Window owner, string title, string message, bool yesNo)
    {
        var tcs = new TaskCompletionSource<bool>();

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
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ThemeBrushes.Base
        };

        if (yesNo)
        {
            var yes = MakeButton(Loc.Tr("Ja", "Yes"), ThemeBrushes.Green, ThemeBrushes.Crust);
            var no = MakeButton(Loc.Tr("Nein", "No"), ThemeBrushes.Surface0, ThemeBrushes.Text);
            yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
        }
        else
        {
            var ok = MakeButton("OK", ThemeBrushes.Blue, ThemeBrushes.Crust);
            ok.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            buttons.Children.Add(ok);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { text, buttons }
        };

        dialog.Closed += (_, _) => tcs.TrySetResult(false);

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

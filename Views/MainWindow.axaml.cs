using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace OfficeInstall.Views;

public partial class MainWindow : Window
{
    private readonly ScrollViewer? _logScroll;
    private readonly SelectableTextBlock? _logText;

    // true = Log folgt automatisch ans Ende. Wird beim manuellen Hochscrollen
    // ausgeschaltet und beim Zurueckscrollen ans Ende wieder eingeschaltet.
    private bool _autoScroll = true;
    private bool _programScroll;

    public MainWindow()
    {
        InitializeComponent();

        _logScroll = this.FindControl<ScrollViewer>("LogScroll");
        _logText = this.FindControl<SelectableTextBlock>("TBLog");

        if (_logScroll is not null)
        {
            // Nutzer-Scrollen erkennen: Offset-Aenderung, die NICHT von uns kommt.
            _logScroll.PropertyChanged += (_, ev) =>
            {
                if (ev.Property != ScrollViewer.OffsetProperty) return;
                if (_programScroll) return;

                var sv = _logScroll!;
                var distanceFromBottom = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height);
                _autoScroll = distanceFromBottom <= 12;
            };

            // Mausrad nach oben stoppt das automatische Nachspringen sofort.
            _logScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (e.Delta.Y > 0) _autoScroll = false;
            }, RoutingStrategies.Tunnel);
        }

        // Bei jeder neuen Log-Zeile ans Ende springen - nur wenn Auto-Scroll aktiv.
        if (_logText is not null)
        {
            _logText.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBlock.TextProperty) return;

                Dispatcher.UIThread.Post(() =>
                {
                    var sv = _logScroll;
                    if (sv is null || !_autoScroll) return;
                    _programScroll = true;
                    sv.ScrollToEnd();
                    _programScroll = false;
                }, DispatcherPriority.Background);
            };
        }
    }

    /// <summary>
    /// Beim Schliessen den Arbeitsordner unter %TEMP% leeren. Das gilt fuer
    /// jede Art zu beenden - Schaltflaeche, Kreuz in der Titelleiste oder
    /// Alt+F4.
    /// </summary>
    protected override void OnClosed(System.EventArgs e)
    {
        (DataContext as ViewModels.MainWindowViewModel)?.CleanTempOnExit();
        base.OnClosed(e);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

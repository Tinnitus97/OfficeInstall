using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;

namespace OfficeInstall;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Selbstaktualisierung: Diese Fassung wurde von der alten mit
        // "--austausch" gestartet, um sich an deren Stelle zu kopieren. Dann
        // gibt es kein Fenster - nur den Austausch und den Neustart.
        if (Services.SelfUpdate.TryRunSwap(args)) return;

        // Globale Absturz-Faenger: schreiben jede UNBEHANDELTE Exception in
        // eine Logdatei auf dem Desktop, statt die App kommentarlos zu beenden.
        // Abgefangene Fehler gehen dagegen ins Protokoll (ActivityLog).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Main", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Schreibt einen Absturzbericht nach
    /// %USERPROFILE%\Desktop\OfficeInstall_Absturz.log
    /// (mit Rueckfall ins TEMP-Verzeichnis). Nur fuer echte Abstuerze - die
    /// sollen auffallen. Waechst die Datei ueber 1 MB, beginnt sie neu.
    /// </summary>
    private static void WriteCrashLog(string source, Exception? ex)
    {
        if (ex is not null) Services.ActivityLog.Error(source, ex);

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine($"Zeit:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Quelle:  {source}");
            sb.AppendLine($"Typ:     {ex?.GetType().FullName ?? "(unbekannt)"}");
            sb.AppendLine($"Meldung: {ex?.Message}");
            sb.AppendLine("StackTrace:");
            sb.AppendLine(ex?.StackTrace ?? "(keiner)");
            if (ex?.InnerException is { } inner)
            {
                sb.AppendLine("--- InnerException ---");
                sb.AppendLine($"Typ:     {inner.GetType().FullName}");
                sb.AppendLine($"Meldung: {inner.Message}");
                sb.AppendLine(inner.StackTrace);
            }
            sb.AppendLine();

            string path;
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                path = Path.Combine(desktop, "OfficeInstall_Absturz.log");
            }
            catch
            {
                path = Path.Combine(Path.GetTempPath(), "OfficeInstall_Absturz.log");
            }
            if (File.Exists(path) && new FileInfo(path).Length > Services.ActivityLog.DefaultMaxBytes)
                File.Delete(path);
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Wenn selbst das Schreiben scheitert, gibt es nichts mehr zu tun.
        }
    }
}

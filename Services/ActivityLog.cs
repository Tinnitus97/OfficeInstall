using System;
using System.IO;
using System.Text;

namespace OfficeInstall.Services;

/// <summary>
/// Das Protokoll als Datei: %LOCALAPPDATA%\OfficeInstall\Protokoll.txt.
///
/// WARUM:
/// Das Protokoll im Fenster lebt nur im Speicher. Schliesst sich das Programm
/// nach der Installation von selbst (Voreinstellung), war damit auch jede
/// Spur weg - gerade dann, wenn hinterher jemand fragt, was passiert ist.
/// Jetzt wird jede Zeile zusaetzlich hier abgelegt, mit Uhrzeit.
///
/// Abgefangene Fehler landen ebenfalls hier (mit Aufrufstapel) - frueher
/// schrieb jeder davon in eine Datei auf dem Desktop, die nie kleiner wurde.
///
/// Die Datei waechst nicht unbegrenzt: Ab gut 1 MB wird sie zu
/// Protokoll.alt.txt, und es beginnt eine neue. Mehr als zwei Dateien gibt es
/// nie. Ein Schreibfehler wird verschluckt - das Protokoll darf nie selbst
/// zum Problem werden.
/// </summary>
public static class ActivityLog
{
    public const long DefaultMaxBytes = 1_000_000;

    private static readonly object Lock = new();

    /// <summary>Ordner der Protokolldateien.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfficeInstall");

    public static string FilePath => Path.Combine(Folder, "Protokoll.txt");

    public static string PreviousFilePath => Path.Combine(Folder, "Protokoll.alt.txt");

    public static long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>Haengt eine Zeile mit Zeitstempel an.</summary>
    public static void Append(string line)
        => Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");

    /// <summary>Haengt einen abgefangenen Fehler samt Aufrufstapel an.</summary>
    public static void Error(string source, Exception ex)
    {
        var text = new StringBuilder()
            .Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [FEHLER in {source}] {ex.GetType().FullName}: {ex.Message}")
            .AppendLine()
            .AppendLine(ex.StackTrace ?? "(kein Aufrufstapel)");
        if (ex.InnerException is { } inner)
            text.AppendLine($"  innen: {inner.GetType().FullName}: {inner.Message}");
        Write(text.ToString());
    }

    private static void Write(string text)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, PreviousFilePath, overwrite: true);

                File.AppendAllText(FilePath, text, Encoding.UTF8);
            }
        }
        catch { /* Protokoll ist Beiwerk */ }
    }
}

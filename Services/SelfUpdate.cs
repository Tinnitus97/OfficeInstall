using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace OfficeInstall.Services;

/// <summary>
/// Tauscht die laufende EXE gegen eine neu heruntergeladene aus.
///
/// Eine laufende EXE kann sich nicht selbst ueberschreiben. Deshalb macht es
/// die NEUE Fassung: Die alte startet sie mit "--austausch &lt;Ziel&gt; &lt;PID&gt;"
/// und beendet sich. Die neue wartet, bis die alte weg ist, kopiert sich ueber
/// sie und startet sie - danach laeuft am gewohnten Ort die neue Fassung.
///
/// WARUM NICHT MEHR PER BATCH-SKRIPT:
/// Das fruehere austausch.cmd war als UTF-8 mit BOM geschrieben, cmd.exe liest
/// Batchdateien aber in der OEM-Codepage (850 bei deutschem Windows). Aus
/// "C:\Users\Jürgen\..." wurde "C:\Users\J├╝rgen\..." - wer einen Umlaut im
/// Benutzernamen hatte, bekam nie ein Update. Und weil das Skript ohne Fenster
/// lief, hing es im Fehlerfall unsichtbar an "pause". Hier gibt es keine
/// Kodierung zu raten, und ein Fehlschlag wird beim naechsten Start gemeldet.
///
/// Die neue EXE erbt beim Start die Administratorrechte der alten - es
/// erscheint keine zweite UAC-Abfrage.
/// </summary>
public static class SelfUpdate
{
    public const string SwapArgument = "--austausch";

    /// <summary>Hier legt ein gescheiterter Austausch seinen Grund ab.</summary>
    public static string FailureFile => Path.Combine(UpdateService.WorkFolder, "austausch-fehler.txt");

    /// <summary>
    /// Startet die neue Fassung im Austauschmodus. Danach muss sich die
    /// laufende Fassung beenden - die neue wartet darauf.
    /// </summary>
    public static void StartSwap(string newExe, string currentExe, int processId)
    {
        var start = new ProcessStartInfo(newExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(newExe) ?? UpdateService.WorkFolder
        };
        start.ArgumentList.Add(SwapArgument);
        start.ArgumentList.Add(currentExe);
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));

        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// Fuehrt den Austausch aus, wenn das Programm mit "--austausch" gestartet
    /// wurde. Rueckgabe true heisst: Das war ein Austauschlauf, das Programm
    /// soll sich sofort beenden, ohne ein Fenster zu oeffnen.
    /// </summary>
    public static bool TryRunSwap(string[] args)
    {
        if (args.Length < 3 || !string.Equals(args[0], SwapArgument, StringComparison.Ordinal)) return false;

        var target = args[1];
        try
        {
            var source = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Der eigene Programmpfad ist unbekannt.");
            var pid = int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;

            var error = Swap(source, target, pid, TimeSpan.FromSeconds(30));
            if (error is not null) WriteFailure(error);
        }
        catch (Exception ex)
        {
            WriteFailure(ex.Message);
        }

        // In jedem Fall das Programm am gewohnten Ort starten: nach Erfolg die
        // neue Fassung, sonst die alte - die meldet dann den Grund.
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target) ?? ""
            })?.Dispose();
        }
        catch (Exception ex)
        {
            WriteFailure(ex.Message);
        }

        return true;
    }

    /// <summary>
    /// Wartet, bis der Vorgang <paramref name="waitForPid"/> beendet ist
    /// (hoechstens <paramref name="timeout"/>), und kopiert dann
    /// <paramref name="source"/> ueber <paramref name="target"/>. Mehrere
    /// Versuche, weil Windows die alte Datei nach dem Beenden noch einen
    /// Moment festhalten kann - oder ein Virenscanner sie gerade liest.
    ///
    /// Rueckgabe null bei Erfolg, sonst der Grund.
    /// </summary>
    public static string? Swap(string source, string target, int waitForPid, TimeSpan timeout,
                               int attempts = 20, TimeSpan? delay = null)
    {
        WaitForExit(waitForPid, timeout);

        if (!File.Exists(source)) return $"Die neue Fassung fehlt: {source}";

        var pause = delay ?? TimeSpan.FromMilliseconds(500);
        string? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex.Message;
                if (attempt < attempts) Thread.Sleep(pause);
            }
        }

        return $"Die Datei ließ sich nicht ersetzen ({target}): {lastError}";
    }

    private static void WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            // Laeuft schon nicht mehr.
        }
        catch (InvalidOperationException)
        {
            // Dito - beendet, waehrend wir nachsahen.
        }
    }

    private static void WriteFailure(string reason)
    {
        try
        {
            Directory.CreateDirectory(UpdateService.WorkFolder);
            File.WriteAllText(FailureFile, reason);
        }
        catch { /* Dann bleibt es beim alten Stand - mehr ist nicht zu tun. */ }
    }

    /// <summary>
    /// Liest und entfernt den Grund eines gescheiterten Austauschs, falls der
    /// letzte Versuch einen hinterlassen hat.
    /// </summary>
    public static string? TakeFailure()
    {
        try
        {
            if (!File.Exists(FailureFile)) return null;
            var text = File.ReadAllText(FailureFile).Trim();
            File.Delete(FailureFile);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }
}

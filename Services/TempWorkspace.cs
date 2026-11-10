using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace OfficeInstall.Services;

/// <summary>
/// Der Arbeitsordner unter %TEMP%\OfficeInstall\&lt;Prozessnummer&gt;.
///
/// Dort landen die Kopien der Konfigurationsdateien, bei der
/// Online-Installation zusaetzlich eine Kopie der setup.exe und beim
/// Aktualisieren des Deployment Tools dessen Selbstentpacker. Alles davon wird
/// nur waehrend eines Vorgangs gebraucht.
///
/// Frueher blieb der Ordner liegen - nach ein paar Durchlaeufen sammelte sich
/// dort sichtbar Unrat an. Deshalb wird hier zentral aufgeraeumt: nach jedem
/// abgeschlossenen Vorgang, beim Beenden und vorsorglich auch beim Start
/// (falls das Programm beim letzten Mal abgestuerzt ist).
///
/// JEDES FENSTER HAT SEINEN EIGENEN ORDNER:
/// Frueher teilten sich zwei gleichzeitig geoeffnete Fenster einen Ordner. Das
/// eine raeumte nach seinem Vorgang auf - und loeschte dem anderen die
/// Konfigurationsdatei unter dem laufenden Deployment Tool weg. Jetzt
/// arbeitet jedes Fenster in einem Unterordner mit seiner Prozessnummer und
/// raeumt nur diesen auf. Beim Start verschwinden zusaetzlich die Ordner
/// von Fenstern, die nicht mehr laufen (<see cref="ClearStale"/>).
/// </summary>
public static class TempWorkspace
{
    /// <summary>Gemeinsamer Oberordner aller Fenster.</summary>
    public static string BaseRoot => Path.Combine(Path.GetTempPath(), "OfficeInstall");

    /// <summary>Arbeitsordner dieses Fensters.</summary>
    public static string Root => Path.Combine(BaseRoot, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>So lange bleiben Ordner beendeter Fenster vorsorglich stehen.</summary>
    public static TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Unterordner fuer das Deployment Tool.</summary>
    public static string DeploymentFolder => Path.Combine(Root, "odt");

    public static bool Exists
    {
        get { try { return Directory.Exists(Root); } catch { return false; } }
    }

    /// <summary>Ergebnis eines Aufraeumversuchs.</summary>
    public readonly record struct Result(bool Removed, long FreedBytes, string? Error)
    {
        /// <summary>Kurzfassung fuer das Protokoll, z.B. "12,4 MB".</summary>
        public string FreedText => OfflineSource.FormatBytes(FreedBytes);
    }

    /// <summary>
    /// Entfernt den Arbeitsordner samt Inhalt.
    ///
    /// Geloescht wird ueber denselben Weg wie beim Offline-Bestand: Der
    /// Selbstentpacker des Deployment Tools legt Dateien teils
    /// schreibgeschuetzt ab, und ein Virenscanner haelt frisch geschriebene
    /// Dateien manchmal noch kurz offen. Deshalb Attribute zuruecksetzen und
    /// mehrfach nachfassen.
    ///
    /// Ein Fehlschlag ist kein Drama - beim naechsten Start wird es erneut
    /// versucht. Deshalb wird hier nie eine Ausnahme durchgereicht.
    /// </summary>
    public static Result Clear()
    {
        try
        {
            if (!Directory.Exists(Root)) return new Result(false, 0, null);

            var size = MeasureSize(Root);
            OfflineSource.ForceDeleteDirectory(Root);
            return new Result(true, size, null);
        }
        catch (Exception ex)
        {
            return new Result(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Entfernt, was andere, nicht mehr laufende Fenster hinterlassen haben:
    /// Ordner mit der Nummer eines beendeten Prozesses sowie alles, was noch
    /// aus der Zeit vor den getrennten Ordnern direkt im Oberordner liegt.
    /// Der eigene Ordner und die Ordner laufender Fenster bleiben.
    ///
    /// Ordner beendeter Fenster bleiben ausserdem <paramref name="minimumAge"/>
    /// lang stehen (Vorgabe <see cref="StaleAfter"/>): Wurde ein Fenster
    /// waehrend eines Vorgangs geschlossen, arbeitet das Deployment Tool im
    /// Hintergrund weiter - mit der Konfiguration aus genau diesem Ordner.
    /// </summary>
    public static Result ClearStale(TimeSpan? minimumAge = null)
    {
        var youngest = DateTime.UtcNow - (minimumAge ?? StaleAfter);

        long freed = 0;
        var removed = false;
        string? error = null;

        try
        {
            if (!Directory.Exists(BaseRoot)) return new Result(false, 0, null);

            foreach (var entry in Directory.EnumerateFileSystemEntries(BaseRoot))
            {
                var name = Path.GetFileName(entry);
                if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    && Directory.Exists(entry)
                    && (pid == Environment.ProcessId || IsOurProcessRunning(pid)))
                    continue;

                try
                {
                    if (Directory.GetLastWriteTimeUtc(entry) > youngest) continue;
                }
                catch { /* Alter unbekannt: dann wie bisher behandeln */ }

                try
                {
                    if (Directory.Exists(entry))
                    {
                        var size = MeasureSize(entry);
                        OfflineSource.ForceDeleteDirectory(entry);
                        freed += size;
                    }
                    else
                    {
                        var size = new FileInfo(entry).Length;
                        File.SetAttributes(entry, FileAttributes.Normal);
                        File.Delete(entry);
                        freed += size;
                    }
                    removed = true;
                }
                catch (Exception ex)
                {
                    error ??= $"{name}: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
        }

        return new Result(removed, freed, error);
    }

    /// <summary>
    /// Laeuft unter dieser Nummer noch ein Fenster dieses Programms? Der Name
    /// wird mitgeprueft, weil Windows Prozessnummern wiederverwendet.
    /// </summary>
    private static bool IsOurProcessRunning(int pid)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            using var other = Process.GetProcessById(pid);
            return !other.HasExited
                   && string.Equals(other.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // gibt es nicht mehr
        }
    }

    private static long MeasureSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in OfflineSource.EnumerateFilesNoLinks(folder))
            {
                try { total += new FileInfo(file).Length; } catch { /* einzelne Datei */ }
            }
        }
        catch { /* Ordner nicht vollstaendig lesbar */ }
        return total;
    }
}

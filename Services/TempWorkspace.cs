using System;
using System.IO;

namespace OfficeInstall.Services;

/// <summary>
/// Der Arbeitsordner unter %TEMP%\OfficeInstall.
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
/// </summary>
public static class TempWorkspace
{
    /// <summary>Vollstaendiger Pfad des Arbeitsordners.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "OfficeInstall");

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

    private static long MeasureSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* einzelne Datei */ }
            }
        }
        catch { /* Ordner nicht vollstaendig lesbar */ }
        return total;
    }
}

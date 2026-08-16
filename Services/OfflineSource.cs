using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OfficeInstall.Services;

/// <summary>
/// Auskunft darueber, ob zu einer Installationsbasis heruntergeladene
/// Office-Quelldateien vorliegen.
///
/// "setup.exe /download configuration.xml" legt die Dateien neben setup.exe in
/// einem Ordner "Office\Data" ab, darin je ein Unterordner pro Version
/// (z.B. 16.0.17928.20216) sowie v32.cab bzw. v64.cab. Genau danach wird hier
/// gesucht - es muss also niemand von Hand angeben, ob offline installiert
/// werden kann.
/// </summary>
public sealed class OfflineSource
{
    /// <summary>true, wenn brauchbare Quelldateien gefunden wurden.</summary>
    public bool Available { get; init; }

    /// <summary>Ordner, der an SourcePath uebergeben wird (enthaelt "Office").</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>Gefundene Versionsordner, absteigend sortiert.</summary>
    public IReadOnlyList<Version> Versions { get; init; } = Array.Empty<Version>();

    /// <summary>Neueste vor Ort vorliegende Version.</summary>
    public Version? LatestVersion => Versions.Count > 0 ? Versions[0] : null;

    /// <summary>true, wenn die zur Architektur passende Cab-Datei vorhanden ist.</summary>
    public bool HasMatchingCab { get; init; }

    /// <summary>Groesse der Quelldateien in Byte (0, wenn nicht ermittelbar).</summary>
    public long SizeInBytes { get; init; }

    public static OfflineSource NotAvailable(string baseFolder) => new() { SourcePath = baseFolder };

    /// <summary>
    /// Prueft den Ordner einer Installationsbasis auf Offline-Quelldateien.
    /// </summary>
    /// <param name="baseFolder">z.B. ...\x64\PerpetualVL2021</param>
    /// <param name="clientEdition">32 oder 64 - bestimmt die erwartete Cab-Datei.</param>
    public static OfflineSource Detect(string baseFolder, int clientEdition)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseFolder) || !Directory.Exists(baseFolder))
                return NotAvailable(baseFolder);

            var dataFolder = Path.Combine(baseFolder, "Office", "Data");
            if (!Directory.Exists(dataFolder)) return NotAvailable(baseFolder);

            var versions = new List<Version>();
            foreach (var directory in Directory.GetDirectories(dataFolder))
            {
                if (Version.TryParse(Path.GetFileName(directory), out var version)) versions.Add(version);
            }
            versions.Sort();
            versions.Reverse();

            if (versions.Count == 0) return NotAvailable(baseFolder);

            var expectedCab = clientEdition == 32 ? "v32.cab" : "v64.cab";

            return new OfflineSource
            {
                Available = true,
                SourcePath = Path.GetFullPath(baseFolder),
                Versions = versions,
                HasMatchingCab = File.Exists(Path.Combine(dataFolder, expectedCab)),
                SizeInBytes = MeasureSize(dataFolder)
            };
        }
        catch
        {
            // Kein Zugriff, kaputtes Verzeichnis: dann eben keine Offline-Quelle.
            return NotAvailable(baseFolder);
        }
    }

    private static long MeasureSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* einzelne Datei nicht lesbar */ }
            }
        }
        catch { /* Ordner nicht vollstaendig lesbar */ }
        return total;
    }

    /// <summary>Was beim Aufraeumen entfernt wurde.</summary>
    public sealed class CleanupResult
    {
        public List<string> RemovedVersions { get; } = new();
        public List<string> RemovedFiles { get; } = new();
        public List<string> Problems { get; } = new();
        public long FreedBytes { get; private set; }

        public bool AnythingRemoved => RemovedVersions.Count > 0 || RemovedFiles.Count > 0;

        /// <summary>
        /// Kurzbeschreibung fuer das Protokoll. Frueher stand dort bei reinen
        /// Dateiloeschungen nur ein leerer Doppelpunkt.
        /// </summary>
        public string Describe()
        {
            var teile = new List<string>();
            if (RemovedVersions.Count > 0) teile.Add(string.Join(", ", RemovedVersions));
            if (RemovedFiles.Count > 0) teile.Add($"{RemovedFiles.Count} Begleitdatei(en)");
            return teile.Count > 0 ? string.Join(" + ", teile) : "nichts";
        }

        internal void AddVersion(string name, long bytes)
        {
            RemovedVersions.Add(name);
            FreedBytes += bytes;
        }

        internal void AddFile(string name, long bytes)
        {
            RemovedFiles.Add(name);
            FreedBytes += bytes;
        }
    }

    /// <summary>
    /// Entfernt aeltere Staende aus Office\Data und behaelt nur die angegebene
    /// Version. Das Deployment Tool laesst den alten Stand beim Herunterladen
    /// naemlich stehen - nach ein paar Aktualisierungen liegen sonst mehrere
    /// Gigabyte Altlast im Ordner.
    ///
    /// Geloescht werden ausschliesslich Ordner, deren Name eine Versionsnummer
    /// KLEINER als die zu behaltende ist, sowie die zugehoerigen
    /// Beschreibungsdateien v32_&lt;Version&gt;.cab / v64_&lt;Version&gt;.cab.
    /// Alles andere - insbesondere v32.cab und v64.cab - bleibt unangetastet.
    /// </summary>
    public static CleanupResult CleanupOldVersions(string baseFolder, Version keepVersion)
    {
        var result = new CleanupResult();

        try
        {
            var dataFolder = Path.Combine(baseFolder, "Office", "Data");
            if (!Directory.Exists(dataFolder)) return result;

            // --- Versionsordner ---
            foreach (var directory in Directory.GetDirectories(dataFolder))
            {
                var name = Path.GetFileName(directory);
                if (!Version.TryParse(name, out var version)) continue;
                if (version >= keepVersion) continue;

                var size = MeasureSize(directory);
                try
                {
                    ForceDeleteDirectory(directory);
                    result.AddVersion(name, size);
                }
                catch (Exception ex)
                {
                    result.Problems.Add($"{name}: {ex.Message}");
                }
            }

            // --- zugehoerige Beschreibungsdateien ---
            foreach (var file in Directory.GetFiles(dataFolder, "v*_*.cab"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(file), @"^v(32|64)_(.+)\.cab$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success) continue;
                if (!Version.TryParse(match.Groups[2].Value, out var version)) continue;
                if (version >= keepVersion) continue;

                try
                {
                    var size = new FileInfo(file).Length;
                    ForceDeleteFile(file);
                    result.AddFile(Path.GetFileName(file), size);
                }
                catch (Exception ex)
                {
                    result.Problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            result.Problems.Add(ex.Message);
        }

        return result;
    }

    /// <summary>Wie oft ein Loeschversuch wiederholt wird.</summary>
    private const int DeleteAttempts = 3;

    /// <summary>
    /// Loescht einen Ordner samt Inhalt - auch dann, wenn Dateien darin
    /// schreibgeschuetzt sind.
    ///
    /// Das Deployment Tool legt die Quelldateien mit gesetztem Schreibschutz
    /// (teils auch versteckt/System) ab. Directory.Delete raeumt solche Dateien
    /// NICHT weg, sondern wirft "Access to the path ... is denied" - genau daran
    /// ist das Aufraeumen frueher gescheitert. Deshalb werden die Attribute
    /// vorher zurueckgesetzt.
    ///
    /// Zusaetzlich wird mehrfach nachgefasst: Virenscanner und der
    /// Explorer-Vorschau halten frisch geschriebene Dateien manchmal noch kurz
    /// offen.
    /// </summary>
    public static void ForceDeleteDirectory(string directory)
    {
        ClearAttributes(directory);

        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception) when (attempt < DeleteAttempts)
            {
                System.Threading.Thread.Sleep(300);
                ClearAttributes(directory);
            }
        }

        // Letzter Versuch - eine Ausnahme wird jetzt bewusst durchgereicht,
        // damit der Grund im Protokoll landet.
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>Loescht eine Datei, auch wenn sie schreibgeschuetzt ist.</summary>
    public static void ForceDeleteFile(string file)
    {
        ClearFileAttributes(file);

        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                File.Delete(file);
                return;
            }
            catch (Exception) when (attempt < DeleteAttempts)
            {
                System.Threading.Thread.Sleep(300);
                ClearFileAttributes(file);
            }
        }

        File.Delete(file);
    }

    /// <summary>
    /// Nimmt Schreibschutz, Versteckt- und System-Kennzeichen vom Ordner selbst
    /// und von allem, was darin liegt.
    /// </summary>
    private static void ClearAttributes(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                ClearFileAttributes(file);

            foreach (var sub in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                ClearDirectoryAttributes(sub);

            ClearDirectoryAttributes(directory);
        }
        catch
        {
            // Auch wenn das Zuruecksetzen teilweise scheitert, wird der
            // eigentliche Loeschversuch trotzdem unternommen.
        }
    }

    private static void ClearFileAttributes(string file)
    {
        try
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(file, FileAttributes.Normal);
        }
        catch { /* Datei bereits weg oder nicht zugaenglich */ }
    }

    private static void ClearDirectoryAttributes(string directory)
    {
        try
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(directory, FileAttributes.Directory);
        }
        catch { /* Ordner bereits weg oder nicht zugaenglich */ }
    }

    /// <summary>Formatiert eine Byte-Zahl menschenlesbar (z.B. "3,4 GB").</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return index == 0
            ? $"{value:0} {units[index]}"
            : $"{value:0.0} {units[index]}";
    }
}

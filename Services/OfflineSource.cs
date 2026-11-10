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

    /// <summary>Vollstaendige Versionsordner, absteigend sortiert.</summary>
    public IReadOnlyList<Version> Versions { get; init; } = Array.Empty<Version>();

    /// <summary>
    /// Versionsordner, die ein noch laufender oder gescheiterter Download
    /// hinterlassen hat - sie werden nicht zum Installieren benutzt.
    /// Siehe <see cref="BeginDownload"/>.
    /// </summary>
    public IReadOnlyList<Version> IncompleteVersions { get; init; } = Array.Empty<Version>();

    /// <summary>true, wenn ein Download begonnen und nicht bestaetigt abgeschlossen wurde.</summary>
    public bool DownloadPending { get; init; }

    /// <summary>Neueste vor Ort vorliegende Version.</summary>
    public Version? LatestVersion => Versions.Count > 0 ? Versions[0] : null;

    /// <summary>true, wenn die zur Architektur passende Cab-Datei vorhanden ist.</summary>
    public bool HasMatchingCab { get; init; }

    /// <summary>
    /// Groesse des neuesten vollstaendigen Stands in Byte (0, wenn nicht
    /// ermittelbar). Frueher die Groesse des ganzen Ordners Office\Data - lag
    /// dort noch ein alter Stand, stand neben der Versionsnummer das Doppelte.
    /// </summary>
    public long SizeInBytes { get; init; }

    /// <summary>
    /// Merkzettel in Office\Data, solange ein Download nicht bestaetigt fertig
    /// ist. Darin stehen die Versionen, die VOR dem Download vollstaendig da
    /// waren - nur die gelten bis dahin als brauchbar.
    /// </summary>
    public const string DownloadMarkerName = "download-unvollstaendig.txt";

    private static string DataFolderOf(string baseFolder) => Path.Combine(baseFolder, "Office", "Data");

    private static string CabName(int clientEdition) => clientEdition == 32 ? "v32.cab" : "v64.cab";

    /// <summary>Alle Versionsordner unter Office\Data, neueste zuerst.</summary>
    private static List<Version> ReadVersionFolders(string dataFolder)
    {
        var versions = new List<Version>();
        foreach (var directory in Directory.GetDirectories(dataFolder))
        {
            if (Version.TryParse(Path.GetFileName(directory), out var version)) versions.Add(version);
        }
        versions.Sort();
        versions.Reverse();
        return versions;
    }

    /// <summary>Die im Merkzettel als vollstaendig genannten Versionen, oder null ohne Merkzettel.</summary>
    private static HashSet<Version>? ReadMarker(string dataFolder)
    {
        var marker = Path.Combine(dataFolder, DownloadMarkerName);
        if (!File.Exists(marker)) return null;

        var complete = new HashSet<Version>();
        try
        {
            foreach (var line in File.ReadAllLines(marker))
            {
                if (Version.TryParse(line.Trim(), out var version)) complete.Add(version);
            }
        }
        catch { /* unlesbar: dann gilt bis zum naechsten Download nichts als vollstaendig */ }
        return complete;
    }

    /// <summary>
    /// Vor dem Start eines Downloads aufrufen.
    ///
    /// WARUM: Das Deployment Tool legt den Ordner der neuen Version gleich zu
    /// Beginn an. Bricht der Download ab (Netz weg, Fehler, Fenster
    /// geschlossen), bleibt ein halb gefuellter Ordner mit der hoechsten
    /// Versionsnummer liegen - und galt bisher als "aktueller Stand": gruen
    /// in der Liste, und die Offline-Installation nahm genau ihn.
    ///
    /// Der Merkzettel haelt fest, was vorher vollstaendig war. Alles, was
    /// danach hinzukommt, zaehlt erst, wenn <see cref="CompleteDownload"/> den
    /// Zettel nach bestandener Nachpruefung entfernt.
    ///
    /// Liegt schon ein Zettel da (frueherer Abbruch), bleibt seine Liste
    /// gueltig - die halben Ordner von damals duerfen jetzt nicht als
    /// vollstaendig eingetragen werden.
    ///
    /// Rueckgabe true, wenn der Zettel jetzt neu angelegt wurde. Startet der
    /// Download danach gar nicht erst, nimmt der Aufrufer ihn damit wieder weg
    /// (<see cref="CompleteDownload"/>) - einen aelteren Zettel aber nicht.
    /// </summary>
    public static bool BeginDownload(string baseFolder)
    {
        var dataFolder = DataFolderOf(baseFolder);
        Directory.CreateDirectory(dataFolder);

        var existing = ReadVersionFolders(dataFolder);
        var known = ReadMarker(dataFolder);
        var complete = known is null ? existing : existing.Where(known.Contains).ToList();

        File.WriteAllLines(Path.Combine(dataFolder, DownloadMarkerName),
                           complete.Select(v => v.ToString()));
        return known is null;
    }

    /// <summary>Nach bestandener Nachpruefung aufrufen: Der neue Stand gilt jetzt.</summary>
    public static void CompleteDownload(string baseFolder)
    {
        try
        {
            var marker = Path.Combine(DataFolderOf(baseFolder), DownloadMarkerName);
            if (File.Exists(marker)) ForceDeleteFile(marker);
        }
        catch { /* bleibt er liegen, gilt der neue Stand eben erst nach dem naechsten Download */ }
    }

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

            var dataFolder = DataFolderOf(baseFolder);
            if (!Directory.Exists(dataFolder)) return NotAvailable(baseFolder);

            var all = ReadVersionFolders(dataFolder);
            var marker = ReadMarker(dataFolder);
            if (all.Count == 0 && marker is null) return NotAvailable(baseFolder);

            // Mit Merkzettel zaehlt nur, was vor dem Download schon da war.
            var complete = marker is null ? all : all.Where(marker.Contains).ToList();
            var incomplete = marker is null ? new List<Version>() : all.Where(v => !marker.Contains(v)).ToList();

            var hasCab = File.Exists(Path.Combine(dataFolder, CabName(clientEdition)));

            return new OfflineSource
            {
                // Ohne die Beschreibungsdatei kann das Deployment Tool aus dem
                // Ordner nicht installieren - dann lieber online als ein
                // Fehlschlag mitten in der Installation.
                Available = complete.Count > 0 && hasCab,
                SourcePath = Path.GetFullPath(baseFolder),
                Versions = complete,
                IncompleteVersions = incomplete,
                DownloadPending = marker is not null,
                HasMatchingCab = hasCab,
                SizeInBytes = complete.Count > 0
                    ? MeasureSize(Path.Combine(dataFolder, complete[0].ToString()))
                    : 0
            };
        }
        catch
        {
            // Kein Zugriff, kaputtes Verzeichnis: dann eben keine Offline-Quelle.
            return NotAvailable(baseFolder);
        }
    }

    /// <summary>
    /// Prueft nach einem Download, ob der Bestand wirklich brauchbar ist.
    /// Rueckgabe null, wenn alles da ist, sonst der Grund.
    ///
    /// Der Rueckgabewert 0 des Deployment Tools allein genuegt nicht: Bei einem
    /// abgebrochenen Netz, einem Proxy, der leere Antworten liefert, oder einer
    /// falschen Konfiguration endet es mitunter "erfolgreich", ohne etwas
    /// geladen zu haben. Wuerde danach aufgeraeumt, gingen sogar die alten,
    /// noch brauchbaren Staende verloren.
    /// </summary>
    /// <param name="expected">Online gemeldete Version, falls bekannt.</param>
    public static string? VerifyDownload(string baseFolder, int clientEdition, Version? expected)
    {
        // Bewusst am Merkzettel vorbei: Geprueft wird, was jetzt im Ordner
        // liegt - der Zettel wird ja erst nach dieser Pruefung entfernt.
        var dataFolder = DataFolderOf(baseFolder);
        List<Version> all;
        try
        {
            all = Directory.Exists(dataFolder) ? ReadVersionFolders(dataFolder) : new List<Version>();
        }
        catch (Exception ex)
        {
            return $"Der Ordner Office\\Data ließ sich nicht lesen: {ex.Message}";
        }

        if (all.Count == 0)
            return "Im Ordner Office\\Data liegt danach kein Versionsordner.";

        if (!File.Exists(Path.Combine(dataFolder, CabName(clientEdition))))
            return $"Die Beschreibungsdatei {CabName(clientEdition)} fehlt.";

        var latest = all[0];
        if (expected is not null && latest < expected)
            return $"Vorhanden ist Version {latest}, erwartet war {expected}.";

        if (MeasureSize(Path.Combine(dataFolder, latest.ToString())) == 0)
            return $"Der Versionsordner {latest} ist leer.";

        return null;
    }

    /// <summary>
    /// Alle Dateien unterhalb eines Ordners - ohne Verknuepfungen auf andere
    /// Ordner zu folgen (Junctions, symbolische Links).
    ///
    /// Das Programm laeuft mit Administratorrechten und raeumt in Ordnern auf,
    /// die auch ohne diese Rechte beschreibbar sind (%TEMP%). Wer dort eine
    /// Verknuepfung nach C:\Windows ablegt, soll damit weder das Messen noch
    /// das Zuruecksetzen von Dateiattributen dorthin umlenken koennen.
    /// </summary>
    internal static IEnumerable<string> EnumerateFilesNoLinks(string folder)
    {
        foreach (var directory in EnumerateDirectoriesNoLinks(folder, includeSelf: true))
        {
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch { continue; }

            foreach (var file in files) yield return file;
        }
    }

    /// <summary>Alle Unterordner, ohne in Verknuepfungen hinabzusteigen (die Verknuepfung selbst wird genannt).</summary>
    internal static IEnumerable<string> EnumerateDirectoriesNoLinks(string folder, bool includeSelf = false)
    {
        if (includeSelf) yield return folder;

        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            string[] children;
            try { children = Directory.GetDirectories(pending.Pop()); }
            catch { continue; }

            foreach (var child in children)
            {
                bool isLink;
                try { isLink = (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0; }
                catch { continue; }

                if (isLink)
                {
                    // Die Verknuepfung selbst gehoert zum Ordner (und wird mit
                    // ihm geloescht), ihr Ziel nicht.
                    if (!includeSelf) yield return child;
                    continue;
                }

                yield return child;
                pending.Push(child);
            }
        }
    }

    private static long MeasureSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in EnumerateFilesNoLinks(folder))
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

            // Ohne Verknuepfungen zu folgen - siehe EnumerateFilesNoLinks.
            foreach (var file in EnumerateFilesNoLinks(directory))
                ClearFileAttributes(file);

            foreach (var sub in EnumerateDirectoriesNoLinks(directory))
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

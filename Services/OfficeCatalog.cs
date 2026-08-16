using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OfficeInstall.Services;

/// <summary>Alles, was zu einer installierbaren Variante gehoert.</summary>
public sealed class CatalogEntry
{
    public required OfficeConfiguration Configuration { get; init; }
    public required OfficeChannel? Channel { get; init; }
    public required OfflineSource Offline { get; init; }

    /// <summary>Pfad zur setup.exe, oder null wenn keine gefunden wurde.</summary>
    public required string? SetupExecutable { get; init; }

    /// <summary>32 oder 64 - aus der XML, ersatzweise aus dem Ordnernamen.</summary>
    public required int ClientEdition { get; init; }

    public bool CanInstall => SetupExecutable is not null && !Configuration.IsBroken;

    public string DisplayName => Configuration.DisplayName;
    public string ChannelFolderName => Configuration.ChannelFolderName;
}

/// <summary>Ergebnis eines kompletten Durchlaufs durch den Paketordner.</summary>
public sealed class OfficeCatalogResult
{
    public List<CatalogEntry> Entries { get; } = new();

    /// <summary>Gefundene Architektur-Ordner (x32, x64).</summary>
    public List<string> ArchitectureFolders { get; } = new();

    /// <summary>Basis-Ordner, die vorhanden sind, aber keine XML enthalten.</summary>
    public List<string> EmptyBases { get; } = new();

    public IEnumerable<CatalogEntry> ForArchitecture(int clientEdition)
        => Entries.Where(e => e.ClientEdition == clientEdition);
}

/// <summary>
/// Durchsucht den Paketordner nach Konfigurationsdateien.
///
/// Aufbau des Pakets (wie in der alten start.bat vorausgesetzt):
///     &lt;Wurzel&gt;\x32\Current\*.xml
///     &lt;Wurzel&gt;\x32\PerpetualVL2019\*.xml
///     &lt;Wurzel&gt;\x64\...
///
/// Angeboten wird ausschliesslich, wofuer auch tatsaechlich eine XML-Datei
/// vorliegt. Die Basen bleiben dabei strikt getrennt: Ein Produkt aus
/// PerpetualVL2021 wird nie mit den Quelldateien einer anderen Basis
/// installiert.
/// </summary>
public static class OfficeCatalog
{
    public static readonly string[] ArchitectureFolders = { "x64", "x32" };

    /// <summary>Ordnername -> Architektur in Bit.</summary>
    public static int EditionFromFolderName(string folderName)
        => string.Equals(folderName, "x32", StringComparison.OrdinalIgnoreCase) ? 32 : 64;

    public static OfficeCatalogResult Scan(string rootFolder)
    {
        var result = new OfficeCatalogResult();
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder)) return result;

        foreach (var architectureFolderName in ArchitectureFolders)
        {
            var architectureFolder = Path.Combine(rootFolder, architectureFolderName);
            if (!Directory.Exists(architectureFolder)) continue;

            result.ArchitectureFolders.Add(architectureFolderName);
            var folderEdition = EditionFromFolderName(architectureFolderName);

            foreach (var channel in OfficeChannels.All.OrderBy(c => c.SortOrder))
            {
                var baseFolder = Path.Combine(architectureFolder, channel.FolderName);
                if (!Directory.Exists(baseFolder)) continue;

                string[] files;
                try { files = Directory.GetFiles(baseFolder, "*.xml"); }
                catch { continue; }

                if (files.Length == 0)
                {
                    result.EmptyBases.Add(Path.Combine(architectureFolderName, channel.FolderName));
                    continue;
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    var configuration = OfficeConfiguration.Load(file);

                    // Die Angabe in der XML hat Vorrang; fehlt sie, gilt der Ordner.
                    var edition = configuration.ClientEdition ?? folderEdition;

                    result.Entries.Add(new CatalogEntry
                    {
                        Configuration = configuration,
                        Channel = channel,
                        ClientEdition = edition,
                        Offline = OfflineSource.Detect(baseFolder, edition),
                        SetupExecutable = FindSetupExecutable(baseFolder, architectureFolder, rootFolder)
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Sucht die setup.exe des Deployment Tools - zuerst im Ordner der Basis
    /// (dort erwartet sie die alte start.bat), dann eine Ebene hoeher und
    /// zuletzt im Wurzelordner.
    /// </summary>
    public static string? FindSetupExecutable(string baseFolder, string architectureFolder, string rootFolder)
    {
        foreach (var folder in new[] { baseFolder, architectureFolder, rootFolder })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var candidate = Path.Combine(folder, "setup.exe");
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch { /* naechster Ordner */ }
        }
        return null;
    }

    /// <summary>
    /// Prueft, ob ein Eintrag zum Suchbegriff passt. Durchsucht werden der
    /// Klartextname, die Produktkennung, der Dateiname und die Basis.
    /// Mehrere durch Leerzeichen getrennte Begriffe muessen alle zutreffen -
    /// "2021 pro" findet damit gezielt Office 2021 Pro Plus.
    /// </summary>
    public static bool Matches(CatalogEntry entry, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var haystack = string.Join(' ',
            entry.DisplayName,
            entry.Configuration.ProductId ?? "",
            entry.Configuration.FileName,
            entry.ChannelFolderName,
            entry.ClientEdition + " Bit",
            entry.Configuration.Language ?? "");

        foreach (var term in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Liest die Paketversion aus Versionscheck\Version.txt ("69|13.08.2026").
    /// Fehlt die Datei, kommt null zurueck.
    /// </summary>
    public static (string Version, string Date)? ReadPackageVersion(string rootFolder)
    {
        try
        {
            var file = Path.Combine(rootFolder, "Versionscheck", "Version.txt");
            if (!File.Exists(file)) return null;

            var content = File.ReadAllText(file).Trim();
            if (content.Length == 0) return null;

            var parts = content.Split('|');
            return parts.Length >= 2
                ? (parts[0].Trim(), parts[1].Trim())
                : (content, "");
        }
        catch { return null; }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OfficeInstall.Services;

/// <summary>Gewuenschte Herkunft der Installationsdateien.</summary>
public enum InstallMode
{
    /// <summary>Offline, wenn Quelldateien vorliegen - sonst online.</summary>
    Automatic,

    /// <summary>Immer aus dem Internet, auch wenn Quelldateien vorliegen.</summary>
    Online,

    /// <summary>Nur aus den vorliegenden Quelldateien.</summary>
    Offline
}

/// <summary>Was konkret ausgefuehrt werden soll.</summary>
public sealed class InstallPlan
{
    public required string SetupExecutable { get; init; }
    public required string ConfigurationFile { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>Der tatsaechlich verwendete Modus (Automatic ist hier aufgeloest).</summary>
    public required InstallMode EffectiveMode { get; init; }

    /// <summary>Gesetzter SourcePath, oder null bei Installation aus dem Internet.</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Festgeschriebene Version (Attribut Version in der Konfiguration).
    /// Nur bei der Offline-Installation gesetzt - beim Herunterladen und bei
    /// der Online-Installation soll ja gerade der neueste Stand gelten.
    /// </summary>
    public Version? PinnedVersion { get; init; }

    /// <summary>
    /// Schalter des Deployment Tools: "configure" installiert,
    /// "download" holt die Quelldateien.
    /// </summary>
    public string Verb { get; init; } = "configure";

    public bool IsDownload => string.Equals(Verb, "download", StringComparison.OrdinalIgnoreCase);

    /// <summary>Wie viele Produkte in der Konfiguration stehen (1 bei Einzelaufrufen).</summary>
    public int ProductCount { get; init; } = 1;

    /// <summary>
    /// Gesetzt, wenn in der Vorlage ein anderer Kanal stand als der Ordner
    /// vorgibt - dann steht hier der Wert aus der Vorlage. Verwendet wird immer
    /// der Ordner; das Feld dient nur dazu, den Fund melden zu koennen.
    /// </summary>
    public string? ChannelCorrectedFrom { get; init; }

    /// <summary>Der tatsaechlich eingetragene Kanal.</summary>
    public string? Channel { get; init; }

    public string Arguments => $"/{Verb} \"{ConfigurationFile}\"";

    public string CommandLine => $"\"{SetupExecutable}\" {Arguments}";
}

/// <summary>
/// Bereitet den Aufruf des Office Deployment Tools vor und startet ihn.
///
/// Das Deployment Tool sucht die Quelldateien standardmaessig im Ordner der
/// setup.exe und geht erst danach ins Internet. Fuer die beiden ausdruecklichen
/// Modi genuegt es daher nicht, einfach die vorhandene XML zu benutzen:
///
///   Offline  - eine Kopie der XML mit gesetztem SourcePath. Damit ist der
///              Ordner festgeschrieben, aus dem installiert wird.
///   Online   - eine Kopie ohne SourcePath in einem leeren Arbeitsordner,
///              in den auch die setup.exe kopiert wird. Da dort keine
///              Quelldateien liegen, laedt das Tool aus dem Internet.
///   Automatik- die XML wird unveraendert im Ordner der Basis ausgefuehrt;
///              das ist genau das Verhalten der alten start.bat.
/// </summary>
public static class InstallRunner
{
    /// <summary>Arbeitsordner fuer die erzeugten Kopien.</summary>
    public static string DefaultTempRoot => TempWorkspace.Root;

    /// <summary>
    /// Loest den Modus auf: Automatik wird zu Offline, wenn Quelldateien da
    /// sind, sonst zu Online.
    /// </summary>
    public static InstallMode ResolveMode(InstallMode requested, OfflineSource offline)
        => requested switch
        {
            InstallMode.Automatic => offline.Available ? InstallMode.Offline : InstallMode.Online,
            _ => requested
        };

    /// <summary>
    /// Stellt alles bereit, was fuer den Aufruf gebraucht wird, und legt die
    /// noetigen Kopien an. Der Vorgang selbst wird noch nicht gestartet -
    /// dadurch laesst sich das Ergebnis pruefen und protokollieren.
    /// </summary>
    public static InstallPlan Prepare(CatalogEntry entry, InstallMode requestedMode, string? tempRoot = null)
    {
        if (entry.SetupExecutable is null)
            throw new InvalidOperationException("Es wurde keine setup.exe gefunden.");

        var mode = ResolveMode(requestedMode, entry.Offline);

        if (mode == InstallMode.Offline && !entry.Offline.Available)
            throw new InvalidOperationException("Für diese Installationsbasis liegen keine Offline-Dateien vor.");

        var workFolder = Path.Combine(tempRoot ?? DefaultTempRoot,
                                      entry.Configuration.ArchitectureFolderName,
                                      entry.Configuration.ChannelFolderName);
        Directory.CreateDirectory(workFolder);

        var configurationCopy = Path.Combine(workFolder, entry.Configuration.FileName);

        // Der Ordner bestimmt die Installationsbasis - nicht das, was in der
        // Vorlage steht. Weicht beides voneinander ab, wird der Ordner
        // eingetragen und die Abweichung gemeldet.
        var folderChannel = entry.Channel?.FolderName ?? entry.Configuration.ChannelFolderName;
        var wrongChannel =
            OfficeConfiguration.ChannelMatchesFolder(entry.Configuration.DeclaredChannel, folderChannel)
                ? null
                : entry.Configuration.DeclaredChannel;

        if (mode == InstallMode.Offline)
        {
            // Die vor Ort tatsaechlich vorliegende Version festschreiben.
            // Ohne diese Angabe fragt das Deployment Tool beim CDN nach dem
            // aktuellen Build des Kanals - und laedt ihn, sobald der oertliche
            // Bestand aelter ist. Der Offline-Ordner waere dann nutzlos.
            var pinned = entry.Offline.LatestVersion;

            OfficeConfiguration.WriteWithSourcePath(
                entry.Configuration.FilePath, configurationCopy, entry.Offline.SourcePath,
                pinned, folderChannel);

            return new InstallPlan
            {
                SetupExecutable = entry.SetupExecutable,
                ConfigurationFile = configurationCopy,
                // Im Ordner der Basis ausfuehren - dort liegen die Quelldateien.
                WorkingDirectory = entry.Configuration.BaseFolder,
                EffectiveMode = InstallMode.Offline,
                SourcePath = entry.Offline.SourcePath,
                PinnedVersion = pinned,
                Channel = folderChannel,
                ChannelCorrectedFrom = wrongChannel
            };
        }

        // Online: setup.exe in den leeren Arbeitsordner kopieren, damit das
        // Deployment Tool dort keine Quelldateien findet und ins Internet geht.
        var setupCopy = Path.Combine(workFolder, "setup.exe");
        File.Copy(entry.SetupExecutable, setupCopy, overwrite: true);

        OfficeConfiguration.WriteWithoutSourcePath(
            entry.Configuration.FilePath, configurationCopy, folderChannel);

        return new InstallPlan
        {
            SetupExecutable = setupCopy,
            ConfigurationFile = configurationCopy,
            WorkingDirectory = workFolder,
            EffectiveMode = InstallMode.Online,
            SourcePath = null,
            Channel = folderChannel,
            ChannelCorrectedFrom = wrongChannel
        };
    }

    /// <summary>
    /// Bereitet das Herunterladen der Offline-Quelldateien vor
    /// ("setup.exe /download"). Der SourcePath wird dabei ausdruecklich auf den
    /// Ordner der Installationsbasis gesetzt - sonst wuerden die Dateien dort
    /// landen, wo die setup.exe liegt, und das kann auch der Wurzelordner sein.
    ///
    /// Sind bereits Dateien vorhanden, laedt das Deployment Tool nur, was fehlt.
    /// Der vorhandene Bestand wird also ergaenzt und nicht neu geholt.
    /// </summary>
    public static InstallPlan PrepareDownload(CatalogEntry entry, string? tempRoot = null)
    {
        if (entry.SetupExecutable is null)
            throw new InvalidOperationException("Es wurde keine setup.exe gefunden.");

        if (entry.Configuration.IsBroken)
            throw new InvalidOperationException("Die Konfigurationsdatei ist nicht auswertbar.");

        var workFolder = Path.Combine(tempRoot ?? DefaultTempRoot,
                                      entry.Configuration.ArchitectureFolderName,
                                      entry.Configuration.ChannelFolderName);
        Directory.CreateDirectory(workFolder);

        var target = Path.Combine(workFolder, "download_" + entry.Configuration.FileName);
        var sourcePath = Path.GetFullPath(entry.Configuration.BaseFolder);

        // Auch hier gilt der Ordner: Sonst landen im Ordner PerpetualVL2019
        // die Quelldateien eines anderen Kanals.
        var folderChannel = entry.Channel?.FolderName ?? entry.Configuration.ChannelFolderName;
        var wrongChannel =
            OfficeConfiguration.ChannelMatchesFolder(entry.Configuration.DeclaredChannel, folderChannel)
                ? null
                : entry.Configuration.DeclaredChannel;

        OfficeConfiguration.WriteWithSourcePath(
            entry.Configuration.FilePath, target, sourcePath, pinnedVersion: null, folderChannel);

        return new InstallPlan
        {
            SetupExecutable = entry.SetupExecutable,
            ConfigurationFile = target,
            WorkingDirectory = entry.Configuration.BaseFolder,
            EffectiveMode = InstallMode.Offline,
            SourcePath = sourcePath,
            Verb = "download",
            Channel = folderChannel,
            ChannelCorrectedFrom = wrongChannel
        };
    }

    /// <summary>
    /// Bereitet das Herunterladen fuer eine komplette Installationsbasis vor -
    /// alle Produkte dieser Basis in einem einzigen Durchlauf.
    ///
    /// Das ist der Unterschied zu PrepareDownload: Statt das Deployment Tool
    /// je Konfigurationsdatei einmal zu starten (bei acht Dateien in "Current"
    /// also achtmal), wird eine gemeinsame Datei mit allen Produkten erzeugt.
    /// Geladen wird dieselbe Menge, aber in einem Rutsch.
    /// </summary>
    public static InstallPlan PrepareBaseDownload(IReadOnlyList<CatalogEntry> entriesOfBase, string? tempRoot = null)
    {
        if (entriesOfBase.Count == 0)
            throw new InvalidOperationException("Zu dieser Installationsbasis gibt es keine Konfiguration.");

        var usable = entriesOfBase.Where(e => e.CanInstall).ToList();
        if (usable.Count == 0)
            throw new InvalidOperationException("Zu dieser Installationsbasis gibt es keine brauchbare Konfiguration.");

        var first = usable[0];
        var setup = first.SetupExecutable
                    ?? throw new InvalidOperationException("Es wurde keine setup.exe gefunden.");

        var workFolder = Path.Combine(tempRoot ?? DefaultTempRoot,
                                      first.Configuration.ArchitectureFolderName,
                                      first.Configuration.ChannelFolderName);
        Directory.CreateDirectory(workFolder);

        var target = Path.Combine(workFolder, "download_alle.xml");
        var sourcePath = Path.GetFullPath(first.Configuration.BaseFolder);

        var products = OfficeConfiguration.WriteMergedDownloadConfig(
            usable.Select(e => e.Configuration.FilePath),
            target,
            sourcePath,
            first.ClientEdition,
            first.Channel?.FolderName ?? first.Configuration.ChannelFolderName);

        if (products == 0)
            throw new InvalidOperationException("Aus den Konfigurationsdateien ließ sich kein Produkt lesen.");

        return new InstallPlan
        {
            SetupExecutable = setup,
            ConfigurationFile = target,
            WorkingDirectory = first.Configuration.BaseFolder,
            EffectiveMode = InstallMode.Offline,
            SourcePath = sourcePath,
            Verb = "download",
            ProductCount = products,
            Channel = first.Channel?.FolderName ?? first.Configuration.ChannelFolderName
        };
    }

    /// <summary>
    /// Startet das Deployment Tool. Es bringt seine eigene Oberflaeche mit
    /// (Display Level="Full" in den Konfigurationsdateien), deshalb wird hier
    /// nichts umgeleitet - der Fortschritt erscheint im Fenster von Microsoft.
    /// </summary>
    public static Process Start(InstallPlan plan)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.SetupExecutable,
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory,
            // Beim Herunterladen zeigt das Deployment Tool nichts an - dann soll
            // auch kein leeres Konsolenfenster aufblitzen.
            UseShellExecute = !plan.IsDownload,
            CreateNoWindow = plan.IsDownload
        };

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Der Installationsvorgang konnte nicht gestartet werden.");
    }

    /// <summary>
    /// Raeumt die erzeugten Kopien auf. Ohne Angabe wird der gemeinsame
    /// Arbeitsordner entfernt - siehe <see cref="TempWorkspace"/>.
    /// </summary>
    public static void CleanUp(string? tempRoot = null)
    {
        if (tempRoot is null) { TempWorkspace.Clear(); return; }

        try
        {
            if (Directory.Exists(tempRoot)) OfflineSource.ForceDeleteDirectory(tempRoot);
        }
        catch { /* wird beim naechsten Mal ueberschrieben */ }
    }
}

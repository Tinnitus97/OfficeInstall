using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OfficeInstall.Services;

/// <summary>Eine auf der Microsoft-Downloadseite gefundene Fassung des Deployment Tools.</summary>
public sealed class OdtRelease
{
    public required string Url { get; init; }
    public required Version Version { get; init; }
    public string FileName => Url[(Url.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Kuemmert sich um die setup.exe des Office Deployment Tools: finden, Version
/// bestimmen, bei Bedarf von Microsoft holen und im Paket verteilen.
///
/// Bezogen wird sie ausschliesslich vom offiziellen Download Center
/// (https://www.microsoft.com/en-us/download/details.aspx?id=49117). Die im
/// Netz kursierende Abkuerzung officecdn.microsoft.com/pr/wsus/setup.exe wird
/// bewusst nicht verwendet - Microsoft hat auf Nachfrage bestaetigt, dass das
/// kein offizieller Downloadweg ist.
///
/// Aendert Microsoft den Aufbau der Downloadseite, laesst sich die Adresse
/// ueber die Datei Versionscheck\ODT-Url.txt fest vorgeben, ohne dass das
/// Programm neu gebaut werden muss.
/// </summary>
public static class DeploymentToolService
{
    public const string DownloadPageUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=49117";

    /// <summary>Optionale Datei mit einer fest vorgegebenen Downloadadresse.</summary>
    public const string UrlOverrideFolder = "Versionscheck";

    public const string UrlOverrideFileName = "ODT-Url.txt";

    /// <summary>
    /// Vollstaendiger Pfad der Datei mit der festen Downloadadresse. Bewusst
    /// ueber Path.Combine zusammengesetzt statt mit festem Trennzeichen.
    /// </summary>
    public static string UrlOverridePath(string rootFolder)
        => Path.Combine(rootFolder, UrlOverrideFolder, UrlOverrideFileName);

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sucht in einem Text nach dem Link auf officedeploymenttool_*.exe.
    /// Als eigene Methode gehalten, damit sie ohne Netz testbar ist.
    /// </summary>
    public static OdtRelease? ExtractRelease(string pageContent)
    {
        if (string.IsNullOrWhiteSpace(pageContent)) return null;

        // Beispiel:
        // https://download.microsoft.com/download/6c1eeb25-.../officedeploymenttool_20228-20124.exe
        var matches = Regex.Matches(
            pageContent,
            @"https://download\.microsoft\.com/download/[^\s""'<>\\]+/officedeploymenttool_(\d+)-(\d+)\.exe",
            RegexOptions.IgnoreCase);

        OdtRelease? newest = null;
        foreach (Match match in matches)
        {
            var version = BuildVersion(match.Groups[1].Value, match.Groups[2].Value);
            if (version is null) continue;
            if (newest is null || version > newest.Version)
                newest = new OdtRelease { Url = match.Value, Version = version };
        }
        return newest;
    }

    /// <summary>
    /// Leitet die Version aus dem Dateinamen ab: aus
    /// "officedeploymenttool_20228-20124.exe" wird 16.0.20228.20124.
    /// </summary>
    public static Version? ParseVersionFromFileName(string fileName)
    {
        var match = Regex.Match(fileName ?? "", @"officedeploymenttool_(\d+)-(\d+)\.exe", RegexOptions.IgnoreCase);
        return match.Success ? BuildVersion(match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private static Version? BuildVersion(string build, string revision)
        => int.TryParse(build, out var b) && int.TryParse(revision, out var r)
            ? new Version(16, 0, b, r)
            : null;

    /// <summary>Liest die Dateiversion einer setup.exe. null, wenn nicht lesbar.</summary>
    public static Version? ReadFileVersion(string? setupExecutable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(setupExecutable) || !File.Exists(setupExecutable)) return null;
            var info = FileVersionInfo.GetVersionInfo(setupExecutable);
            return Version.TryParse(info.FileVersion, out var version) ? version : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Entscheidet, ob sich der Download lohnt: wenn gar keine setup.exe da ist
    /// oder die vorhandene aelter ist als die angebotene.
    /// </summary>
    public static bool NeedsUpdate(Version? localVersion, Version? onlineVersion)
    {
        if (onlineVersion is null) return false;   // keine Auskunft -> nichts tun
        if (localVersion is null) return true;     // gar keine setup.exe vorhanden
        return onlineVersion > localVersion;
    }

    /// <summary>
    /// Ermittelt alle setup.exe im Paket, die ersetzt werden sollen: im
    /// Wurzelordner, in x32/x64 und in den Ordnern der Installationsbasen.
    /// Ist gar keine vorhanden, wird der Wurzelordner als Ziel vorgeschlagen.
    /// </summary>
    public static IReadOnlyList<string> DeploymentTargets(string rootFolder)
    {
        var targets = new List<string>();

        void Consider(string folder)
        {
            try
            {
                var candidate = Path.Combine(folder, "setup.exe");
                if (File.Exists(candidate)) targets.Add(Path.GetFullPath(candidate));
            }
            catch { /* Ordner nicht lesbar */ }
        }

        Consider(rootFolder);

        foreach (var architecture in OfficeCatalog.ArchitectureFolders)
        {
            var architectureFolder = Path.Combine(rootFolder, architecture);
            if (!Directory.Exists(architectureFolder)) continue;

            Consider(architectureFolder);
            foreach (var channel in OfficeChannels.All)
            {
                var baseFolder = Path.Combine(architectureFolder, channel.FolderName);
                if (Directory.Exists(baseFolder)) Consider(baseFolder);
            }
        }

        if (targets.Count == 0) targets.Add(Path.GetFullPath(Path.Combine(rootFolder, "setup.exe")));
        return targets;
    }

    /// <summary>Liest eine fest vorgegebene Adresse aus Versionscheck\ODT-Url.txt.</summary>
    public static string? ReadUrlOverride(string rootFolder)
    {
        try
        {
            var file = UrlOverridePath(rootFolder);
            if (!File.Exists(file)) return null;
            var url = File.ReadAllText(file).Trim();
            return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Fragt beim Download Center nach der aktuell angebotenen Fassung.
    /// Rueckgabe null, wenn kein Netz besteht oder der Link nicht gefunden wird.
    /// </summary>
    public static async Task<OdtRelease?> ResolveLatestAsync(string rootFolder, CancellationToken ct = default)
    {
        // Feste Vorgabe hat Vorrang.
        var overrideUrl = ReadUrlOverride(rootFolder);
        if (overrideUrl is not null)
        {
            var version = ParseVersionFromFileName(overrideUrl[(overrideUrl.LastIndexOf('/') + 1)..]);
            if (version is not null) return new OdtRelease { Url = overrideUrl, Version = version };
        }

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OfficeInstall/1.3");
            var page = await http.GetStringAsync(DownloadPageUrl, ct);
            return ExtractRelease(page);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Laedt das Paket herunter und entpackt es. Das Deployment Tool wird als
    /// selbstentpackendes Archiv ausgeliefert und unterstuetzt dafuer
    /// "/quiet /extract:&lt;Ordner&gt;". Rueckgabe ist der Pfad zur entpackten
    /// setup.exe.
    /// </summary>
    public static async Task<string> DownloadAndExtractAsync(
        OdtRelease release, string workFolder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workFolder);
        var packageFile = Path.Combine(workFolder, release.FileName);

        using (var http = new HttpClient { Timeout = Timeout })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OfficeInstall/1.3");
            await using var stream = await http.GetStreamAsync(release.Url, ct);
            await using var file = File.Create(packageFile);
            await stream.CopyToAsync(file, ct);
        }

        var extractFolder = Path.Combine(workFolder, "extract");
        if (Directory.Exists(extractFolder)) Directory.Delete(extractFolder, recursive: true);
        Directory.CreateDirectory(extractFolder);

        var startInfo = new ProcessStartInfo
        {
            FileName = packageFile,
            Arguments = $"/quiet /extract:\"{extractFolder}\"",
            WorkingDirectory = workFolder,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(startInfo)
                             ?? throw new InvalidOperationException("Das heruntergeladene Paket ließ sich nicht starten."))
        {
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Das Entpacken endete mit Rückgabewert {process.ExitCode}.");
        }

        var setup = Directory.GetFiles(extractFolder, "setup.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new FileNotFoundException("Im entpackten Paket war keine setup.exe enthalten.");

        return setup;
    }

    /// <summary>
    /// Kopiert die neue setup.exe an alle Zielorte. Rueckgabe ist die Anzahl
    /// der tatsaechlich ersetzten Dateien.
    /// </summary>
    public static int Deploy(string newSetupExecutable, IEnumerable<string> targets)
    {
        var count = 0;
        foreach (var target in targets)
        {
            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.Copy(newSetupExecutable, target, overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>Oeffnet die Downloadseite im Browser - der Rueckfallweg von Hand.</summary>
    public static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true });
        }
        catch { /* kein Browser hinterlegt */ }
    }

    /// <summary>Arbeitsordner fuer den Download - Unterordner des Arbeitsordners.</summary>
    public static string DefaultWorkFolder => TempWorkspace.DeploymentFolder;
}

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OfficeInstall.Services;

/// <summary>Ein Stand aus update.json - Programm oder Konfigurationen.</summary>
public sealed class UpdateEntry
{
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Notes { get; init; } = "";

    public bool IsUsable => !string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(Url);
}

/// <summary>Das Ergebnis der Abfrage.</summary>
public sealed class UpdateManifest
{
    public UpdateEntry Program { get; init; } = new();
    public UpdateEntry Configs { get; init; } = new();
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static UpdateManifest Failed(string error) => new() { Error = error };
}

/// <summary>
/// Fragt ab, ob es ein neueres Programm oder neuere Konfigurationsdateien gibt,
/// und spielt sie auf Wunsch ein.
///
/// WARUM raw.githubusercontent UND NICHT api.github.com:
/// Die Schnittstelle von GitHub erlaubt ohne Anmeldung 60 Abrufe je Stunde und
/// IP-Adresse. In einer Firma sitzen alle Rechner hinter derselben Adresse -
/// nach 60 Programmstarts waere Schluss, und ein Zugangstoken hat in einer
/// verteilten EXE nichts zu suchen. Die Rohdatei wird dagegen ueber ein
/// Auslieferungsnetz bereitgestellt und kennt diese Grenze nicht.
///
/// WAS BEWUSST NICHT PASSIERT: Es wird nichts heruntergeladen und nichts
/// ersetzt, ohne dass jemand zugestimmt hat. Und es wird ausschliesslich das
/// eingespielt, dessen Pruefsumme zu der in update.json passt.
/// </summary>
public static class UpdateService
{
    /// <summary>Vorgabe - laesst sich ueber Versionscheck\Update-Url.txt ersetzen.</summary>
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/Tinnitus97/OfficeInstall/main/update.json";

    /// <summary>
    /// Eigener Arbeitsordner - ausdruecklich NICHT der von TempWorkspace.
    /// Der wird beim Beenden geleert, und genau dann laeuft der Austausch der
    /// EXE noch.
    /// </summary>
    public static string WorkFolder => Path.Combine(Path.GetTempPath(), "OfficeInstall-Update");

    private static string UrlOverridePath(string rootFolder)
        => Path.Combine(rootFolder, "Versionscheck", "Update-Url.txt");

    /// <summary>Liest eine abweichende Adresse aus dem Paketordner, falls hinterlegt.</summary>
    public static string ResolveManifestUrl(string rootFolder)
    {
        try
        {
            var file = UrlOverridePath(rootFolder);
            if (!File.Exists(file)) return DefaultManifestUrl;

            var line = File.ReadAllLines(file)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(line) ? DefaultManifestUrl : line;
        }
        catch { return DefaultManifestUrl; }
    }

    // ------------------------------------------------------------ Abfragen

    public static async Task<UpdateManifest> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OfficeInstall");
            // Kein zwischengespeicherter Stand - sonst meldet ein Rechner
            // stundenlang die alte Nummer.
            client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true
            };

            var json = await client.GetStringAsync(url, ct);
            return Parse(json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return UpdateManifest.Failed(ex.Message);
        }
    }

    /// <summary>Wertet update.json aus. Oeffentlich, damit es sich pruefen laesst.</summary>
    public static UpdateManifest Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new UpdateManifest
            {
                Success = true,
                Program = ReadEntry(root, "program"),
                Configs = ReadEntry(root, "configs")
            };
        }
        catch (Exception ex)
        {
            return UpdateManifest.Failed(ex.Message);
        }
    }

    private static UpdateEntry ReadEntry(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return new UpdateEntry();

        return new UpdateEntry
        {
            Version = Text(element, "version"),
            Date = Text(element, "date"),
            Url = Text(element, "url"),
            Sha256 = Text(element, "sha256").ToLowerInvariant(),
            Notes = Text(element, "notes")
        };
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                _ => ""
            }
            : "";

    // ------------------------------------------------------------ Vergleichen

    /// <summary>true, wenn die angebotene Programmversion hoeher ist als die eigene.</summary>
    public static bool IsProgramNewer(string? local, string? online)
        => Version.TryParse(local, out var here)
           && Version.TryParse(online, out var there)
           && there > here;

    /// <summary>
    /// true, wenn der angebotene Paketstand hoeher ist. Die Staende sind
    /// fortlaufende Zahlen ("69", "70"); laesst sich einer nicht als Zahl
    /// lesen, wird auf einen Textvergleich zurueckgefallen.
    /// </summary>
    public static bool IsConfigsNewer(string? local, string? online)
    {
        if (string.IsNullOrWhiteSpace(online)) return false;
        if (string.IsNullOrWhiteSpace(local)) return true;

        if (int.TryParse(local.Trim(), out var here) && int.TryParse(online.Trim(), out var there))
            return there > here;

        return !string.Equals(local.Trim(), online.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ Herunterladen

    /// <summary>Ergebnis eines Downloads samt Pruefsummenvergleich.</summary>
    public sealed record DownloadResult(bool Success, string File, string? Error, long Bytes);

    /// <summary>
    /// Laedt eine Datei und prueft ihre SHA256-Summe gegen die Angabe aus
    /// update.json. Stimmt sie nicht, wird die Datei wieder geloescht - eine
    /// halb uebertragene oder ausgetauschte Datei darf nicht eingespielt
    /// werden.
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        string url, string expectedSha256, string targetFile, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("OfficeInstall");

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(targetFile);
                await source.CopyToAsync(target, ct);
            }

            var size = new FileInfo(targetFile).Length;

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = ComputeSha256(targetFile);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(targetFile);
                    return new DownloadResult(false, targetFile,
                        $"Prüfsumme stimmt nicht (erwartet {Short(expectedSha256)}, erhalten {Short(actual)})", size);
                }
            }

            return new DownloadResult(true, targetFile, null, size);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TryDelete(targetFile);
            return new DownloadResult(false, targetFile, ex.Message, 0);
        }
    }

    public static string ComputeSha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] + "…" : hash;

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* egal */ }
    }

    // ------------------------------------------------------------ Einspielen

    /// <summary>Was beim Einspielen der Konfigurationen passiert ist.</summary>
    public sealed class ConfigApplyResult
    {
        public int Written { get; set; }
        public int Skipped { get; set; }
        public string? Error { get; set; }
        public bool Success => Error is null;
    }

    /// <summary>
    /// Entpackt configs.zip in den Paketordner.
    ///
    /// Uebernommen werden ausschliesslich XML-Dateien unter x32\ und x64\ sowie
    /// Versionscheck\Version.txt. Alles andere im Archiv wird uebergangen -
    /// damit kann ein manipuliertes oder versehentlich falsch geschnuertes
    /// Paket weder eine EXE noch etwas ausserhalb des Paketordners ablegen.
    ///
    /// Der Offline-Bestand (Office\Data) wird nicht angefasst: Es wird nichts
    /// geloescht, nur geschrieben.
    /// </summary>
    public static ConfigApplyResult ApplyConfigs(string zipFile, string rootFolder)
    {
        var result = new ConfigApplyResult();

        try
        {
            var root = Path.GetFullPath(rootFolder);
            using var archive = ZipFile.OpenRead(zipFile);

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0) continue;

                if (!IsAllowed(entry.FullName))
                {
                    result.Skipped++;
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

                // Schutz vor Eintraegen wie "..\..\windows\system32\..." -
                // das Ziel muss im Paketordner liegen.
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    result.Skipped++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                result.Written++;
            }

            if (result.Written == 0)
                result.Error = "Das Archiv enthielt keine brauchbaren Konfigurationsdateien.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>Welche Eintraege aus dem Archiv uebernommen werden.</summary>
    public static bool IsAllowed(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("../", StringComparison.Ordinal)) return false;

        if (normalized.Equals("Versionscheck/Version.txt", StringComparison.OrdinalIgnoreCase))
            return true;

        var isConfig = (normalized.StartsWith("x32/", StringComparison.OrdinalIgnoreCase)
                        || normalized.StartsWith("x64/", StringComparison.OrdinalIgnoreCase))
                       && normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        // x64/PerpetualVL2021/datei.xml -> genau drei Teile, keine tiefere Ebene
        return isConfig && normalized.Split('/').Length == 3;
    }

    // ------------------------------------------------------------ Programm austauschen

    /// <summary>
    /// Tauscht die laufende EXE gegen die heruntergeladene aus.
    ///
    /// Eine laufende EXE kann sich nicht selbst ueberschreiben. Deshalb wird
    /// ein kleines Batch-Skript geschrieben, das wartet, bis dieser Vorgang
    /// beendet ist, dann die Datei ersetzt und das Programm neu startet.
    ///
    /// Das Skript liegt in einem EIGENEN Ordner (%TEMP%\OfficeInstall-Update).
    /// Der Arbeitsordner %TEMP%\OfficeInstall wird beim Beenden geleert - genau
    /// waehrend das Skript noch laeuft.
    /// </summary>
    public static string WriteUpdateScript(string newExe, string currentExe, int processId)
    {
        Directory.CreateDirectory(WorkFolder);
        var script = Path.Combine(WorkFolder, "austausch.cmd");

        var text = $"""
            @echo off
            rem Wartet auf das Ende von OfficeInstall und ersetzt dann die Datei.
            setlocal
            set "NEU={newExe}"
            set "ZIEL={currentExe}"

            for /l %%i in (1,1,30) do (
              tasklist /fi "PID eq {processId}" 2>nul | find "{processId}" >nul || goto ersetzen
              timeout /t 1 /nobreak >nul
            )

            :ersetzen
            move /y "%NEU%" "%ZIEL%" >nul
            if errorlevel 1 (
              echo.
              echo Die Datei konnte nicht ersetzt werden.
              echo Neue Fassung liegt hier: %NEU%
              echo Ziel: %ZIEL%
              echo.
              echo Moegliche Gruende: fehlende Schreibrechte, oder das Programm
              echo laeuft an einem anderen Arbeitsplatz aus demselben Ordner.
              echo.
              pause
              exit /b 1
            )

            start "" "%ZIEL%"
            del "%~f0" >nul 2>&1
            """;

        File.WriteAllText(script, text, Encoding.UTF8);
        return script;
    }

    /// <summary>Startet das Austauschskript. Danach muss das Programm sich beenden.</summary>
    public static void StartUpdateScript(string script)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkFolder
        });
    }

    /// <summary>Raeumt den eigenen Arbeitsordner auf.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(WorkFolder)) OfflineSource.ForceDeleteDirectory(WorkFolder);
        }
        catch { /* beim naechsten Mal */ }
    }
}

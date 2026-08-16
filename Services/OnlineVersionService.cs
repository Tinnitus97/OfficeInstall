using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OfficeInstall.Services;

/// <summary>Ergebnis der Abfrage der Versionsuebersicht.</summary>
public sealed class OnlineVersionResult
{
    /// <summary>Version je Installationsbasis (Schluessel: Ordnername der Basis).</summary>
    public IReadOnlyDictionary<string, Version> Versions { get; init; } =
        new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Warum es nicht geklappt hat - fuer das Protokoll.</summary>
    public string? Error { get; init; }

    public bool Success => Error is null;

    public Version? For(OfficeChannel? channel)
        => channel is not null && Versions.TryGetValue(channel.FolderName, out var version) ? version : null;

    public static OnlineVersionResult Failed(string error) => new() { Error = error };
}

/// <summary>
/// Fragt bei Microsoft ab, welche Office-Version je Installationsbasis derzeit
/// angeboten wird. Damit laesst sich beantworten, ob die vorliegenden
/// Offline-Dateien noch aktuell sind.
///
/// Verwendet wird die Versionsuebersicht unter
/// https://clients.config.office.net/releases/v1.0/OfficeReleases - ein
/// einziger Aufruf liefert alle Kanaele auf einmal.
///
/// Hinweis fuer spaeter: Der naheliegende Weg ueber
/// officecdn.microsoft.com/pr/&lt;GUID&gt;/Office/Data/VersionDescriptor.xml
/// funktioniert nicht. Das CDN liefert dort HTTP 400 - .cab-Dateien werden
/// ausgeliefert, die XML-Datei nicht.
/// </summary>
public static class OnlineVersionService
{
    public const string ReleasesUrl = "https://clients.config.office.net/releases/v1.0/OfficeReleases";

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Zeitgrenze je Abfrage ueber ein CancellationToken - am Client laesst
        // sie sich nach der ersten Anfrage nicht mehr aendern.
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OfficeInstall/1.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static OnlineVersionResult? _cached;

    /// <summary>Verwirft die zwischengespeicherte Antwort.</summary>
    public static void ClearCache() => _cached = null;

    /// <summary>
    /// Holt die Versionsuebersicht. Erfolgreiche Antworten werden fuer die
    /// Laufzeit gemerkt; ein Fehlschlag wird bewusst nicht gemerkt, damit ein
    /// spaeterer Versuch nicht dauerhaft blockiert ist.
    /// </summary>
    public static async Task<OnlineVersionResult> GetReleasesAsync(CancellationToken ct = default)
    {
        if (_cached is { Success: true }) return _cached;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var response = await Http.GetAsync(ReleasesUrl, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return OnlineVersionResult.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            var versions = ParseReleases(json);

            if (versions.Count == 0)
                return OnlineVersionResult.Failed("Antwort enthielt keine bekannte Installationsbasis.");

            var result = new OnlineVersionResult { Versions = versions };
            _cached = result;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return OnlineVersionResult.Failed($"Zeitüberschreitung nach {Timeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return OnlineVersionResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Wertet die Versionsuebersicht aus. Zugeordnet wird ueber die
    /// Kanalkennung (channelId, z.B. "PerpetualVL2021") und zusaetzlich ueber
    /// die GUID in cdnBaseUrl - so passt die Zuordnung auch dann noch, wenn
    /// Microsoft die Namen aendert.
    ///
    /// Als eigene Methode gehalten, damit sie ohne Netz testbar ist.
    /// </summary>
    public static IReadOnlyDictionary<string, Version> ParseReleases(string json)
    {
        var result = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!TryGetString(element, "latestVersion", out var versionText)) continue;
                if (!Version.TryParse(versionText, out var version)) continue;

                var channelId = TryGetString(element, "channelId", out var id) ? id : null;
                var cdnBaseUrl = TryGetString(element, "cdnBaseUrl", out var url) ? url : null;

                foreach (var channel in OfficeChannels.All)
                {
                    var passtUeberKennung =
                        channelId is not null &&
                        string.Equals(channelId, channel.FolderName, StringComparison.OrdinalIgnoreCase);

                    var passtUeberGuid =
                        cdnBaseUrl is not null &&
                        cdnBaseUrl.Contains(channel.CdnGuid, StringComparison.OrdinalIgnoreCase);

                    if (!passtUeberKennung && !passtUeberGuid) continue;

                    // Die hoechste gefundene Angabe gewinnt.
                    if (!result.TryGetValue(channel.FolderName, out var vorhanden) || version > vorhanden)
                        result[channel.FolderName] = version;
                }
            }
        }
        catch
        {
            // Unerwartetes Format: lieber keine Auskunft als eine falsche.
        }

        return result;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return value.Length > 0;
    }
}

using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace OfficeInstall.Services;

/// <summary>
/// Kurze Vorabpruefung, ob der Rechner ueberhaupt am Netz haengt.
///
/// Sinn der Sache: Ist der Rechner offline, soll genau eine Zeile im Protokoll
/// stehen - und nicht fuer jede Installationsbasis eine eigene Fehlermeldung.
/// Geprueft wird gegen denselben Endpunkt, den auch Windows selbst fuer seine
/// Netzwerkerkennung benutzt (NCSI); der ist klein, schnell und antwortet mit
/// einer festen Zeichenkette.
///
/// Manche Firmennetze sperren genau diesen Endpunkt, obwohl alles andere
/// erreichbar ist. Schlaegt er fehl, wird deshalb noch github.com gefragt -
/// ueber https, damit ein Anmeldeportal (Hotel-WLAN) nicht als "online"
/// durchgeht: Dessen Umleitung scheitert an der Zertifikatspruefung.
/// </summary>
public static class ConnectivityService
{
    public const string ProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";

    /// <summary>Zweite Pruefung, falls die erste gesperrt ist. Jede Antwort zaehlt.</summary>
    public const string FallbackProbeUrl = "https://github.com/";

    /// <summary>Erwarteter Inhalt der Pruefdatei.</summary>
    public const string ExpectedContent = "Microsoft Connect Test";

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true
        };
        return client;
    }

    /// <summary>true, wenn Windows ueberhaupt eine Netzwerkverbindung meldet.</summary>
    public static bool HasNetworkInterface()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return true; }   // im Zweifel weiterprobieren
    }

    /// <summary>
    /// Prueft, ob eine brauchbare Internetverbindung besteht. Ein Portal, das
    /// irgendetwas anderes zurueckgibt (Hotel-WLAN, Zwangsanmeldung), gilt
    /// dabei nicht als "online" - genau dann waere jeder weitere Abruf sinnlos.
    /// </summary>
    public static async Task<bool> IsOnlineAsync(CancellationToken ct = default)
    {
        if (!HasNetworkInterface()) return false;

        if (await ProbeNcsiAsync(ct)) return true;
        return await ProbeFallbackAsync(ct);
    }

    private static async Task<bool> ProbeNcsiAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var text = await Http.GetStringAsync(ProbeUrl, timeout.Token);
            return text.Contains(ExpectedContent, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeFallbackAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, FallbackProbeUrl);
            using var response = await Http.SendAsync(request, timeout.Token);
            return true;   // irgendeine Antwort ueber https: Das Netz ist da
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

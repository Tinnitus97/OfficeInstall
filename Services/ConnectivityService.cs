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
/// </summary>
public static class ConnectivityService
{
    public const string ProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";

    /// <summary>Erwarteter Inhalt der Pruefdatei.</summary>
    public const string ExpectedContent = "Microsoft Connect Test";

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OfficeInstall/1.3");
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
}

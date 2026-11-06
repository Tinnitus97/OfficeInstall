namespace OfficeInstall.Services;

/// <summary>
/// Versionsnummer und Kennung des Programms - aus einer Quelle.
///
/// Frueher stand die Nummer zusaetzlich als Konstante im ViewModel und musste
/// bei jeder Veroeffentlichung von Hand nachgezogen werden; die Kennung fuer
/// Webanfragen hing gar bei "OfficeInstall/1.3" fest. Jetzt zaehlt allein
/// &lt;Version&gt; in OfficeInstall.csproj.
/// </summary>
public static class AppInfo
{
    /// <summary>Dreistellige Version, z.B. "1.5.0".</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Kennung fuer Webanfragen, z.B. "OfficeInstall/1.5.0".</summary>
    public static string UserAgent => "OfficeInstall/" + Version;
}

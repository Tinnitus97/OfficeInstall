using System;
using System.Diagnostics;
using System.IO;

namespace OfficeInstall.Services;

/// <summary>
/// Oeffnet Adressen im Standardbrowser des Benutzers.
///
/// ZWEI DINGE, AUF DIE ES ANKOMMT - das Programm laeuft mit
/// Administratorrechten:
///
/// 1. NUR https. Eine Adresse kann aus update.json stammen ("Was ist neu?").
///    Ueber die Windows-Shell gestartet, wuerde aus "C:\irgendwas.exe" oder
///    "file://..." kein Browserfenster, sondern ein Programmstart - mit
///    Administratorrechten.
///
/// 2. OHNE ADMINISTRATORRECHTE. Direkt aus diesem Prozess gestartet, erbt der
///    Browser dessen Rechte (sofern noch keiner offen ist) - und mit ihm jede
///    Datei, die er herunterlaedt und ausfuehrt. Deshalb geht der Aufruf ueber
///    explorer.exe: Der reicht ihn an die laufende Shell des Benutzers weiter,
///    und die startet den Browser mit gewoehnlichen Rechten.
/// </summary>
public static class Browser
{
    /// <summary>true fuer eine Adresse, die geoeffnet werden darf.</summary>
    public static bool IsAllowed(string? url) => UpdateService.IsSecureUrl(url);

    /// <summary>
    /// Baut den Aufruf. Getrennt vom Starten, damit er sich pruefen laesst.
    /// Rueckgabe null, wenn die Adresse nicht geoeffnet werden darf.
    /// </summary>
    public static ProcessStartInfo? BuildStartInfo(string? url, bool onWindows)
    {
        if (!IsAllowed(url)) return null;

        // explorer.exe trennt seine eigenen Schalter mit Kommas - eine Adresse
        // mit Komma oder Anfuehrungszeichen kaeme dort verstuemmelt an. Die
        // geht dann den gewohnten Weg ueber die Shell.
        var viaExplorer = onWindows && url!.IndexOfAny(new[] { ',', '"' }) < 0;
        if (!viaExplorer) return new ProcessStartInfo(url!) { UseShellExecute = true };

        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(url!);
        return start;
    }

    /// <summary>Oeffnet die Adresse. false, wenn sie nicht erlaubt ist oder kein Browser startet.</summary>
    public static bool Open(string? url)
    {
        try
        {
            var start = BuildStartInfo(url, OperatingSystem.IsWindows());
            if (start is null) return false;

            using var process = Process.Start(start);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

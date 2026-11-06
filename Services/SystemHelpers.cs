using System;
using System.IO;

namespace OfficeInstall.Services;

public static class SystemHelpers
{
    /// <summary>
    /// Verzeichnis, in dem die EXE liegt. Das entspricht dem "%~dp0" des alten
    /// Batch-Skripts und ist der Startpunkt der Ordnersuche. Funktioniert auch
    /// bei einer SingleFile-EXE (dort zeigt BaseDirectory ins Extraktions-
    /// verzeichnis, ProcessPath dagegen auf die EXE selbst).
    /// </summary>
    public static string GetApplicationDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
        }
        catch { /* Rueckfall unten */ }

        try
        {
            var baseDir = AppContext.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir)) return baseDir;
        }
        catch { /* Rueckfall unten */ }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Liest aus der Registry, ob Windows im hellen Modus laeuft.
    /// true = heller Modus, false = dunkler Modus (Rueckfall bei Unklarheit: dunkel).
    /// </summary>
    public static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = hell, 0 = dunkel
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* Registry nicht lesbar */ }
        return false; // Rueckfall: dunkel
    }

    /// <summary>
    /// Ermittelt die Windows-Anzeigesprache. Gibt "de" oder "en" zurueck;
    /// bei allem anderen oder Fehlern Rueckfall "de".
    /// </summary>
    public static string GetWindowsLanguage()
    {
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var two = culture.TwoLetterISOLanguageName?.ToLowerInvariant();
            if (two == "en") return "en";
            if (two == "de") return "de";
        }
        catch { /* ignore */ }
        return "de"; // Rueckfall: Deutsch
    }}

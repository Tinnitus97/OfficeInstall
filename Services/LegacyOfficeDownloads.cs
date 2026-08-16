using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OfficeInstall.Services;

/// <summary>
/// Art des Downloads einer alten Office-Fassung.
/// </summary>
public enum LegacyDownloadKind
{
    /// <summary>
    /// Das komplette Sicherungsmedium als .img-Datei. Es enthaelt beide
    /// Architekturen: setup.exe im Wurzelverzeichnis installiert 32 Bit,
    /// Office\setup64.exe installiert 64 Bit.
    /// </summary>
    Image,

    /// <summary>
    /// Der kleine Click-to-Run-Starter (wenige MB), der den Rest live
    /// nachlaedt. Hier ist die Architektur ausdruecklich waehlbar.
    /// </summary>
    Setup
}

/// <summary>
/// Eine alte Office-Fassung (2013 oder 2016), die sich nicht mehr ueber das
/// Deployment Tool ausrollen laesst.
///
/// Warum das hier getrennt behandelt wird: Das Office Deployment Tool kennt nur
/// die Kanaele Current, PerpetualVL2019, PerpetualVL2021 und PerpetualVL2024.
/// Fuer Office 2016 gibt es ueberhaupt keinen Perpetual-Kanal, und Office 2013
/// braeuchte ein eigenes Deployment Tool der Version 15. Beide Fassungen werden
/// deshalb ueber ihr eigenes Medium installiert.
///
/// Hier wird darum ausdruecklich NICHT installiert und NICHTS in den Paketordner
/// gelegt - es wird nur die Adresse im Standardbrowser geoeffnet. Wo die Datei
/// landet, entscheidet allein der Browser (ueblicherweise "Downloads").
/// </summary>
public sealed class LegacyOfficeRelease
{
    /// <summary>2013 oder 2016.</summary>
    public required int Year { get; init; }

    /// <summary>Produktkennung im Verteilweg, z.B. HomeStudentRetail.</summary>
    public required string ProductId { get; init; }

    /// <summary>Kennung des Verteilwegs, z.B. O15GA fuer 2013.</summary>
    public required string VersionTag { get; init; }

    /// <summary>CDN-Kennung der Programmfamilie.</summary>
    public required string CdnGuid { get; init; }

    /// <summary>Tag, an dem der Support endete - reine Anzeige.</summary>
    public required string EndOfSupport { get; init; }

    /// <summary>
    /// Zusatzkennung des Verteilwegs. Professional Plus 2013 wurde ueber das
    /// Home-Use-Programm ausgegeben und wird ohne diese Kennung abgewiesen.
    /// </summary>
    public string? SourceTag { get; init; }

    /// <summary>
    /// Hinweis, wenn eine Fassung erfahrungsgemaess nicht mehr verfuegbar ist.
    /// </summary>
    public string? CaveatDe { get; init; }

    public string? CaveatEn { get; init; }

    public string Title => Loc.Tr($"Office {Year} {EditionNameDe}", $"Office {Year} {EditionNameEn}");

    public string EditionNameDe => ProductId switch
    {
        "HomeStudentRetail" => "Home and Student",
        "HomeBusinessRetail" => "Home and Business",
        "ProfessionalRetail" => "Professional",
        "ProPlusRetail" => "Professional Plus",
        _ => ProductId
    };

    public string EditionNameEn => EditionNameDe;

    /// <summary>
    /// Adresse des Sicherungsmediums. Das Kuerzel hinter dem Rechnernamen ist
    /// eine Regionskennung; neben "db" liefern auch "pr", "sg" und "br"
    /// dieselbe Datei.
    /// </summary>
    public string ImageUrl(string language = DefaultImageLanguage)
        => $"https://officecdn.microsoft.com/db/{CdnGuid}/media/{language}/{ProductId}.img";

    /// <summary>
    /// Adresse des Click-to-Run-Starters. Genau diese Adresse benutzt auch
    /// setup.office.com im Hintergrund.
    /// </summary>
    /// <param name="clientEdition">32 oder 64.</param>
    public string SetupUrl(int clientEdition, string language = DefaultSetupLanguage)
    {
        var platform = clientEdition == 32 ? "X86" : "X64";
        var source = string.IsNullOrWhiteSpace(SourceTag) ? "" : $"&Source={SourceTag}";

        return "https://c2rsetup.officeapps.live.com/c2r/download.aspx"
             + $"?ProductreleaseID={ProductId}&platform={platform}"
             + $"&language={language}&version={VersionTag}{source}";
    }

    public string Url(LegacyDownloadKind kind, int clientEdition)
        => kind == LegacyDownloadKind.Image ? ImageUrl() : SetupUrl(clientEdition);

    public const string DefaultImageLanguage = "de-DE";
    public const string DefaultSetupLanguage = "de-de";
}

/// <summary>
/// Die feste Liste der alten Fassungen. Bewusst fest verdrahtet und ohne
/// Onlineabfrage: Fuer 2013 und 2016 gibt es keine Versionsuebersicht mehr,
/// die man sinnvoll abfragen koennte, und ein Aktualisierungsvergleich waere
/// bei abgekuendigten Fassungen ohnehin gegenstandslos.
/// </summary>
public static class LegacyOfficeReleases
{
    /// <summary>CDN-Kennung der Office-2016-Familie.</summary>
    public const string Guid2016 = "492350F6-3A01-4F97-B9C0-C7C6DDF67D60";

    /// <summary>CDN-Kennung der Office-2013-Familie.</summary>
    public const string Guid2013 = "39168D7E-077B-48E7-872C-B232C3E72675";

    private const string EndOfSupport2016 = "14.10.2025";
    private const string EndOfSupport2013 = "11.04.2023";

    private static readonly string[] Products =
    {
        "HomeStudentRetail", "HomeBusinessRetail", "ProfessionalRetail", "ProPlusRetail"
    };

    public static IReadOnlyList<LegacyOfficeRelease> All { get; } = Build();

    private static LegacyOfficeRelease[] Build()
    {
        var list = new List<LegacyOfficeRelease>();

        foreach (var product in Products)
        {
            list.Add(new LegacyOfficeRelease
            {
                Year = 2016,
                ProductId = product,
                VersionTag = "O16GA",
                CdnGuid = Guid2016,
                EndOfSupport = EndOfSupport2016
            });
        }

        foreach (var product in Products)
        {
            // Professional Plus 2013 wurde ueberwiegend als MSI-Fassung
            // ausgeliefert; als Click-to-Run gab es sie nur ueber das
            // Home-Use-Programm. Deshalb die Zusatzkennung und der Hinweis.
            var isProPlus = product == "ProPlusRetail";

            list.Add(new LegacyOfficeRelease
            {
                Year = 2013,
                ProductId = product,
                VersionTag = "O15GA",
                CdnGuid = Guid2013,
                EndOfSupport = EndOfSupport2013,
                SourceTag = isProPlus ? "O15HUP" : null,
                CaveatDe = isProPlus
                    ? "Diese Fassung wurde überwiegend als MSI ausgeliefert - der Verteilweg ist unsicher."
                    : null,
                CaveatEn = isProPlus
                    ? "This edition was mostly shipped as MSI - this distribution path is unreliable."
                    : null
            });
        }

        return list.ToArray();
    }

    /// <summary>
    /// Filtert nach Suchbegriffen - dieselbe Logik wie bei der Produktliste:
    /// mehrere durch Leerzeichen getrennte Begriffe muessen alle zutreffen.
    /// </summary>
    public static bool Matches(LegacyOfficeRelease release, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var haystack = string.Join(' ',
            release.Year.ToString(),
            release.EditionNameDe,
            release.ProductId,
            "Office");

        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Oeffnet eine Adresse im Standardbrowser. Bewusst ueber die Windows-Shell
    /// (UseShellExecute) - damit gilt genau die Zuordnung, die der Benutzer
    /// eingestellt hat, und der Browser bestimmt auch das Zielverzeichnis.
    /// Im Paketordner wird nichts abgelegt.
    /// </summary>
    public static bool OpenInBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

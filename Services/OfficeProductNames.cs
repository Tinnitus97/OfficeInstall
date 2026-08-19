using System;
using System.Collections.Generic;

namespace OfficeInstall.Services;

/// <summary>
/// Uebersetzt die Produktkennungen aus den Konfigurationsdateien
/// (&lt;Product ID="..."/&gt;) in die Bezeichnungen, die im Menue der alten
/// start.bat standen.
///
/// Steht eine Kennung nicht in der Liste, wird sie unveraendert angezeigt -
/// eine neue XML-Datei taucht also auch dann in der Auswahl auf, wenn hier
/// niemand etwas ergaenzt hat.
/// </summary>
public static class OfficeProductNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- Retail / Current ---
        ["HomeStudent2019Retail"]  = "Office 2019 Home und Student",
        ["HomeBusiness2019Retail"] = "Office 2019 Home und Business",
        ["HomeStudent2021Retail"]  = "Office 2021 Home und Student",
        ["HomeBusiness2021Retail"] = "Office 2021 Home und Business",
        ["Home2024Retail"]         = "Office 2024 Home",
        ["HomeStudent2024Retail"]  = "Office 2024 Home und Student",
        ["HomeBusiness2024Retail"] = "Office 2024 Home und Business",
        ["O365BusinessRetail"]     = "Microsoft 365 Business",
        ["O365ProPlusRetail"]      = "Microsoft 365 Apps for Enterprise",
        ["O365HomePremRetail"]     = "Microsoft 365 Single/Family",

        // --- Volumenlizenz ---
        ["Standard2019Volume"] = "Office 2019 Standard",
        ["ProPlus2019Volume"]  = "Office 2019 Pro Plus",
        ["Standard2021Volume"] = "Office 2021 Standard",
        ["ProPlus2021Volume"]  = "Office 2021 Pro Plus",
        ["Standard2024Volume"] = "Office 2024 Standard",
        ["ProPlus2024Volume"]  = "Office 2024 Pro Plus",

        // --- Einzelprodukte ---
        ["ProjectStd2019Volume"]  = "Project 2019 Standard",
        ["ProjectPro2019Volume"]  = "Project 2019 Professional",
        ["ProjectStd2021Volume"]  = "Project 2021 Standard",
        ["ProjectPro2021Volume"]  = "Project 2021 Professional",
        ["ProjectStd2024Volume"]  = "Project 2024 Standard",
        ["ProjectPro2024Volume"]  = "Project 2024 Professional",
        ["VisioStd2019Volume"]    = "Visio 2019 Standard",
        ["VisioPro2019Volume"]    = "Visio 2019 Professional",
        ["VisioStd2021Volume"]    = "Visio 2021 Standard",
        ["VisioPro2021Volume"]    = "Visio 2021 Professional",
        ["VisioStd2024Volume"]    = "Visio 2024 Standard",
        ["VisioPro2024Volume"]    = "Visio 2024 Professional",
    };

    /// <summary>Klartextname zur Produktkennung, sonst die Kennung selbst.</summary>
    public static string Display(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId)) return Loc.Tr("(unbekanntes Produkt)", "(unknown product)");
        return Names.TryGetValue(productId, out var name) ? name : productId;
    }

    /// <summary>true, wenn es sich um eine Volumenlizenz handelt.</summary>
    public static bool IsVolumeLicence(string? productId)
        => productId?.EndsWith("Volume", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Sortierwert fuer Microsoft 365 - steht immer ganz oben.</summary>
    public const int Microsoft365 = 9999;

    /// <summary>Rang fuer alles, was in keine der bekannten Stufen passt.</summary>
    public const int UnknownEdition = 6;

    /// <summary>
    /// Rang der Ausgabe innerhalb einer Office-Generation - bestimmt die
    /// Reihenfolge der Zeilen einer Generation untereinander:
    ///
    ///   1  Home / Home und Student   (Verbraucher, kleinste Ausstattung)
    ///   2  Home und Business
    ///   3  Pro Plus                  (auch Microsoft 365 Apps for Enterprise)
    ///   4  Business                  (Microsoft 365 Business)
    ///   5  Standard
    ///   6  alles Uebrige, z.B. Project und Visio
    ///
    /// Warum nicht alphabetisch? Dann stuende "Home und Business" vor "Home und
    /// Student", also die groessere Ausstattung vor der kleineren. Gewuenscht
    /// ist die Staffelung von klein nach gross.
    ///
    /// Die Reihenfolge der Abfragen ist wesentlich: "HomeBusiness2021Retail"
    /// enthaelt sowohl "Home" als auch "Business". Deshalb werden die
    /// zusammengesetzten Kennungen zuerst geprueft.
    /// </summary>
    public static int EditionRank(string? productId, string? displayName = null)
    {
        var kennung = productId ?? "";

        if (Has(kennung, "HomeStudent"))  return 1;
        if (Has(kennung, "HomeBusiness")) return 2;
        if (Has(kennung, "Home"))         return 1;   // Home2024Retail
        if (Has(kennung, "ProPlus"))      return 3;
        if (Has(kennung, "Business"))     return 4;   // O365BusinessRetail
        if (Has(kennung, "Standard"))     return 5;

        // Ersatzweise der Klartextname - fuer Kennungen, die hier nicht
        // hinterlegt sind, aber im Namen erkennbar bleiben.
        var name = displayName ?? "";

        if (Has(name, "Home und Student"))  return 1;
        if (Has(name, "Home und Business")) return 2;
        if (Has(name, "Home"))              return 1;
        if (Has(name, "Pro Plus"))          return 3;
        if (Has(name, "Business"))          return 4;
        if (Has(name, "Standard"))          return 5;

        return UnknownEdition;
    }

    private static bool Has(string text, string part)
        => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Das Erscheinungsjahr eines Produkts - fuer die Reihenfolge in der Liste:
    /// neuestes Office zuerst.
    ///
    /// Warum nicht einfach nach Namen sortieren? Alphabetisch steht
    /// "Office 2019 Home und Student" vor "Office 2024 Home", das aelteste
    /// Office also ganz oben. Genau das soll die Liste nicht tun.
    ///
    /// Microsoft 365 hat kein Jahr im Namen und gehoert trotzdem nach oben -
    /// es ist der fortlaufend gepflegte Stand. Dafuer steht
    /// <see cref="Microsoft365"/>.
    ///
    /// Gesucht wird zuerst in der Produktkennung ("HomeBusiness2024Retail"),
    /// ersatzweise im Anzeigenamen. Findet sich nichts, kommt 0 zurueck und der
    /// Eintrag landet unten - besser als ihn nach vorne zu raten.
    /// </summary>
    public static int ReleaseYear(string? productId, string? displayName = null)
    {
        if (Is365(productId) || Is365(displayName)) return Microsoft365;

        return FindYear(productId) ?? FindYear(displayName) ?? 0;
    }

    private static bool Is365(string? text)
        => text is not null
           && (text.StartsWith("O365", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Erste vierstellige Jahreszahl 20xx. Die Abgrenzung nach beiden Seiten
    /// verhindert Treffer mitten in einer laengeren Zahl.
    /// </summary>
    private static int? FindYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var treffer = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)20\d{2}(?!\d)");
        return treffer.Success && int.TryParse(treffer.Value, out var jahr) ? jahr : null;
    }
}

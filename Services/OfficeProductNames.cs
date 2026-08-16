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
}

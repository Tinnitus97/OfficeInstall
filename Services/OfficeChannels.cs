using System;
using System.Collections.Generic;
using System.Linq;

namespace OfficeInstall.Services;

/// <summary>
/// Eine Installationsbasis (Update-Kanal) von Office. Der Ordnername unterhalb
/// von x32 bzw. x64 entspricht genau dem Kanalnamen, den auch das Office
/// Deployment Tool verwendet.
///
/// Wichtig: Die Basen sind nicht mischbar. Aus PerpetualVL2021 laesst sich nur
/// installieren, was dort als XML liegt - deshalb wird der Katalog strikt je
/// Basis aufgebaut.
/// </summary>
public sealed class OfficeChannel
{
    public required string FolderName { get; init; }

    /// <summary>Anzeigename in der Oberflaeche.</summary>
    public required Func<string> Title { get; init; }

    /// <summary>
    /// Kennung des Kanals auf dem Microsoft-CDN. Damit laesst sich unter
    /// https://officecdn.microsoft.com/pr/&lt;GUID&gt;/Office/Data/VersionDescriptor.xml
    /// die aktuell online angebotene Version abfragen.
    /// </summary>
    public required string CdnGuid { get; init; }

    /// <summary>
    /// Reihenfolge in der Anzeige: neuestes Office zuerst.
    /// 1 Current (Microsoft 365), 2 Office 2024, 3 Office 2021, 4 Office 2019.
    /// Bewusst nicht die Reihenfolge der Ordnernamen - alphabetisch stuende
    /// 2019 vor 2024, und damit das aelteste Office ganz oben.
    /// </summary>
    public required int SortOrder { get; init; }
}

public static class OfficeChannels
{
    public const string Current = "Current";
    public const string PerpetualVL2019 = "PerpetualVL2019";
    public const string PerpetualVL2021 = "PerpetualVL2021";
    public const string PerpetualVL2024 = "PerpetualVL2024";

    public static IReadOnlyList<OfficeChannel> All { get; } = new List<OfficeChannel>
    {
        new()
        {
            FolderName = Current,
            SortOrder = 1,
            CdnGuid = "492350f6-3a01-4f97-b9c0-c7c6ddf67d60",
            Title = () => Loc.Tr("Current (Microsoft 365 und Retail)",
                                 "Current (Microsoft 365 and retail)"),
        },
        new()
        {
            FolderName = PerpetualVL2019,
            SortOrder = 4,
            CdnGuid = "f2e724c1-748f-4b47-8fb8-8e0d210e9208",
            Title = () => Loc.Tr("PerpetualVL2019 (Volumenlizenz 2019)",
                                 "PerpetualVL2019 (volume licence 2019)"),
        },
        new()
        {
            FolderName = PerpetualVL2021,
            SortOrder = 3,
            CdnGuid = "5030841d-c919-4594-8d2d-84ae4f96e58e",
            Title = () => Loc.Tr("PerpetualVL2021 (Volumenlizenz 2021)",
                                 "PerpetualVL2021 (volume licence 2021)"),
        },
        new()
        {
            FolderName = PerpetualVL2024,
            SortOrder = 2,
            CdnGuid = "7983bac0-e531-40cf-be00-fd24fe66619c",
            Title = () => Loc.Tr("PerpetualVL2024 (Volumenlizenz 2024)",
                                 "PerpetualVL2024 (volume licence 2024)"),
        },
    };

    public static OfficeChannel? ByFolderName(string? folderName)
        => folderName is null
            ? null
            : All.FirstOrDefault(c => string.Equals(c.FolderName, folderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Basisadresse des Kanals auf dem Microsoft-CDN.</summary>
    public static string CdnBaseUrl(OfficeChannel channel)
        => $"https://officecdn.microsoft.com/pr/{channel.CdnGuid}";
}

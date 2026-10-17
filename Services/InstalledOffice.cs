using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OfficeInstall.Services;

/// <summary>Wie ein gefundenes Office installiert wurde.</summary>
public enum InstalledOfficeKind
{
    /// <summary>Click-to-Run - so installiert auch das Deployment Tool.</summary>
    ClickToRun,

    /// <summary>Klassischer Windows Installer, Office 2010 bis 2016.</summary>
    Msi
}

/// <summary>Ein auf diesem Rechner gefundenes Office-Produkt.</summary>
public sealed class InstalledOfficeProduct
{
    public required InstalledOfficeKind Kind { get; init; }

    /// <summary>Anzeigename, z.B. "Microsoft Office Professional Plus 2016".</summary>
    public required string Name { get; init; }

    /// <summary>Click-to-Run: ProductReleaseId. MSI: Name des Uninstall-Schluessels.</summary>
    public string? ProductId { get; init; }

    public string? Version { get; init; }

    /// <summary>32 oder 64, sofern bekannt.</summary>
    public int? Architecture { get; init; }

    /// <summary>Nur Click-to-Run: der Kanal der Installation.</summary>
    public string? Channel { get; init; }
}

/// <summary>
/// Die Click-to-Run-Installation des Rechners.
///
/// Es gibt immer nur EINE: Alle Click-to-Run-Produkte teilen sich Version,
/// Kanal und Architektur. Ein Microsoft 365 in 32 Bit und ein Office 2021 in
/// 64 Bit nebeneinander geht deshalb nicht - und genauso wenig ein Office
/// aus "Current" neben einer Volumenlizenz aus "PerpetualVL2021".
/// </summary>
public sealed class ClickToRunInstallation
{
    public required IReadOnlyList<string> ProductIds { get; init; }
    public int? Architecture { get; init; }
    public string? Version { get; init; }

    /// <summary>Kanalname (Current, PerpetualVL2021, MonthlyEnterprise ...), oder null.</summary>
    public string? Channel { get; init; }

    public IEnumerable<InstalledOfficeProduct> Products => ProductIds.Select(id => new InstalledOfficeProduct
    {
        Kind = InstalledOfficeKind.ClickToRun,
        Name = OfficeProductNames.Display(id),
        ProductId = id,
        Version = Version,
        Architecture = Architecture,
        Channel = Channel
    });
}

/// <summary>Was an Office auf dem Rechner liegt.</summary>
public sealed class OfficeInventory
{
    public static OfficeInventory Empty { get; } = new();

    public ClickToRunInstallation? ClickToRun { get; init; }

    public IReadOnlyList<InstalledOfficeProduct> Msi { get; init; } = Array.Empty<InstalledOfficeProduct>();
}

/// <summary>Ein gefundenes Office, das der geplanten Installation im Weg steht.</summary>
public sealed class OfficeConflict
{
    public required InstalledOfficeProduct Product { get; init; }

    /// <summary>Kurze Begruendung fuer die Anzeige.</summary>
    public required string Reason { get; init; }
}

/// <summary>Alle Konflikte zu einer geplanten Installation.</summary>
public sealed class OfficeConflictReport
{
    public static OfficeConflictReport None { get; } = new();

    public IReadOnlyList<OfficeConflict> ClickToRun { get; init; } = Array.Empty<OfficeConflict>();
    public IReadOnlyList<OfficeConflict> Msi { get; init; } = Array.Empty<OfficeConflict>();

    public bool HasClickToRun => ClickToRun.Count > 0;
    public bool HasMsi => Msi.Count > 0;
    public bool Any => HasClickToRun || HasMsi;

    public IEnumerable<OfficeConflict> All => ClickToRun.Concat(Msi);

    /// <summary>
    /// Zeilen fuer Dialog und Protokoll - je Installation eine Kopfzeile mit
    /// Art, Architektur, Kanal und Grund, darunter die Produkte:
    ///
    ///   • Click-to-Run, 32 Bit, Current - andere Architektur (32 statt 64 Bit):
    ///      Microsoft 365 Apps for Enterprise, Visio Professional
    ///
    /// Zusammengefasst, weil die Click-to-Run-Produkte ohnehin nur gemeinsam
    /// gehen - der Grund je Produkt wiederholt, waere nur laenger.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = new List<string>();
            foreach (var group in All.GroupBy(c => (c.Product.Kind, c.Product.Architecture, c.Product.Channel, c.Reason)))
            {
                var (kind, bits, channel, reason) = group.Key;
                var details = new List<string> { kind == InstalledOfficeKind.ClickToRun ? "Click-to-Run" : "MSI" };
                if (bits is { } b) details.Add(Loc.Tr($"{b} Bit", $"{b}-bit"));
                if (!string.IsNullOrWhiteSpace(channel)) details.Add(channel);

                lines.Add($"• {string.Join(", ", details)} - {reason}:");
                lines.Add("   " + string.Join(", ", group.Select(c => c.Product.Name)));
            }
            return lines;
        }
    }
}

/// <summary>
/// Findet installiertes Office und entscheidet, was davon sich mit der
/// geplanten Installation nicht vertraegt.
///
/// WAS ALS NICHT KOMPATIBEL GILT:
///
///   MSI           - jedes Office 2010 bis 2016 aus dem Windows Installer.
///                   Das deckt sich mit &lt;RemoveMSI/&gt;, das ohnehin in allen
///                   Konfigurationsdateien des Pakets steht: Wer das Paket
///                   gebaut hat, wollte MSI-Office beim Installieren loswerden.
///                   Office 2007 bleibt aussen vor - das kann RemoveMSI nicht
///                   entfernen, und eine Liste, die "wird entfernt" verspricht,
///                   muss das auch halten.
///
///   Click-to-Run  - die vorhandene Installation, wenn ihre ARCHITEKTUR oder
///                   ihre KANALFAMILIE nicht zur geplanten passt. Weil sich
///                   alle Click-to-Run-Produkte eine Installation teilen,
///                   betrifft das dann immer alle Produkte zugleich.
///
/// Kanalfamilie heisst: Current, Monthly Enterprise, Semi-Annual und die
/// Vorschaukanaele lassen sich ineinander umstellen - ein Office aus "Current"
/// passt also zu einem Microsoft 365 auf "MonthlyEnterprise". Die
/// Volumenlizenz-Kanaele PerpetualVL2019, 2021 und 2024 stehen dagegen jeder
/// fuer sich.
/// </summary>
public static class InstalledOffice
{
    /// <summary>Familie der Retail- und Microsoft-365-Kanaele.</summary>
    public const string RetailFamily = OfficeChannels.Current;

    /// <summary>
    /// Kennungen der Kanaele auf dem CDN. Die vier Installationsbasen kommen
    /// aus <see cref="OfficeChannels"/>; die uebrigen sind Kanaele, auf denen
    /// ein vorhandenes Microsoft 365 stehen kann, ohne dass das Paket sie
    /// selbst anbietet.
    /// </summary>
    private static readonly Dictionary<string, string> ChannelGuids =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["64256afe-f5d9-4f86-8936-8840a6a4f5be"] = "CurrentPreview",
            ["5440fd1f-7ecb-4221-8110-145efaa6372f"] = "BetaChannel",
            ["55336b82-a18d-4dd6-b5f6-9e5095c314a6"] = "MonthlyEnterprise",
            ["7ffbc6bf-bc32-4f92-8982-f9dd17fd3114"] = "SemiAnnual",
            ["b8f9b850-328d-4355-9145-c59439a0c4cf"] = "SemiAnnualPreview",
        }
        .Concat(OfficeChannels.All.Select(c => new KeyValuePair<string, string>(c.CdnGuid, c.FolderName)))
        .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly Regex GuidPattern =
        new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);

    /// <summary>
    /// Kanalname aus einer CDN-Adresse wie
    /// "http://officecdn.microsoft.com/pr/492350f6-.../" - oder null, wenn die
    /// Kennung unbekannt ist.
    /// </summary>
    public static string? ChannelFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = GuidPattern.Match(url);
        return match.Success && ChannelGuids.TryGetValue(match.Value, out var name) ? name : null;
    }

    /// <summary>
    /// Kanalfamilie: der Kanal selbst bei den Volumenlizenzen, sonst
    /// <see cref="RetailFamily"/>. Null bleibt null.
    /// </summary>
    public static string? ChannelFamily(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        return channel.StartsWith("PerpetualVL", StringComparison.OrdinalIgnoreCase)
            ? OfficeChannels.ByFolderName(channel)?.FolderName ?? channel
            : RetailFamily;
    }

    /// <summary>
    /// Kanalfamilie aus den Produkten - fuer den Fall, dass der Kanal selbst
    /// nicht zu ermitteln war. Eine Volumenlizenz mit Jahr laesst sich nur aus
    /// ihrem eigenen Kanal installieren (ProPlus2019Volume nur aus
    /// PerpetualVL2019); alles andere steht auf einem Retail-Kanal.
    /// </summary>
    public static string? ChannelFamilyFromProducts(IEnumerable<string> productIds)
    {
        string? family = null;
        foreach (var id in productIds)
        {
            if (OfficeProductNames.IsVolumeLicence(id)
                && OfficeProductNames.ReleaseYear(id) is var year and >= 2019 and < OfficeProductNames.Microsoft365)
                return $"PerpetualVL{year}";
            family ??= RetailFamily;
        }
        return family;
    }

    /// <summary>
    /// Baut die Click-to-Run-Installation aus den Werten unter
    /// HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration.
    /// Ohne ProductReleaseIds gibt es keine Installation - dann bleibt nur ein
    /// Rest in der Registry, und der stoert nicht.
    /// </summary>
    public static ClickToRunInstallation? ClickToRunFromValues(
        string? productReleaseIds, string? platform, string? version,
        string? updateChannel, string? cdnBaseUrl)
    {
        var ids = (productReleaseIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return null;

        int? architecture = platform?.Trim().ToLowerInvariant() switch
        {
            "x64" => 64,
            "x86" => 32,
            _ => null
        };

        // UpdateChannel gewinnt: Es haelt fest, wohin die Installation
        // umgestellt wurde. CDNBaseUrl ist der Kanal der Erstinstallation.
        var channel = ChannelFromUrl(updateChannel) ?? ChannelFromUrl(cdnBaseUrl);

        return new ClickToRunInstallation
        {
            ProductIds = ids,
            Architecture = architecture,
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            Channel = channel
        };
    }

    /// <summary>
    /// Wertet einen Eintrag unter ...\Windows\CurrentVersion\Uninstall aus.
    /// Gibt ein MSI-Office zurueck, oder null, wenn der Eintrag keins ist.
    ///
    /// Das Erkennungsmerkmal ist der "Office Setup Controller" in der
    /// Deinstallationszeile. Ueber ihn laufen alle MSI-Suiten von Office 2007
    /// bis 2016 samt Visio und Project - und nur die. Bewusst nicht erfasst:
    /// die versteckten Einzelkomponenten (SystemComponent=1), die
    /// Access-Datenbankengine und die Interop-Assemblies; die werden ueber
    /// msiexec entfernt und vertragen sich mit Click-to-Run.
    ///
    /// Von den Setup-Controller-Eintraegen zaehlen nur die Hauptversionen 14
    /// bis 16 (Office 2010, 2013, 2016): Nur die entfernt RemoveMSI.
    /// </summary>
    public static InstalledOfficeProduct? MsiFromUninstallEntry(
        string keyName, string? displayName, string? displayVersion,
        string? uninstallString, int systemComponent, int architecture)
    {
        if (systemComponent == 1) return null;
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        if (uninstallString is null
            || !uninstallString.Contains("Office Setup Controller", StringComparison.OrdinalIgnoreCase))
            return null;

        if (MajorVersion(displayVersion, uninstallString) is not (>= 14 and <= 16)) return null;

        return new InstalledOfficeProduct
        {
            Kind = InstalledOfficeKind.Msi,
            Name = displayName.Trim(),
            ProductId = keyName,
            Version = string.IsNullOrWhiteSpace(displayVersion) ? null : displayVersion.Trim(),
            Architecture = architecture
        };
    }

    /// <summary>
    /// Hauptversion eines MSI-Office: aus DisplayVersion ("16.0.4266.1001"),
    /// ersatzweise aus dem Pfad des Setup Controllers ("...\OFFICE16\...").
    /// </summary>
    private static int? MajorVersion(string? displayVersion, string uninstallString)
    {
        if (Version.TryParse(displayVersion?.Trim(), out var version)) return version.Major;

        var match = Regex.Match(uninstallString, @"\\OFFICE(\d{2})\\", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Was vom vorhandenen Office der geplanten Installation im Weg steht.
    /// </summary>
    /// <param name="inventory">Der Bestand des Rechners.</param>
    /// <param name="targetEdition">32 oder 64.</param>
    /// <param name="targetChannel">Installationsbasis, z.B. "PerpetualVL2021".</param>
    public static OfficeConflictReport FindConflicts(OfficeInventory inventory, int targetEdition, string targetChannel)
    {
        var clickToRun = new List<OfficeConflict>();
        var targetFamily = ChannelFamily(targetChannel);

        if (inventory.ClickToRun is { } installation)
        {
            var family = ChannelFamily(installation.Channel)
                         ?? ChannelFamilyFromProducts(installation.ProductIds);

            string? reason = null;

            if (installation.Architecture is { } bits && bits != targetEdition)
            {
                reason = Loc.Tr($"andere Architektur ({bits} statt {targetEdition} Bit)",
                                $"different architecture ({bits}- instead of {targetEdition}-bit)");
            }
            else if (family is not null && targetFamily is not null
                     && !string.Equals(family, targetFamily, StringComparison.OrdinalIgnoreCase))
            {
                reason = Loc.Tr($"anderer Kanal ({installation.Channel ?? family} statt {targetChannel})",
                                $"different channel ({installation.Channel ?? family} instead of {targetChannel})");
            }

            // Alle Produkte der Installation gemeinsam - sie lassen sich nur
            // zusammen entfernen, und keins davon bliebe danach lauffaehig.
            if (reason is not null)
                clickToRun.AddRange(installation.Products.Select(p => new OfficeConflict { Product = p, Reason = reason }));
        }

        var msi = inventory.Msi
            .Select(p => new OfficeConflict
            {
                Product = p,
                Reason = Loc.Tr("verträgt sich nicht mit Click-to-Run",
                                "incompatible with Click-to-Run")
            })
            .ToList();

        return new OfficeConflictReport { ClickToRun = clickToRun, Msi = msi };
    }

    /// <summary>
    /// Liest den Bestand aus der Registry. Ausserhalb von Windows gibt es
    /// nichts zu finden - dann kommt ein leerer Bestand zurueck.
    /// Fehler beim Lesen werden NICHT verschluckt: Der Aufrufer soll wissen,
    /// dass er nichts weiss, statt "kein Office gefunden" zu melden.
    /// </summary>
    public static OfficeInventory Scan()
        => OperatingSystem.IsWindows() ? ScanRegistry() : OfficeInventory.Empty;

    private const string ClickToRunKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [SupportedOSPlatform("windows")]
    private static OfficeInventory ScanRegistry()
    {
        // 64-Bit-Ansicht zuerst: Dort steht Click-to-Run auf einem 64-Bit-
        // Windows immer, egal in welcher Architektur Office installiert ist.
        // Die 32-Bit-Ansicht (WOW6432Node) liefert die MSI-Fassungen in 32 Bit.
        var views = Environment.Is64BitOperatingSystem
            ? new[] { (View: RegistryView.Registry64, Bits: 64), (View: RegistryView.Registry32, Bits: 32) }
            : new[] { (View: RegistryView.Registry32, Bits: 32) };

        ClickToRunInstallation? clickToRun = null;
        var msi = new List<InstalledOfficeProduct>();

        foreach (var (view, bits) in views)
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);

            if (clickToRun is null)
            {
                using var c2r = hklm.OpenSubKey(ClickToRunKey);
                if (c2r is not null)
                {
                    clickToRun = ClickToRunFromValues(
                        c2r.GetValue("ProductReleaseIds") as string,
                        c2r.GetValue("Platform") as string,
                        c2r.GetValue("VersionToReport") as string,
                        c2r.GetValue("UpdateChannel") as string,
                        c2r.GetValue("CDNBaseUrl") as string);
                }
            }

            using var uninstall = hklm.OpenSubKey(UninstallKey);
            if (uninstall is null) continue;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry is null) continue;

                var product = MsiFromUninstallEntry(
                    name,
                    entry.GetValue("DisplayName") as string,
                    entry.GetValue("DisplayVersion") as string,
                    entry.GetValue("UninstallString") as string,
                    entry.GetValue("SystemComponent") is int flag ? flag : 0,
                    bits);

                if (product is not null
                    && !msi.Any(m => m.ProductId == product.ProductId && m.Architecture == product.Architecture))
                    msi.Add(product);
            }
        }

        return new OfficeInventory { ClickToRun = clickToRun, Msi = msi };
    }
}

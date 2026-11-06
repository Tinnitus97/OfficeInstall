using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OfficeInstall.Services;

/// <summary>
/// Die Konfigurationsvorlagen, die beim Bauen in die EXE eingebettet werden -
/// der Inhalt von package\ im Repository: x32\, x64\ und
/// Versionscheck\Version.txt.
///
/// WARUM EINGEBAUT:
/// Wer nur die EXE hat - selbst gebaut oder heruntergeladen -, stand vorher
/// vor einer leeren Liste. Die Vorlagen kamen ausschliesslich ueber die
/// Veroeffentlichung der Konfigurationen, und die braucht Internet und einen
/// gepflegten Eintrag in update.json. Fehlte eins von beidem, ging es nicht
/// weiter. Mit den eingebauten Vorlagen ist die EXE fuer sich allein
/// vollstaendig: Sie packt aus, was sie mitbringt, ohne Netz.
///
/// Neuere Vorlagen kommen weiterhin ueber die Veroeffentlichung - die EXE
/// vergleicht wie gewohnt den Paketstand und bietet sie an.
///
/// Die Ressourcen heissen "Vorlagen/x64/Current/datei.xml" usw. (siehe
/// OfficeInstall.csproj). Je nach Betriebssystem, auf dem gebaut wurde,
/// stehen darin Schraeg- oder Rueckstriche; beides wird hier gleich behandelt.
/// </summary>
public static class EmbeddedTemplates
{
    public const string ResourcePrefix = "Vorlagen/";

    private const string VersionFile = "Versionscheck/Version.txt";

    /// <summary>Die Ressourcen der Vorlagen: Ressourcenname und Pfad im Paket.</summary>
    public static IReadOnlyList<(string Resource, string PackagePath)> List(Assembly assembly)
        => assembly.GetManifestResourceNames()
            .Select(name => (Resource: name, Normalized: name.Replace('\\', '/')))
            .Where(r => r.Normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(r => (r.Resource, r.Normalized[ResourcePrefix.Length..]))
            .OrderBy(r => r.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>true, wenn die EXE Vorlagen mitbringt.</summary>
    public static bool IsAvailable(Assembly assembly)
        => List(assembly).Any(r => r.PackagePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>Wie viele Vorlagen eingebaut sind.</summary>
    public static int Count(Assembly assembly)
        => List(assembly).Count(r => r.PackagePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Paketstand der eingebauten Vorlagen ("69", "16.08.2026"), oder null,
    /// wenn keine Versionsdatei eingebaut ist.
    /// </summary>
    public static (string Version, string Date)? ReadVersion(Assembly assembly)
    {
        try
        {
            var resource = List(assembly)
                .FirstOrDefault(r => r.PackagePath.Equals(VersionFile, StringComparison.OrdinalIgnoreCase))
                .Resource;
            if (resource is null) return null;

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) return null;

            using var reader = new StreamReader(stream);
            var parts = reader.ReadToEnd().Trim().Split('|');
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) return null;

            return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "");
        }
        catch { return null; }
    }

    /// <summary>
    /// Packt die Vorlagen in den Paketordner aus. Geht durch denselben
    /// abgesicherten Weg wie configs.zip (<see cref="UpdateService.ApplyEntries"/>):
    /// nur XML unter x32\ und x64\ sowie die Versionsdatei, nichts ausserhalb
    /// des Paketordners. Der Offline-Bestand bleibt unberuehrt.
    /// </summary>
    public static UpdateService.ConfigApplyResult Extract(Assembly assembly, string rootFolder)
    {
        var entries = List(assembly)
            .Select(r => (r.PackagePath, (Func<Stream>)(() =>
                assembly.GetManifestResourceStream(r.Resource)
                ?? throw new InvalidOperationException($"Ressource fehlt: {r.Resource}"))));

        return UpdateService.ApplyEntries(entries, rootFolder);
    }
}

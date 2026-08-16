using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OfficeInstall.Services;

/// <summary>
/// Eine eingelesene Konfigurationsdatei des Office Deployment Tools -
/// also genau eine installierbare Variante.
/// </summary>
public sealed class OfficeConfiguration
{
    /// <summary>Vollstaendiger Pfad der XML-Datei.</summary>
    public required string FilePath { get; init; }

    /// <summary>Ordner der Installationsbasis, in dem die Datei liegt.</summary>
    public required string BaseFolder { get; init; }

    /// <summary>Name der Installationsbasis (Current, PerpetualVL2019, ...).</summary>
    public required string ChannelFolderName { get; init; }

    /// <summary>x32 oder x64 - der Ordner eine Ebene darueber.</summary>
    public required string ArchitectureFolderName { get; init; }

    /// <summary>Produktkennung aus &lt;Product ID="..."/&gt;.</summary>
    public string? ProductId { get; init; }

    /// <summary>32 oder 64 aus OfficeClientEdition.</summary>
    public int? ClientEdition { get; init; }

    /// <summary>Kanal aus dem Attribut Channel, sofern gesetzt.</summary>
    public string? DeclaredChannel { get; init; }

    /// <summary>Sprachkennung des ersten Language-Eintrags.</summary>
    public string? Language { get; init; }

    /// <summary>true, wenn die Datei nicht gelesen oder ausgewertet werden konnte.</summary>
    public bool IsBroken { get; init; }

    public string? ErrorMessage { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public string DisplayName => OfficeProductNames.Display(ProductId);

    /// <summary>
    /// Liest eine Konfigurationsdatei ein. Eine kaputte oder fremde XML-Datei
    /// fuehrt nicht zu einer Ausnahme, sondern zu einem Eintrag mit IsBroken -
    /// der Rest des Katalogs bleibt benutzbar.
    /// </summary>
    public static OfficeConfiguration Load(string filePath)
    {
        var baseFolder = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
        var channelName = Path.GetFileName(baseFolder);
        var architecture = Path.GetFileName(Path.GetDirectoryName(baseFolder) ?? "");

        try
        {
            var document = XDocument.Load(filePath);
            var add = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Add");
            var product = add?.Elements().FirstOrDefault(e => e.Name.LocalName == "Product");
            var language = product?.Elements().FirstOrDefault(e => e.Name.LocalName == "Language");

            int? edition = null;
            if (int.TryParse((string?)add?.Attribute("OfficeClientEdition"), out var parsedEdition))
                edition = parsedEdition;

            return new OfficeConfiguration
            {
                FilePath = Path.GetFullPath(filePath),
                BaseFolder = baseFolder,
                ChannelFolderName = channelName,
                ArchitectureFolderName = architecture,
                ProductId = (string?)product?.Attribute("ID"),
                ClientEdition = edition,
                DeclaredChannel = (string?)add?.Attribute("Channel"),
                Language = (string?)language?.Attribute("ID"),
                IsBroken = product is null,
                ErrorMessage = product is null
                    ? Loc.Tr("Kein <Product ID=...> in der Datei gefunden.",
                             "No <Product ID=...> found in the file.")
                    : null
            };
        }
        catch (Exception ex)
        {
            return new OfficeConfiguration
            {
                FilePath = Path.GetFullPath(filePath),
                BaseFolder = baseFolder,
                ChannelFolderName = channelName,
                ArchitectureFolderName = architecture,
                IsBroken = true,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Schreibt eine Kopie der Konfiguration mit gesetztem SourcePath.
    ///
    /// WICHTIG - SourcePath allein genuegt nicht: Ohne Versionsangabe ermittelt
    /// das Deployment Tool beim CDN, welcher Build fuer den Kanal gerade der
    /// aktuelle ist, und will genau den installieren. Liegt vor Ort ein
    /// aelterer Stand, laedt es den neuen aus dem Internet - der Offline-Ordner
    /// wird dann faktisch ignoriert.
    ///
    /// Deshalb wird beim Installieren zusaetzlich die tatsaechlich vorliegende
    /// Version festgeschrieben (und dazu der Kanal, wie von Microsoft
    /// empfohlen). Erst damit ist "offline" wirklich offline.
    ///
    /// Beim Herunterladen bleibt <paramref name="pinnedVersion"/> bewusst leer -
    /// dort soll ja gerade der neueste Stand geholt werden.
    ///
    /// ZUM KANAL: Wird <paramref name="channelName"/> angegeben, wird er
    /// gesetzt - auch dann, wenn in der Vorlage bereits ein anderer steht.
    /// Massgeblich ist der Ordner, in dem die Datei liegt; dort liegen die
    /// Quelldateien genau dieser Installationsbasis. Steht in einer Datei unter
    /// PerpetualVL2019 versehentlich Channel="PerpetualVL2021", lehnt das
    /// Deployment Tool die Installation ab:
    /// "This product can't be installed on the selected update channel."
    /// </summary>
    public static void WriteWithSourcePath(string sourceFile, string targetFile, string sourcePath,
                                           Version? pinnedVersion = null, string? channelName = null)
    {
        var document = XDocument.Load(sourceFile);
        var add = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Add")
                  ?? throw new InvalidOperationException("Die Konfigurationsdatei enthält kein <Add>-Element.");

        add.SetAttributeValue("SourcePath", sourcePath);

        if (!string.IsNullOrWhiteSpace(channelName))
            add.SetAttributeValue("Channel", channelName);

        if (pinnedVersion is not null)
            add.SetAttributeValue("Version", pinnedVersion.ToString());

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetFile));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        document.Save(targetFile);
    }

    /// <summary>
    /// Prueft, ob der in der Datei angegebene Kanal zum Ordner passt.
    /// Ein leeres Attribut gilt als passend - dann wird der Ordner ergaenzt.
    /// </summary>
    public static bool ChannelMatchesFolder(string? declaredChannel, string folderName)
        => string.IsNullOrWhiteSpace(declaredChannel)
           || string.Equals(declaredChannel, folderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Liest die festgeschriebene Version einer Datei aus.</summary>
    public static string? ReadVersion(string file)
    {
        try
        {
            var document = XDocument.Load(file);
            var add = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Add");
            return (string?)add?.Attribute("Version");
        }
        catch { return null; }
    }

    /// <summary>Liest den Kanal einer Datei aus.</summary>
    public static string? ReadChannel(string file)
    {
        try
        {
            var document = XDocument.Load(file);
            var add = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Add");
            return (string?)add?.Attribute("Channel");
        }
        catch { return null; }
    }

    /// <summary>
    /// Schreibt eine Kopie ohne SourcePath. Zusammen mit einem Arbeitsordner,
    /// in dem keine Office-Quelldateien liegen, erzwingt das die Installation
    /// aus dem Internet.
    ///
    /// Der Kanal wird auch hier auf den Ordner gesetzt - ein falscher Kanal in
    /// der Vorlage laesst die Installation genauso scheitern wie offline, nur
    /// dass der Fehler dann erst nach dem Herunterladen kommt.
    /// </summary>
    public static void WriteWithoutSourcePath(string sourceFile, string targetFile,
                                              string? channelName = null)
    {
        var document = XDocument.Load(sourceFile);
        var add = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Add")
                  ?? throw new InvalidOperationException("Die Konfigurationsdatei enthält kein <Add>-Element.");

        add.Attribute("SourcePath")?.Remove();

        if (!string.IsNullOrWhiteSpace(channelName))
            add.SetAttributeValue("Channel", channelName);

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetFile));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        document.Save(targetFile);
    }

    /// <summary>
    /// Baut aus mehreren Konfigurationsdateien einer Installationsbasis EINE
    /// Datei zum Herunterladen. Das Deployment Tool erlaubt mehrere
    /// &lt;Product&gt;-Eintraege in einem &lt;Add&gt; - damit genuegt ein einziger
    /// Durchlauf je Basis statt einem je Produkt.
    ///
    /// Die Datei enthaelt bewusst nur &lt;Add&gt;: Angaben wie RemoveMSI, Display
    /// oder AppSettings betreffen die Installation und haben beim Herunterladen
    /// nichts zu suchen.
    /// </summary>
    public static int WriteMergedDownloadConfig(
        IEnumerable<string> sourceFiles, string targetFile, string sourcePath,
        int clientEdition, string? channelName)
    {
        var add = new XElement("Add",
            new XAttribute("OfficeClientEdition", clientEdition.ToString()),
            new XAttribute("SourcePath", sourcePath));

        // Der Ordnername IST der Kanal - damit ist die Datei eindeutig, auch
        // wenn eine der Vorlagen kein Channel-Attribut hatte.
        if (!string.IsNullOrWhiteSpace(channelName))
            add.Add(new XAttribute("Channel", channelName));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var file in sourceFiles)
        {
            try
            {
                var document = XDocument.Load(file);
                var products = document.Root?
                    .Elements().FirstOrDefault(e => e.Name.LocalName == "Add")?
                    .Elements().Where(e => e.Name.LocalName == "Product");

                foreach (var product in products ?? Enumerable.Empty<XElement>())
                {
                    var id = (string?)product.Attribute("ID");
                    if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;

                    add.Add(new XElement(product));
                    count++;
                }
            }
            catch
            {
                // Eine unlesbare Datei darf die uebrigen nicht mitreissen.
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetFile));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        new XDocument(new XElement("Configuration", add)).Save(targetFile);
        return count;
    }

    /// <summary>Zaehlt die Produkte in einer Konfigurationsdatei.</summary>
    public static int CountProducts(string file)
    {
        try
        {
            var document = XDocument.Load(file);
            return document.Root?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Add")?
                .Elements().Count(e => e.Name.LocalName == "Product") ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>Liest den SourcePath einer Datei aus - fuer die Kontrolle im Test.</summary>
    public static string? ReadSourcePath(string file)
    {
        try
        {
            var document = XDocument.Load(file);
            var add = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Add");
            return (string?)add?.Attribute("SourcePath");
        }
        catch { return null; }
    }
}

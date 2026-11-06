using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OfficeInstall.Services;

namespace OfficeTests;

internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  [OK]   {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail is null ? "" : $"  -> {detail}")}"); }
    }

    private static void WriteConfig(string path, string productId, int edition, string? channel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var channelAttribute = channel is null ? "" : $" Channel=\"{channel}\"";
        File.WriteAllText(path, $"""
            <Configuration>
              <Add OfficeClientEdition="{edition}"{channelAttribute} AllowCdnFallback="FALSE">
                <Product ID="{productId}">
                  <Language ID="de-de" />
                </Product>
              </Add>
              <Property Name="AUTOACTIVATE" Value="1" />
              <RemoveMSI />
              <Display Level="Full" AcceptEULA="TRUE" />
            </Configuration>
            """);
    }

    /// <summary>
    /// Lesehilfen fuer die erzeugten Konfigurationsdateien. Das Programm selbst
    /// braucht sie nicht - sie dienen nur der Kontrolle hier.
    /// </summary>
    private static class TestXml
    {
        private static XElement? Add(string file)
        {
            try { return XDocument.Load(file).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Add"); }
            catch { return null; }
        }

        public static string? ReadChannel(string file) => (string?)Add(file)?.Attribute("Channel");

        public static string? ReadSourcePath(string file) => (string?)Add(file)?.Attribute("SourcePath");

        public static int CountProducts(string file)
            => Add(file)?.Elements().Count(e => e.Name.LocalName == "Product") ?? 0;

        public static bool HasRemoveMsi(string file)
        {
            try { return XDocument.Load(file).Root?.Elements().Any(e => e.Name.LocalName == "RemoveMSI") == true; }
            catch { return false; }
        }
    }

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "office-test-" + Guid.NewGuid().ToString("N"));

        // ---- Paketordner nachbauen -------------------------------------
        WriteConfig(Path.Combine(root, "x64", "Current", "configuration_Student_2019.xml"), "HomeStudent2019Retail", 64, null);
        WriteConfig(Path.Combine(root, "x64", "Current", "configuration_AppsBusiness.xml"), "O365BusinessRetail", 64, "Current");
        WriteConfig(Path.Combine(root, "x64", "PerpetualVL2021", "configuration_Standard_2021.xml"), "Standard2021Volume", 64, "PerpetualVL2021");
        WriteConfig(Path.Combine(root, "x64", "PerpetualVL2024", "configuration_Pro_Plus_2024.xml"), "ProPlus2024Volume", 64, "PerpetualVL2024");
        WriteConfig(Path.Combine(root, "x32", "Current", "configuration_Student_2019.xml"), "HomeStudent2019Retail", 32, null);

        // PerpetualVL2019 existiert als Ordner, enthaelt aber keine XML
        Directory.CreateDirectory(Path.Combine(root, "x64", "PerpetualVL2019"));

        // kaputte Datei - darf den Katalog nicht sprengen
        Touch(Path.Combine(root, "x64", "Current", "configuration_kaputt.xml"), "<Configuration><Add>");

        // setup.exe: in einer Basis vorhanden, sonst im Wurzelordner
        Touch(Path.Combine(root, "x64", "Current", "setup.exe"));
        Touch(Path.Combine(root, "setup.exe"));

        // Offline-Quelldateien nur fuer PerpetualVL2021 (x64)
        var data2021 = Path.Combine(root, "x64", "PerpetualVL2021", "Office", "Data");
        Directory.CreateDirectory(Path.Combine(data2021, "16.0.17928.20216"));
        Touch(Path.Combine(data2021, "16.0.17928.20216", "i640.cab"), new string('x', 2048));
        Touch(Path.Combine(data2021, "v64.cab"), new string('x', 1024));

        // Paketversion
        Touch(Path.Combine(root, "Versionscheck", "Version.txt"), "69|13.08.2026");

        Console.WriteLine("== Katalog ==");
        var catalog = OfficeCatalog.Scan(root);

        Check("beide Architektur-Ordner erkannt",
            catalog.ArchitectureFolders.Count == 2, string.Join(",", catalog.ArchitectureFolders));
        Check("Basis ohne XML wird als leer gemeldet",
            catalog.EmptyBases.Any(b => b.Contains("PerpetualVL2019")), string.Join(",", catalog.EmptyBases));
        Check("nur Basen mit XML liefern Eintraege",
            catalog.Entries.All(e => e.ChannelFolderName != "PerpetualVL2019"));

        var x64 = catalog.ForArchitecture(64).ToList();
        var x32 = catalog.ForArchitecture(32).ToList();
        Check("x64 hat 5 Eintraege (inkl. kaputter Datei)", x64.Count == 5, x64.Count.ToString());
        Check("x32 hat 1 Eintrag", x32.Count == 1, x32.Count.ToString());

        var student = x64.First(e => e.Configuration.FileName == "configuration_Student_2019.xml");
        Check("Produktkennung wird in Klartext uebersetzt",
            student.DisplayName == "Office 2019 Home und Student", student.DisplayName);
        Check("Basis wird aus dem Ordner abgeleitet",
            student.Channel?.FolderName == "Current", student.Channel?.FolderName);
        Check("Architektur kommt aus der XML", student.ClientEdition == 64, student.ClientEdition.ToString());

        var x32Student = x32.Single();
        Check("x32-Eintrag meldet 32 Bit", x32Student.ClientEdition == 32, x32Student.ClientEdition.ToString());

        var vl2021 = x64.First(e => e.ChannelFolderName == "PerpetualVL2021");
        Check("Volumenlizenz wird als solche erkannt",
            OfficeProductNames.IsVolumeLicence(vl2021.Configuration.ProductId));
        Check("Kanal-GUID fuer PerpetualVL2021 stimmt",
            vl2021.Channel?.CdnGuid == "5030841d-c919-4594-8d2d-84ae4f96e58e", vl2021.Channel?.CdnGuid);

        var broken = x64.First(e => e.Configuration.FileName == "configuration_kaputt.xml");
        Check("kaputte XML wird markiert statt zu sprengen", broken.Configuration.IsBroken);
        Check("kaputte XML ist nicht installierbar", !broken.CanInstall);

        Console.WriteLine("\n== setup.exe finden ==");
        Check("setup.exe aus dem Ordner der Basis",
            student.SetupExecutable == Path.Combine(root, "x64", "Current", "setup.exe"), student.SetupExecutable);
        Check("Rueckfall auf setup.exe im Wurzelordner",
            vl2021.SetupExecutable == Path.Combine(root, "setup.exe"), vl2021.SetupExecutable);

        var noSetupRoot = Path.Combine(root, "leer");
        Directory.CreateDirectory(noSetupRoot);
        Check("ohne setup.exe kommt null",
            OfficeCatalog.FindSetupExecutable(noSetupRoot, noSetupRoot, noSetupRoot) is null);

        Console.WriteLine("\n== Offline-Erkennung ==");
        Check("Offline-Dateien bei PerpetualVL2021 gefunden", vl2021.Offline.Available);
        Check("lokale Version wird gelesen",
            vl2021.Offline.LatestVersion?.ToString() == "16.0.17928.20216",
            vl2021.Offline.LatestVersion?.ToString());
        Check("passende Cab-Datei erkannt", vl2021.Offline.HasMatchingCab);
        Check("SourcePath zeigt auf den Basis-Ordner",
            vl2021.Offline.SourcePath == Path.Combine(root, "x64", "PerpetualVL2021"), vl2021.Offline.SourcePath);
        Check("Groesse der Quelldateien wird ermittelt", vl2021.Offline.SizeInBytes > 3000,
            vl2021.Offline.SizeInBytes.ToString());
        Check("ohne Office\\Data keine Offline-Quelle", !student.Offline.Available);

        // Mehrere Versionsordner: die neueste gewinnt
        Directory.CreateDirectory(Path.Combine(data2021, "16.0.18129.20158"));
        var mehrere = OfflineSource.Detect(Path.Combine(root, "x64", "PerpetualVL2021"), 64);
        Check("bei mehreren Versionen gewinnt die neueste",
            mehrere.LatestVersion?.ToString() == "16.0.18129.20158", mehrere.LatestVersion?.ToString());

        Console.WriteLine("\n== Download nachpruefen ==");
        var pruefOrdner = Path.Combine(root, "nachpruefen");
        Check("ohne Office\\Data ist der Download unvollstaendig",
            OfflineSource.VerifyDownload(pruefOrdner, 64, null) is not null);
        var pruefDaten = Path.Combine(pruefOrdner, "Office", "Data");
        Directory.CreateDirectory(Path.Combine(pruefDaten, "16.0.2.2"));
        Check("ohne Cab-Datei ist der Download unvollstaendig",
            OfflineSource.VerifyDownload(pruefOrdner, 64, null)?.Contains("v64.cab") == true,
            OfflineSource.VerifyDownload(pruefOrdner, 64, null));
        Touch(Path.Combine(pruefDaten, "v64.cab"));
        Check("leerer Versionsordner ist unvollstaendig",
            OfflineSource.VerifyDownload(pruefOrdner, 64, null)?.Contains("leer") == true,
            OfflineSource.VerifyDownload(pruefOrdner, 64, null));
        Touch(Path.Combine(pruefDaten, "16.0.2.2", "stream.x64.x-none.dat"), new string('x', 100));
        Check("vollstaendiger Bestand besteht die Pruefung",
            OfflineSource.VerifyDownload(pruefOrdner, 64, null) is null,
            OfflineSource.VerifyDownload(pruefOrdner, 64, null));
        Check("gleiche Version wie online besteht die Pruefung",
            OfflineSource.VerifyDownload(pruefOrdner, 64, new Version("16.0.2.2")) is null);
        Check("aelterer Stand als online gemeldet ist unvollstaendig",
            OfflineSource.VerifyDownload(pruefOrdner, 64, new Version("16.0.3.0"))?.Contains("erwartet") == true);
        Check("32-Bit-Bestand verlangt v32.cab",
            OfflineSource.VerifyDownload(pruefOrdner, 32, null)?.Contains("v32.cab") == true);

        Console.WriteLine("\n== Versionsvergleich ==");
        var lokal = new Version("16.0.17928.20216");
        var neuer = new Version("16.0.20228.20124");
        // Bewusst ein zweites Objekt mit demselben Wert statt lokal > lokal -
        // der Vergleich einer Variablen mit sich selbst ist eine Warnung
        // (CS1718), und die soll hier niemand wegdiskutieren muessen.
        var gleich = new Version("16.0.17928.20216");
        Check("online neuer als lokal wird erkannt", neuer > lokal);
        Check("gleiche Version gilt nicht als neuer", !(lokal > gleich));

        Console.WriteLine("\n== Installationsmodus ==");
        Check("Automatik wird zu Offline, wenn Dateien da sind",
            InstallRunner.ResolveMode(InstallMode.Automatic, vl2021.Offline) == InstallMode.Offline);
        Check("Automatik wird zu Online, wenn nichts da ist",
            InstallRunner.ResolveMode(InstallMode.Automatic, student.Offline) == InstallMode.Online);
        Check("ausdrueckliches Online bleibt Online",
            InstallRunner.ResolveMode(InstallMode.Online, vl2021.Offline) == InstallMode.Online);

        var temp = Path.Combine(root, "temp");

        var offlinePlan = InstallRunner.Prepare(vl2021, InstallMode.Automatic, temp);
        Check("Offline-Plan setzt SourcePath",
            offlinePlan.SourcePath == Path.Combine(root, "x64", "PerpetualVL2021"), offlinePlan.SourcePath);
        Check("SourcePath steht auch in der erzeugten XML",
            TestXml.ReadSourcePath(offlinePlan.ConfigurationFile) == offlinePlan.SourcePath,
            TestXml.ReadSourcePath(offlinePlan.ConfigurationFile));
        Check("Offline laeuft im Ordner der Basis",
            offlinePlan.WorkingDirectory == Path.Combine(root, "x64", "PerpetualVL2021"), offlinePlan.WorkingDirectory);
        Check("Offline benutzt die vorhandene setup.exe",
            offlinePlan.SetupExecutable == vl2021.SetupExecutable);
        Check("Aufrufzeile enthaelt /configure",
            offlinePlan.Arguments.StartsWith("/configure \"", StringComparison.Ordinal), offlinePlan.Arguments);
        Check("Produktangaben bleiben in der Kopie erhalten",
            File.ReadAllText(offlinePlan.ConfigurationFile).Contains("Standard2021Volume", StringComparison.Ordinal));

        // --- Das Entscheidende: ohne festgeschriebene Version fragt das
        //     Deployment Tool beim CDN nach dem aktuellen Build und laedt ihn.
        Check("Offline-Plan schreibt die vorliegende Version fest",
            offlinePlan.PinnedVersion?.ToString() == "16.0.17928.20216",
            offlinePlan.PinnedVersion?.ToString());
        Check("Version steht auch in der erzeugten XML",
            OfficeConfiguration.ReadVersion(offlinePlan.ConfigurationFile) == "16.0.17928.20216",
            OfficeConfiguration.ReadVersion(offlinePlan.ConfigurationFile));
        Check("Kanal der Vorlage bleibt unangetastet",
            TestXml.ReadChannel(offlinePlan.ConfigurationFile) == "PerpetualVL2021",
            TestXml.ReadChannel(offlinePlan.ConfigurationFile));

        var onlinePlan = InstallRunner.Prepare(vl2021, InstallMode.Online, temp);
        Check("Online-Plan setzt keinen SourcePath", onlinePlan.SourcePath is null);
        Check("Online-Plan schreibt keine Version fest", onlinePlan.PinnedVersion is null);
        Check("Online-XML enthaelt keine Version",
            OfficeConfiguration.ReadVersion(onlinePlan.ConfigurationFile) is null,
            OfficeConfiguration.ReadVersion(onlinePlan.ConfigurationFile));
        Check("Online-XML enthaelt keinen SourcePath",
            TestXml.ReadSourcePath(onlinePlan.ConfigurationFile) is null);
        Check("Online laeuft in einem eigenen Arbeitsordner",
            onlinePlan.WorkingDirectory != vl2021.Configuration.BaseFolder, onlinePlan.WorkingDirectory);
        Check("im Arbeitsordner liegt eine Kopie der setup.exe",
            File.Exists(Path.Combine(onlinePlan.WorkingDirectory, "setup.exe")));
        Check("im Arbeitsordner liegen keine Office-Quelldateien",
            !Directory.Exists(Path.Combine(onlinePlan.WorkingDirectory, "Office")));

        var forcedOfflineFailed = false;
        try { InstallRunner.Prepare(student, InstallMode.Offline, temp); }
        catch (InvalidOperationException) { forcedOfflineFailed = true; }
        Check("Offline ohne Quelldateien wird abgelehnt", forcedOfflineFailed);

        var noSetupFailed = false;
        try
        {
            var withoutSetup = new CatalogEntry
            {
                Configuration = student.Configuration,
                Channel = student.Channel,
                Offline = student.Offline,
                SetupExecutable = null,
                ClientEdition = 64
            };
            InstallRunner.Prepare(withoutSetup, InstallMode.Online, temp);
        }
        catch (InvalidOperationException) { noSetupFailed = true; }
        Check("ohne setup.exe wird abgelehnt", noSetupFailed);

        Console.WriteLine("\n== Offline-Bestand nachladen ==");
        var downloadPlan = InstallRunner.PrepareDownload(vl2021, temp);
        Check("Download benutzt den Schalter /download",
            downloadPlan.Arguments.StartsWith("/download \"", StringComparison.Ordinal), downloadPlan.Arguments);
        Check("Download ist als solcher erkennbar", downloadPlan.Verb == "download");
        Check("Download setzt SourcePath auf den Basis-Ordner",
            downloadPlan.SourcePath == Path.Combine(root, "x64", "PerpetualVL2021"), downloadPlan.SourcePath);
        Check("SourcePath steht auch in der Download-XML",
            TestXml.ReadSourcePath(downloadPlan.ConfigurationFile) == downloadPlan.SourcePath);
        // Beim Herunterladen soll gerade der neueste Stand geholt werden -
        // eine festgeschriebene Version wuerde das verhindern.
        Check("Download schreibt keine Version fest",
            downloadPlan.PinnedVersion is null
            && OfficeConfiguration.ReadVersion(downloadPlan.ConfigurationFile) is null,
            OfficeConfiguration.ReadVersion(downloadPlan.ConfigurationFile));
        Check("Download-XML liegt getrennt von der Installations-XML",
            Path.GetFileName(downloadPlan.ConfigurationFile).StartsWith("download_", StringComparison.Ordinal),
            Path.GetFileName(downloadPlan.ConfigurationFile));
        Check("Download laeuft im Ordner der Basis",
            downloadPlan.WorkingDirectory == Path.Combine(root, "x64", "PerpetualVL2021"));

        // Auch fuer eine Basis ganz ohne Offline-Dateien muss der Download gehen
        var erstDownload = InstallRunner.PrepareDownload(student, temp);
        Check("Download geht auch ohne vorhandenen Bestand",
            erstDownload.SourcePath == Path.Combine(root, "x64", "Current"), erstDownload.SourcePath);

        var brokenDownloadRejected = false;
        try { InstallRunner.PrepareDownload(broken, temp); }
        catch (InvalidOperationException) { brokenDownloadRejected = true; }
        Check("Download mit kaputter XML wird abgelehnt", brokenDownloadRejected);

        Check("Installation bleibt bei /configure",
            InstallRunner.Prepare(vl2021, InstallMode.Offline, temp).Arguments
                .StartsWith("/configure \"", StringComparison.Ordinal));

        Console.WriteLine("\n== Ein Durchlauf je Basis ==");
        // In "Current" liegen mehrere Konfigurationen - daraus muss EINE
        // Downloaddatei mit allen Produkten werden.
        var currentEntries = x64.Where(e => e.ChannelFolderName == "Current" && e.CanInstall).ToList();
        var basisPlan = InstallRunner.PrepareBaseDownload(currentEntries, temp);

        Check("gemeinsame Datei enthaelt alle Produkte der Basis",
            basisPlan.ProductCount == 2, basisPlan.ProductCount.ToString());
        Check("gemeinsame Datei ist auch als Datei so gespeichert",
            TestXml.CountProducts(basisPlan.ConfigurationFile) == 2,
            TestXml.CountProducts(basisPlan.ConfigurationFile).ToString());
        Check("gemeinsame Datei setzt SourcePath",
            TestXml.ReadSourcePath(basisPlan.ConfigurationFile)
                == Path.Combine(root, "x64", "Current"));
        Check("gemeinsame Datei benutzt /download", basisPlan.Verb == "download");
        Check("gemeinsame Download-Datei schreibt keine Version fest",
            OfficeConfiguration.ReadVersion(basisPlan.ConfigurationFile) is null);

        Console.WriteLine("\n== Version festschreiben ==");
        // Vorlage ohne Channel-Attribut: dann muss der Kanal ergaenzt werden,
        // weil Microsoft Version und Channel nur gemeinsam empfiehlt.
        var ohneKanal = Path.Combine(root, "x64", "Current", "configuration_Student_2019.xml");
        var festgelegt = Path.Combine(root, "temp", "festgelegt.xml");
        OfficeConfiguration.WriteWithSourcePath(ohneKanal, festgelegt, @"C:\Office\Current",
            new Version("16.0.17928.20216"), "Current");
        Check("Kanal wird ergaenzt, wenn er fehlt",
            TestXml.ReadChannel(festgelegt) == "Current", TestXml.ReadChannel(festgelegt));
        Check("Version wird geschrieben",
            OfficeConfiguration.ReadVersion(festgelegt) == "16.0.17928.20216");
        Check("SourcePath wird geschrieben",
            TestXml.ReadSourcePath(festgelegt) == @"C:\Office\Current");

        var ohneVersion = Path.Combine(root, "temp", "ohne_version.xml");
        OfficeConfiguration.WriteWithSourcePath(ohneKanal, ohneVersion, @"C:\Office\Current");
        Check("ohne Angabe bleibt die Datei ohne Version",
            OfficeConfiguration.ReadVersion(ohneVersion) is null);
        Check("ohne Angabe wird auch kein Kanal erfunden",
            TestXml.ReadChannel(ohneVersion) is null);

        // Der Ordner sticht die Vorlage: Ein abweichender Kanal in der XML
        // laesst das Deployment Tool sonst mit "This product can't be
        // installed on the selected update channel" abbrechen.
        var kanalKorrigiert = Path.Combine(root, "temp", "kanal_korrigiert.xml");
        OfficeConfiguration.WriteWithSourcePath(
            Path.Combine(root, "x64", "PerpetualVL2021", "configuration_Standard_2021.xml"),
            kanalKorrigiert, @"C:\Office\VL2019", new Version("16.0.10417.20197"), "PerpetualVL2019");
        Check("abweichender Kanal wird auf den Ordner gesetzt",
            TestXml.ReadChannel(kanalKorrigiert) == "PerpetualVL2019",
            TestXml.ReadChannel(kanalKorrigiert));

        Check("gleicher Kanal gilt als passend",
            OfficeConfiguration.ChannelMatchesFolder("PerpetualVL2019", "PerpetualVL2019"));
        Check("Gross-/Kleinschreibung spielt keine Rolle",
            OfficeConfiguration.ChannelMatchesFolder("perpetualvl2019", "PerpetualVL2019"));
        Check("fehlender Kanal gilt als passend",
            OfficeConfiguration.ChannelMatchesFolder(null, "PerpetualVL2019"));
        Check("abweichender Kanal wird erkannt",
            !OfficeConfiguration.ChannelMatchesFolder("PerpetualVL2021", "PerpetualVL2019"));

        Console.WriteLine("\n== Falscher Kanal in der Vorlage ==");
        // Genau der beobachtete Fall: Die Datei liegt unter PerpetualVL2019,
        // traegt aber Channel="PerpetualVL2021".
        var schief = Path.Combine(Path.GetTempPath(), "office-kanal-" + Guid.NewGuid().ToString("N"));
        WriteConfig(Path.Combine(schief, "x64", "PerpetualVL2019", "configuration_Standard_2019.xml"),
                    "Standard2019Volume", 64, "PerpetualVL2021");
        Touch(Path.Combine(schief, "setup.exe"));
        var schiefData = Path.Combine(schief, "x64", "PerpetualVL2019", "Office", "Data");
        Directory.CreateDirectory(Path.Combine(schiefData, "16.0.10417.20197"));
        Touch(Path.Combine(schiefData, "16.0.10417.20197", "i640.cab"), new string('x', 1024));
        Touch(Path.Combine(schiefData, "v64.cab"), new string('x', 512));

        var schiefEintrag = OfficeCatalog.Scan(schief).Entries.Single();
        Check("Kanal aus der Vorlage wird eingelesen",
            schiefEintrag.Configuration.DeclaredChannel == "PerpetualVL2021",
            schiefEintrag.Configuration.DeclaredChannel);

        var schiefPlan = InstallRunner.Prepare(schiefEintrag, InstallMode.Offline, Path.Combine(schief, "temp"));
        Check("die Abweichung wird gemeldet",
            schiefPlan.ChannelCorrectedFrom == "PerpetualVL2021", schiefPlan.ChannelCorrectedFrom);
        Check("verwendet wird der Ordner", schiefPlan.Channel == "PerpetualVL2019", schiefPlan.Channel);
        Check("in der erzeugten XML steht der Ordner-Kanal",
            TestXml.ReadChannel(schiefPlan.ConfigurationFile) == "PerpetualVL2019",
            TestXml.ReadChannel(schiefPlan.ConfigurationFile));
        Check("die vorliegende Version wird festgeschrieben",
            OfficeConfiguration.ReadVersion(schiefPlan.ConfigurationFile) == "16.0.10417.20197",
            OfficeConfiguration.ReadVersion(schiefPlan.ConfigurationFile));

        var schiefOnline = InstallRunner.Prepare(schiefEintrag, InstallMode.Online, Path.Combine(schief, "temp"));
        Check("auch online wird der Kanal berichtigt",
            TestXml.ReadChannel(schiefOnline.ConfigurationFile) == "PerpetualVL2019",
            TestXml.ReadChannel(schiefOnline.ConfigurationFile));
        Check("online bleibt ohne SourcePath",
            TestXml.ReadSourcePath(schiefOnline.ConfigurationFile) is null);

        var schiefDownload = InstallRunner.PrepareDownload(schiefEintrag, Path.Combine(schief, "temp"));
        Check("auch der Download benutzt den Ordner-Kanal",
            TestXml.ReadChannel(schiefDownload.ConfigurationFile) == "PerpetualVL2019",
            TestXml.ReadChannel(schiefDownload.ConfigurationFile));
        Check("der Download schreibt weiterhin keine Version fest",
            OfficeConfiguration.ReadVersion(schiefDownload.ConfigurationFile) is null);

        // Passt alles zusammen, wird nichts gemeldet.
        var sauber = InstallRunner.Prepare(vl2021, InstallMode.Offline, temp);
        Check("bei passendem Kanal keine Meldung", sauber.ChannelCorrectedFrom is null,
            sauber.ChannelCorrectedFrom);

        try { Directory.Delete(schief, recursive: true); } catch { }

        var inhalt = File.ReadAllText(basisPlan.ConfigurationFile);
        Check("beide Produktkennungen stehen drin",
            inhalt.Contains("HomeStudent2019Retail", StringComparison.Ordinal)
            && inhalt.Contains("O365BusinessRetail", StringComparison.Ordinal));
        Check("Kanal wird ausdruecklich gesetzt",
            inhalt.Contains("Channel=\"Current\"", StringComparison.Ordinal));
        Check("Installations-Angaben sind nicht mit drin",
            !inhalt.Contains("RemoveMSI", StringComparison.Ordinal)
            && !inhalt.Contains("Display", StringComparison.Ordinal));
        Check("Sprache des Produkts bleibt erhalten",
            inhalt.Contains("de-de", StringComparison.Ordinal));

        // Doppelte Produktkennungen duerfen nur einmal auftauchen
        var doppeltDatei = Path.Combine(root, "temp", "doppelt.xml");
        var anzahl = OfficeConfiguration.WriteMergedDownloadConfig(
            new[] { currentEntries[0].Configuration.FilePath, currentEntries[0].Configuration.FilePath },
            doppeltDatei, @"C:\Ziel", 64, "Current");
        Check("doppelte Produkte werden nur einmal aufgenommen", anzahl == 1, anzahl.ToString());

        var leerAbgelehnt = false;
        try { InstallRunner.PrepareBaseDownload(new List<CatalogEntry>(), temp); }
        catch (InvalidOperationException) { leerAbgelehnt = true; }
        Check("leere Basis wird abgelehnt", leerAbgelehnt);

        Console.WriteLine("\n== Alten Stand entfernen ==");
        var aufraeumBasis = Path.Combine(root, "aufraeumen");
        var aufraeumData = Path.Combine(aufraeumBasis, "Office", "Data");
        Directory.CreateDirectory(Path.Combine(aufraeumData, "16.0.14334.20440"));
        Directory.CreateDirectory(Path.Combine(aufraeumData, "16.0.14334.20848"));
        Touch(Path.Combine(aufraeumData, "16.0.14334.20440", "i640.cab"), new string('x', 4096));
        Touch(Path.Combine(aufraeumData, "16.0.14334.20848", "i640.cab"), new string('x', 4096));
        Touch(Path.Combine(aufraeumData, "v64.cab"), new string('x', 512));
        Touch(Path.Combine(aufraeumData, "v64_16.0.14334.20440.cab"), new string('x', 256));
        Touch(Path.Combine(aufraeumData, "v64_16.0.14334.20848.cab"), new string('x', 256));
        Touch(Path.Combine(aufraeumData, "irgendwas.txt"), "nicht anfassen");

        var aufgeraeumt = OfflineSource.CleanupOldVersions(aufraeumBasis, new Version("16.0.14334.20848"));

        Check("alter Versionsordner wird entfernt",
            !Directory.Exists(Path.Combine(aufraeumData, "16.0.14334.20440")));
        Check("neuer Versionsordner bleibt",
            Directory.Exists(Path.Combine(aufraeumData, "16.0.14334.20848")));
        Check("alte Beschreibungsdatei wird entfernt",
            !File.Exists(Path.Combine(aufraeumData, "v64_16.0.14334.20440.cab")));
        Check("neue Beschreibungsdatei bleibt",
            File.Exists(Path.Combine(aufraeumData, "v64_16.0.14334.20848.cab")));
        Check("v64.cab bleibt unangetastet", File.Exists(Path.Combine(aufraeumData, "v64.cab")));
        Check("fremde Dateien bleiben unangetastet",
            File.Exists(Path.Combine(aufraeumData, "irgendwas.txt")));
        Check("freigegebener Platz wird gemeldet", aufgeraeumt.FreedBytes > 4000,
            aufgeraeumt.FreedBytes.ToString());
        Check("entfernte Version wird benannt",
            aufgeraeumt.RemovedVersions.Contains("16.0.14334.20440"),
            string.Join(",", aufgeraeumt.RemovedVersions));

        // Zweiter Durchlauf: es gibt nichts mehr zu tun
        var nochmal = OfflineSource.CleanupOldVersions(aufraeumBasis, new Version("16.0.14334.20848"));
        Check("zweiter Durchlauf entfernt nichts mehr", !nochmal.AnythingRemoved);

        // Schreibgeschützte Dateien: das Deployment Tool legt die Quelldateien so
        // ab, und Directory.Delete scheitert daran mit "Access is denied".
        var schreibschutzBasis = Path.Combine(root, "schreibschutz");
        var schreibschutzData = Path.Combine(schreibschutzBasis, "Office", "Data");
        Directory.CreateDirectory(Path.Combine(schreibschutzData, "16.0.14334.20440", "tief"));
        Touch(Path.Combine(schreibschutzData, "16.0.14334.20440", "gesperrt.dat"), new string('x', 4096));
        Touch(Path.Combine(schreibschutzData, "16.0.14334.20440", "tief", "auch.dat"), new string('x', 2048));
        Touch(Path.Combine(schreibschutzData, "16.0.14334.20848", "neu.dat"), new string('x', 1024));
        Touch(Path.Combine(schreibschutzData, "v64_16.0.14334.20440.cab"), new string('x', 256));

        File.SetAttributes(Path.Combine(schreibschutzData, "16.0.14334.20440", "gesperrt.dat"),
                           FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(schreibschutzData, "16.0.14334.20440", "tief", "auch.dat"),
                           FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(schreibschutzData, "v64_16.0.14334.20440.cab"),
                           FileAttributes.ReadOnly);

        var geschuetzt = OfflineSource.CleanupOldVersions(schreibschutzBasis, new Version("16.0.14334.20848"));

        Check("schreibgeschuetzter Versionsordner wird trotzdem entfernt",
            !Directory.Exists(Path.Combine(schreibschutzData, "16.0.14334.20440")),
            string.Join(" | ", geschuetzt.Problems));
        Check("schreibgeschuetzte Begleitdatei wird trotzdem entfernt",
            !File.Exists(Path.Combine(schreibschutzData, "v64_16.0.14334.20440.cab")));
        Check("dabei traten keine Probleme auf", geschuetzt.Problems.Count == 0,
            string.Join(" | ", geschuetzt.Problems));
        Check("der neue Stand bleibt erhalten",
            Directory.Exists(Path.Combine(schreibschutzData, "16.0.14334.20848")));

        // Beschreibung fuer das Protokoll darf nicht leer sein
        Check("Beschreibung nennt die entfernte Version",
            geschuetzt.Describe().Contains("16.0.14334.20440", StringComparison.Ordinal),
            geschuetzt.Describe());
        Check("Beschreibung nennt auch die Begleitdateien",
            geschuetzt.Describe().Contains("Begleitdatei", StringComparison.Ordinal),
            geschuetzt.Describe());
        Check("Beschreibung ohne Treffer sagt \"nichts\"",
            new OfflineSource.CleanupResult().Describe() == "nichts");

        // Ordner ohne Office\Data darf nicht umfallen
        Check("Ordner ohne Quelldateien wirft nicht",
            !OfflineSource.CleanupOldVersions(Path.Combine(root, "leer"), new Version("16.0.1.1")).AnythingRemoved);

        Console.WriteLine("\n== Deployment Tool ==");
        const string downloadPage = """
            <html><body>
            <a href="https://download.microsoft.com/download/1a1b/officedeploymenttool_19029-20244.exe">alt</a>
            <a href="https://download.microsoft.com/download/6c1eeb25-cf8b-41d9-8d0d-cc1dbc032140/officedeploymenttool_20228-20124.exe">neu</a>
            <a href="https://download.microsoft.com/download/xyz/irgendwas.exe">nichts</a>
            </body></html>
            """;
        var release = DeploymentToolService.ExtractRelease(downloadPage);
        Check("Downloadlink wird gefunden", release is not null);
        Check("die neuere Fassung gewinnt",
            release?.Version.ToString() == "16.0.20228.20124", release?.Version.ToString());
        Check("Dateiname wird aus der Adresse abgeleitet",
            release?.FileName == "officedeploymenttool_20228-20124.exe", release?.FileName);
        Check("Seite ohne Link ergibt nichts",
            DeploymentToolService.ExtractRelease("<html>nichts hier</html>") is null);
        Check("leere Seite ergibt nichts", DeploymentToolService.ExtractRelease("") is null);

        Check("Version aus dem Dateinamen",
            DeploymentToolService.ParseVersionFromFileName("officedeploymenttool_20228-20124.exe")?.ToString()
                == "16.0.20228.20124");
        Check("fremder Dateiname ergibt keine Version",
            DeploymentToolService.ParseVersionFromFileName("setup.exe") is null);

        Check("fehlende setup.exe heisst: aktualisieren",
            DeploymentToolService.NeedsUpdate(null, new Version("16.0.20228.20124")));
        Check("aeltere setup.exe heisst: aktualisieren",
            DeploymentToolService.NeedsUpdate(new Version("16.0.19029.20244"), new Version("16.0.20228.20124")));
        Check("gleiche Fassung heisst: nichts tun",
            !DeploymentToolService.NeedsUpdate(new Version("16.0.20228.20124"), new Version("16.0.20228.20124")));
        Check("ohne Auskunft aus dem Netz wird nichts angefasst",
            !DeploymentToolService.NeedsUpdate(new Version("16.0.19029.20244"), null));

        var targets = DeploymentToolService.DeploymentTargets(root);
        Check("alle vorhandenen setup.exe sind Ziel",
            targets.Count == 2
            && targets.Contains(Path.Combine(root, "setup.exe"))
            && targets.Contains(Path.Combine(root, "x64", "Current", "setup.exe")),
            string.Join(" | ", targets));

        var leerRoot = Path.Combine(root, "ohne-setup");
        Directory.CreateDirectory(leerRoot);
        var leerTargets = DeploymentToolService.DeploymentTargets(leerRoot);
        Check("ohne vorhandene setup.exe wird der Wurzelordner vorgeschlagen",
            leerTargets.Count == 1 && leerTargets[0] == Path.Combine(leerRoot, "setup.exe"),
            string.Join(" | ", leerTargets));

        Touch(Path.Combine(root, "neu", "setup.exe"), "neue Fassung");
        var kopiert = DeploymentToolService.Deploy(Path.Combine(root, "neu", "setup.exe"), targets);
        Check("setup.exe wird an alle Stellen kopiert", kopiert == 2, kopiert.ToString());
        Check("die Kopie ist tatsaechlich angekommen",
            File.ReadAllText(Path.Combine(root, "x64", "Current", "setup.exe")) == "neue Fassung");

        Touch(Path.Combine(root, "Versionscheck", "ODT-Url.txt"),
              "https://download.microsoft.com/download/abc/officedeploymenttool_20300-20001.exe");
        Check("feste Adresse aus ODT-Url.txt wird gelesen",
            DeploymentToolService.ReadUrlOverride(root)?.EndsWith("20300-20001.exe", StringComparison.Ordinal) == true);
        Touch(Path.Combine(root, "Versionscheck", "ODT-Url.txt"),
              "https://example.invalid/download/officedeploymenttool_20300-20001.exe");
        Check("fremder Server in ODT-Url.txt wird verworfen",
            DeploymentToolService.ReadUrlOverride(root) is null);
        Touch(Path.Combine(root, "Versionscheck", "ODT-Url.txt"),
              "http://download.microsoft.com/download/abc/officedeploymenttool_20300-20001.exe");
        Check("unverschluesselte Adresse in ODT-Url.txt wird verworfen",
            DeploymentToolService.ReadUrlOverride(root) is null);
        Check("Unterdomaene als Tarnung wird nicht als Microsoft erkannt",
            !DeploymentToolService.IsMicrosoftDownloadUrl("https://download.microsoft.com.example.invalid/a.exe"));
        Check("Benutzerangabe als Tarnung wird nicht als Microsoft erkannt",
            !DeploymentToolService.IsMicrosoftDownloadUrl("https://download.microsoft.com@example.invalid/a.exe"));
        Check("Unterzeichner wird aus dem Zertifikatsnamen gelesen",
            Authenticode.OrganizationOf("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US")
                == Authenticode.MicrosoftSigner);
        Check("Unterzeichner mit Komma in Anfuehrungszeichen",
            Authenticode.OrganizationOf("CN=x, O=\"Fremd, Inc.\", C=US") == "Fremd, Inc.");
        Check("ohne O= kein Unterzeichner", Authenticode.OrganizationOf("CN=nur ein Name") is null);
        Touch(Path.Combine(root, "Versionscheck", "ODT-Url.txt"), "kein-link");
        Check("unbrauchbarer Inhalt von ODT-Url.txt wird verworfen",
            DeploymentToolService.ReadUrlOverride(root) is null);
        File.Delete(Path.Combine(root, "Versionscheck", "ODT-Url.txt"));

        Console.WriteLine("\n== Versionsuebersicht auswerten ==");
        const string releases = """
            [
              { "channel":"Monthly", "channelId":"Current", "latestVersion":"16.0.20228.20190",
                "cdnBaseUrl":"http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60" },
              { "channel":"LTSB", "channelId":"PerpetualVL2019", "latestVersion":"16.0.10417.20197",
                "cdnBaseUrl":"http://officecdn.microsoft.com/pr/f2e724c1-748f-4b47-8fb8-8e0d210e9208" },
              { "channel":"LTSB2021", "channelId":"PerpetualVL2021", "latestVersion":"16.0.14334.20848",
                "cdnBaseUrl":"http://officecdn.microsoft.com/pr/5030841d-c919-4594-8d2d-84ae4f96e58e" },
              { "channel":"LTSB2024", "channelId":"PerpetualVL2024", "latestVersion":"16.0.17932.20910",
                "cdnBaseUrl":"http://officecdn.microsoft.com/pr/7983bac0-e531-40cf-be00-fd24fe66619c" },
              { "channel":"BetaChannel", "channelId":"Beta", "latestVersion":"16.0.20411.20000",
                "cdnBaseUrl":"http://officecdn.microsoft.com/pr/5440fd1f-7ecb-4221-8110-145efaa6372f" }
            ]
            """;

        var uebersicht = OnlineVersionService.ParseReleases(releases);
        Check("alle vier Basen werden erkannt", uebersicht.Count == 4, uebersicht.Count.ToString());
        Check("Current wird gelesen",
            uebersicht["Current"].ToString() == "16.0.20228.20190", uebersicht["Current"].ToString());
        Check("PerpetualVL2019 wird gelesen",
            uebersicht["PerpetualVL2019"].ToString() == "16.0.10417.20197");
        Check("PerpetualVL2021 wird gelesen",
            uebersicht["PerpetualVL2021"].ToString() == "16.0.14334.20848");
        Check("PerpetualVL2024 wird gelesen",
            uebersicht["PerpetualVL2024"].ToString() == "16.0.17932.20910");
        Check("fremde Kanaele werden uebergangen", !uebersicht.ContainsKey("Beta"));

        // Zuordnung muss auch ohne channelId klappen - allein ueber die GUID
        const string nurGuid = """
            [ { "channel":"LTSB2021", "latestVersion":"16.0.14334.20848",
                "cdnBaseUrl":"http://officecdn.microsoft.com/pr/5030841D-C919-4594-8D2D-84AE4F96E58E" } ]
            """;
        var ueberGuid = OnlineVersionService.ParseReleases(nurGuid);
        Check("Zuordnung klappt allein ueber die GUID (auch in Grossschreibung)",
            ueberGuid.Count == 1 && ueberGuid["PerpetualVL2021"].ToString() == "16.0.14334.20848",
            string.Join(",", ueberGuid.Keys));

        Check("unbrauchbare Antwort ergibt nichts",
            OnlineVersionService.ParseReleases("<html>Fehler</html>").Count == 0);
        Check("kein Array ergibt nichts",
            OnlineVersionService.ParseReleases("{\"channel\":\"Monthly\"}").Count == 0);
        Check("Eintrag ohne Version wird uebergangen",
            OnlineVersionService.ParseReleases("[{\"channelId\":\"Current\"}]").Count == 0);
        Check("unbrauchbare Versionsangabe wird uebergangen",
            OnlineVersionService.ParseReleases("[{\"channelId\":\"Current\",\"latestVersion\":\"neu\"}]").Count == 0);

        var ergebnis = new OnlineVersionResult { Versions = uebersicht };
        Check("For() findet den Kanal",
            ergebnis.For(OfficeChannels.ByFolderName("PerpetualVL2024"))?.ToString() == "16.0.17932.20910");
        Check("For(null) faellt nicht um", ergebnis.For(null) is null);
        Check("Fehlerergebnis meldet Misserfolg", !OnlineVersionResult.Failed("kaputt").Success);

        // Genau der Fall aus der Praxis: alter Offline-Stand, neuere Online-Version
        Check("veralteter Offline-Stand wird erkannt",
            uebersicht["Current"] > new Version("16.0.19426.20186"));
        Check("aktueller Offline-Stand gilt nicht als veraltet",
            !(uebersicht["PerpetualVL2024"] > new Version("16.0.17932.20910")));

        Console.WriteLine("\n== Netzpruefung ==");
        ConnectivityService.Timeout = TimeSpan.FromSeconds(4);
        var online = ConnectivityService.IsOnlineAsync().GetAwaiter().GetResult();
        Console.WriteLine($"         (Netzpruefung meldet: {(online ? "online" : "offline")})");
        Check("Netzpruefung liefert eine klare Antwort ohne Ausnahme", online || !online);
        Check("Pruefadresse ist die von Windows genutzte",
            ConnectivityService.ProbeUrl.Contains("msftconnecttest.com", StringComparison.Ordinal),
            ConnectivityService.ProbeUrl);

        Check("Ersatzpruefung laeuft ueber https",
            ConnectivityService.FallbackProbeUrl.StartsWith("https://", StringComparison.Ordinal));

        Console.WriteLine("\n== Protokolldatei ==");
        ActivityLog.Folder = Path.Combine(root, "protokoll");
        ActivityLog.MaxBytes = 300;
        ActivityLog.Append("erste Zeile");
        Check("Protokollzeile landet in der Datei",
            File.Exists(ActivityLog.FilePath) && File.ReadAllText(ActivityLog.FilePath).Contains("erste Zeile"));
        Check("mit Zeitstempel",
            System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(ActivityLog.FilePath),
                @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  erste Zeile"));
        try { throw new InvalidOperationException("Testfehler"); }
        catch (Exception ex) { ActivityLog.Error("Pruefung", ex); }
        Check("abgefangener Fehler steht mit Quelle und Typ im Protokoll",
            File.ReadAllText(ActivityLog.FilePath).Contains("[FEHLER in Pruefung] System.InvalidOperationException: Testfehler"));
        for (var i = 0; i < 20; i++) ActivityLog.Append($"Fuellzeile {i}");
        Check("zu grosse Datei wird abgeloest",
            File.Exists(ActivityLog.PreviousFilePath) && new FileInfo(ActivityLog.FilePath).Length <= 400,
            new FileInfo(ActivityLog.FilePath).Length.ToString());
        Check("es bleibt bei zwei Dateien",
            Directory.GetFiles(ActivityLog.Folder).Length == 2, Directory.GetFiles(ActivityLog.Folder).Length.ToString());
        ActivityLog.MaxBytes = ActivityLog.DefaultMaxBytes;

        Console.WriteLine("\n== Suche ==");
        Check("leere Suche zeigt alles", OfficeCatalog.Matches(student, ""));
        Check("Suche nach Produktnamen", OfficeCatalog.Matches(student, "student"));
        Check("Suche ist unabhaengig von Gross-/Kleinschreibung", OfficeCatalog.Matches(student, "STUDENT"));
        Check("Suche nach Jahreszahl", OfficeCatalog.Matches(student, "2019"));
        Check("Suche nach Basis", OfficeCatalog.Matches(vl2021, "perpetual"));
        Check("Suche nach Produktkennung", OfficeCatalog.Matches(vl2021, "Standard2021Volume"));
        Check("Suche nach Dateiname", OfficeCatalog.Matches(vl2021, "configuration_Standard"));
        Check("mehrere Begriffe muessen alle passen", OfficeCatalog.Matches(vl2021, "2021 standard"));
        Check("ein unpassender Begriff schliesst aus", !OfficeCatalog.Matches(vl2021, "2021 visio"));
        Check("Suche nach Architektur", OfficeCatalog.Matches(x32Student, "32 Bit"));
        Check("nicht Passendes wird ausgeblendet", !OfficeCatalog.Matches(student, "visio"));

        Console.WriteLine("\n== Paketversion ==");
        var package = OfficeCatalog.ReadPackageVersion(root);
        Check("Version.txt wird aufgeteilt",
            package?.Version == "69" && package?.Date == "13.08.2026",
            $"{package?.Version}|{package?.Date}");
        Check("fehlende Version.txt ergibt null",
            OfficeCatalog.ReadPackageVersion(Path.Combine(root, "leer")) is null);

        Console.WriteLine("\n== Aeltere Fassungen (2013/2016) ==");
        var legacy = LegacyOfficeReleases.All;
        Check("acht Eintraege: zwei Jahre mal vier Editionen", legacy.Count == 8, legacy.Count.ToString());
        Check("jedes Jahr mit vier Editionen",
            legacy.Count(r => r.Year == 2016) == 4 && legacy.Count(r => r.Year == 2013) == 4);

        var hs2016 = legacy.First(r => r.Year == 2016 && r.ProductId == "HomeStudentRetail");
        var pp2013 = legacy.First(r => r.Year == 2013 && r.ProductId == "ProPlusRetail");
        var prof2013 = legacy.First(r => r.Year == 2013 && r.ProductId == "ProfessionalRetail");

        Check("Medium-Adresse 2016 stimmt",
            hs2016.ImageUrl() == "https://officecdn.microsoft.com/db/492350F6-3A01-4F97-B9C0-C7C6DDF67D60/media/de-DE/HomeStudentRetail.img",
            hs2016.ImageUrl());
        Check("Medium-Adresse 2013 nutzt die andere Kennung",
            prof2013.ImageUrl() == "https://officecdn.microsoft.com/db/39168D7E-077B-48E7-872C-B232C3E72675/media/de-DE/ProfessionalRetail.img",
            prof2013.ImageUrl());
        Check("Sprache ist ueberall Deutsch",
            legacy.All(r => r.ImageUrl().Contains("/de-DE/", StringComparison.Ordinal)));

        Check("Installer 64 Bit setzt X64",
            hs2016.SetupUrl(64).Contains("platform=X64", StringComparison.Ordinal), hs2016.SetupUrl(64));
        Check("Installer 32 Bit setzt X86",
            hs2016.SetupUrl(32).Contains("platform=X86", StringComparison.Ordinal), hs2016.SetupUrl(32));
        Check("2016 benutzt O16GA",
            hs2016.SetupUrl(64).Contains("version=O16GA", StringComparison.Ordinal));
        Check("2013 benutzt O15GA",
            prof2013.SetupUrl(64).Contains("version=O15GA", StringComparison.Ordinal));
        Check("Installer-Sprache ist Deutsch",
            hs2016.SetupUrl(64).Contains("language=de-de", StringComparison.Ordinal));

        // Professional Plus 2013 kam ueber das Home-Use-Programm - ohne diese
        // Kennung wird der Aufruf abgewiesen.
        Check("ProPlus 2013 traegt die Zusatzkennung",
            pp2013.SetupUrl(64).Contains("Source=O15HUP", StringComparison.Ordinal), pp2013.SetupUrl(64));
        Check("alle uebrigen ohne Zusatzkennung",
            legacy.Where(r => r != pp2013).All(r => !r.SetupUrl(64).Contains("Source=", StringComparison.Ordinal)));
        Check("nur ProPlus 2013 hat einen Warnhinweis",
            legacy.Count(r => !string.IsNullOrEmpty(r.CaveatDe)) == 1);

        Check("Url() liefert bei Medium die .img-Adresse",
            hs2016.Url(LegacyDownloadKind.Image, 64) == hs2016.ImageUrl());
        Check("Url() liefert bei Installer die Starter-Adresse",
            hs2016.Url(LegacyDownloadKind.Setup, 32) == hs2016.SetupUrl(32));

        Check("Klartextnamen werden uebersetzt",
            hs2016.EditionNameDe == "Home and Student"
            && pp2013.EditionNameDe == "Professional Plus", hs2016.EditionNameDe);

        Check("leere Suche zeigt alle alten Fassungen",
            legacy.Count(r => LegacyOfficeReleases.Matches(r, "")) == 8);
        Check("Suche nach Jahr filtert",
            legacy.Count(r => LegacyOfficeReleases.Matches(r, "2013")) == 4);
        Check("Suche nach Edition filtert",
            legacy.Count(r => LegacyOfficeReleases.Matches(r, "professional plus")) == 2);
        Check("Suche mit zwei Begriffen filtert",
            legacy.Count(r => LegacyOfficeReleases.Matches(r, "2016 student")) == 1);
        Check("unpassende Suche blendet alles aus",
            legacy.Count(r => LegacyOfficeReleases.Matches(r, "2021")) == 0);

        // Keine dieser Adressen darf in den Paketordner zeigen.
        Check("es wird nichts im Paketordner abgelegt",
            legacy.All(r => r.ImageUrl().StartsWith("https://", StringComparison.Ordinal)
                            && r.SetupUrl(64).StartsWith("https://", StringComparison.Ordinal)));

        Console.WriteLine("\n== Arbeitsordner aufraeumen ==");
        Check("Arbeitsordner liegt unter TEMP",
            TempWorkspace.BaseRoot == Path.Combine(Path.GetTempPath(), "OfficeInstall"), TempWorkspace.BaseRoot);
        Check("jedes Fenster hat seinen eigenen Arbeitsordner",
            TempWorkspace.Root == Path.Combine(TempWorkspace.BaseRoot, Environment.ProcessId.ToString()),
            TempWorkspace.Root);
        Check("Deployment Tool bekommt einen Unterordner",
            TempWorkspace.DeploymentFolder == Path.Combine(TempWorkspace.Root, "odt"),
            TempWorkspace.DeploymentFolder);
        Check("InstallRunner benutzt denselben Ordner",
            InstallRunner.DefaultTempRoot == TempWorkspace.Root);
        Check("DeploymentToolService benutzt denselben Unterordner",
            DeploymentToolService.DefaultWorkFolder == TempWorkspace.DeploymentFolder);

        // Ein Arbeitsordner mit Inhalt - darunter eine schreibtmpSchreibschutze
        // Datei, wie sie der Selbstentpacker des Deployment Tools ablegt.
        Directory.CreateDirectory(Path.Combine(TempWorkspace.Root, "x64", "Current"));
        Touch(Path.Combine(TempWorkspace.Root, "x64", "Current", "configuration.xml"), new string('x', 4096));
        var tmpSchreibschutz = Path.Combine(TempWorkspace.Root, "odt", "setup.exe");
        Touch(tmpSchreibschutz, new string('x', 2048));
        File.SetAttributes(tmpSchreibschutz, FileAttributes.ReadOnly);

        Check("Arbeitsordner wird als vorhanden erkannt", TempWorkspace.Exists);

        var tmpErgebnis = TempWorkspace.Clear();
        Check("Aufräumen meldet Erfolg", tmpErgebnis.Removed && tmpErgebnis.Error is null, tmpErgebnis.Error);
        Check("auch der schreibgeschützte Inhalt ist weg", !Directory.Exists(TempWorkspace.Root));
        Check("die freigewordene Menge wird gemeldet", tmpErgebnis.FreedBytes >= 6144,
            tmpErgebnis.FreedBytes.ToString());

        var tmpNochmal = TempWorkspace.Clear();
        Check("zweites Aufräumen ist wirkungslos statt fehlerhaft",
            !tmpNochmal.Removed && tmpNochmal.Error is null, tmpNochmal.Error);
        Check("nicht vorhandener Arbeitsordner meldet nichts", !TempWorkspace.Exists);

        // Reste anderer Fenster: beendet -> weg, laufend (hier: das eigene) -> bleibt.
        var fremdBeendet = Path.Combine(TempWorkspace.BaseRoot, "2147483000");
        var altesLayout = Path.Combine(TempWorkspace.BaseRoot, "x64");
        Touch(Path.Combine(fremdBeendet, "configuration.xml"), new string('x', 1000));
        Touch(Path.Combine(altesLayout, "Current", "configuration.xml"), new string('x', 1000));
        Touch(Path.Combine(TempWorkspace.BaseRoot, "rest.txt"), "x");
        Touch(Path.Combine(TempWorkspace.Root, "x64", "Current", "configuration.xml"));
        using (var anderesProgramm = OperatingSystem.IsWindows()
                   ? System.Diagnostics.Process.Start("ping", "-n 6 127.0.0.1")
                   : System.Diagnostics.Process.Start("sleep", "5"))
        {
            var fremdLaufend = Path.Combine(TempWorkspace.BaseRoot, anderesProgramm.Id.ToString());
            Touch(Path.Combine(fremdLaufend, "a.xml"));

            var reste = TempWorkspace.ClearStale();
            Check("Reste beendeter Fenster werden entfernt",
                reste.Removed && reste.Error is null && !Directory.Exists(fremdBeendet), reste.Error);
            Check("Reste aus der Zeit vor den getrennten Ordnern werden entfernt",
                !Directory.Exists(altesLayout) && !File.Exists(Path.Combine(TempWorkspace.BaseRoot, "rest.txt")));
            Check("der eigene Arbeitsordner bleibt",
                File.Exists(Path.Combine(TempWorkspace.Root, "x64", "Current", "configuration.xml")));
            Check("fremdes Programm unter wiederverwendeter Nummer schuetzt den Ordner nicht",
                !Directory.Exists(fremdLaufend));
            anderesProgramm.Kill();
        }
        TempWorkspace.Clear();
        Check("Aufraeumen des eigenen Ordners laesst den Oberordner stehen",
            !TempWorkspace.Exists && Directory.Exists(TempWorkspace.BaseRoot));

        Console.WriteLine("\n== Selbstaktualisierung ==");
        var manifestJson = """
            {
              "schema": 1,
              "program": {
                "version": "1.0.1",
                "released": "2026-08-17",
                "url": "https://github.com/Tinnitus97/OfficeInstall/releases/download/v1.0.1/OfficeInstall.exe",
                "sha256": "AABBCC",
                "notes": "https://github.com/Tinnitus97/OfficeInstall/releases/tag/v1.0.1"
              },
              "configs": { "version": 70, "date": "16.08.2026",
                           "url": "https://example.invalid/configs.zip", "sha256": "ddeeff" }
            }
            """;

        var manifest = UpdateService.Parse(manifestJson);
        Check("update.json wird gelesen", manifest.Success, manifest.Error);
        Check("Programmversion wird erkannt", manifest.Program.Version == "1.0.1", manifest.Program.Version);
        Check("Adresse wird uebernommen",
            manifest.Program.Url.EndsWith("/OfficeInstall.exe", StringComparison.Ordinal));
        Check("Pruefsumme wird kleingeschrieben",
            manifest.Program.Sha256 == "aabbcc", manifest.Program.Sha256);
        Check("Zahl als Paketstand wird gelesen", manifest.Configs.Version == "70", manifest.Configs.Version);
        Check("beide Teile sind brauchbar", manifest.Program.IsUsable && manifest.Configs.IsUsable);

        var kaputt = UpdateService.Parse("{ das ist kein JSON");
        Check("unbrauchbare Datei meldet Misserfolg", !kaputt.Success && kaputt.Error is not null);

        var leer = UpdateService.Parse("{}");
        Check("fehlende Angaben ergeben leere Teile",
            leer.Success && !leer.Program.IsUsable && !leer.Configs.IsUsable);

        Check("neuere Programmversion wird erkannt", UpdateService.IsProgramNewer("1.0.0", "1.0.1"));
        Check("gleiche Version ist kein Update", !UpdateService.IsProgramNewer("1.0.0", "1.0.0"));
        Check("aeltere Version ist kein Update", !UpdateService.IsProgramNewer("1.2.0", "1.0.1"));
        Check("unlesbare Version ist kein Update", !UpdateService.IsProgramNewer("1.0.0", "keine Ahnung"));

        Check("hoeherer Paketstand wird erkannt", UpdateService.IsConfigsNewer("69", "70"));
        Check("gleicher Paketstand ist kein Update", !UpdateService.IsConfigsNewer("70", "70"));
        Check("niedrigerer Paketstand ist kein Update", !UpdateService.IsConfigsNewer("71", "70"));
        Check("ohne oertlichen Stand gilt es als Update", UpdateService.IsConfigsNewer(null, "70"));
        Check("ohne Angabe online kein Update", !UpdateService.IsConfigsNewer("69", ""));
        Check("Zahlen werden als Zahlen verglichen", UpdateService.IsConfigsNewer("9", "10"));

        // --- Was aus dem Archiv uebernommen werden darf ---
        Check("XML unter x64 ist erlaubt",
            UpdateService.IsAllowed("x64/PerpetualVL2021/configuration_Standard_2021.xml"));
        Check("XML unter x32 ist erlaubt",
            UpdateService.IsAllowed("x32/Current/configuration_Student_2019.xml"));
        Check("Version.txt ist erlaubt", UpdateService.IsAllowed("Versionscheck/Version.txt"));
        Check("EXE ist nicht erlaubt", !UpdateService.IsAllowed("x64/Current/setup.exe"));
        Check("Cab-Datei ist nicht erlaubt", !UpdateService.IsAllowed("x64/Current/Office/Data/v64.cab"));
        Check("zu tiefe Ablage ist nicht erlaubt",
            !UpdateService.IsAllowed("x64/Current/unterordner/datei.xml"));
        Check("Ausbruch nach oben ist nicht erlaubt",
            !UpdateService.IsAllowed("../../windows/system32/boese.xml"));
        Check("Ausbruch mit Rueckwaertsschraegstrich ist nicht erlaubt",
            !UpdateService.IsAllowed(@"x64\..\..\boese.xml"));
        Check("fremder Ordner ist nicht erlaubt", !UpdateService.IsAllowed("x86/Current/datei.xml"));

        // --- Einspielen ---
        var zielOrdner = Path.Combine(root, "einspielen");
        Directory.CreateDirectory(Path.Combine(zielOrdner, "x64", "Current", "Office", "Data", "16.0.1.1"));
        Touch(Path.Combine(zielOrdner, "x64", "Current", "Office", "Data", "16.0.1.1", "i640.cab"),
              new string('x', 512));

        var archiv = Path.Combine(root, "configs.zip");
        var bauOrdner = Path.Combine(root, "zipbau");
        WriteConfig(Path.Combine(bauOrdner, "x64", "Current", "configuration_neu.xml"),
                    "HomeStudent2024Retail", 64, "Current");
        Touch(Path.Combine(bauOrdner, "Versionscheck", "Version.txt"), "70|16.08.2026");
        Touch(Path.Combine(bauOrdner, "x64", "Current", "boese.exe"), "MZ");
        System.IO.Compression.ZipFile.CreateFromDirectory(bauOrdner, archiv);

        var eingespielt = UpdateService.ApplyConfigs(archiv, zielOrdner);
        Check("Einspielen meldet Erfolg", eingespielt.Success, eingespielt.Error);
        Check("die XML-Datei wurde geschrieben",
            File.Exists(Path.Combine(zielOrdner, "x64", "Current", "configuration_neu.xml")));
        Check("Version.txt wurde geschrieben",
            File.ReadAllText(Path.Combine(zielOrdner, "Versionscheck", "Version.txt")).StartsWith("70"));
        Check("die EXE aus dem Archiv wurde uebergangen",
            !File.Exists(Path.Combine(zielOrdner, "x64", "Current", "boese.exe")) && eingespielt.Skipped >= 1,
            eingespielt.Skipped.ToString());
        Check("der Offline-Bestand blieb unangetastet",
            File.Exists(Path.Combine(zielOrdner, "x64", "Current", "Office", "Data", "16.0.1.1", "i640.cab")));
        Check("der neue Paketstand wird gelesen",
            OfficeCatalog.ReadPackageVersion(zielOrdner)?.Version == "70",
            OfficeCatalog.ReadPackageVersion(zielOrdner)?.Version);

        // --- Pruefsumme ---
        var summeDatei = Path.Combine(root, "summe.txt");
        File.WriteAllText(summeDatei, "abc");
        Check("SHA256 wird richtig gebildet",
            UpdateService.ComputeSha256(summeDatei)
                == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            UpdateService.ComputeSha256(summeDatei));

        Check("Arbeitsordner liegt neben dem Arbeitsordner des Programms",
            UpdateService.WorkFolder != TempWorkspace.BaseRoot
            && !UpdateService.WorkFolder.StartsWith(TempWorkspace.BaseRoot + Path.DirectorySeparatorChar,
                                                    StringComparison.OrdinalIgnoreCase),
            UpdateService.WorkFolder);

        Check("ohne Hinterlegung gilt die Standardadresse",
            UpdateService.ResolveManifestUrl(root) == UpdateService.DefaultManifestUrl);
        Touch(Path.Combine(root, "Versionscheck", "Update-Url.txt"),
              "# eigener Server\nhttps://intern.example.test/office/update.json\n");
        Check("hinterlegte Adresse wird benutzt",
            UpdateService.ResolveManifestUrl(root) == "https://intern.example.test/office/update.json",
            UpdateService.ResolveManifestUrl(root));
        Touch(Path.Combine(root, "Versionscheck", "Update-Url.txt"),
              "http://intern.example.test/office/update.json\n");
        Check("unverschluesselte hinterlegte Adresse wird uebergangen",
            UpdateService.ResolveManifestUrl(root) == UpdateService.DefaultManifestUrl,
            UpdateService.ResolveManifestUrl(root));
        File.Delete(Path.Combine(root, "Versionscheck", "Update-Url.txt"));

        // --- Ohne Pruefsumme oder ueber http wird nichts eingespielt ---
        var ohneSumme = UpdateService.Parse("""
            { "program": { "version": "9.9.9", "url": "https://example.invalid/OfficeInstall.exe" } }
            """);
        Check("Programmeintrag ohne Pruefsumme ist unbrauchbar", !ohneSumme.Program.IsUsable);
        var ueberHttp = UpdateService.Parse("""
            { "program": { "version": "9.9.9", "url": "http://example.invalid/OfficeInstall.exe", "sha256": "aa" } }
            """);
        Check("Programmeintrag ueber http ist unbrauchbar", !ueberHttp.Program.IsUsable);
        Check("https gilt als sicher", UpdateService.IsSecureUrl("https://example.invalid/a.zip"));
        Check("http gilt nicht als sicher", !UpdateService.IsSecureUrl("http://example.invalid/a.zip"));
        Check("Unsinn gilt nicht als sicher", !UpdateService.IsSecureUrl("https//kaputt"));

        var ladeZiel = Path.Combine(root, "geladen.exe");
        var ohneSummeGeladen = UpdateService.DownloadAsync(
            "https://example.invalid/OfficeInstall.exe", "", ladeZiel, System.Threading.CancellationToken.None).Result;
        Check("Laden ohne Pruefsumme wird verweigert",
            !ohneSummeGeladen.Success && !File.Exists(ladeZiel), ohneSummeGeladen.Error);
        var httpGeladen = UpdateService.DownloadAsync(
            "http://example.invalid/OfficeInstall.exe", "aa", ladeZiel, System.Threading.CancellationToken.None).Result;
        Check("Laden ueber http wird verweigert",
            !httpGeladen.Success && !File.Exists(ladeZiel), httpGeladen.Error);

        Console.WriteLine("\n== Programmaustausch ==");
        var tauschOrdner = Path.Combine(root, "tausch");
        Directory.CreateDirectory(tauschOrdner);
        var neueExe = Path.Combine(tauschOrdner, "neu.exe");
        var alteExe = Path.Combine(tauschOrdner, "OfficeInstall.exe");
        File.WriteAllText(neueExe, "neu");
        File.WriteAllText(alteExe, "alt");

        Check("normaler Start ist kein Austauschlauf", !SelfUpdate.TryRunSwap(Array.Empty<string>()));
        Check("fremde Argumente sind kein Austauschlauf", !SelfUpdate.TryRunSwap(new[] { "--irgendwas", "a", "1" }));
        Check("unvollstaendiger Austauschaufruf wird nicht ausgefuehrt",
            !SelfUpdate.TryRunSwap(new[] { SelfUpdate.SwapArgument, alteExe }));

        var tausch = SelfUpdate.Swap(neueExe, alteExe, 0, TimeSpan.FromSeconds(1));
        Check("Austausch ersetzt die alte Datei",
            tausch is null && File.ReadAllText(alteExe) == "neu", tausch);

        var tauschBeendet = SelfUpdate.Swap(neueExe, alteExe, int.MaxValue - 7, TimeSpan.FromSeconds(1));
        Check("nicht (mehr) laufender Vorgang haelt den Austausch nicht auf", tauschBeendet is null, tauschBeendet);

        var ohneQuelle = SelfUpdate.Swap(Path.Combine(tauschOrdner, "fehlt.exe"), alteExe, 0, TimeSpan.Zero);
        Check("fehlende neue Fassung wird gemeldet",
            ohneQuelle is not null && ohneQuelle.Contains("fehlt"), ohneQuelle);

        var ohneZiel = SelfUpdate.Swap(neueExe, Path.Combine(tauschOrdner, "gibt", "es", "nicht.exe"),
                                       0, TimeSpan.Zero, attempts: 2, delay: TimeSpan.FromMilliseconds(10));
        Check("nicht beschreibbares Ziel wird nach den Versuchen gemeldet",
            ohneZiel is not null && ohneZiel.Contains("ließ sich nicht ersetzen"), ohneZiel);

        if (!OperatingSystem.IsWindows())
        {
            using var wartend = System.Diagnostics.Process.Start("sleep", "1");
            File.WriteAllText(alteExe, "alt");
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            var nachWarten = SelfUpdate.Swap(neueExe, alteExe, wartend.Id, TimeSpan.FromSeconds(10));
            Check("Austausch wartet, bis das alte Programm beendet ist",
                nachWarten is null && uhr.ElapsedMilliseconds >= 700 && File.ReadAllText(alteExe) == "neu",
                $"{nachWarten} nach {uhr.ElapsedMilliseconds} ms");
        }

        try { File.Delete(SelfUpdate.FailureFile); } catch { }
        Check("ohne Fehlerdatei gibt es nichts zu melden", SelfUpdate.TakeFailure() is null);
        Directory.CreateDirectory(UpdateService.WorkFolder);
        File.WriteAllText(SelfUpdate.FailureFile, "Datei gesperrt");
        Check("Fehler des letzten Austauschs wird gemeldet", SelfUpdate.TakeFailure() == "Datei gesperrt");
        Check("und danach nur einmal", SelfUpdate.TakeFailure() is null && !File.Exists(SelfUpdate.FailureFile));

        Console.WriteLine("\n== Leerer oder fremder Ordner ==");
        var empty = OfficeCatalog.Scan(Path.Combine(root, "leer"));
        Check("Ordner ohne x32/x64 liefert leeren Katalog", empty.Entries.Count == 0);
        Check("nicht vorhandener Ordner wirft nicht",
            OfficeCatalog.Scan(Path.Combine(root, "gibt-es-nicht")).Entries.Count == 0);

        Console.WriteLine("\n== Reihenfolge der Installationsbasen ==");
        var reihenfolge = OfficeChannels.All.OrderBy(c => c.SortOrder).Select(c => c.FolderName).ToArray();
        Check("neuestes Office zuerst: Current, 2024, 2021, 2019",
            reihenfolge.SequenceEqual(new[]
            {
                OfficeChannels.Current,
                OfficeChannels.PerpetualVL2024,
                OfficeChannels.PerpetualVL2021,
                OfficeChannels.PerpetualVL2019,
            }),
            string.Join(", ", reihenfolge));
        Check("jede Basis hat eine eigene Nummer",
            OfficeChannels.All.Select(c => c.SortOrder).Distinct().Count() == OfficeChannels.All.Count);

        Console.WriteLine("\n== Fortschritt beim Herunterladen ==");
        var fortschritt = Path.Combine(root, "paket", "x64", "Current");
        var daten = Path.Combine(fortschritt, "Office", "Data");

        Check("leerer Ordner ergibt 0 Byte", DownloadProgress.MeasureFolder(daten) == 0);
        Check("nicht vorhandener Ordner wirft nicht",
            DownloadProgress.MeasureFolder(Path.Combine(root, "gibt-es-nicht")) == 0);

        Touch(Path.Combine(daten, "16.0.17928.20216", "a.dat"), new string('x', 1000));
        Touch(Path.Combine(daten, "16.0.17928.20216", "b.dat"), new string('x', 500));
        Touch(Path.Combine(daten, "v64.cab"), "cab");

        Check("Groesse wird ueber Unterordner summiert",
            DownloadProgress.MeasureFolder(Path.Combine(daten, "16.0.17928.20216")) == 1500,
            DownloadProgress.MeasureFolder(Path.Combine(daten, "16.0.17928.20216")).ToString());

        var vorhanden = DownloadProgress.ExistingVersions(fortschritt);
        Check("vorhandene Version wird gefunden", vorhanden.Count == 1);
        Check("Groesse der Version stimmt", vorhanden.Count == 1 && vorhanden[0].Bytes == 1500);

        var neueVersion = new Version("16.0.18000.20000");

        Console.WriteLine("\n-- Fall 2: Aktualisierung eines Bestandes --");
        var planUpdate = DownloadProgress.Plan(fortschritt, neueVersion);
        Check("beobachtet wird der Ordner der Zielversion",
            planUpdate.Folder == Path.Combine(daten, neueVersion.ToString()), planUpdate.Folder);
        Check("die Aktualisierung wird als solche erkannt", planUpdate.IsUpdate);
        Check("bei einer Aktualisierung gibt es KEINEN Nenner",
            planUpdate.Reference == 0, planUpdate.Reference.ToString());

        var berichtUpdate = new DownloadProgressReport
        {
            Bytes = 1400, ReferenceBytes = planUpdate.Reference, IsUpdate = true, BytesPerSecond = 100
        };
        Check("also auch keine Prozentangabe", berichtUpdate.Percent is null);
        Check("und keine Restzeit", berichtUpdate.Remaining is null);
        Check("ohne Zuwachs meldet der Text die Gegenprobe",
            DownloadProgress.Describe(berichtUpdate).Contains("geprüft", StringComparison.Ordinal),
            DownloadProgress.Describe(berichtUpdate));
        Check("und nennt dabei keine Menge",
            !DownloadProgress.Describe(berichtUpdate).Contains("MB", StringComparison.Ordinal)
            && !DownloadProgress.Describe(berichtUpdate).Contains("GB", StringComparison.Ordinal),
            DownloadProgress.Describe(berichtUpdate));

        Console.WriteLine("\n-- Fall 3: reine Gegenprobe, Zielversion liegt schon vor --");
        // Genau die Lage aus dem Fehlerbericht: x64\PerpetualVL2019 enthaelt
        // bereits 1,7 GB in der angebotenen Version, der Nachbar x32 nur
        // 1,5 GB. Frueher galt das als Erstdownload - Anzeige "1,7 GB von ca.
        // 1,5 GB - 99 %", bevor irgendetwas geschehen war.
        var gleicheVersion = new Version("16.0.17928.20216");
        var planGegenprobe = DownloadProgress.Plan(fortschritt, gleicheVersion,
                                                   DownloadProgress.SiblingSourcePath(fortschritt));
        Check("ein gefuellter Zielordner gilt nicht als Erstdownload",
            planGegenprobe.IsUpdate);
        Check("und liefert deshalb keinen Nenner",
            planGegenprobe.Reference == 0, planGegenprobe.Reference.ToString());

        var gegenprobe = new DownloadProgressReport
        {
            Bytes = 1_700_000_000, AddedBytes = 0, ReferenceBytes = planGegenprobe.Reference,
            IsUpdate = true, Elapsed = TimeSpan.FromSeconds(12)
        };
        Check("kein Anteil ueber 100 Prozent mehr moeglich", gegenprobe.Percent is null);
        Check("keine erfundene Restzeit", gegenprobe.Remaining is null);
        Check("der Text meldet die Gegenprobe statt einer Menge",
            DownloadProgress.Describe(gegenprobe).Contains("geprüft", StringComparison.Ordinal)
            && !DownloadProgress.Describe(gegenprobe).Contains("GB", StringComparison.Ordinal),
            DownloadProgress.Describe(gegenprobe));

        var nachgeladen = new DownloadProgressReport
        {
            Bytes = 2_000_000_000, AddedBytes = 300_000_000, IsUpdate = true,
            BytesPerSecond = 10_000_000, Elapsed = TimeSpan.FromMinutes(1)
        };
        Check("beim Nachladen zaehlt der Zuwachs, nicht der Ordnerinhalt",
            DownloadProgress.Describe(nachgeladen).Contains("nachgeladen", StringComparison.Ordinal)
            && DownloadProgress.Describe(nachgeladen).Contains("286", StringComparison.Ordinal)
            && !DownloadProgress.Describe(nachgeladen).Contains("2.0 GB", StringComparison.Ordinal),
            DownloadProgress.Describe(nachgeladen));

        Console.WriteLine("\n-- Fall 1: erstmaliges Laden --");
        var frisch = Path.Combine(root, "paket", "x32", "Current");
        Directory.CreateDirectory(frisch);

        var planFrisch = DownloadProgress.Plan(frisch, neueVersion,
                                               DownloadProgress.SiblingSourcePath(frisch));
        Check("erstmaliges Laden ist keine Aktualisierung", !planFrisch.IsUpdate);
        Check("der Nachbarordner der anderen Architektur wird gefunden",
            DownloadProgress.SiblingSourcePath(frisch) == fortschritt,
            DownloadProgress.SiblingSourcePath(frisch) ?? "null");
        Check("er liefert die Vergleichsgroesse",
            planFrisch.Reference == 1500, planFrisch.Reference.ToString());

        Check("ohne Nachbarordner gibt es keinen Nenner",
            DownloadProgress.Plan(Path.Combine(root, "ganz-allein"), neueVersion).Reference == 0);
        Check("ein Ordner ausserhalb des x32/x64-Aufbaus hat keinen Nachbarn",
            DownloadProgress.SiblingSourcePath(Path.Combine(root, "irgendwo", "Current")) is null);
        Check("ohne Pfad kein Nachbar", DownloadProgress.SiblingSourcePath(null) is null);

        var ohneNenner = new DownloadProgressReport { Bytes = 4711, ReferenceBytes = 0 };
        Check("ohne Vergleichsgroesse keine Prozentangabe", ohneNenner.Percent is null);
        Check("ohne Vergleichsgroesse keine Restzeit", ohneNenner.Remaining is null);
        Check("Text nennt trotzdem die Menge",
            DownloadProgress.Describe(ohneNenner).Contains("4", StringComparison.Ordinal));

        var halb = new DownloadProgressReport
        {
            Bytes = 500, ReferenceBytes = 1000, BytesPerSecond = 100, Elapsed = TimeSpan.FromSeconds(5)
        };
        Check("Anteil wird berechnet", Math.Abs((halb.Percent ?? 0) - 50) < 0.001);
        Check("Restzeit wird geschaetzt",
            halb.Remaining is { } r && Math.Abs(r.TotalSeconds - 5) < 0.001);
        Check("Text enthaelt die Prozentangabe",
            DownloadProgress.Describe(halb).Contains("%", StringComparison.Ordinal),
            DownloadProgress.Describe(halb));

        var drueber = new DownloadProgressReport { Bytes = 5000, ReferenceBytes = 1000 };
        Check("mehr als erwartet bleibt bei 99 Prozent stehen",
            Math.Abs((drueber.Percent ?? 0) - 99) < 0.001, drueber.Percent?.ToString());
        Check("ueber der Schaetzung gibt es keine Restzeit", drueber.Remaining is null);

        var schnecke = new DownloadProgressReport { Bytes = 1, ReferenceBytes = 10_000_000_000, BytesPerSecond = 1 };
        Check("unrealistische Restzeit wird verschwiegen", schnecke.Remaining is null);

        Check("Dauer unter einer Minute in Sekunden",
            DownloadProgress.FormatDuration(TimeSpan.FromSeconds(42)).Contains("42", StringComparison.Ordinal));
        Check("Dauer unter einer Stunde in Minuten",
            DownloadProgress.FormatDuration(TimeSpan.FromMinutes(7)).Contains("7 min", StringComparison.Ordinal));
        Check("Dauer darueber mit Stunden",
            DownloadProgress.FormatDuration(TimeSpan.FromMinutes(125)).Contains("2 h", StringComparison.Ordinal),
            DownloadProgress.FormatDuration(TimeSpan.FromMinutes(125)));

        Console.WriteLine("\n== Beobachtung eines wachsenden Ordners ==");
        DownloadProgress.Interval = TimeSpan.FromMilliseconds(50);
        DownloadProgress.RateWindow = TimeSpan.FromSeconds(5);

        var wachsend = Path.Combine(root, "wachsend");
        var wachsendData = Path.Combine(wachsend, "Office", "Data");
        var zielVersion = new Version("16.0.19000.10000");
        Touch(Path.Combine(wachsendData, "16.0.18000.10000", "alt.dat"), new string('x', 2000));

        var berichte = new List<DownloadProgressReport>();
        using (var stop = new System.Threading.CancellationTokenSource())
        {
            var beobachtungsPlan = DownloadProgress.Plan(wachsend, zielVersion);
            var lauf = DownloadProgress.WatchAsync(
                beobachtungsPlan,
                new Progress<DownloadProgressReport>(b => { lock (berichte) berichte.Add(b); }),
                stop.Token);

            for (var i = 0; i < 4; i++)
            {
                Touch(Path.Combine(wachsendData, zielVersion.ToString(), $"teil{i}.dat"), new string('x', 500));
                System.Threading.Thread.Sleep(120);
            }

            stop.Cancel();
            try { lauf.Wait(2000); } catch { }
        }

        List<DownloadProgressReport> kopie;
        lock (berichte) kopie = berichte.ToList();

        Check("es kamen Messwerte an", kopie.Count > 0, kopie.Count.ToString());
        Check("die geladene Menge waechst",
            kopie.Count > 0 && kopie[^1].Bytes >= kopie[0].Bytes, 
            kopie.Count > 0 ? $"{kopie[0].Bytes} -> {kopie[^1].Bytes}" : "keine");
        Check("am Ende liegen alle vier Teile im Zielordner",
            kopie.Count > 0 && kopie[^1].Bytes == 2000,
            kopie.Count > 0 ? kopie[^1].Bytes.ToString() : "keine");
        Check("eine Aktualisierung meldet keinen Nenner",
            kopie.Count > 0 && kopie[^1].ReferenceBytes == 0 && kopie[^1].IsUpdate,
            kopie.Count > 0 ? kopie[^1].ReferenceBytes.ToString() : "keine");
        Check("die Beobachtung endet mit dem Abbruch",
            kopie.Count < 100, kopie.Count.ToString());
        Check("Zuwachs zaehlt ab der Planung, nicht ab dem ersten Messwert",
            kopie.Count > 0 && kopie[^1].AddedBytes == 2000,
            kopie.Count > 0 ? kopie[^1].AddedBytes.ToString() : "keine");

        // Erstdownload: Was das Deployment Tool gleich nach dem Start schreibt,
        // darf nicht als "schon vorhanden" gelten.
        var erstmals = Path.Combine(root, "erstmals");
        var erstPlan = DownloadProgress.Plan(erstmals, zielVersion);
        Touch(Path.Combine(erstmals, "Office", "Data", zielVersion.ToString(), "sofort.dat"), new string('x', 700));
        Check("vor dem Start geplant bleibt ein Erstdownload ein Erstdownload",
            !erstPlan.IsUpdate && erstPlan.Baseline == 0);

        Console.WriteLine("\n== Reihenfolge nach Office-Generation ==");

        Check("Microsoft 365 Apps steht ganz oben",
            OfficeProductNames.ReleaseYear("O365ProPlusRetail") == OfficeProductNames.Microsoft365);
        Check("Microsoft 365 Business steht ganz oben",
            OfficeProductNames.ReleaseYear("O365BusinessRetail") == OfficeProductNames.Microsoft365);
        Check("Jahr aus der Retail-Kennung",
            OfficeProductNames.ReleaseYear("HomeBusiness2024Retail") == 2024);
        Check("Jahr aus der Volumen-Kennung",
            OfficeProductNames.ReleaseYear("ProPlus2019Volume") == 2019);
        Check("Jahr ersatzweise aus dem Anzeigenamen",
            OfficeProductNames.ReleaseYear("EigeneKennung", "Office 2021 Sonderfall") == 2021);
        Check("365 im Anzeigenamen zaehlt auch",
            OfficeProductNames.ReleaseYear("Unbekannt", "Microsoft 365 Single/Family")
                == OfficeProductNames.Microsoft365);
        Check("ohne Jahr kommt 0 - der Eintrag landet unten",
            OfficeProductNames.ReleaseYear("IrgendwasRetail") == 0);
        Check("die 365 in O365 wird nicht als Jahr gelesen",
            OfficeProductNames.ReleaseYear("O365HomePremRetail") == OfficeProductNames.Microsoft365);
        Check("eine laengere Zahl liefert kein Jahr",
            OfficeProductNames.ReleaseYear("Build20240815Retail") == 0,
            OfficeProductNames.ReleaseYear("Build20240815Retail").ToString());

        // Genau der Bestand aus dem Paketordner - alle vier Basen zusammen.
        // Geprueft wird die Reihenfolge, wie sie RebuildEntries erzeugt.
        var paket = new (string Id, int Basis)[]
        {
            ("HomeStudent2019Retail",  1), ("HomeBusiness2019Retail", 1),
            ("HomeStudent2021Retail",  1), ("HomeBusiness2021Retail", 1),
            ("Home2024Retail",         1), ("HomeBusiness2024Retail", 1),
            ("O365BusinessRetail",     1), ("O365ProPlusRetail",      1),
            ("ProPlus2024Volume",      2), ("Standard2024Volume",     2),
            ("ProPlus2021Volume",      3), ("Standard2021Volume",     3),
            ("ProPlus2019Volume",      4), ("Standard2019Volume",     4),
        };

        var sortiert = paket
            .OrderByDescending(e => OfficeProductNames.ReleaseYear(e.Id, OfficeProductNames.Display(e.Id)))
            .ThenBy(e => OfficeProductNames.EditionRank(e.Id, OfficeProductNames.Display(e.Id)))
            .ThenBy(e => e.Basis)
            .ThenBy(e => OfficeProductNames.Display(e.Id), StringComparer.OrdinalIgnoreCase)
            .Select(e => OfficeProductNames.Display(e.Id))
            .ToArray();

        var erwartet = new[]
        {
            "Microsoft 365 Apps for Enterprise",
            "Microsoft 365 Business",

            "Office 2024 Home",
            "Office 2024 Home und Business",
            "Office 2024 Pro Plus",
            "Office 2024 Standard",

            "Office 2021 Home und Student",
            "Office 2021 Home und Business",
            "Office 2021 Pro Plus",
            "Office 2021 Standard",

            "Office 2019 Home und Student",
            "Office 2019 Home und Business",
            "Office 2019 Pro Plus",
            "Office 2019 Standard",
        };

        for (var i = 0; i < erwartet.Length; i++)
        {
            Check($"Platz {i + 1}: {erwartet[i]}",
                i < sortiert.Length && sortiert[i] == erwartet[i],
                i < sortiert.Length ? sortiert[i] : "fehlt");
        }

        Check("die Liste hat genau so viele Zeilen wie das Paket",
            sortiert.Length == erwartet.Length, sortiert.Length.ToString());

        Console.WriteLine("\n== Rang der Ausstattung ==");
        Check("Home und Student ist die erste Stufe",
            OfficeProductNames.EditionRank("HomeStudent2021Retail") == 1);
        Check("Home ohne Zusatz zaehlt genauso",
            OfficeProductNames.EditionRank("Home2024Retail") == 1);
        Check("Home und Business steht dahinter",
            OfficeProductNames.EditionRank("HomeBusiness2021Retail") == 2);
        Check("Home und Business wird nicht als reines Business gelesen",
            OfficeProductNames.EditionRank("HomeBusiness2021Retail")
                < OfficeProductNames.EditionRank("O365BusinessRetail"));
        Check("Pro Plus vor Business",
            OfficeProductNames.EditionRank("O365ProPlusRetail")
                < OfficeProductNames.EditionRank("O365BusinessRetail"));
        Check("Standard kommt zuletzt",
            OfficeProductNames.EditionRank("Standard2019Volume") == 5);
        Check("Unbekanntes landet dahinter",
            OfficeProductNames.EditionRank("ProjectPro2021Volume") == OfficeProductNames.UnknownEdition,
            OfficeProductNames.EditionRank("ProjectPro2021Volume").ToString());
        Check("ersatzweise wird der Klartextname gelesen",
            OfficeProductNames.EditionRank("EigeneKennung", "Office 2021 Home und Student") == 1);

        Console.WriteLine("\n== Installiertes Office erkennen ==");
        const string cdnCurrent = "http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60";
        const string cdnMonthlyEnterprise = "http://officecdn.microsoft.com/pr/55336b82-a18d-4dd6-b5f6-9e5095c314a6";
        const string cdnVl2019 = "http://officecdn.microsoft.com/pr/f2e724c1-748f-4b47-8fb8-8e0d210e9208";

        Check("Kanal aus der CDN-Adresse", InstalledOffice.ChannelFromUrl(cdnCurrent) == "Current");
        Check("Grossschreibung in der Kennung stoert nicht",
            InstalledOffice.ChannelFromUrl("http://officecdn.microsoft.com/pr/5030841D-C919-4594-8D2D-84AE4F96E58E")
                == "PerpetualVL2021");
        Check("Kanal, den das Paket nicht anbietet, wird trotzdem erkannt",
            InstalledOffice.ChannelFromUrl(cdnMonthlyEnterprise) == "MonthlyEnterprise");
        Check("unbekannte Kennung ergibt keinen Kanal",
            InstalledOffice.ChannelFromUrl("http://officecdn.microsoft.com/pr/00000000-0000-0000-0000-000000000000") is null);
        Check("ohne Adresse kein Kanal", InstalledOffice.ChannelFromUrl(null) is null);

        Check("Monthly Enterprise gehoert zur Familie Current",
            InstalledOffice.ChannelFamily("MonthlyEnterprise") == "Current");
        Check("jede Volumenlizenz ist ihre eigene Familie",
            InstalledOffice.ChannelFamily("PerpetualVL2019") == "PerpetualVL2019");
        Check("Familie aus den Produkten: eine Volumenlizenz entscheidet",
            InstalledOffice.ChannelFamilyFromProducts(new[] { "VisioProRetail", "ProPlus2019Volume" }) == "PerpetualVL2019");
        Check("Familie aus den Produkten: sonst Retail",
            InstalledOffice.ChannelFamilyFromProducts(new[] { "O365ProPlusRetail", "HomeBusiness2021Retail" }) == "Current");

        var m365 = InstalledOffice.ClickToRunFromValues(
            " O365ProPlusRetail , VisioProRetail,o365proplusretail", "x86", "16.0.18129.20158", null, cdnCurrent);
        Check("Click-to-Run: Produkte getrennt, doppelte entfernt",
            m365 is not null && m365.ProductIds.SequenceEqual(new[] { "O365ProPlusRetail", "VisioProRetail" }),
            m365 is null ? "null" : string.Join(",", m365.ProductIds));
        Check("Click-to-Run: x86 ist 32 Bit", m365?.Architecture == 32);
        Check("Click-to-Run: Kanal aus CDNBaseUrl", m365?.Channel == "Current", m365?.Channel);
        Check("Click-to-Run: UpdateChannel gewinnt gegen CDNBaseUrl",
            InstalledOffice.ClickToRunFromValues("O365ProPlusRetail", "x64", null, cdnMonthlyEnterprise, cdnCurrent)?.Channel
                == "MonthlyEnterprise");
        Check("Click-to-Run: ohne Produkte keine Installation",
            InstalledOffice.ClickToRunFromValues("", "x64", "16.0.1", null, cdnCurrent) is null
            && InstalledOffice.ClickToRunFromValues(null, "x64", null, null, null) is null);

        const string setupController =
            "\"C:\\Program Files\\Common Files\\Microsoft Shared\\OFFICE16\\Office Setup Controller\\setup.exe\" " +
            "/uninstall PROPLUS /dll OSETUP.DLL";
        var proPlus2016 = InstalledOffice.MsiFromUninstallEntry(
            "Office16.PROPLUS", "Microsoft Office Professional Plus 2016", "16.0.4266.1001", setupController, 0, 32);
        Check("MSI-Office wird am Setup Controller erkannt",
            proPlus2016 is { Kind: InstalledOfficeKind.Msi, Architecture: 32, Version: "16.0.4266.1001" });
        Check("versteckte Komponenten zaehlen nicht",
            InstalledOffice.MsiFromUninstallEntry("{90160000-0011-0000-0000-0000000FF1CE}",
                "Microsoft Office Professional Plus 2016", "16.0", setupController, 1, 32) is null);
        Check("die Access-Datenbankengine zaehlt nicht",
            InstalledOffice.MsiFromUninstallEntry("{90160000-00D1-0409-1000-0000000FF1CE}",
                "Microsoft Access database engine 2016 (English)", "16.0.5044.1000",
                "MsiExec.exe /X{90160000-00D1-0409-1000-0000000FF1CE}", 0, 64) is null);
        Check("ein Click-to-Run-Eintrag zaehlt nicht als MSI",
            InstalledOffice.MsiFromUninstallEntry("O365ProPlusRetail - de-de", "Microsoft 365 Apps for enterprise - de-de",
                "16.0.18129.20158",
                "\"C:\\Program Files\\Common Files\\Microsoft Shared\\ClickToRun\\OfficeClickToRun.exe\" " +
                "scenario=install scenariosubtype=ARP productstoremove=O365ProPlusRetail.16_de-de_x-none", 0, 64) is null);
        Check("ohne Namen kein Eintrag",
            InstalledOffice.MsiFromUninstallEntry("Office16.PROPLUS", " ", "16.0", setupController, 0, 64) is null);
        Check("Office 2010 wird erkannt",
            InstalledOffice.MsiFromUninstallEntry("Office14.PROPLUS", "Microsoft Office Professional Plus 2010",
                "14.0.7015.1000", setupController.Replace("OFFICE16", "OFFICE14"), 0, 32) is not null);
        Check("Office 2007 nicht - das entfernt RemoveMSI nicht",
            InstalledOffice.MsiFromUninstallEntry("PROPLUS", "Microsoft Office Professional Plus 2007",
                "12.0.6612.1000", setupController.Replace("OFFICE16", "OFFICE12"), 0, 32) is null);
        Check("ohne DisplayVersion zaehlt der Pfad des Setup Controllers",
            InstalledOffice.MsiFromUninstallEntry("Office15.STANDARD", "Microsoft Office Standard 2013",
                null, setupController.Replace("OFFICE16", "OFFICE15"), 0, 64) is not null
            && InstalledOffice.MsiFromUninstallEntry("STANDARD", "Microsoft Office Standard 2007",
                null, setupController.Replace("OFFICE16", "OFFICE12"), 0, 32) is null);

        Check("ausserhalb von Windows ist der Bestand leer",
            OperatingSystem.IsWindows()
            || InstalledOffice.Scan() is { ClickToRun: null, Msi.Count: 0 });

        Console.WriteLine("\n== Was sich nicht vertraegt ==");
        var bestandM365 = new OfficeInventory { ClickToRun = m365 };

        var andereArchitektur = InstalledOffice.FindConflicts(bestandM365, 64, "Current");
        Check("andere Architektur ist ein Konflikt",
            andereArchitektur.HasClickToRun && !andereArchitektur.HasMsi);
        Check("alle Produkte der Installation werden genannt",
            andereArchitektur.ClickToRun.Count == 2, andereArchitektur.ClickToRun.Count.ToString());
        Check("gleiche Architektur, gleiche Familie: kein Konflikt",
            !InstalledOffice.FindConflicts(bestandM365, 32, "Current").Any);

        var bestandMec = new OfficeInventory
        {
            ClickToRun = InstalledOffice.ClickToRunFromValues("O365ProPlusRetail", "x64", null, cdnMonthlyEnterprise, null)
        };
        Check("Monthly Enterprise vertraegt sich mit Current",
            !InstalledOffice.FindConflicts(bestandMec, 64, "Current").Any);
        Check("Microsoft 365 vertraegt sich nicht mit einer Volumenlizenz",
            InstalledOffice.FindConflicts(bestandMec, 64, "PerpetualVL2024").HasClickToRun);

        var bestand2019 = new OfficeInventory
        {
            ClickToRun = InstalledOffice.ClickToRunFromValues("ProPlus2019Volume", "x64", null, null, cdnVl2019)
        };
        Check("PerpetualVL2019 vertraegt sich nicht mit PerpetualVL2021",
            InstalledOffice.FindConflicts(bestand2019, 64, "PerpetualVL2021").HasClickToRun);
        Check("dieselbe Volumenlizenz: kein Konflikt",
            !InstalledOffice.FindConflicts(bestand2019, 64, "PerpetualVL2019").Any);

        var bestandOhneKanal = new OfficeInventory
        {
            ClickToRun = InstalledOffice.ClickToRunFromValues("Standard2021Volume", "x64", null, null, null)
        };
        Check("ohne Kanal entscheidet das Produkt",
            InstalledOffice.FindConflicts(bestandOhneKanal, 64, "Current").HasClickToRun
            && !InstalledOffice.FindConflicts(bestandOhneKanal, 64, "PerpetualVL2021").Any);

        var ohneArchitektur = new OfficeInventory
        {
            ClickToRun = InstalledOffice.ClickToRunFromValues("O365ProPlusRetail", null, null, null, cdnCurrent)
        };
        Check("unbekannte Architektur wird nicht geraten",
            !InstalledOffice.FindConflicts(ohneArchitektur, 32, "Current").Any);

        var bestandMsi = new OfficeInventory { Msi = new[] { proPlus2016! } };
        var msiKonflikt = InstalledOffice.FindConflicts(bestandMsi, 64, "Current");
        Check("MSI-Office ist immer ein Konflikt", msiKonflikt.HasMsi && !msiKonflikt.HasClickToRun);
        Check("leerer Bestand: kein Konflikt",
            !InstalledOffice.FindConflicts(OfficeInventory.Empty, 64, "Current").Any);

        var beides = InstalledOffice.FindConflicts(
            new OfficeInventory { ClickToRun = m365, Msi = new[] { proPlus2016! } }, 64, "Current");
        Check("je Installation eine Kopfzeile und eine Produktzeile",
            beides.Lines.Count == 4, string.Join(" | ", beides.Lines));
        Check("der Grund steht in der Kopfzeile",
            beides.Lines.Count > 0 && beides.Lines[0].Contains("32 statt 64", StringComparison.Ordinal),
            beides.Lines.FirstOrDefault());
        Check("die Produkte stehen beim Namen darunter",
            beides.Lines.Count > 1
            && beides.Lines[1].Contains("Microsoft 365 Apps for Enterprise", StringComparison.Ordinal)
            && beides.Lines[1].Contains("Visio Professional (Retail)", StringComparison.Ordinal),
            beides.Lines.ElementAtOrDefault(1));
        Check("MSI-Office steht mit seinem eigenen Namen da",
            beides.Lines.Count > 3 && beides.Lines[3].Contains("Professional Plus 2016", StringComparison.Ordinal),
            beides.Lines.ElementAtOrDefault(3));

        Console.WriteLine("\n== RemoveMSI in der Kopie ==");
        var mitRemoveMsi = student.Configuration.FilePath;   // die Vorlagen hier enthalten RemoveMSI
        var msiKopie = Path.Combine(root, "removemsi", "msiKopie.xml");

        OfficeConfiguration.WriteWithoutSourcePath(mitRemoveMsi, msiKopie, "Current");
        Check("ohne Angabe bleibt die Vorlage, wie sie ist", TestXml.HasRemoveMsi(msiKopie));

        OfficeConfiguration.WriteWithoutSourcePath(mitRemoveMsi, msiKopie, "Current", removeMsi: false);
        Check("Behalten nimmt RemoveMSI heraus", !TestXml.HasRemoveMsi(msiKopie));

        OfficeConfiguration.WriteWithSourcePath(mitRemoveMsi, msiKopie, @"C:\quelle", null, "Current", removeMsi: true);
        Check("vorhandenes RemoveMSI wird nicht verdoppelt",
            XDocument.Load(msiKopie).Root!.Elements().Count(e => e.Name.LocalName == "RemoveMSI") == 1);

        var ohneRemoveMsi = Path.Combine(root, "removemsi", "ohne.xml");
        Touch(ohneRemoveMsi, """
            <Configuration>
              <Add OfficeClientEdition="64"><Product ID="O365ProPlusRetail"><Language ID="de-de" /></Product></Add>
              <Display Level="Full" AcceptEULA="TRUE" />
            </Configuration>
            """);
        OfficeConfiguration.WriteWithSourcePath(ohneRemoveMsi, msiKopie, @"C:\quelle", null, "Current", removeMsi: true);
        Check("Entfernen ergaenzt RemoveMSI", TestXml.HasRemoveMsi(msiKopie));
        Check("RemoveMSI steht direkt hinter Add",
            XDocument.Load(msiKopie).Root!.Elements().Select(e => e.Name.LocalName)
                .SequenceEqual(new[] { "Add", "RemoveMSI", "Display" }),
            string.Join(",", XDocument.Load(msiKopie).Root!.Elements().Select(e => e.Name.LocalName)));

        var planBehalten = InstallRunner.Prepare(student, InstallMode.Online, temp, removeMsi: false);
        Check("Prepare reicht Behalten an die Kopie durch",
            !TestXml.HasRemoveMsi(planBehalten.ConfigurationFile));
        var planOffline = InstallRunner.Prepare(vl2021, InstallMode.Offline, temp, removeMsi: false);
        Check("auch offline", !TestXml.HasRemoveMsi(planOffline.ConfigurationFile));
        Check("die Vorlage selbst bleibt unberuehrt", TestXml.HasRemoveMsi(mitRemoveMsi));

        Console.WriteLine("\n== Click-to-Run entfernen ==");
        var entfernen = InstallRunner.PrepareClickToRunRemoval(student, temp);
        var entfernenXml = XDocument.Load(entfernen.ConfigurationFile).Root!;

        Check("eigener Durchlauf mit /configure", entfernen.Arguments.StartsWith("/configure \"", StringComparison.Ordinal),
            entfernen.Arguments);
        Check("als Entfernen gekennzeichnet", entfernen.IsRemoval && entfernen.Verb != "download");
        Check("benutzt die setup.exe der Basis", entfernen.SetupExecutable == student.SetupExecutable);
        Check("entfernt alle Click-to-Run-Produkte",
            entfernenXml.Elements("Remove").Any(e => (string?)e.Attribute("All") == "TRUE"));
        Check("installiert dabei nichts", !entfernenXml.Elements("Add").Any());
        Check("zeigt das Fenster von Microsoft",
            entfernenXml.Elements("Display").Any(e => (string?)e.Attribute("Level") == "Full"));
        Check("schliesst offene Office-Programme",
            entfernenXml.Elements("Property").Any(e => (string?)e.Attribute("Name") == "FORCEAPPSHUTDOWN"
                                                       && (string?)e.Attribute("Value") == "TRUE"));

        var ohneSetup = new CatalogEntry
        {
            Configuration = student.Configuration, Channel = student.Channel, Offline = student.Offline,
            SetupExecutable = null, ClientEdition = student.ClientEdition
        };
        var abgelehnt = false;
        try { InstallRunner.PrepareClickToRunRemoval(ohneSetup, temp); }
        catch (InvalidOperationException) { abgelehnt = true; }
        Check("ohne setup.exe wird nichts vorbereitet", abgelehnt);

        Console.WriteLine("\n== Eingebaute Vorlagen ==");
        var eigene = typeof(Program).Assembly;
        var eingebaut = EmbeddedTemplates.List(eigene);

        Check("die Vorlagen sind eingebaut", EmbeddedTemplates.IsAvailable(eigene));
        Check("alle 28 Vorlagen aus package sind dabei",
            EmbeddedTemplates.Count(eigene) == 28, EmbeddedTemplates.Count(eigene).ToString());
        Check("der Pfad im Paket bleibt erhalten",
            eingebaut.Any(v => v.PackagePath == "x64/PerpetualVL2021/configuration_Standard_2021.xml"),
            string.Join(", ", eingebaut.Take(3).Select(v => v.PackagePath)));
        Check("der Paketstand wird gelesen",
            EmbeddedTemplates.ReadVersion(eigene) is { Version: "69", Date: "16.08.2026" },
            EmbeddedTemplates.ReadVersion(eigene)?.ToString() ?? "null");

        var erstesMal = Path.Combine(root, "nur-die-exe");
        Directory.CreateDirectory(erstesMal);
        var ausgepackt = EmbeddedTemplates.Extract(eigene, erstesMal);
        Check("Auspacken gelingt: 28 Vorlagen und die Versionsdatei",
            ausgepackt.Success && ausgepackt.Written == 29 && ausgepackt.Skipped == 0,
            $"{ausgepackt.Written} geschrieben, {ausgepackt.Skipped} uebergangen, {ausgepackt.Error}");

        var nachAuspacken = OfficeCatalog.Scan(erstesMal);
        Check("danach findet der Katalog alle Vorlagen",
            nachAuspacken.Entries.Count == 28, nachAuspacken.Entries.Count.ToString());
        Check("keine davon ist kaputt", nachAuspacken.Entries.All(e => !e.Configuration.IsBroken));
        Check("beide Architekturen sind vertreten",
            nachAuspacken.ForArchitecture(64).Count() == 14 && nachAuspacken.ForArchitecture(32).Count() == 14);
        Check("der Paketstand steht danach im Ordner",
            OfficeCatalog.ReadPackageVersion(erstesMal) is { Version: "69" });
        Check("eine setup.exe bringt die EXE nicht mit",
            !File.Exists(Path.Combine(erstesMal, "setup.exe")));

        // Der Paketordner endet selbst auf einen Trenner - so kommt er aus
        // AppContext.BaseDirectory, und so sieht das Stammverzeichnis eines
        // USB-Sticks aus ("E:\"). Frueher wurde dann keine Datei geschrieben.
        var mitTrenner = Path.Combine(root, "mit-trenner") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(mitTrenner);
        var trennerErgebnis = EmbeddedTemplates.Extract(eigene, mitTrenner);
        Check("Paketordner mit Trenner am Ende: alles wird geschrieben",
            trennerErgebnis.Success && trennerErgebnis.Written == 29,
            $"{trennerErgebnis.Written} geschrieben, {trennerErgebnis.Error}");
        Check("und landet im richtigen Ordner",
            OfficeCatalog.Scan(mitTrenner).Entries.Count == 28);

        var vorhandenerBestand = Path.Combine(erstesMal, "x64", "Current", "Office", "Data", "v64.cab");
        Touch(vorhandenerBestand, "bestand");
        EmbeddedTemplates.Extract(eigene, erstesMal);
        Check("erneutes Auspacken laesst den Offline-Bestand stehen", File.Exists(vorhandenerBestand));

        Console.WriteLine("\n== Vorlagen schreiben: nur Erlaubtes ==");
        var schutzOrdner = Path.Combine(root, "schutz");
        Directory.CreateDirectory(schutzOrdner);
        Func<Stream> xmlInhalt = () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<Configuration/>"));
        var gemischt = UpdateService.ApplyEntries(new (string, Func<Stream>)[]
        {
            ("x64/Current/gut.xml", xmlInhalt),
            ("x64\\Current\\rueckstrich.xml", xmlInhalt),
            ("x64/../../ausserhalb.xml", xmlInhalt),
            ("setup.exe", xmlInhalt),
            ("x64/Current/tiefer/zu.xml", xmlInhalt),
        }, schutzOrdner);
        Check("Erlaubtes wird geschrieben, auch mit Rueckstrichen",
            gemischt.Written == 2 && File.Exists(Path.Combine(schutzOrdner, "x64", "Current", "rueckstrich.xml")),
            $"{gemischt.Written} geschrieben");
        Check("alles andere wird uebergangen", gemischt.Skipped == 3, gemischt.Skipped.ToString());
        Check("nichts landet ausserhalb des Paketordners",
            !File.Exists(Path.Combine(root, "ausserhalb.xml")) && !File.Exists(Path.Combine(schutzOrdner, "setup.exe")));

        Console.WriteLine($"\nErgebnis: {_passed} bestanden, {_failed} fehlgeschlagen.");

        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        Console.WriteLine("\n== Versionsvergleich ==");
        var lokal = new Version("16.0.17928.20216");
        var neuer = new Version("16.0.20228.20124");
        Check("online neuer als lokal wird erkannt", neuer > lokal);
        Check("gleiche Version gilt nicht als neuer", !(lokal > lokal));

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
            OfficeConfiguration.ReadSourcePath(offlinePlan.ConfigurationFile) == offlinePlan.SourcePath,
            OfficeConfiguration.ReadSourcePath(offlinePlan.ConfigurationFile));
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
            OfficeConfiguration.ReadChannel(offlinePlan.ConfigurationFile) == "PerpetualVL2021",
            OfficeConfiguration.ReadChannel(offlinePlan.ConfigurationFile));

        var onlinePlan = InstallRunner.Prepare(vl2021, InstallMode.Online, temp);
        Check("Online-Plan setzt keinen SourcePath", onlinePlan.SourcePath is null);
        Check("Online-Plan schreibt keine Version fest", onlinePlan.PinnedVersion is null);
        Check("Online-XML enthaelt keine Version",
            OfficeConfiguration.ReadVersion(onlinePlan.ConfigurationFile) is null,
            OfficeConfiguration.ReadVersion(onlinePlan.ConfigurationFile));
        Check("Online-XML enthaelt keinen SourcePath",
            OfficeConfiguration.ReadSourcePath(onlinePlan.ConfigurationFile) is null);
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
        Check("Download ist als solcher erkennbar", downloadPlan.IsDownload);
        Check("Download setzt SourcePath auf den Basis-Ordner",
            downloadPlan.SourcePath == Path.Combine(root, "x64", "PerpetualVL2021"), downloadPlan.SourcePath);
        Check("SourcePath steht auch in der Download-XML",
            OfficeConfiguration.ReadSourcePath(downloadPlan.ConfigurationFile) == downloadPlan.SourcePath);
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
            OfficeConfiguration.CountProducts(basisPlan.ConfigurationFile) == 2,
            OfficeConfiguration.CountProducts(basisPlan.ConfigurationFile).ToString());
        Check("gemeinsame Datei setzt SourcePath",
            OfficeConfiguration.ReadSourcePath(basisPlan.ConfigurationFile)
                == Path.Combine(root, "x64", "Current"));
        Check("gemeinsame Datei benutzt /download", basisPlan.IsDownload);
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
            OfficeConfiguration.ReadChannel(festgelegt) == "Current", OfficeConfiguration.ReadChannel(festgelegt));
        Check("Version wird geschrieben",
            OfficeConfiguration.ReadVersion(festgelegt) == "16.0.17928.20216");
        Check("SourcePath wird geschrieben",
            OfficeConfiguration.ReadSourcePath(festgelegt) == @"C:\Office\Current");

        var ohneVersion = Path.Combine(root, "temp", "ohne_version.xml");
        OfficeConfiguration.WriteWithSourcePath(ohneKanal, ohneVersion, @"C:\Office\Current");
        Check("ohne Angabe bleibt die Datei ohne Version",
            OfficeConfiguration.ReadVersion(ohneVersion) is null);
        Check("ohne Angabe wird auch kein Kanal erfunden",
            OfficeConfiguration.ReadChannel(ohneVersion) is null);

        // Der Ordner sticht die Vorlage: Ein abweichender Kanal in der XML
        // laesst das Deployment Tool sonst mit "This product can't be
        // installed on the selected update channel" abbrechen.
        var kanalKorrigiert = Path.Combine(root, "temp", "kanal_korrigiert.xml");
        OfficeConfiguration.WriteWithSourcePath(
            Path.Combine(root, "x64", "PerpetualVL2021", "configuration_Standard_2021.xml"),
            kanalKorrigiert, @"C:\Office\VL2019", new Version("16.0.10417.20197"), "PerpetualVL2019");
        Check("abweichender Kanal wird auf den Ordner gesetzt",
            OfficeConfiguration.ReadChannel(kanalKorrigiert) == "PerpetualVL2019",
            OfficeConfiguration.ReadChannel(kanalKorrigiert));

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
            OfficeConfiguration.ReadChannel(schiefPlan.ConfigurationFile) == "PerpetualVL2019",
            OfficeConfiguration.ReadChannel(schiefPlan.ConfigurationFile));
        Check("die vorliegende Version wird festgeschrieben",
            OfficeConfiguration.ReadVersion(schiefPlan.ConfigurationFile) == "16.0.10417.20197",
            OfficeConfiguration.ReadVersion(schiefPlan.ConfigurationFile));

        var schiefOnline = InstallRunner.Prepare(schiefEintrag, InstallMode.Online, Path.Combine(schief, "temp"));
        Check("auch online wird der Kanal berichtigt",
            OfficeConfiguration.ReadChannel(schiefOnline.ConfigurationFile) == "PerpetualVL2019",
            OfficeConfiguration.ReadChannel(schiefOnline.ConfigurationFile));
        Check("online bleibt ohne SourcePath",
            OfficeConfiguration.ReadSourcePath(schiefOnline.ConfigurationFile) is null);

        var schiefDownload = InstallRunner.PrepareDownload(schiefEintrag, Path.Combine(schief, "temp"));
        Check("auch der Download benutzt den Ordner-Kanal",
            OfficeConfiguration.ReadChannel(schiefDownload.ConfigurationFile) == "PerpetualVL2019",
            OfficeConfiguration.ReadChannel(schiefDownload.ConfigurationFile));
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
            TempWorkspace.Root == Path.Combine(Path.GetTempPath(), "OfficeInstall"), TempWorkspace.Root);
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
            UpdateService.WorkFolder != TempWorkspace.Root
            && !UpdateService.WorkFolder.StartsWith(TempWorkspace.Root + Path.DirectorySeparatorChar,
                                                    StringComparison.OrdinalIgnoreCase),
            UpdateService.WorkFolder);

        Check("ohne Hinterlegung gilt die Standardadresse",
            UpdateService.ResolveManifestUrl(root) == UpdateService.DefaultManifestUrl);
        Touch(Path.Combine(root, "Versionscheck", "Update-Url.txt"),
              "# eigener Server\nhttps://intern.example.test/office/update.json\n");
        Check("hinterlegte Adresse wird benutzt",
            UpdateService.ResolveManifestUrl(root) == "https://intern.example.test/office/update.json",
            UpdateService.ResolveManifestUrl(root));

        Console.WriteLine("\n== Leerer oder fremder Ordner ==");
        var empty = OfficeCatalog.Scan(Path.Combine(root, "leer"));
        Check("Ordner ohne x32/x64 liefert leeren Katalog", empty.Entries.Count == 0);
        Check("nicht vorhandener Ordner wirft nicht",
            OfficeCatalog.Scan(Path.Combine(root, "gibt-es-nicht")).Entries.Count == 0);

        Console.WriteLine($"\nErgebnis: {_passed} bestanden, {_failed} fehlgeschlagen.");

        try { Directory.Delete(root, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }
}

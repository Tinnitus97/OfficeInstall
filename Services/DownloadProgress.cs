using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OfficeInstall.Services;

/// <summary>Ein Messwert waehrend des Herunterladens.</summary>
public sealed class DownloadProgressReport
{
    /// <summary>Was im beobachteten Ordner bisher liegt.</summary>
    public long Bytes { get; init; }

    /// <summary>
    /// Wie viel seit dem Start dazugekommen ist. Das ist die Zahl, die beim
    /// Aktualisieren zaehlt - der Ordner enthaelt ja schon vorher etwas.
    /// </summary>
    public long AddedBytes { get; init; }

    /// <summary>Erwartete Endgroesse. 0 bedeutet: nicht ermittelbar.</summary>
    public long ReferenceBytes { get; init; }

    /// <summary>Geschaetzte Geschwindigkeit in Byte je Sekunde.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>Seit dem Start vergangene Zeit.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// true, wenn schon ein aelterer Versionsordner vorliegt. Dann waechst der
    /// Zielordner nicht nur durch das, was aus dem Netz kommt - das Deployment
    /// Tool bildet die neue Fassung zum grossen Teil aus dem oertlichen
    /// Bestand. Die Menge im Ordner ist dann kein Mass fuer den Fortschritt.
    /// </summary>
    public bool IsUpdate { get; init; }

    /// <summary>true, wenn seit dem Start nichts dazugekommen ist.</summary>
    public bool NothingAddedYet => AddedBytes <= 0;

    /// <summary>
    /// Anteil in Prozent, oder null wenn es keine Vergleichsgroesse gibt.
    /// Gedeckelt bei 99: Fertig ist der Vorgang erst, wenn das Deployment Tool
    /// sich beendet - nicht, wenn eine geschaetzte Groesse erreicht ist.
    /// </summary>
    public double? Percent =>
        ReferenceBytes <= 0 ? null : Math.Min(99d, Bytes * 100d / ReferenceBytes);

    /// <summary>Geschaetzte Restzeit, oder null wenn nicht berechenbar.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (ReferenceBytes <= 0 || BytesPerSecond <= 0) return null;
            var offen = ReferenceBytes - Bytes;
            if (offen <= 0) return null;

            var sekunden = offen / BytesPerSecond;
            // Ueber zwei Stunden hinaus ist die Schaetzung wertlos.
            return sekunden > 7200 ? null : TimeSpan.FromSeconds(sekunden);
        }
    }
}

/// <summary>
/// Fortschritt beim Herunterladen der Office-Quelldateien.
///
/// WARUM DAS UEBER DEN ORDNER LAEUFT:
/// "setup.exe /download" meldet nichts. Kein Fenster, keine Ausgabe auf der
/// Konsole, kein Rueckkanal - der Vorgang laeuft still und endet irgendwann mit
/// einem Rueckgabewert. Vom Deployment Tool ist also keine Prozentangabe zu
/// bekommen, egal wie man es aufruft.
///
/// Was sich beobachten laesst, ist das Ergebnis: Das Tool legt die Dateien
/// unter &lt;SourcePath&gt;\Office\Data\&lt;Version&gt; ab. Dieser Ordner waechst
/// waehrend des Ladens - und dieses Wachstum ist die einzige ehrliche
/// Fortschrittsanzeige, die sich hier bauen laesst.
///
/// WANN ES EINEN ANTEIL GIBT - UND WANN NICHT:
/// Nur beim ERSTMALIGEN Laden waechst der Zielordner ausschliesslich durch das,
/// was aus dem Netz kommt. Nur dann ist ein Anteil aussagekraeftig, und den
/// Nenner liefert der gleiche Kanal unter der anderen Architektur.
///
/// Beim AKTUALISIEREN eines vorhandenen Bestandes gibt es keinen Anteil. Das
/// Deployment Tool bildet die neue Fassung groesstenteils aus dem oertlichen
/// Bestand; der Ordner ist deshalb in Minuten nahezu voll, waehrend das
/// Nachladen und Pruefen noch lange laeuft. Ein Balken daraus stuende sofort
/// bei 99 % und bliebe dort haengen - genau das soll er nicht.
///
/// Gemessen wird ausserdem der BELEGTE Platz, nicht die logische Dateigroesse;
/// die Begruendung steht bei <see cref="MeasureFolder"/>.
/// </summary>
public static class DownloadProgress
{
    /// <summary>Abstand zwischen zwei Messungen.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Zeitfenster fuer die Geschwindigkeit. Ueber eine einzelne Sekunde
    /// gemessen springt der Wert stark, weil das Tool schubweise schreibt.
    /// </summary>
    public static TimeSpan RateWindow { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Summe des tatsaechlich BELEGTEN Platzes unterhalb des Ordners.
    ///
    /// Bewusst nicht FileInfo.Length: Das Deployment Tool legt seine Zieldateien
    /// unter Umstaenden vorab in voller Groesse an und fuellt sie erst danach.
    /// Die logische Groesse stuende dann sofort auf dem Endwert, waehrend real
    /// noch nichts da ist - der Balken waere binnen Sekunden voll. Der belegte
    /// Platz waechst dagegen mit dem, was wirklich geschrieben wurde.
    ///
    /// Unter Windows liefert GetCompressedFileSize genau diesen Wert (trotz des
    /// Namens nicht nur fuer komprimierte Dateien, sondern auch fuer duenn
    /// besetzte). Anderswo - etwa auf dem Linux-Laeufer der Pruefungen - bleibt
    /// es bei der logischen Groesse.
    /// </summary>
    public static long MeasureFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;

        long summe = 0;
        try
        {
            foreach (var datei in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { summe += MeasureFile(datei); } catch { /* einzelne Datei gesperrt */ }
            }
        }
        catch { /* Ordner nicht vollstaendig lesbar */ }
        return summe;
    }

    /// <summary>Belegter Platz einer einzelnen Datei.</summary>
    public static long MeasureFile(string file)
    {
        var logisch = new FileInfo(file).Length;

        if (!OperatingSystem.IsWindows()) return logisch;

        try
        {
            var niedrig = GetCompressedFileSizeW(file, out var hoch);

            // 0xFFFFFFFF ist zweideutig: entweder ein Fehler oder eine Datei
            // dieser Groesse. Nur mit gesetztem Fehlercode ist es ein Fehler.
            if (niedrig == 0xFFFFFFFF &&
                System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 0)
                return logisch;

            return ((long)hoch << 32) + niedrig;
        }
        catch
        {
            return logisch;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    /// <summary>Der Ordner "Office\Data" unterhalb des SourcePath.</summary>
    public static string DataFolder(string sourcePath)
        => Path.Combine(sourcePath, "Office", "Data");

    /// <summary>
    /// Vorhandene Versionsordner mit ihrer Groesse, groesster zuerst.
    /// </summary>
    public static IReadOnlyList<(Version Version, long Bytes)> ExistingVersions(string sourcePath)
    {
        var ergebnis = new List<(Version, long)>();
        var data = DataFolder(sourcePath);
        if (!Directory.Exists(data)) return ergebnis;

        try
        {
            foreach (var ordner in Directory.GetDirectories(data))
            {
                if (!Version.TryParse(Path.GetFileName(ordner), out var version)) continue;
                ergebnis.Add((version, MeasureFolder(ordner)));
            }
        }
        catch { /* nicht lesbar */ }

        return ergebnis.OrderByDescending(e => e.Item2).ToList();
    }

    /// <summary>
    /// Ergebnis der Vorueberlegung: Was wird beobachtet, woran gemessen, und
    /// handelt es sich um eine Aktualisierung?
    /// </summary>
    public readonly record struct WatchPlan(string Folder, long Reference, bool IsUpdate);

    /// <summary>
    /// Der gleiche Kanal unter der anderen Architektur - also x32 statt x64
    /// und umgekehrt.
    ///
    /// Aufbau des Paketordners: &lt;Wurzel&gt;\x64\Current, &lt;Wurzel&gt;\x32\Current.
    /// Beide enthalten dieselben Produkte in derselben Sprache und sind
    /// deshalb nahezu gleich gross. Beim allerersten Herunterladen einer
    /// Architektur ist der Nachbarordner damit die einzige belastbare
    /// Vergleichsgroesse, die es ueberhaupt gibt.
    ///
    /// Gibt null zurueck, wenn der Aufbau nicht passt oder der Nachbar fehlt.
    /// </summary>
    public static string? SiblingSourcePath(string? baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder)) return null;

        try
        {
            var kanal = Path.GetFileName(Path.TrimEndingDirectorySeparator(baseFolder));
            var architektur = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseFolder));
            if (architektur is null) return null;

            var name = Path.GetFileName(architektur);
            var andere = name.Equals("x64", StringComparison.OrdinalIgnoreCase) ? "x32"
                       : name.Equals("x32", StringComparison.OrdinalIgnoreCase) ? "x64"
                       : null;
            if (andere is null) return null;

            var wurzel = Path.GetDirectoryName(architektur);
            if (wurzel is null) return null;

            var nachbar = Path.Combine(wurzel, andere, kanal);
            return Directory.Exists(nachbar) ? nachbar : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Legt fest, was beobachtet wird und woran gemessen wird.
    ///
    /// ZWEI FAELLE, DIE SICH GRUNDLEGEND UNTERSCHEIDEN:
    ///
    /// 1. ERSTMALIGES LADEN - im Ordner liegt noch keine andere Version.
    ///    Dann waechst der Zielordner ausschliesslich durch das, was aus dem
    ///    Netz kommt. Der Anteil ist also aussagekraeftig, sofern es eine
    ///    Vergleichsgroesse gibt. Die liefert der gleiche Kanal unter der
    ///    anderen Architektur, siehe <see cref="SiblingSourcePath"/>.
    ///
    /// 2. AKTUALISIERUNG - es liegt bereits eine aeltere Version vor.
    ///    Hier gibt es KEINEN Anteil. Das Deployment Tool bildet die neue
    ///    Fassung zum grossen Teil aus dem oertlichen Bestand; der Zielordner
    ///    fuellt sich dadurch in Minuten fast vollstaendig, waehrend die
    ///    eigentliche Arbeit - das Nachladen der Unterschiede und das Pruefen -
    ///    noch laeuft. Ein Balken daraus stuende binnen Kurzem bei 99 % und
    ///    bliebe dort lange stehen. Genau deshalb wird hier gar kein Anteil
    ///    mehr angezeigt, sondern nur die Menge im Ordner und das Tempo.
    /// </summary>
    public static WatchPlan Plan(string sourcePath, Version? targetVersion, string? siblingSourcePath = null)
    {
        var data = DataFolder(sourcePath);

        var andere = ExistingVersions(sourcePath)
            .Where(v => targetVersion is null || v.Version != targetVersion)
            .ToList();

        var ordner = targetVersion is null ? data : Path.Combine(data, targetVersion.ToString());

        // Auch der ZIELORDNER SELBST zaehlt. Wird eine Basis nur geprueft -
        // die vorliegende Version ist bereits die angebotene -, dann liegen
        // dort schon mehrere Gigabyte. Ohne diese Abfrage galt das als
        // Erstdownload: Der Nenner kam aus dem Nachbarordner, und die Anzeige
        // meldete "1,7 GB von ca. 1,5 GB - 99 %", noch bevor irgendetwas
        // geschehen war.
        var zielSchonGefuellt = MeasureFolder(ordner) > 0;

        // Fall 2: Es ist schon etwas da - kein Nenner, keine Prozentangabe.
        if (andere.Count > 0 || zielSchonGefuellt) return new WatchPlan(ordner, 0, true);

        // Fall 1: Vergleich aus dem Nachbarordner der anderen Architektur.
        var referenz = siblingSourcePath is null
            ? 0
            : ExistingVersions(siblingSourcePath).Select(v => v.Bytes).DefaultIfEmpty(0).Max();

        return new WatchPlan(ordner, referenz, false);
    }

    /// <summary>
    /// Beobachtet den Ordner, bis abgebrochen wird, und meldet regelmaessig den
    /// Stand. Laeuft, bis das Token gesetzt wird - der Aufrufer beendet also,
    /// sobald das Deployment Tool fertig ist.
    /// </summary>
    public static async Task WatchAsync(
        string sourcePath,
        Version? targetVersion,
        IProgress<DownloadProgressReport> progress,
        CancellationToken ct,
        string? siblingSourcePath = null)
    {
        var plan = await Task.Run(() => Plan(sourcePath, targetVersion, siblingSourcePath), ct)
                             .ConfigureAwait(false);

        var ordner = plan.Folder;
        var referenz = plan.Reference;

        var start = DateTime.UtcNow;
        var verlauf = new Queue<(DateTime Zeit, long Bytes)>();

        // Ausgangsstand: Alles darueber ist in diesem Lauf dazugekommen.
        long anfang;
        try { anfang = await Task.Run(() => MeasureFolder(ordner), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { anfang = 0; }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            long bytes;
            try { bytes = await Task.Run(() => MeasureFolder(ordner), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { continue; }

            var jetzt = DateTime.UtcNow;
            verlauf.Enqueue((jetzt, bytes));
            while (verlauf.Count > 1 && jetzt - verlauf.Peek().Zeit > RateWindow) verlauf.Dequeue();

            double rate = 0;
            if (verlauf.Count > 1)
            {
                var aeltester = verlauf.Peek();
                var spanne = (jetzt - aeltester.Zeit).TotalSeconds;
                if (spanne > 0.5) rate = Math.Max(0, (bytes - aeltester.Bytes) / spanne);
            }

            progress.Report(new DownloadProgressReport
            {
                Bytes = bytes,
                AddedBytes = Math.Max(0, bytes - anfang),
                ReferenceBytes = referenz,
                BytesPerSecond = rate,
                Elapsed = jetzt - start,
                IsUpdate = plan.IsUpdate
            });
        }
    }

    /// <summary>
    /// Eine Zeile fuer die Anzeige - deutsch oder englisch.
    ///
    /// Vier Faelle, bewusst unterschiedlich formuliert:
    ///
    ///   Erstdownload mit Nenner   "1,8 GB von ca. 3,4 GB - 53 % - 12 MB/s - noch ca. 2 min"
    ///   Erstdownload ohne Nenner  "1,8 GB geladen - 12 MB/s - seit 3 min"
    ///   Aktualisierung mit Zuwachs "820 MB nachgeladen - 12 MB/s - seit 3 min"
    ///   Aktualisierung ohne Zuwachs "Bestand wird geprüft - seit 12 s"
    ///
    /// Beim Aktualisieren zaehlt der ZUWACHS, nicht der Inhalt des Ordners.
    /// Dort liegen ja schon Gigabyte, bevor irgendetwas passiert ist - diese
    /// Zahl anzuzeigen hiesse, Arbeit zu melden, die gar nicht stattfand.
    /// </summary>
    public static string Describe(DownloadProgressReport report)
    {
        // Reine Gegenprobe: Das Deployment Tool vergleicht nur und laedt
        // nichts. Eine Mengenangabe waere hier irrefuehrend.
        if (report.IsUpdate && report.NothingAddedYet)
        {
            return Loc.Tr(
                $"Bestand wird geprüft · seit {FormatDuration(report.Elapsed)}",
                $"Checking the existing set · running for {FormatDuration(report.Elapsed)}");
        }

        var teile = new List<string>();

        if (report.IsUpdate)
        {
            teile.Add(Loc.Tr($"{OfflineSource.FormatBytes(report.AddedBytes)} nachgeladen",
                             $"{OfflineSource.FormatBytes(report.AddedBytes)} added"));
        }
        else if (report.Percent is { } prozent && report.ReferenceBytes > 0)
        {
            teile.Add(Loc.Tr(
                $"{OfflineSource.FormatBytes(report.Bytes)} von ca. {OfflineSource.FormatBytes(report.ReferenceBytes)}",
                $"{OfflineSource.FormatBytes(report.Bytes)} of approx. {OfflineSource.FormatBytes(report.ReferenceBytes)}"));
            teile.Add(prozent.ToString("0", CultureInfo.CurrentCulture) + " %");
        }
        else
        {
            teile.Add(Loc.Tr($"{OfflineSource.FormatBytes(report.Bytes)} geladen",
                             $"{OfflineSource.FormatBytes(report.Bytes)} downloaded"));
        }

        if (report.BytesPerSecond > 0)
            teile.Add($"{OfflineSource.FormatBytes((long)report.BytesPerSecond)}/s");

        if (report.Remaining is { } rest)
            teile.Add(Loc.Tr($"noch ca. {FormatDuration(rest)}", $"approx. {FormatDuration(rest)} left"));
        else
            teile.Add(Loc.Tr($"seit {FormatDuration(report.Elapsed)}", $"running for {FormatDuration(report.Elapsed)}"));

        return string.Join(" · ", teile);
    }

    /// <summary>Dauer knapp und ohne Nachkommastellen.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return Loc.Tr($"{(int)span.TotalSeconds} s", $"{(int)span.TotalSeconds} s");
        if (span.TotalMinutes < 60) return Loc.Tr($"{(int)span.TotalMinutes} min", $"{(int)span.TotalMinutes} min");

        var stunden = (int)span.TotalHours;
        var minuten = span.Minutes;
        return Loc.Tr($"{stunden} h {minuten} min", $"{stunden} h {minuten} min");
    }
}

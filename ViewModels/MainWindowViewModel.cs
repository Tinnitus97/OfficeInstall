using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfficeInstall.Services;
using OfficeInstall.Views;

namespace OfficeInstall.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const string VersionString = "1.2.1";

    private readonly object _logLock = new();
    private readonly StringBuilder _logBuffer = new();
    private bool _logUntouched = true;

    private OfficeCatalogResult _catalog = new();

    /// <summary>
    /// Breite der ersten Spalte im Protokoll. Wird beim Durchsuchen aus dem
    /// laengsten Namen einer Installationsbasis bestimmt, damit alle Zeilen
    /// buendig untereinander stehen. "x32\PerpetualVL2019" sind 19 Zeichen -
    /// das ist zugleich die untere Grenze, damit die Zeile zum Deployment Tool
    /// auch dann passt, wenn gar keine Basis gefunden wurde.
    /// </summary>
    private const int LogColumnMinimum = 19;

    private int _logColumn = LogColumnMinimum;

    /// <summary>
    /// true, sobald der Paketordner einmal durchsucht wurde. Ohne diese Sperre
    /// blitzt die Einrichtungshilfe bei jedem Start kurz auf, auch wenn alles
    /// vorhanden ist - die Liste ist in den ersten Millisekunden noch leer.
    /// </summary>
    private bool _scanned;

    /// <summary>
    /// true, waehrend "Alles Noetige einrichten" laeuft.
    ///
    /// Die einzelnen Vorgaenge beginnen mit "if (!UiEnabled) return;" - eine
    /// Sperre gegen Doppelklicks. Im Sammelbetrieb schaltet der erste Vorgang
    /// die Bedienung ab, und der zweite waere daran wortlos gescheitert. Genau
    /// das war der Fall: Es lief immer nur ein Schritt.
    /// </summary>
    private bool _batchSetup;
    private CancellationTokenSource? _scanCts;
    private string _packageInfo = "";

    /// <summary>
    /// Online angebotene Version je Installationsbasis. Bewusst hier gehalten
    /// und nicht aus der Anzeigeliste abgeleitet: Die Liste zeigt immer nur eine
    /// Architektur, sodass beim Umschalten sonst die Angaben der anderen
    /// verlorengingen - dann blieb eine veraltete 32-Bit-Zeile faelschlich gruen.
    /// </summary>
    private readonly Dictionary<string, Version> _onlineVersions = new(StringComparer.OrdinalIgnoreCase);

    public MainWindowViewModel()
    {
        var lang = SystemHelpers.GetWindowsLanguage();
        Localizer.I.Lang = lang == "en" ? AppLang.En : AppLang.De;
        _isEnglish = lang == "en";
        _selectedLanguageIndex = _isEnglish ? 1 : 0;

        _isLightTheme = SystemHelpers.IsWindowsLightTheme();
        var app = Avalonia.Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = _isLightTheme
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;

        // Startordner = Ordner der EXE, dort liegen x32 und x64 daneben.
        _rootFolder = SystemHelpers.GetApplicationDirectory();

        // Standard: die Architektur des laufenden Windows.
        _isX64 = Environment.Is64BitOperatingSystem;

        _logBuffer.Append(InitialLogText());
        LogText = _logBuffer.ToString();

        // Reste eines abgestuerzten oder abgewuergten Durchlaufs entfernen -
        // aber nur, wenn dieses Programm nicht noch ein zweites Mal laeuft.
        // Dessen Arbeitsdateien duerfen nicht verschwinden.
        if (SystemHelpers.IsOnlyInstance()) TempWorkspace.Clear();

        // Unabhaengig vom Paketordner - diese Liste steht fest.
        RebuildLegacyEntries();

        _ = Scan();
    }

    // ------------------------------------------------------------ Zustand

    public ObservableCollection<OfficeEntryViewModel> Entries { get; } = new();

    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _rootFolder = "";
    [ObservableProperty] private bool _uiEnabled = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private string _warningText = "";

    /// <summary>Kurzfassung fuer die Ueberschrift der Produktliste.</summary>
    [ObservableProperty] private string _warningShort = "";

    [ObservableProperty] private bool _warningVisible;

    /// <summary>true, wenn die Netzpruefung keine Verbindung gefunden hat.</summary>
    [ObservableProperty] private bool _isOffline;

    partial void OnIsOfflineChanged(bool value)
    {
        OnPropertyChanged(nameof(OdtStatusText));
        OnPropertyChanged(nameof(OdtStatusBrush));
    }

    /// <summary>Suchbegriff über der Produktliste.</summary>
    [ObservableProperty] private string _searchText = "";

    partial void OnSearchTextChanged(string value) => RebuildEntries();

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ---- Deployment Tool ----

    [ObservableProperty] private Version? _odtLocalVersion;
    [ObservableProperty] private Version? _odtOnlineVersion;

    partial void OnOdtLocalVersionChanged(Version? value) => RefreshOdtLabels();
    partial void OnOdtOnlineVersionChanged(Version? value) => RefreshOdtLabels();

    private void RefreshOdtLabels()
    {
        OnPropertyChanged(nameof(OdtStatusText));
        OnPropertyChanged(nameof(OdtStatusBrush));
        OnPropertyChanged(nameof(BtnOdtUpdate));
        RefreshSetupState();
    }

    public bool OdtNeedsUpdate => DeploymentToolService.NeedsUpdate(OdtLocalVersion, OdtOnlineVersion);

    public string OdtStatusText
    {
        get
        {
            if (OdtLocalVersion is not null && IsOffline)
                return Tr($"Deployment Tool: {OdtLocalVersion} (Rechner offline - kein Abgleich)",
                          $"Deployment tool: {OdtLocalVersion} (computer offline - no comparison)");

            if (OdtLocalVersion is null && OdtOnlineVersion is null)
                return Tr("Deployment Tool: keine setup.exe im Paket gefunden",
                          "Deployment tool: no setup.exe found in the package");

            if (OdtLocalVersion is null)
                return Tr($"Deployment Tool: nicht vorhanden - online steht {OdtOnlineVersion} bereit",
                          $"Deployment tool: missing - {OdtOnlineVersion} is available online");

            if (OdtOnlineVersion is null)
                return Tr($"Deployment Tool: {OdtLocalVersion} (keine Auskunft von Microsoft)",
                          $"Deployment tool: {OdtLocalVersion} (no answer from Microsoft)");

            return OdtNeedsUpdate
                ? Tr($"Deployment Tool: {OdtLocalVersion} - online gibt es bereits {OdtOnlineVersion}",
                     $"Deployment tool: {OdtLocalVersion} - {OdtOnlineVersion} is already available online")
                : Tr($"Deployment Tool: {OdtLocalVersion} - aktueller Stand",
                     $"Deployment tool: {OdtLocalVersion} - up to date");
        }
    }

    public Avalonia.Media.IBrush OdtStatusBrush
    {
        get
        {
            if (OdtLocalVersion is null) return HexBrush("#E03131");   // rot
            if (OdtNeedsUpdate) return HexBrush("#F1A100");            // gelb
            return HexBrush("#40C057");                                // gruen
        }
    }

    private static Avalonia.Media.IBrush HexBrush(string hex)
        => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex));

    public string BtnOdtUpdate => OdtLocalVersion is null
        ? Tr("Deployment Tool laden", "Download deployment tool")
        : OdtNeedsUpdate
            ? Tr("Deployment Tool aktualisieren", "Update deployment tool")
            : Tr("Deployment Tool prüfen", "Check deployment tool");

    partial void OnUiEnabledChanged(bool value)
    {
        foreach (var entry in Entries) entry.ParentEnabled = value;
        RefreshSetupState();
    }

    partial void OnRootFolderChanged(string value) => _ = Scan();

    // ---- Architektur ----

    [ObservableProperty] private bool _isX64 = true;

    public bool IsX86
    {
        get => !IsX64;
        set { if (value) IsX64 = false; }
    }

    partial void OnIsX64Changed(bool value)
    {
        OnPropertyChanged(nameof(IsX86));
        RebuildEntries();
    }

    public int SelectedEdition => IsX64 ? 64 : 32;

    // ---- Installationsmodus ----

    /// <summary>0 = Automatik, 1 = Online, 2 = Offline.</summary>
    [ObservableProperty] private int _modeIndex;

    public bool IsAutomaticMode
    {
        get => ModeIndex == 0;
        set { if (value) ModeIndex = 0; }
    }

    public bool IsOnlineMode
    {
        get => ModeIndex == 1;
        set { if (value) ModeIndex = 1; }
    }

    public bool IsOfflineMode
    {
        get => ModeIndex == 2;
        set { if (value) ModeIndex = 2; }
    }

    public InstallMode SelectedMode => ModeIndex switch
    {
        1 => InstallMode.Online,
        2 => InstallMode.Offline,
        _ => InstallMode.Automatic
    };

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAutomaticMode));
        OnPropertyChanged(nameof(IsOnlineMode));
        OnPropertyChanged(nameof(IsOfflineMode));
        OnPropertyChanged(nameof(ModeHint));
    }

    public string ModeHint => SelectedMode switch
    {
        InstallMode.Online => Tr("Immer aus dem Internet - vorliegende Dateien bleiben unberührt.",
                                 "Always from the internet - local files are left untouched."),
        InstallMode.Offline => Tr("Nur aus dem Paketordner - ohne vorliegende Dateien wird abgebrochen.",
                                  "Only from the package folder - cancels if no local files are present."),
        // Der zweite Satz beginnt ausdruecklich in einer eigenen Zeile. Ohne
        // diesen festen Umbruch bricht der Text irgendwo mitten im ersten Satz
        // um - je nach Fensterbreite an einer anderen Stelle.
        _ => Tr("Das Programm greift zunächst auf die lokalen Installationsdaten zu.\n" +
                "Sollten diese nicht verfügbar sein, werden sie aus dem Internet heruntergeladen.",
                "The program first uses the local installation files.\n" +
                "If they are not available, they are downloaded from the internet.")
    };

    // ------------------------------------------------------------ Sprache

    [ObservableProperty] private bool _isEnglish;
    [ObservableProperty] private int _selectedLanguageIndex;

    public List<string> Languages { get; } = new() { "Deutsch", "English" };

    partial void OnSelectedLanguageIndexChanged(int value) => IsEnglish = value == 1;

    partial void OnIsEnglishChanged(bool value)
    {
        Localizer.I.Lang = value ? AppLang.En : AppLang.De;
        if (SelectedLanguageIndex != (value ? 1 : 0)) SelectedLanguageIndex = value ? 1 : 0;

        OnPropertyChanged(string.Empty);
        foreach (var entry in Entries) entry.RefreshTexts();
        foreach (var entry in LegacyEntries) entry.RefreshTexts();

        lock (_logLock)
        {
            if (_logUntouched)
            {
                _logBuffer.Clear();
                _logBuffer.Append(InitialLogText());
                var snapshot = _logBuffer.ToString();
                Dispatcher.UIThread.Post(() => LogText = snapshot);
            }
        }
    }

    private static string Tr(string de, string en) => Loc.Tr(de, en);

    // ------------------------------------------------------------ Beschriftungen

    public string WindowTitle    => $"OfficeInstall {VersionString}";
    public string AppSubtitle    => string.IsNullOrEmpty(_packageInfo)
        ? Tr("Microsoft Office installieren - online oder aus vorliegenden Dateien",
             "Install Microsoft Office - online or from local files")
        : _packageInfo;

    public string SectionSource  => Tr("Paketordner & Modus", "Package folder & mode");
    public string SectionProducts=> Tr("Verfügbare Installationen", "Available installations");
    public string SectionLegacy  => Tr("Ältere Fassungen: Office 2013 und 2016 - nur Download",
                                       "Older releases: Office 2013 and 2016 - download only");
    public string LegacyHint     => Tr("Diese Fassungen lassen sich nicht über das Deployment Tool ausrollen. " +
                                       "Der Download öffnet den Standardbrowser; die Datei landet dort, wo der " +
                                       "Browser sie ablegt - im Paketordner wird nichts gespeichert.",
                                       "These releases cannot be deployed with the Deployment Tool. The download " +
                                       "opens the default browser; the file goes wherever the browser puts it - " +
                                       "nothing is stored in the package folder.");
    public string LabelFolder    => Tr("Paketordner:", "Package folder:");
    public string BtnRescan      => Tr("Erneut suchen", "Search again");
    public string BtnResetFolder => Tr("Ordner der EXE", "Folder of the EXE");
    public string BtnExit        => Tr("Beenden", "Exit");
    public string SearchHint     => Tr("Suchen: Name, Jahr, Basis oder Dateiname …",
                                       "Search: name, year, base or file name …");
    public string BtnClearSearch => Tr("Suche leeren", "Clear search");
    public string BtnOdtPage     => Tr("Downloadseite", "Download page");
    public string BtnUpdateAll   => Tr("Alle Offline-Bestände aktualisieren oder runterladen",
                                       "Update or download all offline sets");
    public string BtnCancelBatch => Tr("Abbrechen", "Cancel");
    public string OptCleanup     => Tr("Alten Stand nach dem Update löschen",
                                       "Delete the previous build after updating");
    public string OptCleanupHint => Tr("Nach einem erfolgreichen Download werden ältere Versionsordner " +
                                       "aus Office\\Data entfernt. v32.cab und v64.cab bleiben erhalten.",
                                       "After a successful download, older version folders are removed " +
                                       "from Office\\Data. v32.cab and v64.cab are kept.");
    public string BtnClearLog    => Tr("Log leeren", "Clear log");
    public string LogTitle       => Tr("Aktivitäts-Protokoll", "Activity log");
    public string TabX64         => Tr("64 Bit", "64-bit");
    public string TabX86         => Tr("32 Bit", "32-bit");
    public string TabAutomatic   => Tr("Automatisch", "Automatic");
    public string TabOnline      => Tr("Online", "Online");
    public string TabOffline     => Tr("Offline", "Offline");
    public string ThemeIcon      => IsLightTheme ? "☀" : "☽";

    /// <summary>
    /// Vorbelegung des Protokolls. Bewusst leer: Die erste Zeile schreibt die
    /// Suche selbst, und die nennt auch gleich den Ordner - eine Ankuendigung
    /// davor waere nur eine zweite Zeile mit derselben Aussage.
    /// </summary>
    private static string InitialLogText() => "";

    // ------------------------------------------------------------ Protokoll

    private void Log(string text)
    {
        string snapshot;
        lock (_logLock)
        {
            _logUntouched = false;
            _logBuffer.Append(text).Append("\r\n");
            if (_logBuffer.Length > 40000)
            {
                var keep = _logBuffer.ToString(_logBuffer.Length - 30000, 30000);
                _logBuffer.Clear();
                _logBuffer.Append(Tr("... [LOG GEKUERZT] ...\r\n", "... [LOG TRUNCATED] ...\r\n")).Append(keep);
            }
            snapshot = _logBuffer.ToString();
        }
        Dispatcher.UIThread.Post(() => LogText = snapshot);
    }

    [RelayCommand]
    private void ClearLog()
    {
        lock (_logLock)
        {
            _logBuffer.Clear();
            _logBuffer.Append(InitialLogText());
            _logUntouched = true;
            LogText = _logBuffer.ToString();
        }
    }

    // ------------------------------------------------------------ Suche

    /// <summary>
    /// Durchsucht den Paketordner und baut die Liste der installierbaren
    /// Varianten auf. Danach wird im Hintergrund die Online-Version je Basis
    /// abgefragt.
    /// </summary>
    [RelayCommand]
    private async Task Scan()
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        var root = RootFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ScanStatus = Tr("Paketordner existiert nicht.", "Package folder does not exist.");
            Log(Tr($"[FEHLER] Paketordner nicht gefunden: {root}", $"[ERROR] Package folder not found: {root}"));
            Entries.Clear();
            return;
        }

        UiEnabled = false;
        IsBusy = true;
        BusyText = Tr("Paketordner wird durchsucht...", "Scanning the package folder...");
        Log(Tr($"Durchsuche {root} ...", $"Scanning {root} ..."));

        try
        {
            _catalog = await Task.Run(() => OfficeCatalog.Scan(root), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            var package = OfficeCatalog.ReadPackageVersion(root);
            _packageInfo = package is null
                ? ""
                : Tr($"Paketstand {package.Value.Version} vom {package.Value.Date}",
                     $"Package build {package.Value.Version} from {package.Value.Date}");
            OnPropertyChanged(nameof(AppSubtitle));

            if (_catalog.Entries.Count == 0)
            {
                Log(Tr("Noch keine Konfigurationsdateien in diesem Ordner - die nötigen Schritte " +
                       "stehen im Fenster unter \"Erste Schritte\".",
                       "No configuration files in this folder yet - the necessary steps are shown " +
                       "under \"Getting started\"."));
            }
            else
            {
                var groups = _catalog.Entries
                    .GroupBy(e => $"{e.Configuration.ArchitectureFolderName}\\{e.ChannelFolderName}")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Alle Namen auf dieselbe Breite bringen. Das Protokoll ist in
                // einer Festbreitenschrift gesetzt, dadurch stehen Anzahl,
                // Version und Groesse untereinander statt versetzt - und jede
                // Zeile beginnt sichtbar an derselben Stelle.
                _logColumn = Math.Max(LogColumnMinimum, groups.Max(g => g.Key.Length));

                foreach (var group in groups)
                {
                    // Bewusst knapp gehalten: Die Zeile soll in die schmale
                    // Protokollspalte passen, ohne umzubrechen - sonst wirkt
                    // ein sauberer Durchlauf doppelt so lang, wie er ist.
                    var offline = group.First().Offline;
                    var offlineNote = offline.Available
                        ? $"{offline.LatestVersion} · {OfflineSource.FormatBytes(offline.SizeInBytes)}"
                        : Tr("offline nichts vorhanden", "nothing offline");
                    Log($"  {group.Key.PadRight(_logColumn)}  {group.Count()} XML · {offlineNote}");
                }
            }

            foreach (var empty in _catalog.EmptyBases)
                Log(Tr($"  {empty.PadRight(_logColumn)}  [-] keine XML-Datei",
                       $"  {empty.PadRight(_logColumn)}  [-] no XML file"));

            // Ein Kanal, der nicht zum Ordner passt, laesst die Installation
            // scheitern ("This product can't be installed on the selected
            // update channel"). Das Programm setzt zwar den Ordner durch -
            // gemeldet wird es trotzdem, damit die Vorlage berichtigt wird.
            foreach (var entry in _catalog.Entries)
            {
                if (entry.Configuration.IsBroken) continue;

                var folder = entry.Channel?.FolderName ?? entry.ChannelFolderName;
                if (OfficeConfiguration.ChannelMatchesFolder(entry.Configuration.DeclaredChannel, folder))
                    continue;

                Log(Tr($"  [!] {entry.Configuration.FileName}: Channel=\"{entry.Configuration.DeclaredChannel}\" " +
                       $"passt nicht zum Ordner {folder} - es gilt der Ordner",
                       $"  [!] {entry.Configuration.FileName}: Channel=\"{entry.Configuration.DeclaredChannel}\" " +
                       $"does not match folder {folder} - the folder wins"));
            }

            _scanned = true;
            RebuildEntries();
            RefreshDeploymentToolVersion();
        }
        catch (OperationCanceledException)
        {
            // Eine neue Suche hat diese hier abgeloest.
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("Scan", ex);
            Log(Tr($"[FEHLER] Suche fehlgeschlagen: {ex.Message}", $"[ERROR] Search failed: {ex.Message}"));
        }
        finally
        {
            if (!cts.Token.IsCancellationRequested)
            {
                IsBusy = false;
                BusyText = "";
                UiEnabled = true;
            }
        }

        await CheckOnlineVersions(cts.Token);
    }

    /// <summary>
    /// Baut die angezeigte Liste neu auf - gefiltert nach gewaehlter
    /// Architektur und nach dem Suchbegriff.
    /// </summary>
    private void RebuildEntries()
    {
        var all = _catalog.ForArchitecture(SelectedEdition).ToList();
        var visible = all.Where(e => OfficeCatalog.Matches(e, SearchText)).ToList();

        Entries.Clear();
        foreach (var entry in visible
                     .OrderBy(e => e.Channel?.SortOrder ?? 99)
                     .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var viewModel = new OfficeEntryViewModel(entry, InstallEntryAsync, DownloadOfflineAsync)
            {
                ParentEnabled = UiEnabled
            };

            if (entry.Channel is not null && _onlineVersions.TryGetValue(entry.Channel.FolderName, out var online))
                viewModel.OnlineVersion = online;

            Entries.Add(viewModel);
        }

        // Die reine Trefferzahl stand frueher dauerhaft hier - sie sagt aber
        // nichts, was die Liste darunter nicht selbst zeigt. Angezeigt wird
        // deshalb nur noch das Ergebnis einer laufenden Suche.
        ScanStatus = string.IsNullOrWhiteSpace(SearchText)
            ? ""
            : Tr($"{visible.Count} von {all.Count} passen zu \"{SearchText}\".",
                 $"{visible.Count} of {all.Count} match \"{SearchText}\".");

        UpdateWarning();
        RebuildLegacyEntries();
        RefreshSetupState();
    }

    // ------------------------------------------------------------ Ältere Fassungen

    /// <summary>
    /// Office 2013 und 2016. Bewusst eine eigene Liste: Diese Fassungen werden
    /// weder installiert noch auf Aktualisierungen geprueft - es gibt nur den
    /// Download ueber den Standardbrowser.
    /// </summary>
    public ObservableCollection<LegacyEntryViewModel> LegacyEntries { get; } = new();

    /// <summary>true, wenn nach dem Suchbegriff noch alte Fassungen uebrig sind.</summary>
    public bool HasLegacyEntries => LegacyEntries.Count > 0;

    public string LegacyHeader => Tr($"{SectionLegacy}  ({LegacyEntries.Count})",
                                     $"{SectionLegacy}  ({LegacyEntries.Count})");

    private void RebuildLegacyEntries()
    {
        LegacyEntries.Clear();

        foreach (var release in LegacyOfficeReleases.All)
        {
            if (!LegacyOfficeReleases.Matches(release, SearchText)) continue;
            LegacyEntries.Add(new LegacyEntryViewModel(release, OpenLegacyDownload, SelectedEdition));
        }

        OnPropertyChanged(nameof(HasLegacyEntries));
        OnPropertyChanged(nameof(LegacyHeader));
    }

    /// <summary>
    /// Oeffnet die Downloadadresse im Standardbrowser.
    ///
    /// Ausdruecklich NICHT selbst herunterladen: Der Browser bringt
    /// Fortschrittsanzeige, Wiederaufnahme nach Abbruch und die vom Benutzer
    /// eingestellte Zuordnung mit - und legt die Datei in seinem eigenen
    /// Zielordner ab. Der Paketordner bleibt unberuehrt.
    /// </summary>
    private void OpenLegacyDownload(LegacyEntryViewModel entry, LegacyDownloadKind kind)
    {
        var url = entry.Release.Url(kind, SelectedEdition);

        var what = kind == LegacyDownloadKind.Image
            ? Tr("Sicherungsmedium (.img, enthält 32 und 64 Bit)",
                 "backup media (.img, contains 32-bit and 64-bit)")
            : Tr($"Online-Installer {SelectedEdition} Bit", $"online installer {SelectedEdition}-bit");

        Log(Tr($"{entry.Title}: {what} wird im Browser geöffnet.",
               $"{entry.Title}: opening {what} in the browser."));
        Log($"  {url}");

        if (LegacyOfficeReleases.OpenInBrowser(url))
        {
            Log(Tr("  Der Download läuft im Browser - die Datei landet in dessen Zielordner, " +
                   "nicht im Paketordner.",
                   "  The download runs in the browser - the file goes to its download folder, " +
                   "not into the package folder."));
        }
        else
        {
            Log(Tr("  [FEHLER] Es konnte kein Browser gestartet werden. Adresse von Hand kopieren.",
                   "  [ERROR] No browser could be started. Please copy the address manually."));
        }
    }

    /// <summary>Liest die Version der im Paket vorhandenen setup.exe.</summary>
    private void RefreshDeploymentToolVersion()
    {
        var setup = _catalog.Entries.Select(e => e.SetupExecutable).FirstOrDefault(p => p is not null)
                    ?? DeploymentToolService.DeploymentTargets(RootFolder).FirstOrDefault(File.Exists);

        OdtLocalVersion = DeploymentToolService.ReadFileVersion(setup);

        if (OdtLocalVersion is null)
        {
            Log(Tr($"  {"Deployment Tool".PadRight(_logColumn)}  [!] keine setup.exe im Paket gefunden",
                   $"  {"Deployment Tool".PadRight(_logColumn)}  [!] no setup.exe found in the package"));
        }
        else
        {
            // Nur den Teil des Pfades zeigen, der nicht ohnehin schon in der
            // Kopfzeile steht - die vollstaendige Angabe sprengt die Spalte.
            var shown = setup!;
            var root = Path.GetFullPath(RootFolder);
            if (shown.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                shown = "." + shown[root.Length..];

            Log($"  {"Deployment Tool".PadRight(_logColumn)}  {OdtLocalVersion} · {shown}");
        }
    }

    /// <summary>
    /// Gleicht den vorliegenden Stand mit dem ab, was Microsoft derzeit
    /// anbietet - Office je Installationsbasis und das Deployment Tool.
    ///
    /// Vorgeschaltet ist eine kurze Netzpruefung: Ist der Rechner offline,
    /// steht genau eine Zeile im Protokoll statt einer Fehlermeldung je Basis.
    /// </summary>
    private async Task CheckOnlineVersions(CancellationToken ct)
    {
        var channels = _catalog.Entries
            .Where(e => e.Channel is not null)
            .Select(e => (Channel: e.Channel!, e.ClientEdition))
            .Distinct()
            .ToList();

        // --- 1. Ueberhaupt am Netz? ---
        if (!await ConnectivityService.IsOnlineAsync(ct))
        {
            IsOffline = true;
            Log(Tr("Der Rechner ist offline - der Abgleich mit Microsoft wird übersprungen.",
                   "The computer is offline - the comparison with Microsoft is skipped."));
            return;
        }

        IsOffline = false;

        // --- 2. Versionsübersicht holen (ein Aufruf für alle Basen) ---
        // Erst danach wird protokolliert: So steht bei einem sauberen Durchlauf
        // genau eine Zeile da statt einer Ankuendigung plus Ergebnis.
        var releases = await OnlineVersionService.GetReleasesAsync(ct);

        if (!releases.Success)
        {
            Log(Tr($"[FEHLER] Keine Auskunft von Microsoft - {releases.Error}",
                   $"[ERROR] No answer from Microsoft - {releases.Error}"));
        }
        else
        {
            // Gemeldet wird nur, was der Aufmerksamkeit bedarf: ein neuerer
            // Stand oder ein Fehler. Ist alles in Ordnung, bleibt es bei einer
            // einzigen Zusammenfassung - frueher stand hier eine Zeile je Basis
            // und Architektur, also bis zu acht Zeilen ohne Aussage.
            var checkedCount = 0;
            var reported = 0;

            foreach (var (channel, edition) in channels)
            {
                if (ct.IsCancellationRequested) return;

                checkedCount++;

                var online = releases.For(channel);
                if (online is null)
                {
                    reported++;
                    Log(Tr($"  [-] {channel.FolderName}: in der Übersicht nicht enthalten",
                           $"  [-] {channel.FolderName}: not contained in the overview"));
                    continue;
                }

                // Für ALLE Architekturen merken, nicht nur für die angezeigte.
                _onlineVersions[channel.FolderName] = online;
                ApplyOnlineVersions();

                var local = _catalog.Entries.FirstOrDefault(e =>
                    e.Channel?.FolderName == channel.FolderName && e.ClientEdition == edition)?.Offline;

                if (local?.Available == true && local.LatestVersion is not null && online > local.LatestVersion)
                {
                    reported++;
                    Log(Tr($"  [!] {channel.FolderName} ({edition} Bit): offline {local.LatestVersion} · online {online}",
                           $"  [!] {channel.FolderName} ({edition}-bit): offline {local.LatestVersion} · online {online}"));
                }
            }

            if (reported == 0)
            {
                Log(Tr($"Abgleich mit Microsoft: alles aktuell ({checkedCount} Bestände).",
                       $"Comparison with Microsoft: everything up to date ({checkedCount} sets)."));
            }
        }

        // --- 3. Deployment Tool ---
        var release = await DeploymentToolService.ResolveLatestAsync(RootFolder, ct);
        OdtOnlineVersion = release?.Version;

        if (release is null)
        {
            Log(Tr("  [-] Keine Auskunft zum Deployment Tool.",
                   "  [-] No information about the deployment tool."));
        }
        else if (OdtNeedsUpdate)
        {
            Log(Tr($"  [!] Deployment Tool: {OdtLocalVersion?.ToString() ?? "keins"} · online {release.Version}",
                   $"  [!] Deployment tool: {OdtLocalVersion?.ToString() ?? "none"} · online {release.Version}"));
        }

        // --- 4. Eigener Stand: Programm und Konfigurationen ---
        await CheckOwnUpdates(ct);

        UpdateWarning();
    }

    // ------------------------------------------------------------ Selbstaktualisierung

    private UpdateManifest? _manifest;

    /// <summary>Zeile ueber der Produktliste - nur sichtbar, wenn es etwas gibt.</summary>
    [ObservableProperty] private bool _selfUpdateVisible;

    [ObservableProperty] private string _selfUpdateText = "";

    [ObservableProperty] private bool _programUpdateAvailable;

    [ObservableProperty] private bool _configsUpdateAvailable;

    public string BtnUpdateProgram => Tr("Programm aktualisieren", "Update program");
    public string BtnUpdateConfigs => Tr("Konfigurationen einspielen", "Apply configurations");
    public string BtnUpdateNotes   => Tr("Was ist neu?", "What's new?");

    /// <summary>Paketstand aus Versionscheck\Version.txt, oder null.</summary>
    private string? LocalConfigVersion => OfficeCatalog.ReadPackageVersion(RootFolder)?.Version;

    /// <summary>
    /// Fragt update.json ab und meldet, ob es ein neueres Programm oder neuere
    /// Konfigurationsdateien gibt. Heruntergeladen wird hier noch nichts - das
    /// geschieht erst auf ausdrueckliche Zustimmung.
    /// </summary>
    private async Task CheckOwnUpdates(CancellationToken ct)
    {
        var url = UpdateService.ResolveManifestUrl(RootFolder);
        var manifest = await UpdateService.FetchAsync(url, ct);
        _manifest = manifest;

        if (!manifest.Success)
        {
            Log(Tr($"  [-] Kein eigener Versionsabgleich möglich: {manifest.Error}",
                   $"  [-] Own version check not possible: {manifest.Error}"));
            return;
        }

        ProgramUpdateAvailable = manifest.Program.IsUsable
                                 && UpdateService.IsProgramNewer(VersionString, manifest.Program.Version);

        var localConfigs = LocalConfigVersion;
        ConfigsUpdateAvailable = manifest.Configs.IsUsable
                                 && UpdateService.IsConfigsNewer(localConfigs, manifest.Configs.Version);

        var teile = new List<string>();
        if (ProgramUpdateAvailable)
            teile.Add(Tr($"Programm {manifest.Program.Version} (hier {VersionString})",
                         $"program {manifest.Program.Version} (here {VersionString})"));
        if (ConfigsUpdateAvailable)
            teile.Add(Tr($"Konfigurationen {manifest.Configs.Version} (hier {localConfigs ?? "?"})",
                         $"configurations {manifest.Configs.Version} (here {localConfigs ?? "?"})"));

        SelfUpdateVisible = teile.Count > 0;
        SelfUpdateText = teile.Count > 0
            ? Tr($"Neu verfügbar: {string.Join(" · ", teile)}", $"Available: {string.Join(" · ", teile)}")
            : "";

        foreach (var teil in teile) Log($"  [!] {teil}");

        // Ohne hinterlegte Veröffentlichung kann Schritt 1 nichts laden.
        RefreshSetupState();
    }

    /// <summary>Laedt die neue EXE, prueft ihre Pruefsumme und tauscht sie aus.</summary>
    [RelayCommand]
    private async Task UpdateProgram()
    {
        if (!UiEnabled || _manifest is null) return;
        var window = ActiveWindow;
        var entry = _manifest.Program;

        try
        {
            UiEnabled = false;

            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current))
            {
                Log(Tr("[FEHLER] Der eigene Programmpfad ließ sich nicht ermitteln.",
                       "[ERROR] Could not determine own program path."));
                return;
            }

            if (window is not null)
            {
                var frage = Tr(
                    $"Version {entry.Version} herunterladen und die laufende Fassung {VersionString} ersetzen?\n\n" +
                    $"Datei: {current}\n\n" +
                    "Das Programm wird dabei beendet und danach neu gestartet.",
                    $"Download version {entry.Version} and replace the running {VersionString}?\n\n" +
                    $"File: {current}\n\n" +
                    "The program will close and restart afterwards.");

                if (!await MessageBox.ShowYesNo(window, Tr("Programm aktualisieren", "Update program"), frage))
                {
                    Log(Tr("Abgebrochen.", "Cancelled."));
                    return;
                }
            }

            IsBusy = true;
            BusyText = Tr($"Version {entry.Version} wird geladen...", $"Downloading version {entry.Version}...");

            var target = Path.Combine(UpdateService.WorkFolder, "OfficeInstall.exe");
            var result = await UpdateService.DownloadAsync(entry.Url, entry.Sha256, target, CancellationToken.None);

            if (!result.Success)
            {
                Log(Tr($"[FEHLER] Download fehlgeschlagen: {result.Error}",
                       $"[ERROR] Download failed: {result.Error}"));
                if (window is not null)
                    await MessageBox.ShowInfo(window, Tr("Download fehlgeschlagen", "Download failed"),
                                              result.Error ?? "");
                return;
            }

            Log(Tr($"Version {entry.Version} geladen ({OfflineSource.FormatBytes(result.Bytes)}), Prüfsumme stimmt.",
                   $"Version {entry.Version} downloaded ({OfflineSource.FormatBytes(result.Bytes)}), checksum matches."));

            var script = UpdateService.WriteUpdateScript(target, current, Environment.ProcessId);
            UpdateService.StartUpdateScript(script);

            Log(Tr("Das Programm wird jetzt beendet und ersetzt.",
                   "The program will now close and be replaced."));

            window?.Close();
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("UpdateProgram", ex);
            Log(Tr($"[FEHLER] {ex.Message}", $"[ERROR] {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            UiEnabled = true;
        }
    }

    /// <summary>Laedt configs.zip und spielt die XML-Dateien in den Paketordner ein.</summary>
    [RelayCommand]
    private async Task UpdateConfigs()
    {
        if (!UiEnabled && !_batchSetup) return;
        if (_manifest is null) return;
        var window = ActiveWindow;
        var entry = _manifest.Configs;

        try
        {
            UiEnabled = false;

            if (window is not null)
            {
                var frage = Tr(
                    $"Konfigurationsstand {entry.Version} vom {entry.Date} einspielen?\n\n" +
                    $"Ziel: {RootFolder}\n\n" +
                    "Ersetzt werden ausschließlich die XML-Dateien unter x32 und x64.\n" +
                    "Der Offline-Bestand (Office\\Data) wird nicht angefasst.",
                    $"Apply configuration build {entry.Version} from {entry.Date}?\n\n" +
                    $"Target: {RootFolder}\n\n" +
                    "Only the XML files under x32 and x64 are replaced.\n" +
                    "The offline files (Office\\Data) are left untouched.");

                if (!await MessageBox.ShowYesNo(window, Tr("Konfigurationen einspielen", "Apply configurations"), frage))
                {
                    Log(Tr("Abgebrochen.", "Cancelled."));
                    return;
                }
            }

            IsBusy = true;
            BusyText = Tr("Konfigurationen werden geladen...", "Downloading configurations...");

            var target = Path.Combine(UpdateService.WorkFolder, "configs.zip");
            var result = await UpdateService.DownloadAsync(entry.Url, entry.Sha256, target, CancellationToken.None);

            if (!result.Success)
            {
                Log(Tr($"[FEHLER] Download fehlgeschlagen: {result.Error}",
                       $"[ERROR] Download failed: {result.Error}"));
                if (window is not null)
                    await MessageBox.ShowInfo(window, Tr("Download fehlgeschlagen", "Download failed"),
                                              result.Error ?? "");
                return;
            }

            var applied = await Task.Run(() => UpdateService.ApplyConfigs(target, RootFolder));

            if (!applied.Success)
            {
                Log(Tr($"[FEHLER] Einspielen fehlgeschlagen: {applied.Error}",
                       $"[ERROR] Applying failed: {applied.Error}"));
                if (window is not null)
                    await MessageBox.ShowInfo(window, Tr("Einspielen fehlgeschlagen", "Applying failed"),
                                              applied.Error ?? "");
                return;
            }

            Log(Tr($"Konfigurationsstand {entry.Version} eingespielt: {applied.Written} Datei(en)" +
                   (applied.Skipped > 0 ? $", {applied.Skipped} übergangen" : "") + ".",
                   $"Configuration build {entry.Version} applied: {applied.Written} file(s)" +
                   (applied.Skipped > 0 ? $", {applied.Skipped} skipped" : "") + "."));

            UpdateService.CleanUp();
            ConfigsUpdateAvailable = false;
            SelfUpdateVisible = ProgramUpdateAvailable;

            UiEnabled = true;
            await Scan();
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("UpdateConfigs", ex);
            Log(Tr($"[FEHLER] {ex.Message}", $"[ERROR] {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            UiEnabled = true;
        }
    }

    /// <summary>Oeffnet die Seite zur Veroeffentlichung im Browser.</summary>
    [RelayCommand]
    private void OpenUpdateNotes()
    {
        var url = ProgramUpdateAvailable ? _manifest?.Program.Notes : _manifest?.Configs.Notes;
        if (!string.IsNullOrWhiteSpace(url)) LegacyOfficeReleases.OpenInBrowser(url);
    }

    // ------------------------------------------------------------ Einrichtungshilfe

    /// <summary>
    /// Beim ersten Start liegt neben der EXE noch nichts: keine
    /// Konfigurationsdateien, kein Deployment Tool. Frueher stand dann nur eine
    /// leere Liste da und im Protokoll "Keine Konfigurationsdateien gefunden" -
    /// ohne einen Hinweis, was jetzt zu tun ist.
    ///
    /// Deshalb tritt an die Stelle der leeren Liste eine kurze Anleitung mit
    /// genau den Schaltflaechen, die es braucht. Sobald beides vorhanden ist,
    /// verschwindet sie von selbst.
    /// </summary>
    public bool HasConfigs => _catalog.Entries.Count > 0;

    public bool HasOdt => OdtLocalVersion is not null;

    public bool HasOffline => _catalog.Entries.Any(e => e.Offline.Available);

    /// <summary>true, solange das Noetigste fehlt.</summary>
    public bool SetupVisible => _scanned && (!HasConfigs || !HasOdt);

    /// <summary>true, wenn zu den Konfigurationen eine Veroeffentlichung hinterlegt ist.</summary>
    public bool ConfigsDownloadPossible => _manifest?.Configs.IsUsable == true;

    public string SetupTitle => Tr("Erste Schritte", "Getting started");

    public string SetupIntro => !HasConfigs && !HasOdt
        ? Tr("In diesem Ordner liegt noch nichts. Beides holt das Programm selbst - " +
             "danach ist es einsatzbereit.",
             "This folder is still empty. The program fetches both itself - after that it is ready.")
        : Tr("Es fehlt noch eine Kleinigkeit:", "One thing is still missing:");

    // ---- Schritt 1: Konfigurationsdateien ----

    public bool Step1Done => HasConfigs;
    public string Step1Icon => Step1Done ? "✔" : "1";
    public IBrush Step1Brush => StepBrush(Step1Done);

    public string Step1Title => Tr("Konfigurationsdateien", "Configuration files");

    public string Step1Hint => Step1Done
        ? Tr($"{_catalog.Entries.Count} Vorlagen vorhanden.",
             $"{_catalog.Entries.Count} templates present.")
        : ConfigsDownloadPossible
            ? Tr($"Vorlagen für Office und Microsoft 365 - Stand {_manifest!.Configs.Version}, " +
                 "wenige Kilobyte.",
                 $"Templates for Office and Microsoft 365 - build {_manifest!.Configs.Version}, a few kilobytes.")
            : Tr("Noch keine Veröffentlichung hinterlegt - die Vorlagen lassen sich von Hand " +
                 "neben die EXE legen (Ordner x32 und x64).",
                 "No release available yet - the templates can be placed next to the EXE by hand " +
                 "(folders x32 and x64).");

    public string Step1Button => ConfigsDownloadPossible
        ? Tr("Vorlagen laden", "Get templates")
        : Tr("Seite öffnen", "Open page");

    // Bewusst auch ohne hinterlegte Veroeffentlichung bedienbar: Dann fuehrt
    // die Schaltflaeche zur Uebersicht, statt grau und ohne Erklaerung
    // dazustehen.
    public bool Step1Enabled => UiEnabled && !Step1Done;

    /// <summary>
    /// Holt die Vorlagen - oder oeffnet die Uebersicht, wenn dazu noch nichts
    /// veroeffentlicht wurde.
    /// </summary>
    [RelayCommand]
    private async Task Step1()
    {
        if (ConfigsDownloadPossible)
        {
            await UpdateConfigs();
            return;
        }

        Log(Tr("Zu den Konfigurationsdateien ist noch keine Veröffentlichung hinterlegt - " +
               "die Übersicht wird im Browser geöffnet.",
               "No release is available for the configuration files - opening the overview in the browser."));
        LegacyOfficeReleases.OpenInBrowser(UpdateService.ReleasesPageUrl);
    }

    // ---- Schritt 2: Deployment Tool ----

    public bool Step2Done => HasOdt;
    public string Step2Icon => Step2Done ? "✔" : "2";
    public IBrush Step2Brush => StepBrush(Step2Done);

    public string Step2Title => Tr("Office Deployment Tool", "Office Deployment Tool");

    public string Step2Hint => Step2Done
        ? Tr($"setup.exe {OdtLocalVersion} vorhanden.", $"setup.exe {OdtLocalVersion} present.")
        : Tr("Die setup.exe von Microsoft - ohne sie lässt sich nichts installieren. " +
             "Wird direkt vom Microsoft Download Center geholt.",
             "Microsoft's setup.exe - nothing can be installed without it. " +
             "Fetched directly from the Microsoft Download Center.");

    public string Step2Button => Tr("Tool laden", "Get the tool");
    public bool Step2Enabled => UiEnabled && !Step2Done;

    // ---- Schritt 3: Offline-Bestand (freiwillig) ----

    public bool Step3Done => HasOffline;
    public string Step3Icon => Step3Done ? "✔" : "3";
    public IBrush Step3Brush => StepBrush(Step3Done);

    public string Step3Title => Tr("Offline-Bestand (kann warten)", "Offline files (can wait)");

    public string Step3Hint => Step3Done
        ? Tr("Quelldateien vorhanden - Installation ohne Internet möglich.",
             "Source files present - installation without internet is possible.")
        : Tr("Mehrere Gigabyte. Nur nötig, wenn ohne Internetverbindung installiert werden soll. " +
             "Ohne diesen Schritt läuft die Installation online.",
             "Several gigabytes. Only needed for installing without an internet connection. " +
             "Without this step the installation runs online.");

    public string Step3Button => Tr("Später laden", "Get it later");
    public bool Step3Enabled => UiEnabled && Step1Done && Step2Done;

    private static IBrush StepBrush(bool done)
        => new SolidColorBrush(Color.Parse(done ? "#40C057" : "#7AA2F7"));

    public string BtnSetupAll => Tr("Alles Nötige einrichten", "Set up everything needed");

    // Nur anbieten, wenn es auch von allein klappen kann.
    public bool SetupAllEnabled =>
        UiEnabled && ((!Step1Done && ConfigsDownloadPossible) || !Step2Done);

    /// <summary>
    /// Warnung, wenn die EXE an einem Ort liegt, der offensichtlich nur
    /// zwischengelagert ist. Dorthin gehoert kein Paketordner - beim naechsten
    /// Aufraeumen waere alles weg.
    /// </summary>
    public bool SetupFolderNoteVisible => IsTemporaryFolder(RootFolder);

    public string SetupFolderNote => Tr(
        "Hinweis: Das Programm liegt gerade in einem Ordner, der nur zum Zwischenlagern gedacht ist. " +
        "Lege die EXE besser dorthin, wo das Paket dauerhaft bleiben soll - die Vorlagen und der " +
        "Offline-Bestand landen daneben.",
        "Note: the program is currently in a folder meant only for temporary storage. Better move the " +
        "EXE to where the package should live permanently - templates and offline files go next to it.");

    /// <summary>Downloads, Desktop, Papierkorb, %TEMP% und Ähnliches.</summary>
    private static bool IsTemporaryFolder(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

            var verdaechtig = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            };

            return verdaechtig
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
                .Any(p => string.Equals(full, p, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Meldet der Oberflaeche, dass sich der Einrichtungsstand geaendert hat.</summary>
    private void RefreshSetupState()
    {
        foreach (var name in new[]
                 {
                     nameof(HasConfigs), nameof(HasOdt), nameof(HasOffline),
                     nameof(SetupVisible), nameof(SetupIntro), nameof(ConfigsDownloadPossible),
                     nameof(Step1Done), nameof(Step1Icon), nameof(Step1Brush), nameof(Step1Hint),
                     nameof(Step1Enabled), nameof(Step1Button),
                     nameof(Step2Done), nameof(Step2Icon), nameof(Step2Brush), nameof(Step2Hint), nameof(Step2Enabled),
                     nameof(Step3Done), nameof(Step3Icon), nameof(Step3Brush), nameof(Step3Hint), nameof(Step3Enabled),
                     nameof(SetupAllEnabled), nameof(SetupFolderNoteVisible),
                     nameof(EmptySearchVisible), nameof(EmptySearchText)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Erledigt der Reihe nach, was noch fehlt.</summary>
    [RelayCommand]
    private async Task SetupAll()
    {
        if (!UiEnabled || _batchSetup) return;

        try
        {
            _batchSetup = true;

            Log("");
            Log("=========================================");
            Log(Tr("Einrichtung: die fehlenden Schritte werden nacheinander erledigt.",
                   "Setup: the missing steps are done one after another."));

            // ---- Schritt 1: Vorlagen ----
            if (Step1Done)
            {
                Log(Tr("  Schritt 1 übersprungen - die Vorlagen sind bereits vorhanden.",
                       "  Step 1 skipped - the templates are already present."));
            }
            else if (!ConfigsDownloadPossible)
            {
                Log(Tr("  [!] Schritt 1 nicht möglich - zu den Konfigurationsdateien ist noch " +
                       "keine Veröffentlichung hinterlegt.",
                       "  [!] Step 1 not possible - no release is available for the configuration files."));
            }
            else
            {
                await UpdateConfigs();
            }

            // ---- Schritt 2: Deployment Tool ----
            // Der Zustand wird hier neu bewertet: Schritt 1 hat den Paketordner
            // veraendert und damit unter Umstaenden auch eine setup.exe gebracht.
            if (Step2Done)
            {
                Log(Tr($"  Schritt 2 übersprungen - setup.exe {OdtLocalVersion} ist bereits vorhanden.",
                       $"  Step 2 skipped - setup.exe {OdtLocalVersion} is already present."));
            }
            else
            {
                await UpdateDeploymentTool();
            }

            // ---- Ergebnis ----
            if (Step1Done && Step2Done)
            {
                Log(Tr("Einrichtung abgeschlossen - die Installationen stehen jetzt in der Liste.",
                       "Setup complete - the installations are now listed."));
            }
            else
            {
                var fehlt = new List<string>();
                if (!Step1Done) fehlt.Add(Tr("Vorlagen", "templates"));
                if (!Step2Done) fehlt.Add(Tr("Deployment Tool", "deployment tool"));

                Log(Tr($"[!] Es fehlt weiterhin: {string.Join(" und ", fehlt)}.",
                       $"[!] Still missing: {string.Join(" and ", fehlt)}."));
            }
        }
        finally
        {
            _batchSetup = false;
            UiEnabled = true;
            RefreshSetupState();
        }
    }

    /// <summary>Hinweis anstelle einer leeren Liste, wenn die Suche nichts findet.</summary>
    public bool EmptySearchVisible => _scanned && HasConfigs && Entries.Count == 0;

    public string EmptySearchText => string.IsNullOrWhiteSpace(SearchText)
        ? Tr($"Für {SelectedEdition} Bit ist nichts vorhanden - bitte oben die andere Architektur wählen.",
             $"Nothing available for {SelectedEdition}-bit - please switch the architecture above.")
        : Tr($"Kein Treffer für \"{SearchText}\".", $"No match for \"{SearchText}\".");

    /// <summary>Traegt die gemerkten Online-Versionen in die angezeigten Zeilen ein.</summary>
    private void ApplyOnlineVersions()
    {
        foreach (var entry in Entries)
        {
            if (entry.Entry.Channel is null) continue;
            if (_onlineVersions.TryGetValue(entry.Entry.Channel.FolderName, out var online))
                entry.OnlineVersion = online;
        }
        UpdateWarning();
    }

    /// <summary>
    /// Setzt den Kurzhinweis in der Ueberschrift der Produktliste. Bewusst
    /// knapp gehalten - die ausfuehrliche Erklaerung steht als Kurzinfo daran
    /// und kostet keinen Platz.
    /// </summary>
    private void UpdateWarning()
    {
        var outdated = Entries.Where(e => e.IsOutdated)
            .Select(e => e.Entry.ChannelFolderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (outdated.Count == 0)
        {
            WarningVisible = false;
            WarningText = "";
            WarningShort = "";
            return;
        }

        WarningShort = Tr($"Updates verfügbar: {string.Join(", ", outdated)}",
                          $"Updates available: {string.Join(", ", outdated)}");

        WarningText = Tr(
            $"Für {string.Join(", ", outdated)} gibt es online einen neueren Stand als in den Offline-Dateien. " +
            "Für eine aktuelle Installation entweder online installieren oder die Offline-Dateien über " +
            "\"Offline aktualisieren\" erneuern.",
            $"A newer build than the offline files is available online for {string.Join(", ", outdated)}. " +
            "Either install online or refresh the offline files using \"Update offline\".");
        WarningVisible = true;
    }

    // ------------------------------------------------------------ Installation

    private async Task InstallEntryAsync(OfficeEntryViewModel entry)
    {
        if (!UiEnabled) return;
        var window = ActiveWindow;

        try
        {
            UiEnabled = false;
            Log("");
            Log("=========================================");
            Log(Tr($"{entry.Title} ({entry.Entry.ClientEdition} Bit, {entry.Entry.ChannelFolderName})",
                   $"{entry.Title} ({entry.Entry.ClientEdition}-bit, {entry.Entry.ChannelFolderName})"));

            if (!entry.Entry.CanInstall)
            {
                var reason = entry.Entry.SetupExecutable is null
                    ? Tr("Es wurde keine setup.exe gefunden. Sie gehört in den Ordner der Installationsbasis.",
                         "No setup.exe was found. It belongs in the folder of the installation base.")
                    : Tr($"Die Konfigurationsdatei ist nicht auswertbar: {entry.Entry.Configuration.ErrorMessage}",
                         $"The configuration file cannot be read: {entry.Entry.Configuration.ErrorMessage}");
                Log("[FEHLER] " + reason);
                if (window is not null)
                    await MessageBox.ShowInfo(window, Tr("Installation nicht möglich", "Cannot install"), reason);
                return;
            }

            var mode = InstallRunner.ResolveMode(SelectedMode, entry.Entry.Offline);

            // --- Offline gewuenscht, aber nichts da ---
            if (mode == InstallMode.Offline && !entry.Entry.Offline.Available)
            {
                var message = Tr(
                    $"Für {entry.Entry.ChannelFolderName} liegen keine Offline-Dateien vor.\n\n" +
                    "Entweder den Modus auf \"Online\" oder \"Automatisch\" stellen, oder die Dateien mit\n" +
                    "setup.exe /download <Konfiguration>\nin den Ordner der Basis herunterladen.",
                    $"There are no offline files for {entry.Entry.ChannelFolderName}.\n\n" +
                    "Either switch the mode to \"Online\" or \"Automatic\", or download the files using\n" +
                    "setup.exe /download <configuration>\ninto the folder of the base.");
                Log(Tr("[FEHLER] Keine Offline-Dateien vorhanden.", "[ERROR] No offline files present."));
                if (window is not null)
                    await MessageBox.ShowInfo(window, Tr("Keine Offline-Dateien", "No offline files"), message);
                return;
            }

            // --- Warnung: Offline-Stand ist aelter als online ---
            if (mode == InstallMode.Offline && entry.IsOutdated && window is not null)
            {
                var question = Tr(
                    $"Die Offline-Dateien haben Version {entry.Entry.Offline.LatestVersion},\n" +
                    $"online wird bereits {entry.OnlineVersion} angeboten.\n\n" +
                    "Trotzdem mit den älteren Offline-Dateien installieren?\n" +
                    "(Nein bricht ab - dann kann im Modus \"Online\" installiert werden.)",
                    $"The offline files are version {entry.Entry.Offline.LatestVersion},\n" +
                    $"while {entry.OnlineVersion} is already offered online.\n\n" +
                    "Install from the older offline files anyway?\n" +
                    "(No cancels - you can then install using the \"Online\" mode.)");

                var proceed = await MessageBox.ShowYesNo(window,
                    Tr("Offline-Dateien sind veraltet", "Offline files are outdated"), question);

                if (!proceed)
                {
                    Log(Tr("Abgebrochen - Offline-Dateien sind veraltet.",
                           "Cancelled - offline files are outdated."));
                    return;
                }
            }

            // --- Vorbereiten ---
            InstallPlan plan;
            try
            {
                plan = await Task.Run(() => InstallRunner.Prepare(entry.Entry, SelectedMode));
            }
            catch (Exception ex)
            {
                Log(Tr($"[FEHLER] Vorbereitung fehlgeschlagen: {ex.Message}",
                       $"[ERROR] Preparation failed: {ex.Message}"));
                if (window is not null)
                    await MessageBox.ShowInfo(window, Tr("Vorbereitung fehlgeschlagen", "Preparation failed"), ex.Message);
                return;
            }

            var source = plan.EffectiveMode == InstallMode.Offline
                ? Tr($"Offline aus {plan.SourcePath}", $"offline from {plan.SourcePath}")
                : Tr("Online vom Microsoft-CDN", "online from the Microsoft CDN");

            Log(Tr($"Modus: {source}", $"Mode: {source}"));

            // Ein Kanal, der nicht zum Ordner passt, ist der haeufigste Grund
            // fuer "This product can't be installed on the selected update
            // channel". Deshalb steht der Fund direkt vor dem Start im
            // Protokoll - nicht nur beim Durchsuchen.
            if (plan.ChannelCorrectedFrom is not null)
                Log(Tr($"[!] In der Vorlage steht Channel=\"{plan.ChannelCorrectedFrom}\", der Ordner ist " +
                       $"{plan.Channel}. Verwendet wird {plan.Channel} - bitte die XML-Datei berichtigen.",
                       $"[!] The template says Channel=\"{plan.ChannelCorrectedFrom}\" but the folder is " +
                       $"{plan.Channel}. Using {plan.Channel} - please correct the XML file."));

            // Ohne festgeschriebene Version wuerde das Deployment Tool den
            // aktuellen Build beim CDN erfragen und ihn herunterladen.
            if (plan.PinnedVersion is not null)
                Log(Tr($"Version festgeschrieben: {plan.PinnedVersion} - es wird nichts aus dem Internet geladen.",
                       $"Version pinned: {plan.PinnedVersion} - nothing will be downloaded from the internet."));
            Log(Tr($"Konfiguration: {plan.ConfigurationFile}", $"Configuration: {plan.ConfigurationFile}"));
            Log(Tr($"Aufruf: {plan.CommandLine}", $"Command: {plan.CommandLine}"));

            // --- Rueckfrage vor dem eigentlichen Start ---
            if (window is not null)
            {
                var confirm = await MessageBox.ShowYesNo(window,
                    Tr("Installation starten", "Start installation"),
                    Tr($"{entry.Title}\n{entry.Entry.ClientEdition} Bit · {entry.Entry.ChannelFolderName}\n\n{source}" +
                       (plan.PinnedVersion is not null ? $"\nVersion {plan.PinnedVersion}" : "") + "\n\n" +
                       "Vorhandene Office-Installationen werden dabei entfernt (RemoveMSI) und laufende " +
                       "Office-Programme geschlossen. Jetzt starten?",
                       $"{entry.Title}\n{entry.Entry.ClientEdition}-bit · {entry.Entry.ChannelFolderName}\n\n{source}" +
                       (plan.PinnedVersion is not null ? $"\nVersion {plan.PinnedVersion}" : "") + "\n\n" +
                       "Existing Office installations will be removed (RemoveMSI) and running Office " +
                       "applications closed. Start now?"));

                if (!confirm)
                {
                    Log(Tr("Abgebrochen.", "Cancelled."));
                    return;
                }
            }

            // --- Starten und warten ---
            IsBusy = true;
            BusyText = Tr($"{entry.Title} wird installiert - der Fortschritt erscheint im Fenster von Microsoft.",
                          $"Installing {entry.Title} - progress is shown in the Microsoft window.");

            using var process = InstallRunner.Start(plan);
            Log(Tr($"Gestartet (PID {process.Id}). Warte auf das Ende der Installation...",
                   $"Started (PID {process.Id}). Waiting for the installation to finish..."));

            await process.WaitForExitAsync();

            var code = process.ExitCode;
            if (code == 0)
            {
                Log(Tr("Fertig! Die Installation wurde ohne Fehler beendet.",
                       "Done! The installation finished without errors."));
            }
            else
            {
                Log(Tr($"[FEHLER] Das Deployment Tool endete mit Rückgabewert {code}.",
                       $"[ERROR] The deployment tool exited with code {code}."));
                if (window is not null)
                    await MessageBox.ShowInfo(window,
                        Tr("Installation fehlgeschlagen", "Installation failed"),
                        Tr($"Das Office Deployment Tool endete mit Rückgabewert {code}.\n\n" +
                           "Einzelheiten stehen in den Protokolldateien unter %TEMP%.",
                           $"The Office Deployment Tool exited with code {code}.\n\n" +
                           "Details are in the log files under %TEMP%."));
            }
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("InstallEntryAsync", ex);
            Log(Tr($"[FEHLER] {ex.Message}", $"[ERROR] {ex.Message}"));
        }
        finally
        {
            CleanTemp();
            IsBusy = false;
            BusyText = "";
            UiEnabled = true;
        }
    }

    // ------------------------------------------------------------ Offline-Bestand

    /// <summary>
    /// Laedt die Offline-Quelldateien einer Installationsbasis herunter
    /// beziehungsweise zieht einen vorhandenen Bestand auf den neuesten Stand.
    /// Das Deployment Tool holt dabei nur, was fehlt.
    /// </summary>
    private async Task DownloadOfflineAsync(OfficeEntryViewModel entry)
    {
        if (!UiEnabled) return;
        var window = ActiveWindow;

        try
        {
            UiEnabled = false;
            Log("");
            Log("=========================================");

            if (!entry.Entry.CanInstall)
            {
                var reason = entry.Entry.SetupExecutable is null
                    ? Tr("Es wurde keine setup.exe gefunden.", "No setup.exe was found.")
                    : Tr("Die Konfigurationsdatei ist nicht auswertbar.", "The configuration file cannot be read.");
                Log("[FEHLER] " + reason);
                if (window is not null)
                    await MessageBox.ShowInfo(window, Tr("Nicht möglich", "Not possible"), reason);
                return;
            }

            var vorhanden = entry.Entry.Offline.Available;
            var titel = vorhanden
                ? Tr("Offline-Dateien aktualisieren", "Update offline files")
                : Tr("Offline-Dateien herunterladen", "Download offline files");

            var bestand = vorhanden
                ? Tr($"Vorhanden: Version {entry.Entry.Offline.LatestVersion} " +
                     $"({OfflineSource.FormatBytes(entry.Entry.Offline.SizeInBytes)})",
                     $"Present: version {entry.Entry.Offline.LatestVersion} " +
                     $"({OfflineSource.FormatBytes(entry.Entry.Offline.SizeInBytes)})")
                : Tr("Bisher liegen keine Offline-Dateien vor.", "No offline files present so far.");

            var ziel = entry.OnlineVersion is null
                ? Tr("Es wird der aktuell angebotene Stand geladen.", "The currently offered build will be downloaded.")
                : Tr($"Geladen wird Version {entry.OnlineVersion}.", $"Version {entry.OnlineVersion} will be downloaded.");

            if (window is not null)
            {
                var confirm = await MessageBox.ShowYesNo(window, titel,
                    Tr($"{entry.Title}\n{entry.Entry.ClientEdition} Bit · {entry.Entry.ChannelFolderName}\n\n" +
                       $"{bestand}\n{ziel}\n\n" +
                       $"Zielordner:\n{entry.Entry.Configuration.BaseFolder}\n\n" +
                       "Ein vollständiger Bestand ist mehrere Gigabyte groß; vorhandene Dateien werden " +
                       "nicht erneut geladen. Der Vorgang läuft ohne eigenes Fenster und kann dauern.\n\n" +
                       "Jetzt starten?",
                       $"{entry.Title}\n{entry.Entry.ClientEdition}-bit · {entry.Entry.ChannelFolderName}\n\n" +
                       $"{bestand}\n{ziel}\n\n" +
                       $"Target folder:\n{entry.Entry.Configuration.BaseFolder}\n\n" +
                       "A full set is several gigabytes; existing files are not downloaded again. " +
                       "The process runs without a window of its own and may take a while.\n\n" +
                       "Start now?"));

                if (!confirm)
                {
                    Log(Tr("Abgebrochen.", "Cancelled."));
                    return;
                }
            }

            var plan = await Task.Run(() => InstallRunner.PrepareDownload(entry.Entry));
            Log(Tr($"Lade Offline-Dateien nach {plan.SourcePath}", $"Downloading offline files to {plan.SourcePath}"));
            Log(Tr($"Aufruf: {plan.CommandLine}", $"Command: {plan.CommandLine}"));

            IsBusy = true;
            BusyText = Tr($"Offline-Dateien für {entry.Entry.ChannelFolderName} werden geladen - das kann dauern.",
                          $"Downloading offline files for {entry.Entry.ChannelFolderName} - this may take a while.");

            using var process = InstallRunner.Start(plan);
            Log(Tr($"Gestartet (PID {process.Id}).", $"Started (PID {process.Id})."));
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Log(Tr("Fertig! Die Offline-Dateien wurden geladen.", "Done! The offline files were downloaded."));

                if (CleanupAfterUpdate)
                    AufraeumenNachDownload(entry.Entry.Configuration.BaseFolder);

                UiEnabled = true;
                OnlineVersionService.ClearCache();
                await Scan();   // damit die neue Version sofort in der Liste steht
                return;
            }

            Log(Tr($"[FEHLER] Das Deployment Tool endete mit Rückgabewert {process.ExitCode}.",
                   $"[ERROR] The deployment tool exited with code {process.ExitCode}."));
            if (window is not null)
                await MessageBox.ShowInfo(window,
                    Tr("Download fehlgeschlagen", "Download failed"),
                    Tr($"Das Office Deployment Tool endete mit Rückgabewert {process.ExitCode}.\n\n" +
                       "Häufige Ursachen: kein Schreibrecht im Zielordner, zu wenig Plattenplatz " +
                       "oder eine unterbrochene Internetverbindung.",
                       $"The Office Deployment Tool exited with code {process.ExitCode}.\n\n" +
                       "Common causes: no write permission in the target folder, not enough disk space " +
                       "or an interrupted internet connection."));
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("DownloadOfflineAsync", ex);
            Log(Tr($"[FEHLER] {ex.Message}", $"[ERROR] {ex.Message}"));
        }
        finally
        {
            CleanTemp();
            IsBusy = false;
            BusyText = "";
            UiEnabled = true;
        }
    }

    // ------------------------------------------------------------ Alles aktualisieren

    [ObservableProperty] private bool _isBatchRunning;

    /// <summary>Nach erfolgreichem Download den alten Stand entfernen.</summary>
    [ObservableProperty] private bool _cleanupAfterUpdate = true;

    private CancellationTokenSource? _batchCts;

    [RelayCommand]
    private void CancelBatch()
    {
        _batchCts?.Cancel();
        Log(Tr("Abbruch angefordert - der gerade laufende Download wird noch zu Ende geführt.",
               "Cancellation requested - the download currently running will still finish."));
    }

    /// <summary>
    /// Bringt saemtliche Installationsbasen auf den aktuellen Stand - beide
    /// Architekturen. Basen mit vorhandenen Dateien werden nachgezogen, leere
    /// Basen komplett geladen.
    ///
    /// Je Basis laeuft genau EIN Aufruf des Deployment Tools mit allen Produkten
    /// dieser Basis in einer gemeinsamen Konfiguration. Frueher war es ein
    /// Aufruf je Konfigurationsdatei - bei acht Dateien in "Current" also
    /// achtmal derselbe Vorgang.
    /// </summary>
    [RelayCommand]
    private async Task UpdateAllOffline()
    {
        if (!UiEnabled) return;
        var window = ActiveWindow;

        // Je Installationsbasis eine Gruppe (Architektur + Basis).
        var groups = _catalog.Entries
            .Where(e => e.CanInstall)
            .GroupBy(e => $"{e.Configuration.ArchitectureFolderName}\\{e.ChannelFolderName}")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Name = g.Key,
                Entries = g.ToList(),
                HasOffline = g.First().Offline.Available,
                Local = g.First().Offline.LatestVersion,
                Online = g.First().Channel is not null
                         && _onlineVersions.TryGetValue(g.First().Channel!.FolderName, out var v) ? v : null
            })
            .ToList();

        if (groups.Count == 0)
        {
            Log(Tr("Nichts zu tun - es wurde keine brauchbare Konfiguration gefunden.",
                   "Nothing to do - no usable configuration was found."));
            if (window is not null)
                await MessageBox.ShowInfo(window, Tr("Nichts zu tun", "Nothing to do"),
                    Tr("Im Paketordner wurde keine brauchbare Konfiguration gefunden. " +
                       "Fehlt vielleicht die setup.exe?",
                       "No usable configuration was found in the package folder. " +
                       "Is setup.exe perhaps missing?"));
            return;
        }

        var zuAktualisieren = groups.Where(g => g.HasOffline).ToList();
        var neuZuLaden = groups.Where(g => !g.HasOffline).ToList();
        var veraltet = zuAktualisieren.Count(g => g.Online is not null && g.Local is not null && g.Online > g.Local);

        if (window is not null)
        {
            var uebersicht = string.Join("\n", groups.Select(g =>
            {
                var zustand = !g.HasOffline
                    ? Tr("wird komplett geladen", "will be downloaded in full")
                    : g.Online is not null && g.Local is not null && g.Online > g.Local
                        ? Tr($"{g.Local} -> {g.Online}", $"{g.Local} -> {g.Online}")
                        : Tr("aktuell, wird nur geprüft", "up to date, will only be verified");
                return $"  {g.Name}  ({g.Entries.Count} " + Tr("Produkte", "products") + $", {zustand})";
            }));

            var warnung = neuZuLaden.Count > 0
                ? Tr($"\nACHTUNG: {neuZuLaden.Count} Basis/Basen haben noch gar keine Dateien - " +
                     "dort werden mehrere Gigabyte geladen.\n",
                     $"\nWARNING: {neuZuLaden.Count} base(s) have no files at all - " +
                     "several gigabytes will be downloaded there.\n")
                : "";

            var aufraeumen = CleanupAfterUpdate
                ? Tr("\nAlte Stände werden nach dem Download entfernt.",
                     "\nOld builds will be removed after the download.")
                : Tr("\nAlte Stände bleiben erhalten.", "\nOld builds will be kept.");

            var confirm = await MessageBox.ShowYesNo(window,
                Tr("Alle Offline-Bestände aktualisieren oder herunterladen",
                   "Update or download all offline sets"),
                Tr($"{groups.Count} Installationsbasen, je ein Durchlauf:\n\n{uebersicht}\n" +
                   (veraltet > 0 ? $"\nBei {veraltet} Basis/Basen ist online ein neuerer Stand bekannt.\n" : "") +
                   warnung + aufraeumen +
                   "\n\nEs wird nur geladen, was fehlt. Abbrechen ist jederzeit möglich.\n\nJetzt starten?",
                   $"{groups.Count} installation bases, one run each:\n\n{uebersicht}\n" +
                   (veraltet > 0 ? $"\nA newer build is known online for {veraltet} base(s).\n" : "") +
                   warnung + aufraeumen +
                   "\n\nOnly missing files are downloaded. You can cancel at any point.\n\nStart now?"));

            if (!confirm)
            {
                Log(Tr("Abgebrochen.", "Cancelled."));
                return;
            }
        }

        var cts = new CancellationTokenSource();
        _batchCts = cts;

        var erfolg = 0;
        var fehler = 0;
        var abgebrochen = false;
        long freigegeben = 0;

        try
        {
            UiEnabled = false;
            IsBatchRunning = true;
            IsBusy = true;

            Log("");
            Log("=========================================");
            Log(Tr($"Bearbeite {groups.Count} Installationsbasen ({zuAktualisieren.Count} aktualisieren, " +
                   $"{neuZuLaden.Count} neu laden)...",
                   $"Processing {groups.Count} installation bases ({zuAktualisieren.Count} to update, " +
                   $"{neuZuLaden.Count} to download)..."));

            for (var index = 0; index < groups.Count; index++)
            {
                if (cts.IsCancellationRequested) { abgebrochen = true; break; }

                var group = groups[index];
                BusyText = Tr($"{index + 1} von {groups.Count}: {group.Name}",
                              $"{index + 1} of {groups.Count}: {group.Name}");
                Log(Tr($"[{index + 1}/{groups.Count}] {group.Name} - {group.Entries.Count} Produkte",
                       $"[{index + 1}/{groups.Count}] {group.Name} - {group.Entries.Count} products"));

                try
                {
                    var plan = await Task.Run(() => InstallRunner.PrepareBaseDownload(group.Entries), cts.Token);

                    using var process = InstallRunner.Start(plan);
                    await process.WaitForExitAsync(CancellationToken.None);

                    if (process.ExitCode != 0)
                    {
                        fehler++;
                        Log(Tr($"      [FEHLER] Rückgabewert {process.ExitCode}.",
                               $"      [ERROR] exit code {process.ExitCode}."));
                        continue;
                    }

                    erfolg++;
                    Log(Tr("      fertig.", "      done."));

                    if (CleanupAfterUpdate)
                        freigegeben += AufraeumenNachDownload(group.Entries[0].Configuration.BaseFolder);
                }
                catch (Exception ex)
                {
                    fehler++;
                    Log(Tr($"      [FEHLER] {ex.Message}", $"      [ERROR] {ex.Message}"));
                }
            }

            Log("");
            Log(Tr($"Ergebnis: {erfolg} erfolgreich, {fehler} fehlgeschlagen" +
                   (abgebrochen ? ", vorzeitig abgebrochen." : ".") +
                   (freigegeben > 0 ? $" {OfflineSource.FormatBytes(freigegeben)} durch Aufräumen freigegeben." : ""),
                   $"Result: {erfolg} succeeded, {fehler} failed" +
                   (abgebrochen ? ", cancelled early." : ".") +
                   (freigegeben > 0 ? $" {OfflineSource.FormatBytes(freigegeben)} freed by cleanup." : "")));
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("UpdateAllOffline", ex);
            Log(Tr($"[FEHLER] {ex.Message}", $"[ERROR] {ex.Message}"));
        }
        finally
        {
            CleanTemp();
            _batchCts = null;
            IsBatchRunning = false;
            IsBusy = false;
            BusyText = "";
            UiEnabled = true;
        }

        OnlineVersionService.ClearCache();
        await Scan();

        if (ActiveWindow is { } fenster)
        {
            await MessageBox.ShowInfo(fenster,
                Tr("Abgeschlossen", "Finished"),
                Tr($"{erfolg} von {groups.Count} Installationsbasen wurden verarbeitet." +
                   (fehler > 0 ? $"\n{fehler} davon mit Fehler - Einzelheiten stehen im Protokoll." : "") +
                   (freigegeben > 0 ? $"\nDurch das Entfernen alter Stände wurden {OfflineSource.FormatBytes(freigegeben)} frei." : "") +
                   (abgebrochen ? "\nDer Vorgang wurde vorzeitig abgebrochen." : ""),
                   $"{erfolg} of {groups.Count} installation bases were processed." +
                   (fehler > 0 ? $"\n{fehler} of them failed - see the log for details." : "") +
                   (freigegeben > 0 ? $"\nRemoving old builds freed {OfflineSource.FormatBytes(freigegeben)}." : "") +
                   (abgebrochen ? "\nThe process was cancelled early." : "")));
        }
    }

    /// <summary>
    /// Entfernt nach einem Download die aelteren Staende aus dem Ordner und
    /// gibt zurueck, wie viel dadurch frei wurde. Behalten wird immer der
    /// neueste vorhandene Stand.
    /// </summary>
    private long AufraeumenNachDownload(string baseFolder)
    {
        try
        {
            // Frisch einlesen - der Download hat den Ordner gerade veraendert.
            var aktuell = OfflineSource.Detect(baseFolder, 64);
            if (aktuell.LatestVersion is null) return 0;

            var ergebnis = OfflineSource.CleanupOldVersions(baseFolder, aktuell.LatestVersion);

            if (ergebnis.AnythingRemoved)
            {
                Log(Tr($"      Alten Stand entfernt: {ergebnis.Describe()} " +
                       $"({OfflineSource.FormatBytes(ergebnis.FreedBytes)} frei)",
                       $"      Removed old build: {ergebnis.Describe()} " +
                       $"({OfflineSource.FormatBytes(ergebnis.FreedBytes)} freed)"));
            }

            foreach (var problem in ergebnis.Problems)
                Log(Tr($"      [!] Aufräumen: {problem}", $"      [!] Cleanup: {problem}"));

            return ergebnis.FreedBytes;
        }
        catch (Exception ex)
        {
            Log(Tr($"      [!] Aufräumen fehlgeschlagen: {ex.Message}",
                   $"      [!] Cleanup failed: {ex.Message}"));
            return 0;
        }
    }

    // ------------------------------------------------------------ Deployment Tool

    /// <summary>
    /// Prueft die Fassung des Deployment Tools und ersetzt sie auf Wunsch durch
    /// die aktuelle vom Microsoft Download Center.
    /// </summary>
    [RelayCommand]
    private async Task UpdateDeploymentTool()
    {
        if (!UiEnabled && !_batchSetup) return;
        var window = ActiveWindow;

        try
        {
            UiEnabled = false;
            IsBusy = true;
            BusyText = Tr("Suche nach dem Office Deployment Tool...", "Looking for the Office Deployment Tool...");
            Log("");
            Log("=========================================");
            if (!await ConnectivityService.IsOnlineAsync())
            {
                IsOffline = true;
                Log(Tr("Der Rechner ist offline - es kann nichts geladen werden.",
                       "The computer is offline - nothing can be downloaded."));
                if (window is not null)
                    await MessageBox.ShowInfo(window,
                        Tr("Keine Internetverbindung", "No internet connection"),
                        Tr("Der Rechner ist derzeit offline. Das Deployment Tool lässt sich erst holen, " +
                           "wenn wieder eine Verbindung besteht.",
                           "The computer is currently offline. The deployment tool can only be fetched " +
                           "once a connection is available again."));
                return;
            }

            IsOffline = false;
            Log(Tr("Frage beim Microsoft Download Center nach dem Deployment Tool...",
                   "Asking the Microsoft Download Center about the deployment tool..."));

            var release = await DeploymentToolService.ResolveLatestAsync(RootFolder);
            OdtOnlineVersion = release?.Version;

            if (release is null)
            {
                Log(Tr("[FEHLER] Der Downloadlink ließ sich nicht ermitteln.",
                       "[ERROR] The download link could not be determined."));

                if (window is not null)
                {
                    var open = await MessageBox.ShowYesNo(window,
                        Tr("Kein Downloadlink gefunden", "No download link found"),
                        Tr("Die Downloadseite von Microsoft war nicht erreichbar oder ihr Aufbau hat sich geändert.\n\n" +
                           "Soll die Seite jetzt im Browser geöffnet werden?\n\n" +
                           "Die heruntergeladene Datei einfach mit\n" +
                           "officedeploymenttool.exe /quiet /extract:<Ordner>\n" +
                           "entpacken und die setup.exe ins Paket legen.\n\n" +
                           "Dauerhaft lässt sich die Adresse in Versionscheck\\ODT-Url.txt hinterlegen.",
                           "The Microsoft download page was unreachable or its layout has changed.\n\n" +
                           "Open the page in the browser now?\n\n" +
                           "Extract the downloaded file using\n" +
                           "officedeploymenttool.exe /quiet /extract:<folder>\n" +
                           "and place setup.exe in the package.\n\n" +
                           "A fixed address can be stored in Versionscheck\\ODT-Url.txt."));

                    if (open) DeploymentToolService.OpenDownloadPage();
                }
                return;
            }

            Log(Tr($"Angeboten wird {release.Version} ({release.FileName}).",
                   $"Version {release.Version} is offered ({release.FileName})."));

            if (!DeploymentToolService.NeedsUpdate(OdtLocalVersion, release.Version))
            {
                Log(Tr($"Das vorhandene Deployment Tool {OdtLocalVersion} ist aktuell - nichts zu tun.",
                       $"The installed deployment tool {OdtLocalVersion} is up to date - nothing to do."));
                if (window is not null)
                    await MessageBox.ShowInfo(window,
                        Tr("Bereits aktuell", "Already up to date"),
                        Tr($"Das Deployment Tool im Paket hat Version {OdtLocalVersion}.\n" +
                           "Eine neuere Fassung wird nicht angeboten.",
                           $"The deployment tool in the package is version {OdtLocalVersion}.\n" +
                           "No newer release is offered."));
                return;
            }

            var targets = DeploymentToolService.DeploymentTargets(RootFolder);

            if (window is not null)
            {
                var vorhanden = OdtLocalVersion is null
                    ? Tr("Im Paket ist bisher keine setup.exe vorhanden.",
                         "There is no setup.exe in the package yet.")
                    : Tr($"Vorhanden ist Version {OdtLocalVersion}.", $"Version {OdtLocalVersion} is present.");

                var confirm = await MessageBox.ShowYesNo(window,
                    Tr("Deployment Tool holen", "Get deployment tool"),
                    Tr($"{vorhanden}\nAngeboten wird {release.Version}.\n\n" +
                       $"Die Datei wird vom offiziellen Microsoft Download Center geladen, entpackt und an " +
                       $"{targets.Count} Stelle(n) im Paket abgelegt:\n\n" +
                       string.Join("\n", targets.Take(6)) +
                       (targets.Count > 6 ? "\n..." : "") +
                       "\n\nJetzt holen?",
                       $"{vorhanden}\nVersion {release.Version} is offered.\n\n" +
                       $"The file will be downloaded from the official Microsoft Download Center, extracted and " +
                       $"placed at {targets.Count} location(s) in the package:\n\n" +
                       string.Join("\n", targets.Take(6)) +
                       (targets.Count > 6 ? "\n..." : "") +
                       "\n\nGet it now?"));

                if (!confirm)
                {
                    Log(Tr("Abgebrochen.", "Cancelled."));
                    return;
                }
            }

            BusyText = Tr($"Deployment Tool {release.Version} wird geladen...",
                          $"Downloading deployment tool {release.Version}...");
            Log(Tr($"Lade {release.Url}", $"Downloading {release.Url}"));

            var extracted = await DeploymentToolService.DownloadAndExtractAsync(
                release, DeploymentToolService.DefaultWorkFolder);

            var extractedVersion = DeploymentToolService.ReadFileVersion(extracted);
            Log(Tr($"Entpackt: setup.exe {extractedVersion}", $"Extracted: setup.exe {extractedVersion}"));

            var count = DeploymentToolService.Deploy(extracted, targets);
            Log(Tr($"{count} Datei(en) im Paket ersetzt.", $"Replaced {count} file(s) in the package."));

            UiEnabled = true;
            await Scan();

            if (window is not null)
                await MessageBox.ShowInfo(window,
                    Tr("Deployment Tool aktualisiert", "Deployment tool updated"),
                    Tr($"Version {extractedVersion} liegt jetzt an {count} Stelle(n) im Paket.",
                       $"Version {extractedVersion} is now present at {count} location(s) in the package."));
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("UpdateDeploymentTool", ex);
            Log(Tr($"[FEHLER] {ex.Message}", $"[ERROR] {ex.Message}"));
            if (ActiveWindow is { } win)
                await MessageBox.ShowInfo(win, Tr("Fehlgeschlagen", "Failed"), ex.Message);
        }
        finally
        {
            CleanTemp();
            IsBusy = false;
            BusyText = "";
            UiEnabled = true;
        }
    }

    [RelayCommand]
    private void OpenDeploymentToolPage() => DeploymentToolService.OpenDownloadPage();

    // ------------------------------------------------------------ Weitere Befehle

    [RelayCommand]
    private async Task SelectRoot()
    {
        var window = ActiveWindow;
        if (window is null) return;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Tr("Paketordner wählen (enthält x32 und x64)",
                       "Select the package folder (contains x32 and x64)"),
            AllowMultiple = false
        });

        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path)) RootFolder = path;
    }

    [RelayCommand]
    private void ResetRoot() => RootFolder = SystemHelpers.GetApplicationDirectory();

    [RelayCommand]
    private void ToggleTheme() => IsLightTheme = !IsLightTheme;

    partial void OnIsLightThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeIcon));
        var app = Avalonia.Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = value
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
    }

    [RelayCommand]
    private void Exit() => ActiveWindow?.Close();

    // ------------------------------------------------------------ Arbeitsordner

    /// <summary>
    /// Entfernt %TEMP%\OfficeInstall samt Inhalt.
    ///
    /// Dort liegen ausschliesslich Kopien, die waehrend eines Vorgangs
    /// gebraucht werden: die angepassten Konfigurationsdateien, bei der
    /// Online-Installation eine Kopie der setup.exe und beim Aktualisieren des
    /// Deployment Tools dessen Selbstentpacker. Nach dem Vorgang wird davon
    /// nichts mehr benoetigt.
    ///
    /// Protokolliert wird bewusst nur, was der Rede wert ist: ein Fehlschlag
    /// oder eine nennenswerte Menge. Ein paar Kilobyte Konfigurationsdateien
    /// waeren im Protokoll nur Rauschen.
    /// </summary>
    private void CleanTemp()
    {
        var result = TempWorkspace.Clear();

        if (result.Error is not null)
        {
            Log(Tr($"  [-] Arbeitsordner konnte nicht geleert werden: {result.Error}",
                   $"  [-] Could not clear the working folder: {result.Error}"));
        }
        else if (result.Removed && result.FreedBytes >= 1024 * 1024)
        {
            Log(Tr($"  Arbeitsdateien entfernt ({result.FreedText}).",
                   $"  Working files removed ({result.FreedText})."));
        }
    }

    /// <summary>
    /// Aufraeumen beim Schliessen des Fensters.
    ///
    /// Laeuft gerade noch ein Vorgang, wird nichts angefasst: Das Deployment
    /// Tool arbeitet dann noch mit der Konfigurationsdatei aus diesem Ordner.
    /// Der Rest verschwindet beim naechsten Start.
    /// </summary>
    public void CleanTempOnExit()
    {
        if (IsBusy || IsBatchRunning) return;
        TempWorkspace.Clear();
    }

    // ------------------------------------------------------------ Helfer

    private static Window? ActiveWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

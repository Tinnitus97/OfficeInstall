using System;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfficeInstall.Services;

namespace OfficeInstall.ViewModels;

/// <summary>Eine Zeile der Produktliste - genau eine installierbare Variante.</summary>
public partial class OfficeEntryViewModel : ObservableObject
{
    private readonly Func<OfficeEntryViewModel, Task> _install;
    private readonly Func<OfficeEntryViewModel, Task> _downloadOffline;

    public OfficeEntryViewModel(CatalogEntry entry,
                                Func<OfficeEntryViewModel, Task> install,
                                Func<OfficeEntryViewModel, Task> downloadOffline)
    {
        Entry = entry;
        _install = install;
        _downloadOffline = downloadOffline;
    }

    public CatalogEntry Entry { get; }

    public string Title => Entry.DisplayName;
    public string FileName => Entry.Configuration.FileName;

    public string ChannelBadge => Entry.ChannelFolderName;
    public string ChannelTitle => Entry.Channel?.Title() ?? Entry.ChannelFolderName;

    public string SubTitle => $"{Entry.ClientEdition} Bit  ·  {Entry.Configuration.FileName}";

    /// <summary>Vom Hauptfenster gesetzte Online-Version des Kanals (null = unbekannt).</summary>
    [ObservableProperty] private Version? _onlineVersion;

    [ObservableProperty] private bool _parentEnabled = true;

    public bool CanInstall => ParentEnabled && Entry.CanInstall;

    /// <summary>true, wenn Offline-Dateien vorliegen, online aber neuer ist.</summary>
    public bool IsOutdated =>
        Entry.Offline.Available && OnlineVersion is not null && Entry.Offline.LatestVersion is not null
        && OnlineVersion > Entry.Offline.LatestVersion;

    partial void OnOnlineVersionChanged(Version? value) => RefreshStatus();
    partial void OnParentEnabledChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsOutdated));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(OfflineLabel));
    }

    public void RefreshTexts() => OnPropertyChanged(string.Empty);

    // ------------------------------------------------------------ Statuszeile

    public string StatusText
    {
        get
        {
            if (Entry.Configuration.IsBroken)
                return Loc.Tr($"Datei nicht auswertbar: {Entry.Configuration.ErrorMessage}",
                              $"File cannot be read: {Entry.Configuration.ErrorMessage}");

            if (Entry.SetupExecutable is null)
                return Loc.Tr("Keine setup.exe gefunden - Installation nicht möglich",
                              "No setup.exe found - cannot install");

            if (!Entry.Offline.Available)
                return Loc.Tr("Keine Offline-Dateien - wird aus dem Internet installiert",
                              "No offline files - will be installed from the internet");

            var local = Entry.Offline.LatestVersion?.ToString() ?? "?";
            var size = OfflineSource.FormatBytes(Entry.Offline.SizeInBytes);

            if (IsOutdated)
                return Loc.Tr($"Offline {local} vorhanden - online gibt es bereits {OnlineVersion}",
                              $"Offline {local} available - the internet already offers {OnlineVersion}");

            if (OnlineVersion is not null)
                return Loc.Tr($"Offline {local} ({size}) - aktueller Stand",
                              $"Offline {local} ({size}) - up to date");

            return Loc.Tr($"Offline {local} ({size}) vorhanden",
                          $"Offline {local} ({size}) available");
        }
    }

    public IBrush StatusBrush
    {
        get
        {
            if (Entry.Configuration.IsBroken || Entry.SetupExecutable is null) return Brush("#E03131"); // rot
            if (IsOutdated) return Brush("#F1A100");                                                    // gelb
            if (Entry.Offline.Available) return Brush("#40C057");                                        // gruen
            return Brush("#7AA2F7");                                                                     // blau
        }
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public string InstallLabel => Loc.Tr("Installieren", "Install");

    /// <summary>
    /// Beschriftung der zweiten Schaltflaeche - je nachdem, ob der
    /// Offline-Bestand erst angelegt, nachgezogen oder nur geprueft wird.
    /// </summary>
    public string OfflineLabel => !Entry.Offline.Available
        ? Loc.Tr("Offline laden", "Download offline")
        : IsOutdated
            ? Loc.Tr("Offline aktualisieren", "Update offline")
            : Loc.Tr("Offline erneuern", "Refresh offline");

    [RelayCommand]
    private Task Install() => _install(this);

    /// <summary>Holt beziehungsweise ergaenzt die Offline-Quelldateien.</summary>
    [RelayCommand]
    private Task DownloadOffline() => _downloadOffline(this);
}

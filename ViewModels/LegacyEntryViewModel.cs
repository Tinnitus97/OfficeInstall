using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfficeInstall.Services;

namespace OfficeInstall.ViewModels;

/// <summary>
/// Eine Zeile der Liste alter Fassungen (Office 2013 / 2016).
///
/// Diese Eintraege werden ausdruecklich NICHT installiert und NICHT auf
/// Aktualisierungen geprueft - es gibt nur den Weg "Download im Browser
/// oeffnen". Deshalb hat diese Klasse weder einen Bezug zum Deployment Tool
/// noch zu OnlineVersionService.
/// </summary>
public partial class LegacyEntryViewModel : ObservableObject
{
    private readonly Action<LegacyEntryViewModel, LegacyDownloadKind> _open;

    public LegacyEntryViewModel(LegacyOfficeRelease release,
                                Action<LegacyEntryViewModel, LegacyDownloadKind> open,
                                int clientEdition)
    {
        Release = release;
        _open = open;
        _clientEdition = clientEdition;
    }

    public LegacyOfficeRelease Release { get; }

    /// <summary>Architektur, die im Hauptfenster gewaehlt ist (32 oder 64).</summary>
    [ObservableProperty] private int _clientEdition = 64;

    partial void OnClientEditionChanged(int value)
    {
        OnPropertyChanged(nameof(SetupLabel));
        OnPropertyChanged(nameof(SetupUrl));
        OnPropertyChanged(nameof(SetupTip));
    }

    public string Title => Release.Title;

    public string YearBadge => Release.Year.ToString();

    public string ImageUrl => Release.ImageUrl();

    public string SetupUrl => Release.SetupUrl(ClientEdition);

    /// <summary>
    /// Statuszeile. Die Kernaussage: abgekuendigt, und das Medium bringt beide
    /// Architekturen mit - deshalb gibt es dafuer keine getrennten Zeilen.
    /// </summary>
    public string StatusText
    {
        get
        {
            var caveat = Loc.Tr(Release.CaveatDe ?? "", Release.CaveatEn ?? "");
            if (!string.IsNullOrWhiteSpace(caveat)) return caveat;

            return Loc.Tr(
                $"Support endete {Release.EndOfSupport} · Medium enthält 32 und 64 Bit",
                $"Support ended {Release.EndOfSupport} · media contains both 32-bit and 64-bit");
        }
    }

    public IBrush StatusBrush => string.IsNullOrWhiteSpace(Release.CaveatDe)
        ? new SolidColorBrush(Color.Parse("#F1A100"))   // gelb: abgekuendigt
        : new SolidColorBrush(Color.Parse("#E03131"));  // rot: Verteilweg unsicher

    public string ImageLabel => Loc.Tr("Medium (.img)", "Media (.img)");

    public string SetupLabel => Loc.Tr($"Installer {ClientEdition} Bit", $"Installer {ClientEdition}-bit");

    public string ImageTip => Loc.Tr(
        $"Komplettes Sicherungsmedium, mehrere Gigabyte.\nEnthält beide Architekturen: setup.exe = 32 Bit, Office\\setup64.exe = 64 Bit.\n\n{ImageUrl}",
        $"Complete backup media, several gigabytes.\nContains both architectures: setup.exe = 32-bit, Office\\setup64.exe = 64-bit.\n\n{ImageUrl}");

    public string SetupTip => Loc.Tr(
        $"Kleiner Starter (wenige MB), der den Rest live nachlädt.\nBraucht während der Installation eine Internetverbindung.\n\n{SetupUrl}",
        $"Small stub (a few MB) that streams the rest during installation.\nNeeds an internet connection while installing.\n\n{SetupUrl}");

    public void RefreshTexts() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void OpenImage() => _open(this, LegacyDownloadKind.Image);

    [RelayCommand]
    private void OpenSetup() => _open(this, LegacyDownloadKind.Setup);
}

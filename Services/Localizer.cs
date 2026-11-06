namespace OfficeInstall.Services;

public enum AppLang { De, En }

/// <summary>
/// Zentrale Sprachverwaltung. Haelt die aktuelle Sprache und liefert ueber
/// Tr(de, en) den passenden Text. Die Oberflaeche frischt ihre Beschriftungen
/// beim Sprachwechsel selbst auf (MainWindowViewModel.OnIsEnglishChanged).
/// </summary>
public sealed class Localizer
{
    public static Localizer I { get; } = new();

    public AppLang Lang { get; set; } = AppLang.De;

    /// <summary>Waehlt je nach aktueller Sprache den deutschen oder englischen Text.</summary>
    public string Tr(string de, string en) => Lang == AppLang.En ? en : de;
}

/// <summary>Bequemer statischer Zugriff: Loc.Tr("...", "...").</summary>
public static class Loc
{
    public static string Tr(string de, string en) => Localizer.I.Tr(de, en);
}

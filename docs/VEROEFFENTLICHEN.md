# Veröffentlichen

Zwei Stände, zwei Etiketten, ein `update.json`.

| | Programm | Konfigurationen |
| --- | --- | --- |
| **Was** | `OfficeInstall.exe` | die 28 XML-Dateien |
| **Nummer** | `1.0.0` (aus der `.csproj`) | fortlaufend: `69`, `70`, … |
| **Etikett** | `v1.0.0` | `configs-70` |
| **Ergebnis** | `OfficeInstall.exe` + `SHA256SUMS.txt` | `configs.zip` + `SHA256SUMS.txt` |

Der Sinn der Trennung: Eine neue XML-Datei soll sich verteilen lassen, ohne das
Programm anzufassen — und ein Programmfehler lässt sich beheben, ohne dass alle
Konfigurationen neu ausgerollt werden müssen.

---

## Aufbau des Repositories

```
OfficeInstall.csproj          das Programm
Services/  ViewModels/  Views/
Assets/app.ico

package/                      die Konfigurationsdateien - die Quelle der Wahrheit
  x32/Current/*.xml
  x32/PerpetualVL2019/*.xml
  …
  x64/PerpetualVL2024/*.xml

tools/validate-configs.py     Prüfung der XML-Dateien
tools/write-update-json.py    schreibt update.json fort

update.json                   was das Programm abfragt
.github/workflows/            Prüfung und Veröffentlichung
```

**Nicht ins Repository gehört der Offline-Bestand.** `Office\Data` sind mehrere
Gigabyte je Basis, und Git bewahrt jede Fassung dauerhaft auf — ein einziges
Update bläht das Repository dauerhaft auf. Die `.gitignore` sperrt diese Pfade
deshalb ausdrücklich.

---

## Ein neues Programm veröffentlichen

1. `<Version>` in der `.csproj` und `VersionString` in
   `ViewModels/MainWindowViewModel.cs` auf dieselbe Nummer setzen.
2. `CHANGELOG.md` ergänzen.
3. Committen, dann:

```bash
git tag v1.0.1
git push origin v1.0.1
```

Der Workflow prüft, ob das Etikett zur `.csproj` passt (sonst bricht er ab),
baut die EXE, bildet die Prüfsumme, legt die Veröffentlichung an und schreibt
`update.json` fort.

---

## Neue Konfigurationen veröffentlichen

1. XML-Datei unter `package/…` ändern oder hinzufügen.
2. Örtlich prüfen — dauert eine Sekunde und erspart eine Fehlinstallation:

```bash
python tools/validate-configs.py package
```

3. Committen, dann mit der **nächsthöheren Zahl** etikettieren:

```bash
git tag configs-70
git push origin configs-70
```

Der Workflow prüft erneut, packt `x32/`, `x64/` und ein frisch erzeugtes
`Versionscheck/Version.txt` (Inhalt: `70|16.08.2026`) zu `configs.zip` und
schreibt `update.json` fort.

---

## Was die Prüfung findet

`validate-configs.py` prüft genau das, was in der XML nicht auffällt, aber beim
Ausrollen scheitert:

| Prüfung | Warum |
| --- | --- |
| `Channel` passt zum Ordner | Sonst: *„This product can't be installed on the selected update channel"* |
| `OfficeClientEdition` passt zu `x32`/`x64` | Sonst installiert die 32-Bit-Zeile 64 Bit |
| Volumenlizenz nur in `PerpetualVL*`, Retail nur in `Current` | Beides zusammen geht nicht |
| Produktkennung je Basis nur einmal | Zwei Dateien für dasselbe Produkt |
| Jede `<Product>` hat eine `<Language>` | Sonst greift die Systemsprache |
| Beide Architekturen bieten dasselbe an | Fällt sonst erst beim Umschalten auf |

Fehler lassen den Workflow scheitern, Warnungen nicht.

---

## Wie das Programm davon erfährt

Es holt sich **eine** Datei:

```
https://raw.githubusercontent.com/Tinnitus97/OfficeInstall/main/update.json
```

Bewusst nicht über `api.github.com`: Die Schnittstelle erlaubt ohne Anmeldung
nur 60 Abrufe je Stunde und IP-Adresse. In einer Firma sitzen alle Rechner
hinter derselben Adresse — nach 60 Programmstarts wäre Schluss.
`raw.githubusercontent.com` wird über ein Auslieferungsnetz bereitgestellt und
kennt diese Grenze nicht.

Die Datei nennt beide Stände samt Adresse und Prüfsumme:

```json
{
  "schema": 1,
  "program": { "version": "1.0.1", "url": "…/OfficeInstall.exe", "sha256": "…" },
  "configs": { "version": "70", "date": "16.08.2026", "url": "…/configs.zip", "sha256": "…" }
}
```

Das Programm vergleicht `program.version` mit seiner eigenen Nummer und
`configs.version` mit `Versionscheck\Version.txt` im Paketordner.

<div align="center">

# OfficeInstall

**Microsoft Office installieren — online oder aus vorliegenden Offline-Dateien, mit Warnung bei veraltetem Stand.**

![Version](https://img.shields.io/badge/Version-1.1.1-blue)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

</div>

---

Die native Ablösung für die `start.bat` des OfficeInstall-Pakets. Gleiche Aufgabe,
gleiche Konfigurationsdateien — aber als kompilierte Windows-Anwendung mit
derselben Technik und Optik wie das *Windows DaSi Tool*, der *Skript-Launcher*
und die *Inventarisierung*.

---

## Was sich gegenüber der start.bat ändert

Das alte Menü hatte 28 fest verdrahtete Einträge. Stand eine XML-Datei nicht
(mehr) im Ordner, lief die Auswahl trotzdem los und `setup.exe` brach ab.

Der Launcher **liest den Paketordner aus**: Angeboten wird genau das, wofür auch
eine Konfigurationsdatei vorhanden ist. Kommt eine XML dazu, erscheint sie ohne
Codeänderung in der Liste; fehlt eine, taucht sie gar nicht erst auf.

Der Produktname kommt dabei aus der Datei selbst (`<Product ID="…"/>`) und wird
in Klartext übersetzt — aus `HomeStudent2019Retail` wird
*Office 2019 Home und Student*.

---

## Installationsbasen bleiben getrennt

`Current`, `PerpetualVL2019`, `PerpetualVL2021` und `PerpetualVL2024` sind
eigenständige Installationsbasen. Sie lassen sich nicht mischen: Aus
PerpetualVL2021 kann nur installiert werden, was dort als XML liegt, und die
Quelldateien einer Basis werden nie für eine andere verwendet.

Der Katalog wird deshalb strikt je Basis aufgebaut:

| | |
| --- | --- |
| **Current** | Microsoft 365 und die Retail-Versionen (Home & Student, Home & Business) |
| **PerpetualVL2019** | Volumenlizenz 2019 (Standard, Pro Plus) |
| **PerpetualVL2021** | Volumenlizenz 2021 (Standard, Pro Plus) |
| **PerpetualVL2024** | Volumenlizenz 2024 (Standard, Pro Plus) |

Über den Umschalter **64 Bit / 32 Bit** wird zwischen den Ordnern `x64` und
`x32` gewechselt.

---

## Online und Offline

| Modus | Verhalten |
| --- | --- |
| **Automatisch** | Liegen Offline-Dateien vor, wird daraus installiert — sonst aus dem Internet. Das entspricht dem Verhalten der alten `start.bat`. |
| **Online** | Immer aus dem Internet, auch wenn Offline-Dateien vorhanden sind. |
| **Offline** | Nur aus den vorliegenden Dateien. Fehlen sie, wird der Vorgang mit einem Hinweis abgelehnt statt heimlich online zu installieren. |

### Erkennung der Offline-Dateien

`setup.exe /download <Konfiguration>` legt die Quelldateien neben der `setup.exe`
in einem Ordner `Office\Data` ab — darin je ein Unterordner pro Version und die
passende `v32.cab` beziehungsweise `v64.cab`. Genau danach sucht der Launcher
selbstständig:

```
x64\PerpetualVL2021\Office\Data\16.0.17928.20216\...
x64\PerpetualVL2021\Office\Data\v64.cab
```

Gefundene Dateien erscheinen mit Version und Größe direkt an der jeweiligen
Zeile. Es muss also niemand von Hand angeben, ob offline installiert werden kann.

### Warum überhaupt zwei Modi

Ohne `SourcePath` sucht das Deployment Tool die Quelldateien im Ordner der
`setup.exe` und geht erst danach ins Internet. Für die beiden ausdrücklichen
Modi genügt es deshalb nicht, einfach die vorhandene XML zu starten:

- **Offline** — es wird eine Kopie der Konfiguration mit gesetztem `SourcePath`
  **und festgeschriebener Version** erzeugt. Damit steht fest, aus welchem
  Ordner und welcher Stand installiert wird.
- **Online** — die Kopie wird ohne `SourcePath` in einen leeren Arbeitsordner
  unter `%TEMP%\OfficeInstall` gelegt, zusammen mit einer Kopie der
  `setup.exe`. Da dort keine Quelldateien liegen, lädt das Tool aus dem Internet.

Die Originaldateien im Paketordner werden dabei **nicht verändert**.

Der Arbeitsordner `%TEMP%\OfficeInstall` wird **nach jedem Vorgang und beim
Beenden wieder entfernt** — und vorsorglich auch beim Start, falls das Programm
beim letzten Mal abgestürzt ist. Wird das Fenster geschlossen, während noch ein
Vorgang läuft, bleibt er stehen: Dort liegt dann die Konfigurationsdatei, mit
der `setup.exe` gerade arbeitet.

### Warum `SourcePath` allein nicht genügt

`SourcePath` sagt nur, **wo** gesucht wird — nicht, **was**. Ohne
Versionsangabe fragt das Deployment Tool beim CDN nach, welcher Build für den
Kanal gerade der aktuelle ist, und will genau den installieren. Liegt vor Ort
ein älterer Stand, lädt es die fehlende Version aus dem Internet: Der
Offline-Ordner wird faktisch übergangen, obwohl `SourcePath` korrekt gesetzt
ist. Das erklärt das häufig beobachtete „es lädt trotzdem alles aus dem Netz".

Deshalb schreibt die Offline-Installation zusätzlich die tatsächlich
vorliegende Version fest:

```xml
<Add SourcePath="…\x64\PerpetualVL2021" Version="16.0.19426.20186"
     Channel="PerpetualVL2021" OfficeClientEdition="64">
```

Der Kanal gehört laut Microsoft zur Versionsangabe dazu und kommt immer aus dem
Ordner — siehe den nächsten Abschnitt. Im Protokoll erscheint dann die Zeile
`Version festgeschrieben: … - es wird nichts aus dem Internet geladen.`

Beim **Herunterladen** bleibt die Version bewusst offen — dort soll ja gerade
der neueste Stand geholt werden.

### Der Ordner bestimmt den Kanal

Steht in einer Konfigurationsdatei ein anderer Kanal, als der Ordner vorgibt —
etwa `Channel="PerpetualVL2021"` in einer Datei unter `x64\PerpetualVL2019` —,
lehnt das Deployment Tool die Installation rundheraus ab:

> This product can't be installed on the selected update channel.

Ein Volumenlizenz-Produkt von 2019 lässt sich auf dem 2021er-Kanal eben nicht
installieren. Deshalb trägt das Programm in seine Arbeitskopie **immer den Kanal
des Ordners** ein — beim Installieren wie beim Herunterladen. Die Datei im
Paketordner bleibt unverändert; die Abweichung wird aber im Protokoll gemeldet,
mit Dateinamen, damit sie sich dort berichtigen lässt.

---

## Selbstaktualisierung

Beim Start holt das Programm **eine** Datei:

```
https://raw.githubusercontent.com/Tinnitus97/OfficeInstall/main/update.json
```

Darin stehen zwei getrennte Stände:

| | Nummer | Woher | Was passiert |
| --- | --- | --- | --- |
| **Programm** | `1.1.1` aus der EXE | Release `v1.1.1` | Neue EXE laden, Prüfsumme vergleichen, austauschen, neu starten |
| **Konfigurationen** | `Versionscheck\Version.txt` | Release `configs-70` | `configs.zip` laden, XML-Dateien in den Paketordner schreiben |

Gibt es etwas Neueres, erscheint über der Produktliste ein Streifen mit den
passenden Schaltflächen — gibt es nichts, kostet das keinen Pixel.
**Heruntergeladen und ersetzt wird nichts ohne Rückfrage.**

### Warum nicht die GitHub-Schnittstelle

`api.github.com` erlaubt ohne Anmeldung nur **60 Abrufe je Stunde und
IP-Adresse**. In einer Firma sitzen alle Rechner hinter derselben Adresse — nach
60 Programmstarts wäre Schluss. Und ein Zugangstoken hat in einer verteilten EXE
nichts zu suchen. `raw.githubusercontent.com` wird über ein Auslieferungsnetz
bereitgestellt und kennt diese Grenze nicht.

Eine abweichende Adresse lässt sich in `Versionscheck\Update-Url.txt`
hinterlegen (eine Zeile, vollständige `https://…`-Adresse) — etwa für ein
internes Spiegelverzeichnis.

### Was beim Einspielen geschützt ist

- Übernommen wird **nur**, was unter `x32\` oder `x64\` liegt, genau eine Ebene
  tief und auf `.xml` endet — dazu `Versionscheck\Version.txt`. Eine EXE im
  Archiv, eine `.cab`, ein Pfad mit `..\`: alles wird übergangen.
- **Gelöscht wird nichts.** Der Offline-Bestand (`Office\Data`, mehrere
  Gigabyte) wird nicht angefasst.
- Vor dem Einspielen wird die **SHA256-Summe** gegen die Angabe in `update.json`
  geprüft. Stimmt sie nicht, wird die Datei verworfen.

### Warum die EXE sich nicht selbst überschreibt

Eine laufende EXE kann das unter Windows nicht. Deshalb schreibt das Programm
ein kleines Skript, das auf sein Ende wartet, die Datei ersetzt und neu startet.
Es liegt in `%TEMP%\OfficeInstall-Update` — bewusst **nicht** im Arbeitsordner
`%TEMP%\OfficeInstall`, denn der wird beim Beenden geleert, also genau
währenddessen.

Schlägt das Ersetzen fehl — fehlende Schreibrechte, oder das Programm läuft an
einem anderen Arbeitsplatz aus demselben Ordner —, bleibt ein Fenster mit dem
Grund und dem Pfad zur neuen Fassung stehen.

---

## Suchen statt scrollen

Über der Liste sitzt ein Suchfeld. Gesucht wird in Klartextname,
Produktkennung, Dateiname, Installationsbasis und Architektur — mehrere durch
Leerzeichen getrennte Begriffe müssen alle zutreffen:

| Eingabe | Ergebnis |
| --- | --- |
| `2021` | alles rund um Office 2021 |
| `2021 pro` | nur Office 2021 Pro Plus |
| `perpetual` | alle Volumenlizenz-Basen |
| `365` | Microsoft 365 Business und Apps for Enterprise |
| `AppsBusiness` | die Zeile zu `configuration_AppsBusiness.xml` |

Das `✕` daneben leert die Suche wieder. Die Suche gilt auch für den Bereich
*Ältere Fassungen* — `2013` blendet dort genau die vier Editionen von 2013 ein.

---

## Ältere Fassungen: Office 2013 und 2016

Unter der Produktliste sitzt ein zugeklappter Bereich mit acht Einträgen —
Office 2013 und Office 2016, je Home & Student, Home & Business, Professional
und Professional Plus, auf Deutsch.

**Diese Fassungen werden nicht installiert und nicht auf Updates geprüft.** Es
gibt für sie nur den Download, und der läuft ausdrücklich über den
Standardbrowser:

| Schaltfläche | Was passiert |
| --- | --- |
| **Medium (.img)** | Öffnet das komplette Sicherungsmedium (mehrere GB). Es enthält **beide Architekturen**: `setup.exe` im Wurzelverzeichnis installiert 32 Bit, `Office\setup64.exe` installiert 64 Bit. |
| **Installer 32/64 Bit** | Öffnet den kleinen Click-to-Run-Starter. Die Bitzahl folgt dem Umschalter oben; der Rest wird während der Installation nachgeladen. |

**Wo die Datei landet, entscheidet allein der Browser** (üblicherweise
*Downloads*). Im Paketordner wird nichts abgelegt — das Programm lädt hier
selbst nichts herunter, es reicht die Adresse an Windows weiter.

### Warum getrennt vom Rest

Das Office Deployment Tool kennt nur `Current`, `PerpetualVL2019`,
`PerpetualVL2021` und `PerpetualVL2024`. Für Office 2016 gibt es **keinen**
Perpetual-Kanal, und Office 2013 bräuchte ein eigenes Deployment Tool der
Version 15. Beide werden über ihr eigenes Medium installiert — deshalb ein
eigener Bereich statt einer weiteren Zeile in der Produktliste.

### Zwei Dinge zum Mitdenken

- **Beide Fassungen sind abgekündigt** — Office 2013 seit 11.04.2023,
  Office 2016 seit 14.10.2025. Die Zeile darunter nennt das Datum.
- **Das sind Retail-Abbilder.** Sie verlangen einen Retail-Schlüssel.
  Volumenlizenz-Fassungen (ProPlus VL) gibt es auf diesem Weg nicht — die
  liegen im Microsoft 365 Admin Center. *Professional Plus 2013* ist rot
  gekennzeichnet: Diese Edition kam überwiegend als MSI, der Verteilweg ist
  unsicher.

---

## Deployment Tool holen und aktuell halten

Ohne `setup.exe` läuft nichts — deshalb steht ihr Zustand oben im Fenster:
gefundene Version, Vergleich mit der von Microsoft angebotenen und eine farbige
Kennzeichnung (grün aktuell, gelb veraltet, rot gar nicht vorhanden).

Ein Klick auf **Deployment Tool laden / aktualisieren** holt die aktuelle
Fassung vom offiziellen
[Microsoft Download Center](https://www.microsoft.com/en-us/download/details.aspx?id=49117),
entpackt sie mit `/quiet /extract:` und legt die `setup.exe` an **allen**
Stellen im Paket ab, an denen bisher eine lag — war gar keine da, landet sie im
Paketordner.

Der Abgleich kostet dabei keinen Download: Die angebotene Version steckt bereits
im Dateinamen (`officedeploymenttool_20228-20124.exe` → 16.0.20228.20124).
Geladen wird nur, wenn es wirklich etwas Neueres gibt, und erst nach Rückfrage.

> **Zur Bezugsquelle:** Im Netz kursiert die Abkürzung
> `officecdn.microsoft.com/pr/wsus/setup.exe`. Microsoft hat auf Nachfrage
> bestätigt, dass das *kein* offizieller Downloadweg ist — deshalb wird
> ausschließlich das Download Center verwendet.
>
> Sollte Microsoft den Aufbau der Seite ändern, lässt sich die Adresse dauerhaft
> in `Versionscheck\ODT-Url.txt` hinterlegen (eine Zeile, die vollständige
> `https://…/officedeploymenttool_*.exe`-Adresse). Schlägt beides fehl, bietet
> das Programm an, die Downloadseite im Browser zu öffnen.

---

## Offline-Bestand anlegen und nachziehen

Jede Zeile hat eine zweite Schaltfläche, deren Beschriftung sich nach dem
Zustand richtet:

| Beschriftung | Situation |
| --- | --- |
| **Offline laden** | Es liegen noch keine Quelldateien vor |
| **Offline aktualisieren** | Online gibt es einen neueren Stand |
| **Offline erneuern** | Bestand ist aktuell, wird nur geprüft |

Dahinter steckt `setup.exe /download` mit ausdrücklich gesetztem `SourcePath`,
damit die Dateien sicher im Ordner der jeweiligen Installationsbasis landen und
nicht dort, wo zufällig die `setup.exe` liegt.

**Vorhandene Dateien werden nicht erneut geladen** — das Deployment Tool
ergänzt nur, was fehlt. Ein Update auf eine neue Version zieht also lediglich
die Unterschiede nach, statt mehrere Gigabyte neu zu holen. Nach dem Download
wird der Ordner automatisch neu eingelesen, die neue Version steht sofort in
der Liste.

Der Vorgang läuft ohne eigenes Fenster und kann bei einem frischen Bestand
dauern; solange zeigt der Launcher einen Fortschrittsbalken und protokolliert
das Ergebnis.

### Alles auf einmal

**Alle Offline-Bestände aktualisieren oder runterladen** arbeitet sämtliche
Installationsbasen ab — beide Architekturen. Vorhandene Bestände werden
nachgezogen, leere komplett geladen.

Je Basis läuft dabei **ein** Durchlauf, nicht einer je Konfigurationsdatei:
Alle Produkte einer Basis kommen in eine gemeinsame Konfiguration (das
Deployment Tool erlaubt mehrere `<Product>`-Einträge in einem `<Add>`). Bei
acht XML-Dateien in `Current` ist das ein Aufruf statt acht — geladen wird
dieselbe Menge.

Die Rückfrage zeigt vorher je Basis, was passiert:

```
  x64\Current           (8 Produkte, 16.0.19426.20186 -> 16.0.20228.20190)
  x64\PerpetualVL2019   (2 Produkte, aktuell, wird nur geprüft)
  x32\PerpetualVL2021   (2 Produkte, wird komplett geladen)
```

Steht dabei eine Basis ganz ohne Dateien in der Liste, wird ausdrücklich
gewarnt — dort kommen mehrere Gigabyte zusammen. Abbrechen ist jederzeit
möglich; der gerade laufende Download wird noch zu Ende geführt.

### Alter Stand wird entfernt

Das Deployment Tool lässt die vorherige Version in `Office\Data` liegen — nach
ein paar Aktualisierungen sammeln sich dort schnell mehrere Gigabyte. Nach
einem erfolgreichen Download räumt der Launcher deshalb auf:

- entfernt werden **nur** Ordner mit einer *kleineren* Versionsnummer als der
  neuesten sowie die zugehörigen `v32_<Version>.cab` / `v64_<Version>.cab`
- `v32.cab`, `v64.cab` und alles Übrige bleiben unangetastet

Der freigewordene Platz steht im Protokoll. Abschaltbar über die Option
**Alten Stand nach dem Update löschen**.

---

## Warnung bei veraltetem Offline-Stand

Beim Start prüft der Launcher zuerst mit einer kurzen Anfrage, ob überhaupt
eine Internetverbindung besteht — gegen denselben Endpunkt, den auch Windows
für seine Netzwerkerkennung benutzt. Ist der Rechner offline, steht **eine**
Zeile im Protokoll und der Abgleich entfällt; es wird nicht für jede
Installationsbasis eine eigene Fehlermeldung erzeugt.

Besteht Verbindung, wird die Versionsübersicht von Microsoft geholt:

```
https://clients.config.office.net/releases/v1.0/OfficeReleases
```

Ein einziger Aufruf liefert alle Kanäle. Zugeordnet wird über die Kanalkennung
(`channelId`, z. B. `PerpetualVL2021`) **und** über die GUID in `cdnBaseUrl` —
die Zuordnung hält also auch, wenn Microsoft die Anzeigenamen ändert.

Ist die Online-Version höher als die der vorliegenden Offline-Dateien, erscheint
ein Warnband über der Liste und die betroffene Zeile wird gelb. Vor einer
Offline-Installation kommt zusätzlich eine Rückfrage — mit *Nein* lässt sich
abbrechen und stattdessen online installieren.

> **Nicht verwendbar:** `officecdn.microsoft.com/pr/<GUID>/Office/Data/VersionDescriptor.xml`
> klingt naheliegend, wird vom CDN aber mit **HTTP 400** beantwortet — dort
> werden nur `.cab`-Dateien ausgeliefert. Frühere Fassungen dieses Programms
> haben genau deshalb nie eine Auskunft bekommen.

---

## Systemanforderungen

| Anforderung | Details |
| --- | --- |
| **Betriebssystem** | Windows 10 oder Windows 11 |
| **Berechtigungen** | Administratorrechte — die UAC-Abfrage erfolgt automatisch beim Start (ersetzt den `Get_Admin`-Block der alten `start.bat`) |
| **setup.exe** | Das Office Deployment Tool muss im Ordner der Installationsbasis liegen (ersatzweise im Architektur- oder Paketordner) |
| **.NET** | Nicht erforderlich — die Laufzeit ist in der EXE enthalten |

---

## Nutzung

1. `OfficeInstall.exe` in den Ordner legen, in dem vorher die
   `start.bat` lag — also neben `x32` und `x64`.
2. EXE starten und die UAC-Abfrage mit **Ja** bestätigen. Der Paketordner wird
   sofort durchsucht.
3. **64 Bit** oder **32 Bit** wählen und den **Modus** festlegen.
4. Bei der gewünschten Zeile auf **Installieren** klicken und die Rückfrage
   bestätigen. Der Fortschritt erscheint anschließend im Fenster von Microsoft;
   das Ergebnis steht danach im Protokoll.

### Offline-Dateien anlegen oder erneuern

Am einfachsten über die Schaltfläche **Offline laden / aktualisieren** an der
jeweiligen Zeile. Von Hand geht es weiterhin genauso:

```bat
cd x64\PerpetualVL2021
setup.exe /download configuration_Standard_2021.xml
```

Danach im Launcher auf **Erneut suchen** klicken — die Dateien werden sofort
erkannt.

---

## Technische Details

| | |
| --- | --- |
| **Sprache** | C# |
| **UI-Framework** | [Avalonia](https://avaloniaui.net/) 12.1 |
| **Zielplattform** | .NET 10, `win-x64`, self-contained |
| **Installation** | `setup.exe /configure <Konfiguration>` des Office Deployment Tools |
| **Fehlerbehandlung** | Unerwartete Fehler landen in `%USERPROFILE%\Desktop\OfficeInstall_Absturz.log` |

### Aufbau

```
Services/OfficeChannels.cs        die vier Installationsbasen samt CDN-Kennung
Services/OfficeProductNames.cs    Produktkennung -> Klartext
Services/OfficeConfiguration.cs   eine XML lesen, Kopien mit SourcePath/Version schreiben
Services/LegacyOfficeDownloads.cs Office 2013/2016: Adressen bauen, Browser öffnen
Services/OfficeCatalog.cs         Paketordner durchsuchen, setup.exe finden
Services/OfflineSource.cs         Office\Data erkennen und Version bestimmen
Services/OnlineVersionService.cs  Versionsübersicht von Microsoft auswerten
Services/ConnectivityService.cs   kurze Netzprüfung vor jedem Onlinezugriff
Services/InstallRunner.cs         Modus auflösen, Aufruf vorbereiten und starten
Services/TempWorkspace.cs         Arbeitsordner unter %TEMP% anlegen und leeren
Services/UpdateService.cs         update.json abfragen, Programm und Konfigurationen einspielen
Services/DeploymentToolService.cs setup.exe finden, holen, entpacken und verteilen
ViewModels/                       Zustand und Ablauf
Views/                            Fenster und Meldungen
```

### Selbst bauen

Voraussetzung ist das [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
.\build.ps1
```

Die fertige EXE liegt anschließend im Ordner `publish`.

| Parameter | Wirkung |
| --- | --- |
| `.\build.ps1` | Self-contained Build (Standard) — läuft ohne installiertes .NET |
| `.\build.ps1 -Mode framework` | Deutlich kleinere EXE, setzt .NET 10 Desktop Runtime voraus |
| `.\build.ps1 -Clean` | Vorher `bin/` und `obj/` löschen |
| `.\build.ps1 -Sign` | Signiert die EXE, um SmartScreen-Warnungen zu reduzieren |

---

## Was nicht übernommen wurde

Die alte `Batch\Onlinecheck.bat` hat per `aria2c.exe` eine Batch-Datei von
SharePoint geladen und **ungeprüft ausgeführt**, um das OfficeInstall-Paket
selbst zu aktualisieren. Das ist bewusst nicht mit übernommen: Eine kompilierte
Anwendung, die beliebigen heruntergeladenen Code ausführt, ist ein Einfallstor
und wird von Virenscannern zu Recht angefasst.

Der Paketstand aus `Versionscheck\Version.txt` wird weiterhin gelesen und in der
Kopfzeile angezeigt. Wer eine Aktualisierungsmeldung möchte, kann sie wie im
Windows DaSi Tool nachrüsten: eine Textdatei mit der aktuellen Versionsnummer im
Netz, ein Vergleich, ein Hinweisband — ohne Ausführung von Fremdcode.

`aria2c.exe` wird nicht mehr benötigt.

---

## Anpassen

**Weitere Produkte:** einfach die passende XML-Datei in den Ordner der
Installationsbasis legen. Sie erscheint automatisch in der Liste. Ist die
Produktkennung in `Services/OfficeProductNames.cs` nicht hinterlegt, wird die
Kennung selbst angezeigt — eine Ergänzung dort macht daraus einen Klartextnamen.

**Weitere Installationsbasis:** in `Services/OfficeChannels.cs` ergänzen
(Ordnername plus CDN-Kennung des Kanals).

**Icon:** `Assets/app.ico` austauschen — aktuell liegt dort das Icon des
Windows DaSi Tool als Platzhalter.

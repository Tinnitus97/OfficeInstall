# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden hier dokumentiert.

## [1.1.0] – 2026-08-16

### Neu

- **Selbstaktualisierung über GitHub.** Beim Start fragt das Programm eine
  einzige Datei ab:

  ```
  https://raw.githubusercontent.com/Tinnitus97/OfficeInstall/main/update.json
  ```

  Darin stehen zwei getrennte Stände — das **Programm** und die
  **Konfigurationsdateien**. Gibt es etwas Neueres, erscheint über der
  Produktliste ein Streifen mit den passenden Schaltflächen; gibt es nichts,
  kostet das keinen Pixel.

  Bewusst **nicht** über `api.github.com`: Die Schnittstelle erlaubt ohne
  Anmeldung nur 60 Abrufe je Stunde und IP-Adresse — in einer Firma sitzen alle
  Rechner hinter derselben Adresse. Ein Zugangstoken hätte in einer verteilten
  EXE ohnehin nichts zu suchen. `raw.githubusercontent.com` wird über ein
  Auslieferungsnetz bereitgestellt und kennt diese Grenze nicht.

  Eine abweichende Adresse lässt sich in `Versionscheck\Update-Url.txt`
  hinterlegen — für ein internes Spiegelverzeichnis.

- **Programm aktualisieren.** Lädt die neue EXE, prüft ihre SHA256-Summe gegen
  die Angabe in `update.json` und tauscht sie aus. Weil eine laufende EXE sich
  nicht selbst überschreiben kann, erledigt das ein kleines Skript, das auf das
  Ende des Vorgangs wartet und danach neu startet. Es liegt in einem **eigenen**
  Ordner (`%TEMP%\OfficeInstall-Update`) — der Arbeitsordner
  `%TEMP%\OfficeInstall` wird beim Beenden geleert, also genau währenddessen.

  Schlägt das Ersetzen fehl (fehlende Rechte, oder das Programm läuft an einem
  anderen Arbeitsplatz aus demselben Ordner), bleibt ein Fenster mit dem Grund
  und dem Pfad zur neuen Fassung stehen.

- **Konfigurationen einspielen.** Lädt `configs.zip`, prüft die Summe und
  schreibt die XML-Dateien in den Paketordner. Übernommen wird ausschließlich,
  was unter `x32\` oder `x64\` liegt, genau eine Ebene tief und auf `.xml`
  endet — dazu `Versionscheck\Version.txt`. Alles andere im Archiv wird
  übergangen; Einträge, die aus dem Paketordner herausführen würden, ebenso.
  **Gelöscht wird nichts**, der Offline-Bestand bleibt unangetastet.

  Danach wird der Paketordner neu eingelesen, die Liste ist sofort aktuell.

- Heruntergeladen und ersetzt wird **nichts ohne Rückfrage**. Beide Vorgänge
  fragen vorher mit Versionsnummer und Zielpfad nach.

## [1.0.0] – 2026-08-16 · Erste Veröffentlichung

Native Ablösung der `start.bat`: C# / .NET 10 mit Avalonia, als eine EXE ohne
Installation und ohne Abhängigkeiten.

### Was das Programm kann

- **Liest den Paketordner aus** statt fest verdrahteter Menüeinträge. Angeboten
  wird genau das, wofür eine Konfigurationsdatei vorhanden ist; Produktnamen
  kommen aus der XML und werden in Klartext übersetzt.
- **Vier Installationsbasen bleiben strikt getrennt** — Current,
  PerpetualVL2019, PerpetualVL2021, PerpetualVL2024, jeweils für 32 und 64 Bit.
- **Drei Modi:** Automatisch, Online, Offline. Offline-Quelldateien werden
  selbstständig erkannt, mit Version und Größe an der jeweiligen Zeile.
- **Echte Offline-Installation:** `SourcePath` *und* die vorliegende Version
  werden festgeschrieben, der Kanal kommt aus dem Ordner. Ohne beides holt sich
  das Deployment Tool den aktuellen Build aus dem Internet.
- **Abgleich mit Microsoft** über die offizielle Versionsübersicht, mit
  vorgeschalteter Netzprüfung. Gemeldet wird nur ein neuerer Stand oder ein
  Fehler.
- **Offline-Bestand anlegen und nachziehen** — einzeln oder für alle Basen in
  einem Durchlauf je Basis, auf Wunsch mit Entfernen des alten Standes.
- **Deployment Tool holen und aktuell halten** aus dem Microsoft Download
  Center, inklusive Verteilung der `setup.exe` an alle Stellen im Paket.
- **Suchfeld** über der Produktliste; **Office 2013 und 2016** als eigener,
  zugeklappter Bereich mit Download über den Standardbrowser.
- **Deutsch und Englisch**, heller und dunkler Modus, ausführliches Protokoll.
- Der Arbeitsordner unter `%TEMP%` wird nach jedem Vorgang, beim Beenden und
  beim Start wieder geleert.

---

## Entwicklungsstände bis zur ersten Veröffentlichung

Die folgenden Nummern waren interne Zwischenstände während der Entwicklung und
wurden nicht ausgeliefert. Sie bleiben hier stehen, weil in den Einträgen steht,
*warum* etwas so gelöst ist — das ist beim späteren Nachlesen mehr wert als eine
saubere Zählung.

### Stand 1.9.0 – 2026-08-16

#### Behoben

- **„This product can't be installed on the selected update channel."** Office
  2019 Standard ließ sich nicht installieren. Der Setup-Log von Microsoft nennt
  den Grund unmissverständlich:

  ```
  ConfigFile::ParseAttribute: Value of SourcePath: …\x64\PerpetualVL2019
  ConfigFile::ParseAttribute: Value of Version:    16.0.10417.20197
  ConfigFile::ParseAttribute: Value of Channel:    PerpetualVL2021   ← falsch
  …
  ChannelCompute::GetSelectedChannel: Set incoming channel by admin/PB is not
  compatible with incoming set of skus {'Incoming Channel':'PerpetualVL2021'}
  ```

  In `x64\PerpetualVL2019\configuration_Standard_2019.xml` steht
  `Channel="PerpetualVL2021"`. Ein Volumenlizenz-Produkt von 2019 lässt sich auf
  dem 2021er-Kanal nicht installieren — das Deployment Tool bricht ab, noch
  bevor es die Quelldateien anfasst.

  Bisher wurde ein in der Vorlage vorhandener Kanal unangetastet gelassen. Jetzt
  gilt: **Der Ordner bestimmt die Installationsbasis, nicht die Vorlage.** Beim
  Installieren (offline wie online) und beim Herunterladen wird der Kanal des
  Ordners eingetragen. Das betrifft nur die Arbeitskopie unter `%TEMP%`; die
  Datei im Paketordner bleibt unverändert.

  Weicht die Vorlage ab, steht es zweimal im Protokoll — beim Durchsuchen mit
  Dateinamen, damit sich die Vorlage berichtigen lässt, und direkt vor dem
  Start:

  ```
  [!] configuration_Standard_2019.xml: Channel="PerpetualVL2021" passt nicht
      zum Ordner PerpetualVL2019 - es gilt der Ordner
  ```

  **Zum Nachziehen:** Die Datei im Paket sollte trotzdem berichtigt werden —
  sonst lädt jedes andere Werkzeug, das sie benutzt, weiterhin den falschen
  Kanal.

### Stand 1.8.4 – 2026-08-16

#### Geändert

- **Die Modus-Zeile bricht jetzt an der richtigen Stelle um.** Der zweite Satz
  („Sollten diese nicht verfügbar sein…") beginnt in einer eigenen Zeile statt
  irgendwo im ersten Satz umzubrechen — bisher hing die Stelle von der
  Fensterbreite ab.
- **Das Protokoll steht auf einem festen Raster.** Die Namen der
  Installationsbasen werden auf gleiche Breite gebracht; Anzahl, Version und
  Größe stehen dadurch untereinander statt versetzt:

  ```
    x32\Current          8 XML · 16.0.20228.20190 · 2,5 GB
    x32\PerpetualVL2019  2 XML · 16.0.10417.20197 · 1,5 GB
    x64\Current          8 XML · 16.0.20228.20190 · 3,2 GB
    Deployment Tool      16.0.20228.20124 · .\setup.exe
  ```

  Die Zeile zum Deployment Tool und die Meldung über eine Basis ohne XML-Datei
  liegen jetzt auf demselben Raster — vorher standen sie für sich.

### Stand 1.8.3 – 2026-08-16

#### Behoben

- **`%TEMP%\OfficeInstall` bleibt nicht mehr liegen.** Der Ordner enthält
  ausschließlich Kopien, die während eines Vorgangs gebraucht werden: die
  angepassten Konfigurationsdateien, bei der Online-Installation eine Kopie der
  `setup.exe` und beim Aktualisieren des Deployment Tools dessen
  Selbstentpacker. Bisher blieb das alles nach getaner Arbeit stehen.

  Aufgeräumt wird jetzt an vier Stellen:

  - **nach jedem abgeschlossenen Vorgang** — Installation, Offline-Download,
    Sammel-Update und Deployment-Tool-Update, jeweils erst nachdem das
    Deployment Tool sich beendet hat;
  - **beim Beenden** — egal ob über die Schaltfläche, das Kreuz in der
    Titelleiste oder Alt+F4;
  - **beim Start**, damit Reste eines Absturzes verschwinden — allerdings nur,
    wenn das Programm nicht gerade ein zweites Mal läuft, sonst würde man einer
    parallelen Sitzung die Arbeitsdateien wegräumen;
  - **nicht** beim Schließen während eines laufenden Vorgangs — dann arbeitet
    `setup.exe` noch mit der Konfigurationsdatei aus diesem Ordner. Der Rest
    verschwindet beim nächsten Start.

  Gelöscht wird über denselben Weg wie beim Offline-Bestand: Schreibschutz,
  Versteckt- und System-Kennzeichen werden vorher zurückgesetzt und es wird
  dreimal nachgefasst — der Selbstentpacker des Deployment Tools legt Dateien
  schreibgeschützt ab. Im Protokoll steht davon nur etwas, wenn es fehlschlägt
  oder mindestens ein Megabyte frei wird.

### Stand 1.8.2 – 2026-08-16

#### Geändert

- **Das Protokoll ist noch einmal kürzer.** Aus 13 Zeilen (von denen die Hälfte
  in der schmalen Spalte umbrach, also rund 19 Bildschirmzeilen) werden 11, von
  denen keine mehr umbricht:

  - Die Ankündigung *„Der Paketordner wird nach Installationsbasen
    durchsucht…"* ist weg — die nächste Zeile nennt den Ordner ohnehin.
  - Die Zeilen je Installationsbasis sind gestrafft:
    `[+] x64\Current: 8 Konfiguration(en), Offline 16.0.20228.20190 (3,2 GB)`
    wird zu `x64\Current: 8 XML · 16.0.20228.20190 · 3,2 GB`.
  - Beim Deployment Tool steht der Pfad relativ zum Paketordner
    (`.\setup.exe` statt der vollständigen Angabe).
  - Die Ankündigung *„Prüfe, ob online…"* entfällt; protokolliert wird erst das
    Ergebnis — bei sauberem Stand die eine Zeile
    `Abgleich mit Microsoft: alles aktuell (8 Bestände).`
- **Die Modus-Zeile im Automatikbetrieb ist wieder ausführlich** formuliert:
  „Das Programm greift zunächst auf die lokalen Installationsdaten zu. Sollten
  diese nicht verfügbar sein, werden sie aus dem Internet heruntergeladen."
  Damit der Satz vollständig sichtbar bleibt, darf diese Zeile jetzt umbrechen
  statt abgeschnitten zu werden.

### Stand 1.8.1 – 2026-08-16

#### Geändert

- **Der Versionscheck meldet nur noch, was wichtig ist.** Bisher stand im
  Protokoll je Installationsbasis und Architektur eine Zeile
  `[+] Current (64 Bit): online 16.0.20228.20190` — bei acht Beständen also acht
  Zeilen, die nichts aussagen. Gemeldet wird jetzt nur noch ein **neuerer Stand**
  oder ein **Fehler**; ist alles in Ordnung, bleibt es bei einer einzigen Zeile
  `Alles auf dem neuesten Stand (8 Bestände geprüft).`
- **Die dauerhafte Trefferzahl ist weg.** `14 Installation(en) für 64 Bit
  gefunden.` stand immer da, sagte aber nichts, was die Liste darunter nicht
  selbst zeigt. Übrig bleibt die Rückmeldung während einer **Suche**
  (`3 von 14 passen zu "2021".`) — sie erscheint nur, solange etwas im Suchfeld
  steht.
- **Die Modus-Zeile ist kürzer und klarer formuliert:**

  | Modus | vorher | jetzt |
  | --- | --- | --- |
  | Automatisch | Offline-Dateien werden bevorzugt; fehlen sie, wird aus dem Internet installiert. | Erst aus dem Paketordner, sonst aus dem Internet. |
  | Online | Es wird immer aus dem Internet installiert, auch wenn Offline-Dateien vorliegen. | Immer aus dem Internet - vorliegende Dateien bleiben unberührt. |
  | Offline | Es wird nur aus vorliegenden Offline-Dateien installiert. Ohne Dateien nicht möglich. | Nur aus dem Paketordner - ohne vorliegende Dateien wird abgebrochen. |

### Stand 1.8.0 – 2026-08-16

#### Geändert

- **Eigenes Logo.** `Assets\app.ico` zeigt jetzt einen hochovalen Ring — das „O"
  von Office — mit Download-Pfeil, in Office-Orangerot auf dunklem Grund.
  Enthalten sind zehn Größen von 16 bis 256 Pixel; die Größen bis 32 Pixel sind
  eine eigene, kräftigere Zeichnung mit dickerem Ring, damit in der Taskleiste
  noch etwas zu erkennen ist. Das bisherige Symbol liegt unverändert als
  `Assets\app-bisher.ico` daneben — Umbenennen genügt, um zurückzuwechseln.

#### Neu

- **Office 2013 und 2016 als eigener Bereich** unter der Produktliste, zugeklappt
  — im Normalbetrieb kostet er genau eine Zeile Platz. Acht Einträge: beide
  Jahrgänge, je Home & Student, Home & Business, Professional und
  Professional Plus, auf Deutsch.

  Bewusst **ohne Update-Prüfung**: Für abgekündigte Fassungen gibt es keine
  Versionsübersicht mehr, die man sinnvoll abfragen könnte, und ein Vergleich
  wäre ohnehin gegenstandslos. Diese Einträge fassen weder den
  Online-Versionscheck noch das Deployment Tool an.

  Bewusst **ohne Installation**: Es gibt nur den Download, und der öffnet den
  **Standardbrowser**. Das Programm lädt selbst nichts herunter und legt nichts
  im Paketordner ab — wo die Datei landet, entscheidet allein der Browser.

  Zwei Schaltflächen je Zeile:

  - **Medium (.img)** — das komplette Sicherungsmedium. Es enthält beide
    Architekturen: `setup.exe` installiert 32 Bit, `Office\setup64.exe`
    installiert 64 Bit. Deshalb gibt es dafür nur eine Zeile je Edition.
  - **Installer 32/64 Bit** — der kleine Click-to-Run-Starter. Die Bitzahl folgt
    dem Umschalter oben im Fenster, die Beschriftung ändert sich mit.

  Die Statuszeile nennt das Ende des Supports (2013: 11.04.2023,
  2016: 14.10.2025). *Professional Plus 2013* ist rot gekennzeichnet — diese
  Edition kam überwiegend als MSI, der Click-to-Run-Weg ist unsicher.

  Das Suchfeld gilt auch hier: `2013` blendet genau die vier Editionen von 2013
  ein, alles andere aus.

### Stand 1.7.0 – 2026-08-16

#### Behoben

- **Offline war nicht wirklich offline.** Bei der Offline-Installation wurde
  bisher nur `SourcePath` in die Konfiguration geschrieben. Das genügt nicht:
  Ohne Versionsangabe fragt das Deployment Tool beim CDN nach, welcher Build
  für den Kanal gerade der aktuelle ist, und will genau den installieren. Ist
  der örtliche Bestand älter — der Normalfall, sobald ein paar Wochen ins Land
  gehen —, lädt es die fehlende Version aus dem Internet. Der Offline-Ordner
  wurde dabei faktisch übergangen. Genau das ist das Verhalten, das mit einem
  zentralen Deployment Tool und `setup.exe /configure <XML>` beobachtet wurde.

  Jetzt wird zusätzlich die tatsächlich vorliegende Version festgeschrieben
  (`Version="16.0.…"`), und dazu der Kanal, wie von Microsoft empfohlen. Steht
  in der Vorlage bereits ein `Channel`, bleibt er unangetastet. Protokoll und
  Rückfrage vor dem Start nennen die festgeschriebene Version.

  Betroffen war nur die **Installation**. Beim **Herunterladen** bleibt die
  Version bewusst offen — dort soll ja gerade der neueste Stand geholt werden.
  Die Online-Installation schreibt weiterhin weder `SourcePath` noch `Version`.

### Stand 1.6.1 – 2026-08-15

#### Behoben

- **Der alte Stand wurde nicht entfernt** — weder beim einzelnen noch beim
  Sammel-Update. Im Protokoll stand
  `Access to the path '...\Office\Data\16.0.10417.20080' is denied`.

  Ursache: Das Deployment Tool legt die Quelldateien **schreibgeschützt** ab
  (teils zusätzlich versteckt oder als Systemdatei). `Directory.Delete` räumt
  solche Dateien nicht weg, sondern bricht mit „Zugriff verweigert" ab. Dass die
  Begleitdateien `v32_<Version>.cab` verschwanden (die gemeldeten 12 KB), die
  Ordner aber blieben, passte genau dazu.

  Vor dem Löschen werden jetzt Schreibschutz-, Versteckt- und
  System-Kennzeichen von Ordner und gesamtem Inhalt zurückgesetzt. Zusätzlich
  wird bis zu dreimal nachgefasst, falls ein Virenscanner eine frisch
  geschriebene Datei noch kurz offen hält.
- **Protokollzeile war unvollständig**: Wenn nur Begleitdateien entfernt wurden,
  stand dort `Alten Stand entfernt:  (12,1 KB frei)` mit leerer Aufzählung.
  Jetzt steht dort, was tatsächlich weg ist — Versionsnummer und/oder Anzahl der
  Begleitdateien.

### Stand 1.6.0 – 2026-08-15

#### Geändert

- **Der Firmenname ist überall entfernt.** Das Programm heißt jetzt schlicht
  *OfficeInstall*. Betroffen sind Fenstertitel, Dateiname der EXE
  (`OfficeInstall.exe`), Namensraum im Quelltext, Anwendungsmanifest,
  Arbeitsordner unter `%TEMP%\OfficeInstall`, die Absturzdatei
  (`OfficeInstall_Absturz.log`), die Kennung bei Onlineabfragen sowie
  Projektordner, Projektdatei und Dokumentation.

  **Zu beachten:** Die EXE heißt damit anders als bisher. Wird sie aus einem
  Skript oder über den Skript-Launcher gestartet, muss der Name dort
  nachgezogen werden. Alte Absturzdateien bleiben unter ihrem alten Namen
  liegen.
- **Kein Umbruch mitten im Satz mehr.** Modus-Erklärung und Trefferzahl stehen
  jetzt als eigenständige Blöcke nebeneinander: Passen sie in eine Zeile,
  bleiben sie dort — sonst rutscht „14 Installationen für 64 Bit gefunden"
  **vollständig** in die nächste Zeile, statt nach dem ersten Wort umzubrechen.

### Stand 1.5.0 – 2026-08-15

Die Oberfläche ist deutlich kompakter — Ziel war, weniger scrollen zu müssen.
Der obere Bereich braucht jetzt rund 100 Pixel weniger, jede Karte der
Produktliste rund 23 Pixel; in der Standardgröße sind damit etwa doppelt so
viele Installationen gleichzeitig sichtbar.

#### Geändert

- **Aufräum-Option nach oben gewandert**: „Alten Stand nach dem Update löschen"
  steht jetzt rechts neben der Modus-Erklärung statt in einer eigenen Zeile.
- **Modus-Erklärung und Trefferzahl in einer Zeile**: „Offline-Dateien werden
  bevorzugt … · 14 Installationen für 32 Bit gefunden".
- **Der Update-Hinweis sitzt in der Überschrift** der Produktliste und ist auf
  „Updates verfügbar: PerpetualVL2019, PerpetualVL2021" gekürzt. Die
  ausführliche Erklärung erscheint als Kurzinfo beim Darüberfahren und kostet
  keinen Platz mehr. Das breite Warnband über der Liste entfällt.
- **Ordnerzeile zusammengefasst**: Pfad, `…`, „Erneut suchen" und „Ordner der
  EXE" stehen in einer Zeile statt in zweien.
- **Karten zweizeilig statt dreizeilig**: Titel mit Basis-Kennzeichen in der
  ersten Zeile, Zustand und Dateiname in der zweiten. Die beiden Schaltflächen
  liegen nebeneinander statt übereinander. Die Architektur steht nicht mehr an
  jeder Karte — sie ist ja oben umgeschaltet; sie erscheint als Kurzinfo am
  Dateinamen.
- **Durchgehend kleinere Schrift und flachere Bedienelemente**: Titel 20 → 17,
  Karten-Titel 12 → 11,5, Zustandszeilen 10,5 → 10, Schaltflächen 27 → 24 Pixel
  hoch, Eingabefelder 28 → 25, schmalere Innenabstände.

### Stand 1.4.1 – 2026-08-15

#### Behoben

- Beim Bauen erschien die Warnung `AVLN5001: 'TextBox.Watermark' is obsolete`.
  Der Platzhaltertext des Suchfelds nutzt jetzt `PlaceholderText` — die
  Eigenschaft, die Avalonia 12 dafür vorsieht. Der Build läuft damit ohne
  Warnungen durch, das Verhalten bleibt unverändert.

### Stand 1.4.0 – 2026-08-15

#### Behoben

- **Veralteter Offline-Stand wurde in der Liste nicht angezeigt**, obwohl das
  Protokoll ihn nannte. Die online angebotenen Versionen wurden nur an die
  gerade sichtbaren Zeilen geschrieben — beim Umschalten der Architektur gingen
  sie verloren, und eine veraltete 32-Bit-Zeile blieb fälschlich grün. Sie
  werden jetzt je Installationsbasis zentral gehalten und beim Aufbau der Liste
  immer angewandt, unabhängig von Architektur und Suchfilter.

#### Geändert

- **Ein Durchlauf je Installationsbasis statt einer je Konfigurationsdatei.**
  Bisher wurde das Deployment Tool 28-mal gestartet — für jede der 28
  XML-Dateien einmal, obwohl es nur 8 Basen gibt. Jetzt entsteht je Basis
  **eine** gemeinsame Konfiguration mit allen Produkten dieser Basis (das
  Deployment Tool erlaubt mehrere `<Product>`-Einträge in einem `<Add>`), und
  es läuft ein einziger Durchlauf. Geladen wird dieselbe Menge, nur ohne die
  siebenfache Wiederholung.
- Die Sammel-Datei enthält bewusst nur `<Add>`; Angaben wie `RemoveMSI`,
  `Display` oder `AppSettings` betreffen die Installation und haben beim
  Herunterladen nichts zu suchen. Der Kanal wird aus dem Ordnernamen
  ausdrücklich gesetzt.

#### Neu

- **Alter Stand wird nach dem Update entfernt.** Das Deployment Tool lässt die
  vorherige Version in `Office\Data` stehen — nach ein paar Aktualisierungen
  liegen dort mehrere Gigabyte Altlast. Entfernt werden ausschließlich Ordner
  mit einer *kleineren* Versionsnummer und die zugehörigen
  `v32_<Version>.cab` / `v64_<Version>.cab`; `v32.cab`, `v64.cab` und alles
  Übrige bleiben unangetastet. Der freigewordene Platz steht im Protokoll.
  Über die Option **Alten Stand nach dem Update löschen** abschaltbar
  (standardmäßig an).
- **Der Knopf heißt jetzt „Alle Offline-Bestände aktualisieren oder
  runterladen"** und arbeitet auch Basen ab, in denen noch gar keine Dateien
  liegen. Die Rückfrage zeigt je Basis, was passiert — `16.0.14334.20440 ->
  16.0.14334.20848`, „aktuell, wird nur geprüft" oder „wird komplett geladen" —
  und warnt ausdrücklich, wenn dabei mehrere Gigabyte neu geladen werden.

### Stand 1.3.0 – 2026-08-13

#### Behoben

- **Der Versionsabgleich hat nie funktioniert — die Adresse war falsch.**
  Abgefragt wurde
  `officecdn.microsoft.com/pr/<GUID>/Office/Data/VersionDescriptor.xml`.
  Das CDN beantwortet diesen Pfad mit **HTTP 400**; ausgeliefert werden dort nur
  `.cab`-Dateien, die XML-Datei nicht. Weder ein anderer Hostname noch eine
  andere Kennung im Aufruf konnten daran etwas ändern.

  Stattdessen wird jetzt die Versionsübersicht von Microsoft ausgewertet:

  ```
  https://clients.config.office.net/releases/v1.0/OfficeReleases
  ```

  Ein einziger Aufruf liefert alle Installationsbasen. Die Zuordnung erfolgt
  über die Kanalkennung (`channelId`, z. B. `PerpetualVL2021`) **und** über die
  GUID in `cdnBaseUrl` — sie hält damit auch, wenn Microsoft die Anzeigenamen
  ändert.

#### Neu

- **Netzprüfung vorgeschaltet.** Vor dem Abgleich wird einmal geprüft, ob
  überhaupt eine Internetverbindung besteht — gegen denselben Endpunkt, den auch
  Windows für seine Netzwerkerkennung nutzt
  (`msftconnecttest.com`). Ist der Rechner offline, steht genau **eine** Zeile im
  Protokoll statt einer Fehlermeldung je Installationsbasis. Der
  Deployment-Tool-Bereich vermerkt in diesem Fall „Rechner offline – kein
  Abgleich" statt fälschlich „aktueller Stand".
- Auch **Deployment Tool laden/aktualisieren** prüft vorher die Verbindung und
  meldet sich mit einem klaren Hinweis, statt in einen Zeitablauf zu laufen.

#### Geändert

- Statt acht Einzelabfragen (eine je Basis und Architektur) gibt es nur noch
  einen Aufruf. Das Protokoll bleibt entsprechend kurz.
- Erfolgreiche Antworten werden für die Laufzeit gemerkt, Fehlschläge weiterhin
  nicht.

### Stand 1.2.0 – 2026-08-13

#### Behoben

- **Der Versionsabgleich mit dem Microsoft-CDN schlug immer fehl.** Der Aufruf
  ging ohne Kennung (`User-Agent`) hinaus; der CDN-Vorposten beantwortet solche
  Anfragen je nach Regelwerk mit `403`. Genau daran lag es, dass die Abfrage der
  Downloadseite (die eine Kennung mitschickte) funktionierte, die des CDN aber
  nicht. Jetzt schicken beide Wege eine Kennung mit.
- **Fehler werden nicht mehr verschluckt.** Statt „Keine Auskunft vom
  Microsoft-CDN erhalten" steht nun je Installationsbasis im Protokoll, woran es
  gelegen hat — HTTP-Status, Zeitüberschreitung, Namensauflösung oder
  Proxy-Meldung im Klartext.
- **Ausweichadresse**: Antwortet `officecdn.microsoft.com` nicht, wird
  `officecdn-microsoft-com.akamaized.net` versucht — derselbe Inhalt über die
  Akamai-Verteilung, die Microsoft dafür verwendet.
- **Fehlschläge werden nicht mehr zwischengespeichert.** Bisher merkte sich das
  Programm auch ein „ging nicht", sodass ein späterer Versuch bis zum Neustart
  scheiterte. Gemerkt werden jetzt nur noch erfolgreiche Antworten.
- Die Zeitgrenze wird je Abfrage gesetzt statt am wiederverwendeten Client, wo
  sie nach der ersten Anfrage ohnehin nicht mehr änderbar war.

#### Neu

- **Alle Offline-Bestände aktualisieren.** Eine Schaltfläche zieht sämtliche
  vorhandenen Bestände nach — beide Architekturen, alle Installationsbasen, jede
  Konfiguration, nacheinander. Vorher zeigt eine Übersicht, was bearbeitet wird
  und bei wie vielen Basen online ein neuerer Stand bekannt ist. Während des
  Laufs erscheint der Fortschritt („3 von 16: x64\PerpetualVL2021 · Office 2021
  Pro Plus"), am Ende eine Bilanz aus Erfolgen und Fehlern.
- **Abbrechen** während der Sammel-Aktualisierung: Der gerade laufende Download
  wird noch zu Ende geführt, danach wird angehalten.

Bearbeitet werden nur Basen, in denen bereits Quelldateien liegen. Ein neuer
Bestand wird bewusst nicht nebenbei angelegt — das wären pro Basis mehrere
Gigabyte. Dafür gibt es weiterhin die Schaltfläche an der einzelnen Zeile.

### Stand 1.1.0 – 2026-08-13

#### Neu

- **Suchfeld über der Produktliste.** Gesucht wird in Klartextname,
  Produktkennung, Dateiname, Installationsbasis und Architektur. Mehrere durch
  Leerzeichen getrennte Begriffe müssen alle zutreffen — `2021 pro` führt also
  direkt zu Office 2021 Pro Plus. Die Statuszeile zeigt, wie viele der
  vorhandenen Einträge gerade passen.
- **Deployment Tool holen und aktualisieren.** Der Zustand der `setup.exe` steht
  jetzt im oberen Bereich: gefundene Version, Vergleich mit der von Microsoft
  angebotenen, farbige Kennzeichnung. Auf Klick wird die aktuelle Fassung vom
  **offiziellen Microsoft Download Center** geladen, mit
  `/quiet /extract:` entpackt und an **allen** Stellen im Paket ersetzt, an
  denen bisher eine `setup.exe` lag. War gar keine vorhanden, landet sie im
  Paketordner. Der Abgleich läuft ohne Download: Die angebotene Version steckt
  im Dateinamen (`officedeploymenttool_20228-20124.exe` → 16.0.20228.20124),
  heruntergeladen wird nur, wenn es wirklich etwas Neueres gibt.
- **Offline-Bestand anlegen und nachziehen.** Jede Zeile hat eine zweite
  Schaltfläche — *Offline laden*, *Offline aktualisieren* oder
  *Offline erneuern*, je nach Zustand. Dahinter steckt
  `setup.exe /download` mit einem ausdrücklich gesetzten `SourcePath`, damit die
  Dateien sicher im Ordner der jeweiligen Installationsbasis landen und nicht
  dort, wo zufällig die `setup.exe` liegt. Vorhandene Dateien werden vom
  Deployment Tool nicht erneut geladen, es wird also nur die Lücke gefüllt.
  Nach dem Download wird automatisch neu eingelesen.

#### Geändert

- `InstallPlan` kennt jetzt den Schalter (`configure` oder `download`). Beim
  Herunterladen läuft das Deployment Tool ohne eigenes Fenster.
- Beim Filtern der Liste bleiben bereits ermittelte Online-Versionen erhalten,
  es wird also nicht bei jedem Tastendruck erneut beim CDN nachgefragt.

### Hinweise

- Bezugsquelle für das Deployment Tool ist ausschließlich
  <https://www.microsoft.com/en-us/download/details.aspx?id=49117>. Die im Netz
  kursierende Abkürzung `officecdn.microsoft.com/pr/wsus/setup.exe` wird bewusst
  nicht verwendet — Microsoft hat auf Nachfrage bestätigt, dass das kein
  offizieller Downloadweg ist.
- Sollte Microsoft den Aufbau der Downloadseite ändern, lässt sich die Adresse
  dauerhaft in `Versionscheck\ODT-Url.txt` hinterlegen; ohne diese Datei wird
  der Link automatisch von der Seite gelesen. Schlägt beides fehl, bietet das
  Programm an, die Downloadseite im Browser zu öffnen.

### Stand 1.0.0 (erste lauffähige Fassung) – 2026-08-13

Erste Fassung – Ablösung der `start.bat` des OfficeInstall-Pakets durch eine
kompilierte Windows-Anwendung (C# / Avalonia / .NET 10), technisch und optisch
an den übrigen übrigen Werkzeugen ausgerichtet.

### Unverändert übernommen

- Dieselben Konfigurationsdateien im gewohnten Aufbau
  (`x32`/`x64` je Installationsbasis) – es muss nichts umgestellt werden.
- Installation weiterhin über `setup.exe /configure <Konfiguration>` des Office
  Deployment Tools; die Original-XMLs bleiben unangetastet.
- UAC-Erhöhung beim Start, jetzt über das `app.manifest` statt über eine
  erzeugte `getadmin.vbs`.
- Paketstand aus `Versionscheck\Version.txt` wird gelesen und angezeigt.

#### Neu

- **Katalog statt festem Menü**: Angeboten wird genau das, wofür eine XML-Datei
  vorliegt. Neue Dateien erscheinen ohne Codeänderung, fehlende tauchen nicht
  auf. Der Produktname wird aus `<Product ID="…"/>` gelesen und in Klartext
  übersetzt.
- **Installationsbasen strikt getrennt**: Current, PerpetualVL2019,
  PerpetualVL2021 und PerpetualVL2024 werden je Ordner ausgewertet; Quelldateien
  einer Basis werden nie für eine andere verwendet.
- **Drei Modi**: Automatisch (offline bevorzugt), Online (immer aus dem
  Internet) und Offline (nur aus vorliegenden Dateien).
- **Selbstständige Offline-Erkennung**: `Office\Data` neben der `setup.exe` wird
  gefunden, die vorliegende Version und die Größe werden angezeigt.
- **Warnung bei veraltetem Offline-Stand**: Die online angebotene Version wird je
  Basis über `VersionDescriptor.xml` des Microsoft-CDN abgefragt und mit den
  Offline-Dateien verglichen. Ist online etwas Neueres verfügbar, erscheint ein
  Warnband, die Zeile wird gelb und vor der Offline-Installation kommt eine
  Rückfrage. Ohne Internet entfällt der Abgleich stillschweigend.
- **Deterministische Modi**: Für Offline wird eine Kopie der Konfiguration mit
  gesetztem `SourcePath` erzeugt, für Online eine Kopie ohne `SourcePath` in
  einem leeren Arbeitsordner samt `setup.exe`. Damit ist die Quelle festgelegt
  und nicht dem Zufall überlassen.
- **Rückfrage vor dem Start** mit Produkt, Architektur, Basis und Quelle – die
  Installation entfernt vorhandene Office-Versionen (`RemoveMSI`).
- **Auf das Ende wird gewartet**: Der Rückgabewert des Deployment Tools landet im
  Protokoll, Fehler werden gemeldet statt übersehen.
- **Prüfung auf setup.exe**: Fehlt sie, wird die Zeile rot markiert und der Grund
  genannt, statt dass die Installation ins Leere läuft.
- **Zweisprachig** (Deutsch/Englisch) und **Hell-/Dunkelmodus**, beides beim
  Start aus den Windows-Einstellungen übernommen.

### Bewusst nicht übernommen

- Die Selbstaktualisierung aus `Batch\Onlinecheck.bat`: Sie hat per `aria2c.exe`
  eine Batch-Datei von SharePoint geladen und ungeprüft ausgeführt. Eine
  kompilierte Anwendung, die heruntergeladenen Fremdcode ausführt, ist ein
  Einfallstor und fällt Virenscannern zu Recht auf. `aria2c.exe` wird damit
  nicht mehr gebraucht. Eine Aktualisierungsmeldung ohne Codeausführung lässt
  sich wie im Windows DaSi Tool nachrüsten.

### Technik

- `OfficeCatalog`, `OfficeConfiguration`, `OfflineSource`, `OnlineVersionService`
  und `InstallRunner` sind frei von UI-Bezügen und dadurch einzeln testbar;
  51 Testfälle decken Katalogaufbau, Offline-Erkennung, Auswertung des
  VersionDescriptors und die Erzeugung der Konfigurationskopien ab.
- Unerwartete Fehler landen in
  `%USERPROFILE%\Desktop\OfficeInstall_Absturz.log`.

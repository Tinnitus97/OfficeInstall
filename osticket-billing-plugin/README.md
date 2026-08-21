# Time Billing – osTicket Plugin

**Entwickler / Anpassung: tinnitus-ost**

Ein Abrechnungs-/Billing-Modul für osTicket, abgeleitet aus dem
*TimeTrackingMod* von Strobe Technologies. Die **Zeiterfassung** aus dem
Originalprojekt wurde vollständig **entfernt** – dieses Plugin liefert nur die
**Billing-Funktion** und liest die erfassten Zeiten aus eurer vorhandenen
Zeiterfassung (Plugin *Time Recording*, Tabelle `ost_timesheet`).

* Läuft als **Plugin** (kein Core-Patch nötig)
* Getestet gegen **osTicket 1.18.4** (euer Fork `JensEB/osTicket`)
* **PHP 8.4-kompatibel** geschrieben
* Integriert sich in eure Zeiterfassung über `ost_timesheet`

## Installation

Es gibt zwei Varianten – beide gehören nach `include/plugins/`:

1. **PHAR** (`billing.phar`) – Datei einfach nach
   `include/plugins/billing.phar` kopieren.
2. **ZIP** (`billing-plugin.zip`) – entpacken, sodass der Ordner
   `include/plugins/billing/` mit `plugin.php` darin entsteht.

Danach unter *Admin → Verwalten → Plugins* installieren und aktivieren.
Auf IIS nach dem Kopieren `iisreset` ausführen (OPcache).

---

## Voraussetzungen

1. osTicket **1.18+**
2. **PHP 8.4** (läuft ebenso unter 8.1–8.3)
3. Das Plugin **Time Recording** (`time_recording.phar`) muss installiert und
   aktiv sein – es befüllt die Tabelle `ost_timesheet`, aus der das Billing
   seine Daten bezieht. Ohne diese Tabelle zeigt das Billing einen Hinweis an.

---

## Installation

1. Den Ordner `billing/` nach `include/plugins/` kopieren:

   ```
   include/plugins/billing/
   ```

2. Im Admin-Bereich: **Verwalten → Erweiterungen (Plugins) → Hinzufügen**,
   „Time Billing" installieren und anschließend **aktivieren**.

3. Auf **Konfiguration** klicken. Die Einstellungen sind in fünf Reiter
   gegliedert (siehe unten); für den Start genügen *Abrechnung* und
   *Bericht & Export*.

> Beim ersten Laden legt das Plugin automatisch die Tabelle
> `ost_billing_time_type` an und erzeugt den Standard-Zeittyp (ID 1), der zu den
> vorhandenen Zeiteinträgen (`time_type_id = 1`) passt.

---

## Einstellungen

Die Konfigurationsseite ist in Reiter und Karten gegliedert; Einstellungen,
die im aktuellen Zustand nichts bewirken, werden ausgeblendet (z. B. alle
Betragsfelder im Modus „Nur Zeit“).

| Reiter | Inhalt |
|---|---|
| **Abrechnung** | Abrechnungsmodell (Beträge oder nur Zeit), Standard-Stundensatz, Steuersatz, Rundung, Zahlenformat, Link zu den Zeitarten |
| **Tickets** | Status-Einschränkung, was im Ticket angezeigt und protokolliert wird, Zugriff für Agenten |
| **Bericht & Export** | Dateiname, Summen und freie Zeilen unter der Tabelle, Zusatzblock (Notiz oder Prüftabelle) |
| **PDF-Layout** | Seite, Briefkopf mit Logo, Titel und Kopf-/Fußtexte |
| **System** | Statusanzeige der Installation, Diagnose |

Besonderheiten:

* **Zahlenformat** – statt vier Einzelfeldern gibt es eine Auswahl
  (Deutsch, Schweiz, Englisch, Französisch). Sie setzt Dezimal- und
  Tausendertrennzeichen sowie die Position des Währungssymbols. Wer eine
  eigene Kombination braucht, wählt *Benutzerdefiniert…* und bekommt die
  Einzelfelder zurück. Bestehende Installationen behalten ihre Werte: beim
  Öffnen der Seite wird das passende Format automatisch erkannt, und nur
  wenn ein Format ausgewählt ist, werden die Einzelwerte überschrieben.
* **Platzhalter** – die Liste aller `%{report.*}`-Platzhalter liegt als
  aufklappbares Feld unter den Reitern *Bericht & Export* und *PDF-Layout*.
  Ein Klick fügt den Platzhalter an der Cursorposition des zuletzt
  bearbeiteten Feldes ein.
* **Zusammenfassung im Ticket-Menü** – schaltet den Eintrag im Zahnrad-Menü
  des Tickets ab; die Zeitart-Auswahl unter den Ticket-Details bleibt
  erhalten, weil damit erfasst wird.

Technisch: `config.php` beschreibt nur noch, *was* einstellbar ist,
`class.BillingConfigUi.php` beschreibt, *wie* es dargestellt wird (Reiter,
Karten, Reihenfolge, Ausblende-Regeln) und liefert CSS und Skript dafür.
Läuft das Skript nicht, fällt die Seite auf die schlichte Liste zurück –
vollständig, lesbar und speicherbar; es wird nichts serverseitig
ausgeblendet.

---

## Nutzung

Nach der Aktivierung erscheint im Staff-Panel unter **Applications** der Eintrag
**Billing**. Von dort aus:

* **Ticket-Rechnung** – Ticketnummer eingeben → Zeitbericht + Rechnung eines
  Tickets, mit Summen je Zeittyp, Beträgen, Zwischensumme, Steuer und Gesamt.
  Einzelne Einträge lassen sich als **abgerechnet** (`settled`) markieren.
* **Organisations-Abrechnung** – Organisation + Zeitraum wählen → Übersicht
  aller Tickets der Organisation mit Zeit- und Betragssummen.
* **Zeittypen & Sätze** (nur Admin) – Namen, Stundensätze, „abrechenbar"-Flag
  und Aktiv-Status je Zeittyp pflegen.

Zusätzlich wird – sofern in der Konfiguration aktiviert – auf der
**Ticket-Detailseite** ein kleines Billing-Panel mit Gesamtzeit, Betrag und
einem Link zur Rechnung eingeblendet (`ticket.view.more`).

### URLs / Rewrite

Die Seiten werden über den Staff-Dispatcher aufgerufen, z. B.:

```
scp/billing
scp/billing/ticket/<id>
scp/billing/org/<org-id>?start=YYYY-MM-DD&end=YYYY-MM-DD
scp/billing/timetypes
```

Unter **Apache** funktioniert das direkt über die mitgelieferte
`scp/.htaccess`. Unter **nginx** muss – wie für osTicket üblich – ein Rewrite
existieren, das nicht vorhandene Pfade unter `scp/` an `scp/dispatcher.php`
weiterleitet, z. B.:

```nginx
location /scp/ {
    try_files $uri $uri/ /scp/dispatcher.php?$query_string;
}
```

---

## Integration mit der Zeiterfassung (`ost_timesheet`)

Das Billing schreibt **keine** Zeiten – es liest ausschließlich aus
`ost_timesheet` und setzt beim Abrechnen nur das Feld `settled`.

| `ost_timesheet` Spalte | Bedeutung im Billing |
|---|---|
| `time` (Sekunden)      | abzurechnende Dauer |
| `time_type_id`         | verweist auf einen Eintrag in `ost_billing_time_type` (Name, Stundensatz, abrechenbar) |
| `settled` (`'1'`/`'0'`)| Abrechnungsstatus – wird vom Billing gesetzt |
| `object_id`/`object_type` | Ticket (`'T'`) bzw. Task (`'A'`) |
| `staff_id`             | ausführende:r Agent:in |

Betragsberechnung: `Betrag = (Zeit / 3600) × Stundensatz`, optional pro Zeittyp
auf das konfigurierte Rundungs-Inkrement aufgerundet; nicht-abrechenbare
Zeittypen fließen nicht in die Rechnung ein. Auf die Zwischensumme wird der
konfigurierte Steuersatz angewandt.

---

## Was getestet wurde

* Syntax-Lint aller PHP-Dateien; Prüfung auf PHP-8.4-Deprecations
* Rechenlogik (Formatierung, Rundung, Rechnung, Steuer) – Unit-Tests
* Reale SQL-Abfragen (Joins, Gruppierung, `settled`-Rückschreiben) gegen eine
  echte MariaDB mit osTicket-Kernschema
* URL-Routing mit dem **echten** osTicket-Dispatcher
* Sauberes Laden/Vererben der Klassen gegen die osTicket-Basisklassen

> Hinweis: PHP 8.4 war in der Build-Umgebung nicht installierbar; die
> 8.4-Kompatibilität wurde per Code-Review (keine impliziten nullable-Parameter,
> keine entfernten Funktionen, korrekte String-Interpolation) plus 8.3-Lint
> sichergestellt. Vor dem Produktiveinsatz empfiehlt sich ein kurzer Test auf
> eurer 8.4-Instanz.

---

## Sprache / Übersetzung

Die gesamte Oberfläche ist **vollständig auf Deutsch** übersetzt. Alle Texte
laufen über die Plugin-Textdomäne `billing`; die deutsche Übersetzung liegt in
`i18n/de/LC_MESSAGES/billing.mo.php` (das von osTicket direkt eingebundene
Format) sowie als bearbeitbare `billing.po`. Läuft osTicket auf Deutsch, werden
alle Beschriftungen, Meldungen und Konfigurationstexte deutsch angezeigt.

Übersetzung anpassen: `billing.po` bearbeiten und daraus `billing.mo.php` neu
erzeugen (eine einfache PHP-Datei mit `return array('Englisch' => 'Deutsch', …);`).

## Prüfung der Zeiterfassung

Das Modul setzt die installierte **Zeiterfassung** (Tabelle `ost_timesheet`)
voraus. Fehlt sie, gibt es an mehreren Stellen eine klare Meldung statt eines
Fehlers:

* auf jeder Abrechnungsseite (Übersicht, Ticket, Aufgabe, Organisation) ein
  Hinweis „Das Zeiterfassungs-Plugin (Tabelle ost_timesheet) wurde nicht
  gefunden. Bitte installieren und aktivieren Sie es zuerst.“
* eine rot hervorgehobene Warnung oben auf der Plugin-Konfigurationsseite
* das Abrechnungs-Panel in der Ticket-Ansicht wird in diesem Fall ausgeblendet

---

## Lizenz

Wie osTicket und der Ursprungs-Mod: **GNU General Public License**, ohne
Gewährleistung. Siehe `LICENSE.TXT` von osTicket.

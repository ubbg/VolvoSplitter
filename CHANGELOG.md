# Änderungen

Das Format folgt lose [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
die Versionsnummern [Semantic Versioning](https://semver.org/lang/de/).

## Unveröffentlicht

### Bericht als Text, Markdown oder HTML

Der Befund lässt sich jetzt in drei Formaten speichern. In der Oberfläche entscheidet die Endung
im Speichern-Dialog (`.txt`, `.md`, `.html`), im Stapelbetrieb der neue Schalter
`--report-format txt|md|html`. Ein unbekannter Wert bricht ab, statt still auf Text
zurückzufallen — sonst bekäme man eine `.txt`, wo man eine `.html` erwartet hat.

Der Bericht wird dafür **einmal aufgebaut und dreimal ausgegeben**: `DumpReport.Compose` liefert
ein Dokument aus wenigen Bausteinen (Überschrift, Absatz, Aufzählung, Feldliste, Tabelle,
vorformatierter Block, Gruppe), drei Ausgeber setzen es um. Ein neuer Abschnitt erscheint dadurch
in allen drei Formaten, ohne dass ein Ausgeber angefasst werden muss.

* **Markdown** bekommt echte Tabellen statt ausgerichteter Leerzeichen. Der Unterstrich wird
  bewusst *nicht* maskiert: CommonMark liest ihn im Wortinneren nicht als Betonung, und die Werte
  dieses Berichts sind voll davon (`EV_ECM30TDI011`, `SB_CRC32_ALGO_E`).
* **HTML** ist eine eigenständige Seite mit eingebettetem Stil, ohne Verweis nach außen, hell und
  dunkel über `prefers-color-scheme`. Zeilen tragen ein Gewicht: eine abweichende Prüfsumme wird
  farblich abgesetzt — sie ist die Aussage, die zuerst auffallen soll.
* **Text** bleibt die Vorgabe. Spaltenbreiten ergeben sich jetzt aus dem breitesten Eintrag statt
  fest verdrahtet zu sein; dadurch läuft keine Spalte mehr über. Das Zeichenbild ändert sich
  dadurch leicht, der Inhalt nicht.

`DumpReport.Build(dump)` ohne Formatangabe liefert unverändert Text — bestehende Aufrufer bleiben
unberührt. Der Bericht war bislang ungetestet; er hat jetzt zwanzig Tests, darunter die Probe,
dass alle drei Formate dieselben Angaben tragen, und dass Markup in einem Dateinamen als Text
auf der HTML-Seite landet statt als Tag.

---

## v1.2.0

### VAG-Teilenummer und Softwarestand werden gelesen

Von VAG-Abbildern liest das Werkzeug jetzt auch die Angaben, die auf dem Steuergerät stehen und
die ein Diagnosetester meldet: Teilenummern, **Softwarestand**, Systemkennung, Motorbezeichnung
und Motorkennbuchstaben.

Sie stehen im Dataset-Block als Feld mit festen Breiten, verankert an der Systemkennung `EV_…`
— dieselbe Art Beleg wie die Variantenkennung bei `+0x78`, kein Mustersuchen. Der Fund gilt
erst, wenn jede Prüfung zutrifft; die schärfste ist der Softwarestand aus genau vier Ziffern,
dazu kommt die Gegenprobe, dass die zweifach abgelegte Software-Teilenummer übereinstimmt.
Scheitert eine Prüfung, gibt es keine Angabe statt einer erfundenen.

Beim Audi-Abbild kommt so `04L906027BB 7155` heraus — genau die Werte, die auch im Dateinamen
stehen. Diese Gegenprobe stammt nicht aus dem Abbild und ist deshalb unabhängig.

### Teilenummern: Form statt Aufzählung

Die freie Teilenummernsuche prüfte bisher gegen eine Liste von vierzehn Präfixen. Die Liste war
beweisbar zu eng: Sie kannte `8V0`, `298`, `4M0` und `7P0` nicht und verschwieg damit vier von
acht Teilenummern der ausgewerteten Abbilder. An ihre Stelle tritt das VAG-Namensschema — drei
Zeichen Fahrzeugkennung, Baugruppe aus `906`/`907`/`910`/`997`, drei Ziffern, null bis zwei
Buchstaben. Das ist zugleich schärfer (die Baugruppenstelle trennt Teilenummern von
Bosch-Nummern) und offener (keine Aufzählung, die veraltet).

Der Rückfall bleibt, überspringt aber Nummern, die das Identifikationsfeld schon gesichert
geliefert hat — sonst stünde dieselbe Nummer zweimal im Bericht, einmal „gesichert" und einmal
„stark gestützt".

---

## v1.1.1

### ADD16 vollständig geklärt

Die zwei ADD16-Strukturen, die in v1.1.0 noch als offener Punkt geführt wurden, gehen jetzt
auf. Bei `SB_ADD16_ALGO_E` zählt das **letzte** 16-Bit-Wort des Bereichs um 16 Bit nach links
geschoben, alle übrigen normal.

Der Hinweis lag in der Messung schon vor: Ohne die Verschiebung stimmten in beiden Fällen die
unteren 16 Bit der Summe exakt, die oberen nicht — und die Differenz war jeweils genau das
letzte Wort. Bestätigt am Quelltext des „MEDC17 Checksum Analyzer“, dessen Schleife das letzte
Wort ebenfalls aussetzt und gesondert addiert.

Damit gehen **alle 59 Prüfsummenstrukturen** der fünf ausgewerteten Abbilder auf — mit einer
Ausnahme, die keine ist: der Dataset-Block des getunten Porsche-Abbilds. Dieselbe Firmware
liegt unverändert daneben und geht vollständig auf.

Nebenwirkung im Testgerüst: Die ADD16-Stellgröße schrumpft von bis zu 128 KiB auf 4 Byte. Das
vorletzte Wort trägt die untere Hälfte des Abstands, das letzte die obere — zusammen decken
sie jeden 32-Bit-Abstand ab, ohne Verteilungsschleife.

### CVN: Mehrfachvorkommen als offener Punkt

Der Bosch-Funktionsrahmen für MED17.5 spricht bei OBD-Mode $09 durchgehend im Plural von
CVNunknowns. `FindCvn` bricht beim ersten Fund ab; ob weitere überhaupt eine eigene
Konfigurationsstruktur im Abbild haben, ist ungeprüft. Dokumentiert, Verhalten unverändert.

---

## v1.1.0

### VAG-Steuergeräte auf Infineon TriCore lesen

Neben den Volvo/TRW-Abbildern liest das Werkzeug jetzt auch Abbilder von VAG-Steuergeräten auf
Infineon TriCore — Bosch EDC17 (Diesel) und MED17/ME17 (Benzin). Little endian, ohne
TRW-Sektorköpfe, ohne festen CRC-Trailer.

**Umfang: nur lesen und zerlegen.** Für TriCore-Abbilder werden Prüfsummen gerechnet und
gemeldet, aber nicht gestellt. Kein Schreiben, kein Prüfsummen-Korrigieren, kein Blockersatz.
Das ist keine Lücke, sondern eine Festlegung: `EcuProfile.SupportsWriteBack` ist false, und
`Save`, `RepairCrc`, `ReplaceSector` und `PatchUInt32Be` verweigern.

* **Bosch-Blockkette** — die verkettete Blockstruktur wird mit zwei unabhängigen Verfahren
  gelesen (Abtastung über das Abbild, Kettenlauf über `nextSector`); Übereinstimmung und
  Abweichung werden beide gemeldet. Ein Kopf gilt erst als gültig, wenn `0xDEADBEEF` am
  Blockende steht, `blockEnd == blockStart + size - 4` auf das Byte aufgeht, beide
  Zeigertabellen im eigenen Block liegen und der Block ganz in einer Bank sitzt.
* **Prüfsummen nachrechnen** — die drei Verfahren `SB_CRC32_ALGO_E`, `SB_ADD32_ALGO_E` und
  `SB_ADD16_ALGO_E` werden gerechnet und gegen den Sollwert geprüft. Erst das rechtfertigt
  `Verified`; ein Block mit gültigem Kopf und abweichender Prüfsumme wird `CrcMismatch`.
* **Physische Karten** von TC1796 und TC1797 aus den Infineon-Datenblättern; beide Bänke
  summieren sich auf exakt 2048 KiB. PMU0, PMU1, externer Flash am EBU und angehängter DFLASH
  werden auf die Datei abgebildet, gecachte und ungecachte Adressen gleich behandelt.
* **CVN und Variantenkennung** werden gelesen und ausgewiesen — die CVN nur, wenn ihre
  Konfigurationsstruktur wirklich gefunden wird.
* **Kennungen mit Fundort** — Hardware-, Software- und Blockkennungen, Steuergerätetyp und VIN,
  jeweils in strenger Form. Ein Fundort ist ein Beleg, eine Zeichenkette allein nur eine
  Beobachtung.

### An echten Abbildern nachgerechnet

Fünf EDC17-Abbilder (Audi A4, VW Touran, Porsche Panamera, zwei weitere) haben zwei Fehler
aufgedeckt. Beide sind behoben, beide durch Tests festgehalten.

* **`csEnd` war um drei Byte falsch gedeutet.** Der Leser nahm an, `csEnd` zeige wie `blockEnd`
  auf das letzte *Wort* des Bereichs. Tatsächlich zeigt es auf das letzte *Byte*: Im ersten
  Block steht `csStart` 0x80000000, `csEnd` 0x8000FFFB, der `0xDEADBEEF`-Abschluss bei
  Datei-Offset 0xFFFC — 0xFFFB ist keine Wortgrenze. Über alle fünf Abbilder gehen mit der
  Byte-Deutung 57 von 59 Prüfsummenstrukturen auf, mit der Wort-Deutung 11. Die 11 waren
  ausschließlich `ADD32`-Fälle, deren Wortschleife die drei überzähligen Bytes nie las — der
  Fehler blieb dadurch lange unsichtbar.
* **Abbilder mit durchgehendem Programmflash fanden nur sechs von acht Blöcken.**
  EDC17CP44-Abbilder legen Blöcke auf 0x80200000 und 0x80340000 — Adressen, die es bei zwei
  2-MiB-Bänken nicht gibt. Die Steuergerätetabelle nennt für EDC17CP44 einen TC1797, und
  die Gegenprobe prüfte die durchgehende Aufteilung gar nicht erst. Sie ist jetzt ein
  eigener Kandidat: bestätigt sie mehr Blockköpfe, gewinnt sie — mit Belegzeile. Der
  Baustein bleibt dabei offen, gemessen ist nur die Bankaufteilung.

Alle fünf Abbilder liefern jetzt acht Blöcke. Die Prüfsummen gehen vollständig auf, außer bei
zwei `ADD16`-Strukturen (siehe `BoschChecksum.Add16`, offener Punkt) und im Dataset-Block des
Porsche-Abbilds — das ist eine getunte Datei, und dieselbe Firmware liegt unverändert daneben
und geht auf. Genau diesen Unterschied soll das Werkzeug zeigen.

### Erkennung statt Dateigröße

Bis v1.0.0 entschied die Dateigröße über die Gerätefamilie. Das reicht nicht mehr — eine
2-MiB-Datei kann ein EMS2.3-Abbild oder ein TC1796 sein. `EcuDetector` wiegt jetzt Belege
gegeneinander und legt seine Belegliste offen. Die Größe entscheidet nie zwischen Herstellern;
ein knapper Vorsprung heißt „nicht eindeutig“, zu wenig Belege heißen „unbekannt“.

Widerspricht eine Steuergerätekennung dem Abbild, gewinnt das Abbild: die Steuergerätetabelle ist
eine Nutzerangabe, die Bankgrenze im Abbild eine Messung.

### Was ausdrücklich nicht behauptet wird

* Ohne geprüfte Blockkette gibt es keine Blöcke, nur Bereiche mit Konfidenzangabe.
* Ein Datenbereich, der zu Kennfeldern passt, heißt „Kalibrierungskandidat“ und behält die Art
  `Daten` — ein eigener `RegionKind` wäre eine Behauptung im Typsystem.
* Ein Anhang beliebiger Größe ist kein Datenflash, nur weil er hinten steht.
* Bei 2 MiB ohne Steuergerätekennung wird kein Baustein gewählt: TC1796 und TC1797-PMU0 bilden
  dieselben 2 MiB gleich ab.
* Für sechs der acht TriCore-Bausteine ist keine Sektorkarte hinterlegt. Sie werden benannt, aber
  nicht mit einer geratenen Karte gefüllt.
* Ein unbekannter Prüfalgorithmus führt zu „nicht nachgerechnet“, nicht zu einem geratenen
  Verfahren.

### Grenzen dieser Ausgabe

**Geprüft wurde gegen synthetische Abbilder, nicht gegen echte.** Im Repository liegen keine
Steuergeräte-Abbilder, weder Volvo noch VAG. Die 126 Tests prüfen die Mechanik — die Blockkarte
eines ausgewerteten MED17.1-Abbilds ist als synthetisches Abbild nachgebaut —, nicht die
Trefferquote in der Wirklichkeit.

Die Formatkenntnis stammt aus zwei Community-Werkzeugen, die im Blockkopf übereinstimmen, aber
beide an *einem* Abbild entstanden sind. Es ist kein Herstellerdokument. Ob EDC17 denselben Kopf
trägt wie MED17, ist plausibel, aber unbelegt — der Parser prüft es an der Struktur selbst, statt
es vorauszusetzen. Andere Stände können abweichen; das Werkzeug wird dadurch schlechter, nicht
falsch.

Die Länge eines geprüften Bereichs (`csEnd - csStart + 4`) ist ein Analogieschluss zu `blockEnd`,
nicht nachgerechnet.

### Umbau

* `PhysicalLayout` löst `Mpc5777cLayout` als Typ ab und bekommt mit `ToFile` die Rückrichtung,
  mit `EraseSectors` die feine Sektorkarte und mit `SourceNote` eine Herkunftsangabe, die nicht
  mehr fest im Anzeigetext steht.
* `TrwContainer` nimmt das TRW-Sektorformat aus `FlashDump` auf; `EcuProfile` ersetzt die
  Ternärausdrücke in `FlashFormat`; `ByteOrder`, `BinaryHeuristics` und `Hex` legen Wortzugriffe,
  Messungen und Adressdarstellung frei.
* Der Befund führt neue Abschnitte für Erkennung, Kennungen, Blockkette und Löschsektoren.
* Kommandozeile: `--profile` übersteuert die Erkennung, `--list-profiles` zeigt die bekannten,
  `.ori` wird als Endung mitgesucht.
* Der nutzersichtbare Produktname heißt **Flash File Splitter**; Namensraum, Assembly- und
  Repository-Name bleiben `VolvoSplitter`, damit veröffentlichte Verknüpfungen und Nutzerskripte
  weiter funktionieren.

---

## v1.0.0

Erste Veröffentlichung.

### Enthalten

* **Oberfläche** (WPF, `net10.0-windows`) — maßstäbliche Adresskarte des Abbilds, Sektorkarten
  mit Teilenummer, Datei- und CPU-Adressbereich, Kopfdaten und Prüfsummenstand, dunkles Thema
  samt dunkler Titelleiste. Öffnen per Dialog, Drag & Drop oder Startargument.
* **Kommandozeile** `volvosplit` (`net10.0`) — Stapelbetrieb über einzelne Dateien und ganze
  Ordner, schreibt jeden Sektor als Rohdatei und legt `bericht.txt` daneben.
  Optionen `--fixed` und `--out`.
* **Bibliothek** `VolvoSplitter.Core` (`net10.0`) — die gesamte Analyse ohne WPF-Abhängigkeit,
  von Oberfläche und CLI gemeinsam genutzt und damit plattformunabhängig verwendbar.

### Analyse

* Zerlegung von EMS2.3- (MPC5674F) und EMS2.4-Abbildern (MPC5777C) in Parameter-, ASW- und
  Kalibrierungssektor; Familienerkennung über die Dateigröße.
* Suchlauf über das gesamte Abbild nach Sektorköpfen `v=1;a=`, sodass auch **verschobene**
  Sektoren ihrer Rolle zugeordnet werden — über Adresse, CPU-Offset oder die Datensatzkennung
  `.dst1` / `.dst2`. Alternativ nur die fest verdrahteten Standardadressen (`--fixed`).
* Fehlende Sektoren werden mit dem tatsächlichen Inhalt der Adresse begründet statt mit einem
  pauschalen „leer oder verschlüsselt“.
* Fahrzeugdaten aus dem Parameter-Sektor: VIN, Fahrgestellnummer, Herstellerkennung.
* Begleitende Protokolldatei des Auslesegeräts wird automatisch gefunden und ausgewertet.

### Prüfsummen und Änderungen

* CRC32-Prüfung je Sektor; abweichende Sektoren werden gemeldet, aber weiterhin gelesen und
  geschrieben.
* Reparatur einzelner oder aller Prüfsummen. Kopien des alten Prüfwerts im übrigen Abbild werden
  gesucht und zum Mitziehen angeboten — ausdrücklich als Nachfrage, weil sich aus dem Abbild
  nicht ableiten lässt, ob das Steuergerät sie prüft.
* Sektor durch eine Datei ersetzen, mit Prüfung auf verfügbaren Platz und Mindestlänge; die
  Prüfsumme wird nur bei gültigem Kopf in der Ersatzdatei neu berechnet.
* Export als Motorola-S-Record (S0/S3/S5/S7) mit den echten CPU-Adressen.
* Die Originaldatei wird nie überschrieben — gearbeitet wird auf einer Arbeitskopie im Speicher.

### Physisches MPC5777C-Layout

* `Mpc5777cLayout` modelliert die physischen Blöcke mit Datei-Offset und rekonstruierter
  CPU-Adresse nach NXP-Referenzhandbuch, Tabellen 4-2 und 4-3: 8 MiB Large Flash ab CPU
  `0x800000`, vier 64-KiB-Low/Mid-Blöcke ab CPU `0x000000`, 16 KiB UTEST ab CPU `0x400000`.
  Die Summe ergibt genau die Containergröße `0x844000`; die beiden CSE-High-Blöcke sind nicht
  enthalten.
* Neue Übersicht der physischen Blöcke in Oberfläche, Bericht und CLI, jeweils mit Zustand
  belegt / gelöscht / nicht ausgelesen.
* Ein vollständig mit `0xFF` gelesener UTEST-Block gilt als **nicht ausgelesen**, nicht als
  gelöscht: er trägt ab Werk Sensorkalibrierung und Chip-Kennung.
* Passt die Dateigröße zu keinem bekannten Layout, wird der Teil hinter dem Large Flash als
  „Anhang“ ohne CPU-Adresse ausgewiesen.

### Bereichserkennung

* `RegionScanner` ordnet belegte Bereiche ohne Sektorkopf nach **Inhalt statt Adresse** ein:
  `Code`, `NvmData`, `Opaque`, `Data`, jeweils mit Konfidenzstufe (gesichert / stark gestützt /
  unbekannt).
* NVM-Daten werden an `UPTIME`-Records, den AN4868-Blockstatus-Doppelwörtern und Kopien bekannter
  Sektorprüfwerte erkannt — und nur in den Low/Mid-Blöcken, wo das Steuergerät die
  EEPROM-Emulation betreibt.
* Bereiche innerhalb eines Low/Mid-Blocks werden je Block zusammengefasst: Kopf und angehängte
  Records liegen weit auseinander und fielen einzeln unter die Meldeschwelle.
* Hohe Entropie ergibt einen **„Opaken Block“**, nicht „verschlüsselt“ — eine Beobachtung ist
  kein Nachweis, und Chiffretext, Kompression und signierte Container sehen gleich aus.
* `VolvoEcuBlock` parst das VOLVOECU-Format (Magic, Start- und CRC-Adresse, Teilenummer, CRC32
  über den ganzen Block). Die CPU-Basis stammt aus dem Block selbst und bestätigt die
  Containerabbildung unabhängig.
* Die Bereichskarte färbt Code, NVM-Daten und opake Blöcke unterschiedlich.

### Sonstiges

* 49 xUnit-Tests, deren Abbilder synthetisch erzeugt werden — echte Steuergerätedaten sind zum
  Testen nicht nötig.
* Die NXP-Referenzunterlagen liegen im Repository, damit die Herleitung des Layouts nachvollzogen
  werden kann. Sie stammen von NXP Semiconductors und sind von der MIT-Lizenz ausgenommen.
* Veröffentlicht unter der MIT-Lizenz.

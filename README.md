# Flash File Splitter

[![CI](https://github.com/ubbg/VolvoSplitter/actions/workflows/ci.yml/badge.svg)](https://github.com/ubbg/VolvoSplitter/actions/workflows/ci.yml)

Zerlegt Flash-Abbilder von Motorsteuergeräten in ihre Datensätze, rechnet die Prüfsummen nach
und ordnet ein, was dazwischen liegt.

| Gerätefamilie | Mikrocontroller | Container | Umfang |
| --- | --- | --- | --- |
| Volvo / TRW **EMS2.3** | MPC5674F | TRW-Sektoren `v=1;a=` | lesen und zurückschreiben |
| Volvo / TRW **EMS2.4** | MPC5777C | TRW-Sektoren `v=1;a=` | lesen und zurückschreiben |
| VAG / Bosch **EDC17 · MED17 · ME17** | Infineon TriCore | Bosch-Blockkette | **nur lesen** |

Für TriCore-Abbilder werden Prüfsummen **gerechnet und gemeldet, aber nicht gestellt**. Es wird
nichts zurückgeschrieben — kein Prüfsummen-Korrigieren, kein Blockersatz.

Drei Wege in dieselbe Analyse:

* **Oberfläche** — WPF-Anwendung mit Adresskarte, Sektorkarten und Textbefund
* **Kommandozeile** — `volvosplit` für den Stapelbetrieb über ganze Ordner
* **Bibliothek** — `VolvoSplitter.Core`, ohne WPF-Abhängigkeit und plattformunabhängig

> Namensraum, Assembly- und Repository-Name bleiben `VolvoSplitter`. v1.0.0 ist mit
> Release-Artefakten veröffentlicht; eine Umbenennung bräche jede Verknüpfung und jedes
> Nutzerskript, ohne eine einzige Funktion zu verbessern.

---

## Hinweis

Das Werkzeug liest und zerlegt Steuergeräte-Abbilder; Volvo-Abbilder kann es auch verändern. Es
ist für Analyse, Diagnose und Instandsetzung gedacht; was mit den Ergebnissen geschieht, liegt in
deiner Verantwortung.

Ausdrücklich **nicht** enthalten: Entschlüsseln, Entpacken von Auslieferungscontainern (FRF,
ODX-F, SGO), Seed/Key, OBD- oder Bench-Kommunikation, Flashen, A2L-/DAMOS-Einlesen und
Kennfeldbenennung.

Die Originaldatei wird **nie überschrieben**: geladen wird in eine Arbeitskopie im Speicher,
und der Speichern-Dialog schlägt grundsätzlich `<name>_mod<endung>` vor.

---

## Überblick

Ein ausgelesenes Steuergerät liegt als eine einzige große `.mpc`-, `.bin`- oder `.ori`-Datei
vor — bei EMS2.4 rund 8,5 MiB, bei einem MED17.1 mit externem Flash 8 MiB. Darin stecken mehrere
unabhängige Datensätze mit eigenem Kopf und eigener Prüfsumme, dazwischen Programmcode ohne Kopf,
laufzeitveränderte NVM-Daten und gelöschtes Flash.

Das Werkzeug macht daraus:

* **Sektoren** (TRW) — Parameter, ASW und Kalibrierung, jeweils als eigene Rohdatei mit der
  Teilenummer im Dateinamen, dazu `CRC ok` oder die Abweichung
* **Blöcke** (Bosch) — die verkettete Blockstruktur mit Blockart, Softwarekennung und je Block
  den nachgerechneten Prüfsummen
* **Bereiche ohne Kopf** — eingeordnet nach Inhalt, mit Angabe, wie belastbar die Einordnung ist
* **Physische Blöcke** — die Zuordnung Datei-Offset → CPU-Adresse, beim MPC5777C für Large Flash,
  Low/Mid und UTEST, beim TriCore für PMU0, PMU1, externen Flash und DFLASH
* **Kennungen** — Fahrzeugdaten aus dem Volvo-Parametersektor bzw. Hardware-, Software- und
  Steuergerätekennungen aus dem Bosch-Abbild, jeweils **mit Fundort**
* **Befund** — alles davon als Text, **Markdown** oder **HTML**, bzw. in der Zwischenablage

---

## Funktionen

### Oberfläche

* Adresskarte des gesamten Abbilds, maßstäblich — Sektoren, Code, opake Blöcke und NVM-Daten
  farblich unterschieden (`Controls/FlashMap.cs`)
* Je Sektor eine Karte mit Teilenummer, Datei- und CPU-Adressbereich, Größe, Prüfsummenstand,
  Kopfdaten (`p=`, `d=`, `t=`, `f=`, `b=`) und Hinweis, falls der Sektor nicht an der
  Standardadresse liegt
* Bei Bosch-Abbildern je Block die Blockart, die Softwarekennung und die nachgerechneten
  Prüfsummen; ein Abzeichen sagt, wenn Erkennung oder Baustein nicht eindeutig sind
* Einzelne oder alle Prüfsummen korrigieren, inklusive Nachfrage zu veralteten Prüfwertkopien —
  bei TriCore-Abbildern abgeschaltet, sichtbar an einem Abzeichen „nur lesen“
* Sektor bzw. Block extrahieren, als Motorola-S-Record exportieren oder durch eine Datei ersetzen
* Bereiche ohne Kopf einzeln herausschreiben
* Befund in die Zwischenablage oder als Textdatei
* Dunkles Thema samt dunkler Titelleiste (`Native/WindowTheme.cs`)

### Kommandozeile

* Ganze Ordner in einem Lauf, alle Sektoren als Rohdateien plus Befund
* Dieselbe Analyse wie die Oberfläche — beide benutzen `VolvoSplitter.Core`
* Läuft auf Windows, Linux und macOS

### Bibliothek

* Abbild aus Datei oder aus Bytes laden, Sektoren lesen, extrahieren, ersetzen, Prüfsummen
  reparieren, S-Records schreiben
* Begleitende Protokolldatei des Auslesegeräts wird automatisch mitgelesen (`EcuReport`)

---

## Download

Fertige Pakete liegen unter [Releases](https://github.com/ubbg/VolvoSplitter/releases):

| Datei | Inhalt |
| --- | --- |
| `VolvoSplitter-<version>-win-x64.zip` | Oberfläche, Windows x64 |
| `volvosplit-<version>-win-x64.zip` | Kommandozeile, Windows x64 |
| `volvosplit-<version>-linux-x64.tar.gz` | Kommandozeile, Linux x64 |

Alle Pakete sind *self-contained* — die .NET-Laufzeit ist enthalten, es muss nichts installiert
werden. Entpacken und starten genügt.

Die Oberfläche setzt Windows 10 oder 11 (x64) voraus; die Kommandozeile läuft auch ohne Windows.

---

## Oberfläche bedienen

Abbild öffnen — drei Wege:

* **Datei auswählen** bzw. `Strg+O`
* Datei per **Drag & Drop** auf das Fenster ziehen
* Pfad als Startargument übergeben, z. B. eine Verknüpfung mit der `.mpc` als Ziel
  (`App.xaml.cs` reicht `args[0]` durch)

Der Dateidialog filtert auf `*.mpc;*.bin;*.ori`.

### Schalter „Nur Standardadressen“

Standardmäßig **aus**: das ganze Abbild wird nach Sektorköpfen `v=1;a=` durchsucht, und jeder
Fund wird einer Rolle zugeordnet — zuerst über die Adresse, sonst über den CPU-Offset aus dem
Kopf, sonst über die Datensatzkennung `.dst1` / `.dst2` im Dateinamensfeld. Ein verschobener
Sektor wird dadurch trotzdem als Parameter- oder Kalibrierungssektor erkannt und bekommt einen
Hinweis der Form:

```
Gefunden bei 0x7A0000 statt 0x7C0000 — Adresse weicht von der Tabelle ab
```

**Ein** geschaltet werden nur die fest verdrahteten Standardadressen gelesen. Das entspricht
`--fixed` auf der Kommandozeile.

### Tastenkürzel

| Kürzel | Wirkung |
| --- | --- |
| `Strg+O` | Abbild öffnen |
| `Strg+S` | Geändertes Abbild speichern |
| `Strg+E` | Alle Sektoren extrahieren |

### Aktionen

Je Sektor: `Extrahieren`, `Als S-Record`, `Prüfsumme korrigieren`, `Ersetzen…`, `Kopfdaten`.
Global: `Alle Sektoren extrahieren`, `Alle Prüfsummen korrigieren`, `Dump speichern…`,
`Bericht kopieren`, `Bericht speichern…`, `Ordner öffnen`, `Andere Datei`.

Beim Schließen mit ungespeicherten Änderungen wird nachgefragt.

---

## Kommandozeile

```
Flash File Splitter — Stapelbetrieb

  volvosplit <Datei|Ordner> [weitere...] [Optionen]

Zerlegt jedes Flash-Abbild in seine Sektoren bzw. Blöcke, schreibt sie als
Rohdateien und legt einen Textbefund daneben.

Unterstützt werden Volvo/TRW EMS2.3 (MPC5674F) und EMS2.4 (MPC5777C) sowie
lesend die VAG-Steuergeräte auf Infineon TriCore (Bosch EDC17 / MED17).
Für TriCore-Abbilder werden Prüfsummen gerechnet und gemeldet, aber nicht
gestellt — es wird nichts zurückgeschrieben.

Optionen:
  -f, --fixed           Nur die fest verdrahteten Standardadressen lesen
  -o, --out <Ordner>    Zielordner (Standard: neben dem Abbild)
  -p, --profile <name>  Erkennung übersteuern
  -r, --report-format <f>  Befund als txt (Vorgabe), md oder html
      --list-profiles   Bekannte Profile auflisten
  -h, --help            Diese Hilfe
      --                Alles Folgende ist ein Dateiname, keine Option

Beispiele:
  volvosplit C:\Dumps\ecu_Micro.mpc
  volvosplit C:\Dumps --out C:\Ausgabe
  volvosplit ecu.ori --profile tc1797
  volvosplit ecu.bin --report-format html
```

| Option | Wirkung |
| --- | --- |
| `-f`, `--fixed` | Nur die Standardadressen lesen, kein Suchlauf über das ganze Abbild |
| `-o`, `--out <Ordner>` | Gemeinsamer Zielordner statt eines Ordners neben jedem Abbild |
| `-p`, `--profile <name>` | Erkennung übersteuern: `ems23`, `ems24`, `tricore`, `tc1796`, `tc1797`, `unknown` |
| `-r`, `--report-format <f>` | `txt` (Vorgabe), `md` oder `html`. Unbekanntes bricht ab, statt still auf Text zurückzufallen |
| `--list-profiles` | Bekannte Profile auflisten |
| `-h`, `--help`, `/?` | Hilfe ausgeben |
| `--` | Alles Folgende ist ein Dateiname — für Abbilder, die wie eine Option heißen |

**Ein Aufruffehler bricht ab**, statt eine Option unter den Tisch fallen zu lassen: eine Option
ohne Wert (`-o` am Zeilenende), eine Option als Wert (`-o -f` legte einen Ordner namens `-f` an
und ließ `--fixed` fallen) und eine unbekannte Option (`--fixd` galt als Dateiname) melden und
enden mit Rückgabewert 1. Ein Abbild, das wirklich `-f` heißt, schreibt man `./-f` oder stellt
`--` davor.

**Ordner als Argument** werden **nicht rekursiv** nach `*.mpc`, `*.bin` und `*.ori` durchsucht —
nur die oberste Ebene. Mehrfach genannte Dateien werden einmal verarbeitet, und das wird gesagt.
Ob zwei Pfade dieselbe Datei bezeichnen, entscheidet die **Vorgabe des Dateisystems**: Windows
und macOS ohne, Linux mit Rücksicht auf Groß- und Kleinschreibung. Pauschal ohne Rücksicht zu
vergleichen hieß unter Linux, von `A.bin` und `a.bin` eine nie zu lesen — ohne jede Meldung.

**Zielordner:** ohne `--out` entsteht `<abbildname>_sektoren/` neben dem Abbild, mit `--out`
stattdessen `<zielordner>/<abbildname>/`. In beiden Fällen landet dort zusätzlich der Befund als
`bericht.txt`, `bericht.md` oder `bericht.html` — je nach `--report-format`.

**Zwei gleichnamige Abbilder in einem Aufruf** teilen sich diesen Ordner **nicht**. Vorher taten
sie es: `volvosplit a/Original.bin b/Original.bin --out o` schrieb die Sektoren beider
Steuergeräte nebeneinander in `o/Original/`, und die eine `bericht.txt` beschrieb nur das zuletzt
zerlegte — bei gleicher Teilenummer wurde byteweise überschrieben, lautlos. Das zweite Abbild
bekommt jetzt einen eigenen Ordner, benannt nach seinem Elternordner (`o/b_Original/`), notfalls
durchnummeriert; die Ausweichung wird gemeldet. Abgebrochen wird nicht — ein Stapellauf über
hunderte Abbilder darf nicht am zweiten Fund sterben. Belegt heißt „in diesem Lauf vergeben“,
nicht „liegt schon auf der Platte“: derselbe Aufruf zweimal ausgeführt beschreibt dieselben
Ordner.

**Berichtsformate.** Der Befund wird einmal aufgebaut und wahlweise als Text, Markdown oder HTML
ausgegeben; der Inhalt ist in allen dreien derselbe. Markdown liefert echte Tabellen für Ticket
oder Dokumentation. HTML ist eine eigenständige Seite mit eingebettetem Stil — kein Verweis nach
außen, hell und dunkel über `prefers-color-scheme`, und **abweichende Prüfsummen sind farblich
abgesetzt**. In der Oberfläche entscheidet die Endung im Speichern-Dialog über das Format.

**Ausgabe:**

```
ecu_Micro.mpc  ·  EMS2.4  ·  3 Sektoren
   ok    Parameter      P1234567890         12.345 B  ->  param_P1234567890
   ok    ASW            P1234567891      1.048.576 B  ->  asw_P1234567891
   CRC!  Kalibrierung   P1234567892        262.144 B  ->  cal_P1234567892
   Bericht  ->  bericht.txt

1/1 Abbilder verarbeitet.
```

Ein Sektor mit abweichender Prüfsumme wird mit `CRC!` markiert, aber **trotzdem geschrieben**.

**Rückgabewert:** `0`, wenn alle gefundenen Abbilder verarbeitet wurden; sonst `1` — auch dann,
wenn gar kein Abbild gefunden wurde.

---

## Als Bibliothek verwenden

`VolvoSplitter.Core` zielt auf `net10.0` und hängt an keiner WPF-Klasse; die Bibliothek läuft
überall, wo .NET 10 läuft.

```csharp
using VolvoSplitter.Core;

var dump = FlashDump.Load("ecu_Micro.mpc");

Console.WriteLine(dump.Profile.Headline);        // "EMS2.4 · MPC5777C"
Console.WriteLine(dump.Detection.Summary);       // Punktzahl und Zweitplatzierter
Console.WriteLine(dump.Vehicle?.Vin);

foreach (var sector in dump.Sectors.Where(s => s.Present))
{
    Console.WriteLine($"{sector.Label}: {sector.PartNumber} {sector.AddressRange} " +
                      (sector.CrcOk ? "CRC ok" : $"CRC 0x{sector.CrcStored:X8} != 0x{sector.CrcComputed:X8}"));
    dump.ExtractSector(sector, "ausgabe");
}

File.WriteAllText("ausgabe/bericht.txt", DumpReport.Build(dump));
```

| Einstieg | Zweck |
| --- | --- |
| `FlashDump.Load(path, fixedAddressesOnly = false, forceProfile = null)` | Abbild aus einer Datei laden und analysieren; sucht auch die Protokolldatei daneben |
| `FlashDump.FromBytes(data, name, fixedAddressesOnly = false, forceProfile = null)` | Abbild aus Bytes im Speicher — für Aufrufer, die die Daten schon halten, und für Tests |
| `dump.Profile` | Gerätefamilie, Baustein, Bytereihenfolge, Containerformat (`EcuProfile`) |
| `dump.Detection` | Wie das Profil zustande kam, samt Belegliste und Mehrdeutigkeit |
| `dump.Sectors` | Gefundene und fehlende Sektoren (`SectorInfo`) |
| `dump.Regions` | Belegte Bereiche ohne Sektorkopf (`FlashRegion`) |
| `dump.Partitions` | Physische Blöcke mit Zustand (`PartitionInfo`) |
| `dump.Vehicle` | VIN, Fahrgestellnummer, Hersteller — oder `null` |
| `dump.Report` | Protokolldatei des Auslesegeräts (`EcuReport`) — oder `null` |
| `dump.Chain` | Gelesene Bosch-Blockkette samt Prüfsummen, Lücken und CVN (`BoschChainResult`) |
| `dump.Identity` | Kennungen eines Bosch-Abbilds mit Fundort (`BoschIdentity`) — oder `null` |
| `dump.Layout` | Physisches Layout des Bausteins (`PhysicalLayout`) — oder `null` |
| `dump.ExtractSector(sector, dir)` | Sektor als Rohdatei schreiben |
| `dump.ExportSRecord(sector, path)` | Sektor als Motorola-S-Record mit echten CPU-Adressen |
| `dump.RepairCrc(sector)` | Prüfsumme neu berechnen; meldet die verbliebenen alten Kopien zurück |
| `dump.ReplaceSector(sector, bytes, repairCrc)` | Sektor durch eine Datei ersetzen |
| `dump.Save(path)` | Arbeitskopie schreiben |

Alle verändernden Vorgänge — `RepairCrc`, `ReplaceSector`, `PatchUInt32Be`, `Save` — werfen
`InvalidOperationException`, wenn `dump.Profile.SupportsWriteBack` nicht gesetzt ist. Für
TriCore-Abbilder ist das keine Lücke, sondern eine Festlegung.
| `DumpReport.Build(dump)` | Vollständiger Textbefund |

---

## TRW-Sektorformat

**Gilt nur für die Volvo-Geräte.** Für die TriCore-Abbilder der VAG-Steuergeräte trifft davon
nichts zu — deren Aufbau steht unter [Bosch-Blockkette](#bosch-blockkette).

Jeder Sektor ist ein eigenständiger Container mit ASCII-Kopf und CRC32-Trailer:

```
+0x000  ASCII-Kopf   "v=1;a=<TeileNr>;p=...;o=<CPU-Adresse>;"
+0x006  Teilenummer  (11 Zeichen)              -> Dateiname
+0x0F8  Endadresse   (4 Byte, big endian, CPU-Adressraum)
+0x0F8  ... Nutzdaten, über die die CRC32 läuft
Ende-4  CRC32        (4 Byte, big endian)

Länge = Endadresse - CPU-Offset + 44
```

Die Prüfsumme läuft also **ab `0x0F8`** bis vor den Trailer — der ASCII-Kopf ist nicht Teil der
CRC-Berechnung.

Felder des Kopfes, soweit ausgewertet:

| Feld | Bedeutung |
| --- | --- |
| `a=` | Teilenummer |
| `p=` | Projekt |
| `d=` / `t=` | Datum und Uhrzeit des Datensatzes |
| `f=` | Ursprünglicher Dateiname — enthält `.dst1`, `.dst2` oder `.pbc` |
| `b=` | Baseline |
| `o=` | CPU-Offset (hexadezimal) |

Im **Parameter-Sektor** stehen zusätzlich die Fahrzeugdaten:

| Offset | Länge | Inhalt |
| --- | --- | --- |
| `0x100` | 11 | Fahrgestellnummer |
| `0x10B` | 5 | Herstellerkennung `VOLVO` |
| `0x120` | 17 | VIN |

Steht bei `0x10B` nicht `VOLVO`, werden gar keine Fahrzeugdaten gemeldet.

---

## Unterstützte Steuergeräte

### Erkennung

Bis Version 1 genügte die Dateigröße: bis `0x500000` EMS2.3, darüber EMS2.4. Das reicht nicht
mehr — eine 2-MiB-Datei kann ein EMS2.3-Abbild oder ein TC1796 sein. Erkannt wird deshalb über
eine **Prüfkette**: jeder Beleg im Abbild gibt Punkte, der höchste Stand gewinnt.

| Beleg | Punkte |
| --- | --- |
| Block mit **rechnerisch bestätigter** Bosch-Prüfsumme | 80 |
| Gültiger Bosch-Blockkopf samt `0xDEADBEEF` am Blockende | 60 |
| Sektorkopf `v=1;a=` mit lesbarem `o=`-Feld | 50 |
| Variantenkennung `EDC17…` / `MED17…` im Dataset-Block bei `+0x78` | 50 |
| `Micro:` der Protokolldatei nennt einen TC17xx | 45 |
| Bosch-Kennungen (`0281…`, `1037…`, `EDC17…`, `MED17…`, `\d{2}SW\d{6}`) | 40 |
| Sektorkopf deckt sich mit der EMS2.3- bzw. EMS2.4-Tabelle | 30 |
| Dateigröße ist exakt `0x844000` — die Summe der MPC5777C-Blockkarte | 30 |
| Zeigerdichte: ausgerichtete LE-Wörter im TriCore-Adressraum über der Schwelle | 30 |
| `VOLVOECU`-Modul parsebar · `VOLVO` im Parametersektor | je 20 |
| Plausible TriCore-Größe · PFLASH + angehängter DFLASH | je 10 |

Zwei Regeln halten das ehrlich:

* **Die Dateigröße entscheidet nie zwischen Herstellern**, sie bricht nur Gleichstände innerhalb
  eines Herstellers. Einzige Ausnahme ist `0x844000` — das ist kein Größenmaß, sondern die auf
  das Byte genaue Summe der MPC5777C-Blockkarte.
* **Weniger als 20 Punkte Vorsprung heißt „nicht eindeutig“**, nicht „gewonnen“. Unter 30 Punkten
  wird gar kein Container behauptet; dann gibt es nur Bereiche mit Konfidenzangabe.

Mit `--profile` bzw. `FlashDump.Load(path, forceProfile: …)` lässt sich die Erkennung übersteuern.

### Volvo / TRW

| Familie | Micro | Parameter | ASW | Kalibrierung |
| --- | --- | --- | --- | --- |
| EMS2.3 | MPC5674F | `0x060000` | `0x100000` | `0x380000` |
| EMS2.4 | MPC5777C | `0x7C0000` → CPU `0xFC0000` | `0x200000` → CPU `0xA00000` | `0x740000` → CPU `0xF40000` |

Bei EMS2.3 sind Datei-Offset und CPU-Adresse identisch; bei EMS2.4 liegt der Large Flash im
CPU-Adressraum ab `0x800000`, weshalb sich beide Spalten unterscheiden.

### VAG / Bosch auf Infineon TriCore

Die Zuordnung Steuergerätetyp → Mikrocontroller ist eine **Nutzerangabe, kein
Herstellerdokument**. Sie ist auch kein Layout, sondern löst eine Mehrdeutigkeit auf: eine
2-MiB-Datei kann ein TC1796 oder die PMU0 eines TC1797 sein, und die Sektorkarten ihrer oberen
Hälften unterscheiden sich.

| Steuergerät | Kraftstoff | Mikrocontroller | Sektorkarte hinterlegt |
| --- | --- | --- | --- |
| EDC17U01, EDC17U05 | Diesel | TC1766 | nein |
| EDC17CP04, EDC17CP14 | Diesel | TC1796 | **ja** |
| EDC17CP20 | Diesel | TC1796 *oder* TC1797 | mehrdeutig |
| EDC17CP44, EDC17C54 | Diesel | TC1797 | **ja** |
| EDC17C46 | Diesel | TC1767 | nein |
| EDC17C64 | Diesel | TC1767 *oder* TC1782 | mehrdeutig |
| EDC17C74 | Diesel | TC1793 *oder* TC1797 | mehrdeutig |
| ME17.5 | Benzin | TC1762 *oder* TC1766 | mehrdeutig |
| MED17.5.2, MED17.5.5 | Benzin | TC1766 *oder* TC1767 | mehrdeutig |
| MED17.5.21, MED17.5.25 | Benzin | TC1782 | nein |
| MED17.5.26, MED17.5.27 | Benzin | TC1782 *oder* TC1793 | mehrdeutig |
| MED17.1, MED17.1.1 | Benzin | TC1796 | **ja** |
| MED17.1.6 | Benzin | TC1797 | **ja** |
| MED17.1.21, MED17.1.27 | Benzin | TC1793 | nein |

Ausgeliefert werden nur die Sektorkarten von **TC1796** und **TC1797** — genau die beiden
Bausteine, deren Datenblätter ausgewertet wurden. Alle übrigen werden erkannt und benannt,
bekommen aber keine Karte: `Complete` ist dann `false`, `EraseSectors` bleibt leer, und die
Herkunftszeile sagt es. Eine geratene Karte auszuliefern wäre genau die Erfindung, die dieses
Werkzeug vermeidet.

**Nennt eine Kennung einen Baustein, dessen Bankaufteilung dem Abbild widerspricht, gewinnt das
Abbild.** Die Tabelle ist eine Nutzerangabe, die Bankgrenze eine Messung: nur eine richtige
Bankgrenze lässt `blockEnd` und `0xDEADBEEF` zusammenpassen. Der Widerspruch wird gemeldet.

**Und der Nullpunkt wird ebenso gemessen.** Ein Abbild beginnt nicht zwangsläufig an der
PFLASH-Basis — eine Teilauslesung ab `0x80180000` ist im ausgewerteten Bestand der Normalfall,
nicht die Ausnahme. Wo das Fenster anfängt, sagt der Blockkopf selbst:
`blockStart = blockEnd − size + 4`, und damit ist der Nullpunkt `blockStart − fileStart`. Die so
gemessenen Basen laufen als weitere Layout-Kandidaten durch dieselbe Zählung bestätigter Köpfe.
Sie gewinnen nur mit **echtem** Vorsprung; bei Gleichstand bleibt es bei der Vorgabe, denn ein
einzelner Kopf bestätigt die aus ihm selbst abgeleitete Basis zwangsläufig. Der Beleg nennt sie:

```
Nullpunkt 0x80180000 aus den Blockköpfen gemessen: dort bestätigen sich 2 Köpfe,
ab dem Anfang von TC1796 nur 0 — das Abbild beginnt nicht an der PFLASH-Basis
```

Gemessen an 1516 VAG-Abbildern: 225 davon verloren vorher **sämtliche** Blöcke allein daran,
dass Datei-Offset 0 fest auf `0x80000000` stand.

Fehlt eine Rolle im Abbild, wird sie trotzdem aufgeführt — mit der Begründung, was an der
Adresse tatsächlich steht, statt eines pauschalen „leer oder verschlüsselt“:

```
Kein Sektorkopf bei 0x200000 — dort steht Programmcode im Klartext, aber ohne Sektorkopf
(0x200000 – 0x33F000, 1.306.624 B)
```

---

## Physische Layouts

### MPC5777C

Das Auslesegerät hängt drei getrennte Adressräume hintereinander in **eine** Datei. Rekonstruiert
aus dem NXP-Referenzhandbuch, Kapitel 4, Tabellen 4-2 und 4-3:

| Datei | CPU | Größe | Block |
| --- | --- | --- | --- |
| `0x000000 – 0x7FFFFF` | `0x800000 – 0xFFFFFF` | 8 MiB | Large Flash, 32 Sektoren zu 256 KiB |
| `0x800000 – 0x80FFFF` | `0x000000 – 0x00FFFF` | 64 KiB | Low-Block 0 (RWW-Partition 0) |
| `0x810000 – 0x81FFFF` | `0x010000 – 0x01FFFF` | 64 KiB | Low-Block 1 (RWW-Partition 1) |
| `0x820000 – 0x82FFFF` | `0x020000 – 0x02FFFF` | 64 KiB | Mid-Block 0 (RWW-Partition 2) |
| `0x830000 – 0x83FFFF` | `0x030000 – 0x03FFFF` | 64 KiB | Mid-Block 1 (RWW-Partition 3) |
| `0x840000 – 0x843FFF` | `0x400000 – 0x403FFF` | 16 KiB | UTEST |

Die Summe ergibt genau `0x844000` — es bleibt kein Byte unerklärt. Die beiden 16-KiB-High-Blöcke
(CSE, CPU `0x600000 – 0x607FFF`) sind **nicht** im Container; mit ihnen wäre die Datei
`0x84C000` groß.

Die Zuordnung Container → CPU ist eine Interpretation: NXP dokumentiert das proprietäre
Dateiformat nicht. Gestützt wird sie durch die Dateigröße, durch die Sektorköpfe (der Sektor
bei `0x740000` nennt selbst `0xF40000`) und durch die Adressfelder im VOLVOECU-Block.

Passt die Dateigröße zu keinem bekannten Layout, wird der Teil hinter dem Large Flash als
**„Anhang“ ohne CPU-Adresse** ausgewiesen, statt eine zu erfinden.

Jeder Block bekommt einen Zustand:

| Zustand | Bedeutung |
| --- | --- |
| `belegt` | Enthält Daten — aufgeführt wird, welche Sektoren und Bereiche darin liegen |
| `gelöscht` | Vollständig `0xFF`; bei Low/Mid der mögliche Tauschpartner für einen Block-Swap |
| `nicht ausgelesen` | Vollständig `0xFF`, obwohl der Block das nicht sein kann — betrifft UTEST |

### Infineon TriCore

Aus den Infineon-Datenblättern:

| | TC1796 | TC1797 |
| --- | --- | --- |
| PFLASH gesamt | 2 MiB (ein PMU) | 4 oder 3 MiB — *derivative dependent*: PMU0 2 MiB + PMU1 2 MiB |
| Sektoren je 2-MiB-Bank | 8 × 16 KiB, 1 × 128 KiB, 1 × 256 KiB, 3 × 512 KiB | 8 × 16 KiB, 1 × 128 KiB, 7 × 256 KiB |
| DFLASH | 128 KiB, 2 Bänke à 64 KiB | 64 KiB, 2 Bänke à 32 KiB |
| BROM | 16 KiB | 16 KiB (nur PMU0) |
| Externer Bus | EBU vorhanden | EBU vorhanden |

Beide Sektorsummen ergeben je Bank exakt 2048 KiB (128 + 128 + 256 + 1536 bzw.
128 + 128 + 1792) — das ist die Prüfbedingung der Tabellen, und sie wird getestet.

Für die **3-MiB-Ausführung** des TC1797 nennt das Datenblatt *keine* Sektoraufteilung der dann
1 MiB großen PMU1. Dieser Fall wird benannt, aber nicht mit einer geratenen Karte gefüllt:
`Complete` ist `false`, und die Bank erscheint ohne Sektorgliederung.

Adressen:

| Bereich | CPU-Adresse |
| --- | --- |
| PFLASH / PMU0 | `0x80000000` gecacht, `0xA0000000` ungecacht |
| PMU1 (TC1797) | `0x80800000` / `0xA0800000` |
| Externer Flash über die EBU | ab `0x84000000` |
| DFLASH | `0xAF000000` |
| BROM | `0xAFFFC000` |

**Gecacht und ungecacht zeigen auf denselben Flash.** Die Adressabbildung maskiert deshalb das
Bit `0x20000000` heraus. Das ist keine Feinheit: die Infineon-Unterlagen nennen `0xA…`, die
Bosch-Blockköpfe im Abbild nennen `0x8…`.

So wird ein Abbild auf die Bänke abgebildet:

| Dateigröße | Deutung |
| --- | --- |
| `0x200000` | eine PFLASH-Bank (TC1796 vollständig, oder TC1797-PMU0) |
| `0x300000` | TC1797, 3-MiB-Ausführung — PMU1 ohne Sektorgliederung |
| `0x400000` | TC1797: PMU0 (`0x80000000`) + PMU1 (`0x80800000`) |
| `0x800000` | 4 MiB intern + 4 MiB externer Flash ab `0x84000000` — die MED17.1-Form |
| oben + `0x10000` (TC1797) bzw. + `0x20000` (TC1796) | zusätzlich DFLASH ab `0xAF000000` |
| sonst | Rest als „Anhang“ **ohne** CPU-Adresse |

Ein Anhang beliebiger Größe ist **kein** Datenflash, nur weil er hinten steht — dieselbe Regel
wie beim MPC5777C.

**Bekannte Grenze:** Auslesewerkzeuge legen DFLASH und externen Flash oft als *eigene* Dateien ab
(`…_Eeprom.bin`, `…_ExtFlash.bin`). `FlashDump` hält genau ein Abbild; ein Mehrdateimodell ist
nicht Teil dieses Werkzeugs. Eine allein geladene DFLASH-Datei wird deshalb als `unbekannt`
gemeldet und nicht als geschrumpftes PFLASH gedeutet.

---

## Bereiche ohne Sektorkopf

Was zwischen den Sektoren liegt, wird nach **Inhalt** eingeordnet, nicht nach Adresse. Ein
Bereich hinter dem Large Flash ist deshalb nicht automatisch EEPROM: dort liegen beim MPC5777C
vier gewöhnliche Flash-Blöcke, die ebenso gut ausführbaren Code enthalten können — „EEPROM data“
ist bei NXP nur die Spalte *Example use*.

| Art | Bedeutung |
| --- | --- |
| `Programmcode` | Klartextcode ohne Sektorkopf — vor allem der ASW-Code |
| `Opaker Block` | Hohe Entropie ohne erkennbare Struktur; Format unbekannt |
| `NVM-Daten` | Laufzeitveränderte Daten der EEPROM-Emulation |
| `Daten` | Belegt, aber weder erkennbarer Code noch opak |

Dazu jeweils eine Konfidenz:

| Stufe | Bedeutung |
| --- | --- |
| `gesichert` | Durch Kopf, Prüfsumme oder eindeutigen Inhalt belegt |
| `stark gestützt` | Mehrere übereinstimmende Anzeichen, aber kein formaler Nachweis |
| `unbekannt` | Beobachtung ohne belastbare Deutung |

### Herangezogene Merkmale

* **Shannon-Entropie** über den gesamten Bereich
* **Anteil wiederkehrender 16-Byte-Blöcke** in einem zusammenhängenden Fenster von höchstens
  1 MiB — compilierter Code wiederholt sich stark, Chiffretext praktisch nie
* **Quelldateipfade** (`src/`, `../`) und die SCCS-Kennung `@(#)` aus Assert- und
  Versionsstrings — belegen unverschlüsselten Code
* **Anteil monotoner 64-Byte-Fenster** als u8- oder u16-Folge — Kennfeldachsen und Stützstellen
  sind genau das
* **`UPTIME`-Records** — Beleg für Laufzeitänderungen
* **Blockstatus-Doppelwörter** am Blockanfang nach NXP AN4868, Tabelle 4
  (`$erased`, `$verified`, `$copy`, `$active`)
* **Kopien bekannter Sektor-Prüfwerte** — der eigene Trailer des Sektors zählt dabei nicht

### Was das Werkzeug bewusst nicht behauptet

* Hohe Entropie heißt **„Opaker Block“**, nicht „verschlüsselt“. Chiffretext, komprimierte Daten
  und signierte Container sehen gleich aus — die Meldung lautet ausdrücklich
  *„Verschlüsselung oder Kompression möglich, aber nicht belegt“*.
* Ein vollständig `0xFF` gelesener **UTEST-Block** heißt **„nicht ausgelesen“**, nicht „gelöscht“:
  NXP programmiert dort ab Werk Sensorkalibrierung und Chip-Kennung, der Bereich *kann* nicht
  leer sein.
* Trifft kein AN4868-Blockstatus zu, ist das **kein Gegenbeweis** — Volvo muss die
  NXP-Vorschlagswerte nicht verwenden.
* Die Volvo-eigenen Felder der NVM-Records werden **nicht dekodiert**, sondern nur gezählt.
* Liegt ein Bereich hinter dem Flash-Baustein und lässt sich keiner Partition zuordnen, wird er
  mit *„Herkunft nicht bestimmt“* und Konfidenz `unbekannt` gemeldet.
* Ohne **geprüfte Blockkette** gibt es keine Blöcke — auch dann nicht, wenn ein TriCore-Abbild
  plausibel aussieht. Es bleibt bei Bereichen mit Konfidenzangabe.
* Ein Datenbereich, der zu Kennfeldern passt, heißt **„Kalibrierungskandidat“**, nicht
  „Kalibrierungssektor“ — und behält die Art `Daten`. Ein eigener `RegionKind.Calibration` wäre
  eine Behauptung im Typsystem; ohne A2L oder DAMOS ist ein Kalibrierbereich aus dem Abbild
  allein nicht belegbar.
* Die **Prüfsummenstrukturen** der Bosch-Blöcke werden gerechnet und gemeldet, aber nicht
  gedeutet: ein unbekannter Algorithmus führt zu „nicht nachgerechnet“, nicht zu einem geratenen
  Verfahren.
* Bei einer 2-MiB-Datei ohne Steuergerätekennung wird **kein Baustein gewählt**: TC1796 und
  TC1797-PMU0 bilden dieselben 2 MiB gleich ab. Die Mehrdeutigkeit wird gemeldet, die
  Löschsektorkarte bleibt offen.
* Die acht Bytes bei `+0x24` im Blockkopf sind **unerklärt**. Sie werden roh ausgewiesen.
* Für sechs der acht TriCore-Bausteine ist **keine Sektorkarte hinterlegt**. Das steht in der
  Herkunftszeile, statt eine zu erfinden.

---

## Bosch-Blockkette

Die TriCore-Abbilder tragen keinen TRW-Sektorkopf. Statt dessen steht im Abbild eine **verkettete
Liste von Blöcken**, little endian:

```
+0x00  u32   blockIdentifier   unteres Byte = Blockart, Bit 0x00800000 = OTP
+0x04  u32   size              Blocklänge
+0x08  u32   nextSector        CPU-Adresse des nächsten Kopfes, 0 = Kettenende
+0x0C  u32   blockEnd          = blockStart + size - 4
+0x10  u32   table1Pointer     CPU-Adresse einer Wortliste
+0x14  u32   table2Pointer     dito
+0x18  u8    table1Size        Anzahl Einträge
+0x19  u8    table2Size        Anzahl Einträge
+0x1A  10 B  swIdentifier      ASCII, z. B. „10SW008917“
+0x24   8 B  unerklärt         im ausgewerteten Abbild 0xFF-Füllung
+0x2C  u32   numChecksumStructures
+0x30  u32   checksumAdjust
+0x34  n×32  Prüfsummenstrukturen
       u32   Prüfwort des Blocks
Ende-4 u32   0xDEADBEEF
```

Eine **Prüfsummenstruktur** ist 32 Byte groß:

| Versatz | Breite | Feld |
| --- | --- | --- |
| `+0x00` | u8 | `csBlockId` |
| `+0x04` | u32 | `csStart` — CPU-Adresse, Beginn des geprüften Bereichs |
| `+0x08` | u32 | `csEnd` — CPU-Adresse des **letzten Byte**, einschließlich |
| `+0x0C` | u32 | `csStartVal` — Startwert, im Regelfall `0xFADECAFE` |
| `+0x10` | u32 | `csExpectedVal` — Sollwert, im Regelfall `0xCAFEAFFE` |
| `+0x14` | u32 | `blockIdRef` |
| `+0x18` | u32 | `blockIdAddr` |
| `+0x1C` | u16 | `csAlgorithm` — unteres Byte zählt |

### Die drei Prüfalgorithmen

| ID | Name | Verfahren | Sollergebnis |
| --- | --- | --- | --- |
| `0x00` | `SB_CRC32_ALGO_E` | CRC32, Polynom `0xEDB88320`, ohne Schlussabgleich | **`0x35015001`** |
| `0x01` | `SB_ADD32_ALGO_E` | Summe der u32-Doppelworte, Überlauf verworfen | `csExpectedVal` |
| `0x10` | `SB_ADD16_ALGO_E` | Summe der u16-Worte | `csExpectedVal` |

`0x35015001` ist kein eigener Sollwert, sondern das Einerkomplement von `0xCAFEAFFE`: die CRC32
läuft ohne Schlussabgleich, und der geprüfte Bereich schließt das Stellwort mit ein.

**Erst eine nachgerechnete Prüfsumme rechtfertigt `Verified`.** Ein Block, dessen Kopf aufgeht,
dessen Prüfsummen aber nicht stimmen, bekommt `CrcMismatch` — ein Abbild mit bearbeiteter
Kalibrierung sieht damit sofort so aus, wie es ist.

### Blockarten

| ID | Bedeutung | ID | Bedeutung |
| --- | --- | --- | --- |
| `0x10` | Startup Block | `0x80` | Variant dataset |
| `0x20` | Tuning protection | `0x90` | Customer Tuning protection |
| `0x30` | Customer Block | `0xA0` | Application software #2 |
| `0x40` | Application software #0 | `0xB0` | Application software #3 |
| `0x50` | Application software #1 | `0xC0` | Absolute constants #0 |
| `0x60` | Dataset #0 | `0xD0` | Emulation extension chip |
| `0x70` | Dataset #1 | `0xE0` | Customer specific |
| | | `0xF0` | Ramloader |
| | | `0xF1` | Application Attestation |

### Was einen Kopf bestätigt

Erst wenn **alle** Regeln zutreffen, gilt ein Kopf als gültig. Die Reihenfolge folgt der Schärfe:

1. **`0xDEADBEEF` bei `blockStart + size - 4`** — unabhängig von jeder Adressrechnung
2. Die Blockart steht in der Tabelle oben
3. `size` zwischen `0x40` und der Dateigröße, Block passt vollständig hinein —
   **keine** Prüfung auf Sektorbündigkeit, Blockgrößen sind nicht sektorbündig
4. **`blockEnd == blockStart + size - 4`**, exakt und ohne Toleranz
5. Der Block liegt ganz in *einer* physischen Bank — Bänke sind im CPU-Raum nicht zusammenhängend
6. `numChecksumStructures` ist plausibel und passt in den Block
7. Beide Zeigertabellen liegen im eigenen Block, sofern ihre Anzahl > 0 ist

Der `swIdentifier` ist **keine** dieser Regeln. Er wird gelesen, nicht geprüft: ein Feld, das
sich nicht als druckbares ASCII lesen lässt, ergibt eine leere Kennung — es verwirft den Kopf
nicht. 25 von 1516 ausgewerteten VAG-Abbildern füllen es mit `0xAF` und verloren dadurch
zusammen 123 Blöcke, 14 davon restlos alle, obwohl die vier scharfen Regeln bei jedem dieser
Köpfe zutrafen. Ein Textfeld darf keine strukturelle Prüfung sein. Gelesen wird alles oder
nichts: aus Binärrauschen den druckbaren Teil herauszuklauben erfände eine Teilenummer.

### Zwei Suchverfahren

Die Struktur wird **doppelt** gelesen: eine Abtastung über das ganze Abbild als Hauptverfahren
und der Kettenlauf über `nextSector` als Gegenprobe. Stimmen beide Ergebnismengen überein, ist
das ein eigener Beleg; weichen sie ab, wird die Differenz gemeldet — nicht stillschweigend
vereinigt. Der Kettenlauf bricht nach höchstens 64 Schritten ab und erkennt Zyklen.

**Was der Kettenleser dabei sieht, steht im Befund.** Verworfene Kopfkandidaten (höchstens fünf
aufgezählt, der Rest gezählt), eine abgerissene oder zyklische Kette, ein fehlender Einstiegspunkt
und die Übereinstimmung beider Verfahren erscheinen als Belege. „Kein Blockkopf gefunden“ und
„zwei Kandidaten tragen `0xDEADBEEF`, aber ihr `blockEnd` passt nicht zur angenommenen Basis“
sind zwei völlig verschiedene Aussagen — ohne diese Sätze sieht „0 Sektoren“ in beiden Fällen
gleich aus.

### Nachgerechnete Invarianten

Aus der Beispielausgabe des ausgewerteten Abbilds, jede acht von acht Mal:

* **I1** `blockEnd == blockStart + size - 4` — `blockEnd` zeigt auf das *letzte Wort*, nicht
  hinter das Blockende. Die schärfste einzelne Prüfregel.
* **I2** Beide Zeigertabellen liegen im eigenen Block. Blöcke ohne Tabellen tragen Zeiger `0`
  und Anzahl `0`.
* **I3** Kettenende ist `nextSector == 0` — keine Sonderbehandlung nötig.
* **I4** Der `swIdentifier` gruppiert die Blöcke zu Softwareeinheiten; die Form `\d{2}SW\d{6}`
  ist belegt.
* **I5** Blockgrößen sind **nicht** sektorbündig — Block 4 ist `0xFD04` groß.
* **I6** Die Blöcke überlappen sich nicht und lassen sechs Lücken von 256 B bis 512 KiB. Der
  Parser meldet sie, behauptet aber nichts über ihren Inhalt.
* **I7** `0xDEADBEEF` steht bei `blockStart + size - 4`.

### Die Blockkarte des ausgewerteten Abbilds

```
Block                 CPU-Bereich          Datei-Offset     Bank     Größe
4 Customer            80000000-8000FD04    000000-00FD04    PMU0        64.772 B
3 Cust. tuning prot.  80010000-80012000    010000-012000    PMU0         8.192 B
2 Tuning protection   80014000-80017F00    014000-017F00    PMU0        16.128 B
1 Startup             80018000-8001FF00    018000-01FF00    PMU0        32.512 B
5 ASW #0              80020000-80200000    020000-200000    PMU0     1.966.080 B
6 ASW #1              80800000-80A00000    200000-400000    PMU1     2.097.152 B
8 Dataset #0          84002000-84100000    402000-500000    Extern   1.040.384 B
7 ASW #2              84100000-84380000    500000-780000    Extern   2.621.440 B
```

Die Kette ist **nicht adressgeordnet**: sie beginnt bei `0x80018000` und läuft rückwärts durch
PMU0, dann nach PMU1 und in den externen Flash. Das Speichermodell — 2 MiB PMU0 + 2 MiB PMU1 +
4 MiB extern — ist damit unabhängig von den Datenblättern bestätigt.

Genau diese Karte baut `TriCoreDump.SampleMed17Image()` als synthetisches Abbild nach; sie ist
die Positivkontrolle der Blockkette.

### Weitere lesbare Angaben

**Steuergerätevariante.** Im Dataset-Block (`0x60`) steht bei Blockversatz `+0x78` eine
schrägstrichgetrennte Zeichenkette, z. B. `34/1/EDC17_C46/5/P643//C643X5L8///`. Das ist eine
**feste Fundstelle** und damit deutlich belastbarer als freies Durchsuchen nach Zeichenketten.

**VAG-Identifikationsfeld.** Ebenfalls im Dataset-Block steht die VAG-Sicht auf das Gerät:
Teilenummern, Softwarestand, Motor — die Angaben, nach denen ein Diagnosetester fragt. Anker
ist die Systemkennung `EV_…`; alle Felder liegen bei festem Versatz dazu und sind rechts mit
Leerzeichen gefüllt:

| Versatz | Breite | Feld | Beispiel |
| --- | --- | --- | --- |
| `-0x0C` | 12 | Hardware-Teilenummer | `04L907309L` |
| `+0x00` | 14 | Systemkennung | `EV_ECM20TDI011` |
| `+0x0E` | 13 | Software-Teilenummer | `04L906027BB` |
| `+0x1B` | 6 | Index | `005001` |
| `+0x22` | 13 | Software-Teilenummer, nochmals | gleicher Wert |
| `+0x2F` | 4 | **Softwarestand** | `7155` |
| `+0x33` | 22 | Freitext, meist leer | `MED 17.1.62` |
| `+0x49` | 21 | Motorbezeichnung | `R4 2.0l TDI` |
| `+0x5E` | 5×n | Motorkennbuchstaben | `DAZA`, `----` = Leerplatz |

Eine **VAG-Teilenummer** hat die Form „drei Zeichen Fahrzeugkennung, Baugruppe, drei Ziffern,
null bis zwei Buchstaben": `04L906027BB`, `03L906018B`, `7P0907401`. Die Baugruppe ist auf
`906`, `907`, `910` und `997` eingegrenzt — darunter laufen Motorsteuergeräte. Genau diese
Stelle trennt eine Teilenummer von einer gleich langen Bosch-Nummer wie `1037540589`.

Der Fund gilt erst, wenn **jede** Prüfung zutrifft: Systemkennung in Form und Breite,
Softwarestand aus genau vier Ziffern, Index aus sechs, beide Teilenummern im VAG-Schema — und
die zwei Ablagen der Software-Teilenummer müssen übereinstimmen. Scheitert eine davon, gibt es
keine Angabe statt einer erfundenen.

An fünf Abbildern vermessen und in allen fünf bestätigt. Zwei davon lassen sich unabhängig
gegenprüfen: Ihre Dateinamen (`…_04L906027BB_7155_…`, `…_04L906026GP_3021_…`) nennen
Teilenummer und Stand, und beides deckt sich mit dem, was aus dem Abbild gelesen wird.

**CVN** (Calibration Verification Number). Eine CRC32 über mehrere Speicherbereiche, deren Lage
aus einer Konfigurationsstruktur im Abbild gelesen wird. Sie wird **gelesen und ausgewiesen** —
sie ist die Zahl, die die OBD-Diagnose zur Prüfung der Kalibrierung meldet. Wird die
Konfigurationsstruktur nicht gefunden, gibt es keinen Ratewert und keine Null, sondern
schlicht keine CVN.

> **Offener Punkt — es kann mehrere CVNs geben.** Der Bosch-Funktionsrahmen für MED17.5
> spricht im Kapitel zu OBD-Mode $09 durchgehend im Plural: „die Anzahl der Antwortbotschaften
> ist abhängig von der Anzahl der CVNunknowns“, über CAN werden „alle CVNunknowns in einer
> einzigen Botschaft gesendet“. Dieser Leser bricht beim ersten Fund ab und gibt genau eine
> zurück. Ob die weiteren überhaupt eine eigene Konfigurationsstruktur dieser Form im Abbild
> haben, ist ungeprüft — in den fünf ausgewerteten Abbildern wurde nicht danach gesucht. Der
> ausgewiesene Wert ist belegt, seine Vollständigkeit nicht. Aus derselben Quelle: eine CVN
> kann kürzer als vier Byte sein; der hier gerechnete Wert ist immer eine volle CRC32.

### Herkunft dieser Formatkenntnis

Die Struktur stammt aus **zwei unabhängig entstandenen Community-Werkzeugen**, die im Blockkopf
exakt übereinstimmen:

* [`github.com/fanyi3315/bosch-med17-block-reader`](https://github.com/fanyi3315/bosch-med17-block-reader) —
  läuft die Blockkette ab; ausgewertet wurden Quelltext und die vollständige Beispielausgabe
  über alle acht Blöcke eines 8-MiB-MED17-Abbilds
* ein „MEDC17 Checksum Analyzer & Corrector“ — kennt zusätzlich die Prüfsummenstrukturen, die
  drei Algorithmen, den `0xDEADBEEF`-Abschluss, das OTP-Flag und die CVN

Beide sind **Reverse Engineering an einem einzigen Abbild, kein Herstellerdokument.** Die Namen
der Blockarten und der Algorithmen (`SB_CRC32_ALGO_E` usw.) sind eine Deutung dieser Werkzeuge,
keine Bosch-Angabe. Ob EDC17 denselben Kopf trägt wie MED17, ist plausibel, aber unbelegt.

Deshalb setzt dieses Werkzeug die Struktur **nicht voraus, sondern prüft sie an jedem einzelnen
Kopf nach** — und rechnet die Prüfsummen nach. Ohne gültigen Block gibt es keine Blöcke, nur
Bereiche.

Zwei Fehler des Vorbilds werden ausdrücklich **nicht** übernommen:

* Es löst Zeiger relativ zur Bank des Blocks auf und greift deshalb bei einer PMU1-Adresse in
  der Tabelle eines PMU0-Blocks ins Leere. Hier wird **jeder** Zeiger global über das Layout
  aufgelöst.
* Es deutet die Einträge der Zeigertabellen als Adressen. Sie sind gemischt: Flash-, Extern- und
  LDRAM-Adressen, Wächterwerte `0` und `0xFFFFFFFF` und schlichte Zahlen ohne Adresscharakter.
  Hier werden sie **roh ausgegeben, ohne Deutung**, und kein Eintrag wird als Adresse geprüft.

### Der Analogieschluss, der nicht galt

Hier stand einmal eine Annahme: `csEnd` zeige — wie `blockEnd` — auf das **letzte Wort** des
geprüften Bereichs, die Länge sei `csEnd - csStart + 4`. Für `blockEnd` war das nachgerechnet,
für `csEnd` war es der Analogieschluss. Er ist falsch.

Die beiden Felder folgen verschiedenen Konventionen. `blockEnd` zeigt auf das letzte **Wort**,
den `0xDEADBEEF`-Abschluss. `csEnd` zeigt auf das letzte **Byte** des geprüften Bereichs; die
Länge ist `csEnd - csStart + 1`. Der erste Block eines EDC17-Abbilds macht es unmittelbar
sichtbar: `csStart` 0x80000000, `csEnd` 0x8000FFFB, `0xDEADBEEF` bei Datei-Offset 0xFFFC —
0xFFFB ist keine Wortgrenze, und der geprüfte Bereich ist der Blockinhalt ohne den Abschluss.

Über fünf EDC17-Abbilder gehen mit der Byte-Deutung 57 von 59 Prüfsummenstrukturen auf, mit der
Wort-Deutung 11. Dass der Fehler so lange unentdeckt blieb, hat einen Grund: die 11 waren
ausschließlich `ADD32`-Fälle, deren Wortschleife die drei überzähligen Bytes gar nicht liest.
Ein Verfahren maskierte den Fehler der anderen beiden.

Die zwei zunächst verbliebenen Abweichungen waren `ADD16`-Strukturen und sind inzwischen
geklärt: Bei ADD16 zählt das **letzte** Wort des Bereichs um 16 Bit nach links geschoben, alle
übrigen normal. Ohne die Verschiebung stimmen die unteren 16 Bit der Summe, die oberen nicht —
und genau die Differenz ist das letzte Wort. Damit gehen **alle 59 Strukturen** der fünf
Abbilder auf, bis auf den Dataset-Block eines nachweislich getunten Abbilds.

---

## VOLVOECU-Blöcke

Im Low-Flash liegen ausführbare Module in einem eigenen Format — weder Sektor noch
EEPROM-Datensatz:

```
+0x000  ASCII „VOLVOECU“                        Magic
+0x008  Startadresse des Codes (4 B, big endian, CPU-Adressraum)
+0x00C  Adresse des CRC-Trailers (4 B, big endian, CPU-Adressraum)
+0x400  Beginn des PowerPC/VLE-Codes
+0x4F0  Teilenummer (11 Zeichen)
CRC-Pos CRC32 (4 B, big endian) über den Block vom Anfang bis hierher
```

Anders als beim Sektorformat läuft die Prüfsumme über den **gesamten** Block einschließlich Kopf,
nicht erst ab `0x0F8`.

Da der Code stets `0x400` hinter dem Kopf beginnt, gilt `CpuStart = CodeStart - 0x400`. Damit
lässt sich die Containerabbildung unabhängig gegenprüfen: Der Wert muss dem CPU-Anfang der
Partition entsprechen, in der der Block liegt.

---

## Prüfsummen korrigieren

**Nur für die Volvo-Geräte.** Bei TriCore-Abbildern werden Prüfsummen gerechnet und gemeldet,
aber nicht gestellt: `Speichern`, `Prüfsumme korrigieren` und `Ersetzen…` sind abgeschaltet, und
die Bibliothek wirft `InvalidOperationException`. Die Prüfsummenkorrektur wäre mit dem
vorliegenden Material technisch möglich, ist aber nicht Aufgabe dieses Werkzeugs.

Ein Sektor mit abweichender CRC32 wird gemeldet, aber weiterhin gelesen, extrahiert und
geschrieben. Korrigieren geht einzeln oder für alle Sektoren auf einmal.

### Veraltete Prüfwertkopien

Das Steuergerät hält Kopien der Sektorprüfwerte in Tabellen — im NVM-Bereich und am Ende des
ASW-Codes. Wer nur den Trailer korrigiert, lässt diese Kopien veralten.

Nach jeder Korrektur sucht das Werkzeug deshalb den **alten** Wert im gesamten Abbild und fragt,
ob die Fundstellen mitgezogen werden sollen — mit dem ausdrücklichen Vorbehalt, dass sich aus dem
Abbild nicht ableiten lässt, ob das Steuergerät diese Kopien überhaupt prüft. Deshalb eine Frage
und keine Automatik; die Voreinstellung im Dialog ist **Nein**.

### Sektor ersetzen

Beim Ersetzen wird geprüft, ob die Ersatzdatei überhaupt bis zum nächsten Sektor bzw. Dateiende
passt und ob sie die Mindestlänge eines Sektors erreicht. Ist der neue Sektor kürzer, wird der
Rest bis zum bisherigen Ende auf `0xFF` gesetzt.

Die Prüfsumme wird nur dann neu berechnet, wenn die Ersatzdatei **selbst einen gültigen
Sektorkopf** trägt — Länge und CPU-Offset stammen dann aus diesem Kopf, nicht aus dem ersetzten
Sektor. Nennt die Ersatzdatei einen anderen CPU-Offset als der ersetzte Sektor, wird das
gemeldet.

### S-Record-Export

Sektoren lassen sich als Motorola-S-Record (S0/S3/S5/S7, 32-Bit-Adressen) mit den echten
CPU-Adressen exportieren — das Format, in dem die Original-Datensätze ausgeliefert werden
(siehe `f=...s3` im Sektorkopf). 32 Byte je Zeile, jede Zeile mit eigenem Prüfsummenbyte.

---

## Protokolldatei des Auslesegeräts

Liegt neben dem Abbild eine `.TXT`-Datei des Auslesegeräts, wird sie automatisch mitgelesen und
liefert Angaben, die nicht im Flash stehen: Hardware- und Softwarenummer, Plugin, Micro,
Fahrgestellnummer, Verbindungsart, Datum und Uhrzeit des Auslesens.

Gesucht wird nach dem Dateinamen des Abbilds sowie nach demselben Namen ohne die Endungen
`_Micro`, `_ExtFlash` oder `_Eeprom`, jeweils mit `.TXT` und `.txt`. Das Format ist
zeilenweise `Schlüssel: Wert`.

---

## Projektstruktur

```
VolvoSplitter.slnx                 Projektmappe (vier Projekte)

VolvoSplitter.csproj               WPF-Anwendung, net10.0-windows
├── App.xaml(.cs)                  Einstiegspunkt; reicht args[0] als Startdatei durch
├── MainWindow.xaml(.cs)           Hauptfenster, Sektorkarten, Aktionen
├── Theme.xaml                     Dunkles Farbschema und Steuerelementstile
├── Controls/FlashMap.cs           Maßstäbliche Adresskarte des Abbilds
└── Native/WindowTheme.cs          Dunkle Titelleiste über dwmapi.dll

VolvoSplitter.Cli/                 Stapelbetrieb, erzeugt „volvosplit“
└── Program.cs                     Argumentauswertung, Ordnerdurchlauf, Konsolenausgabe

VolvoSplitter.Core/                Analyse, ohne WPF — von Oberfläche und CLI gemeinsam genutzt
├── FlashDump.cs                   Laden, Analysieren, Extrahieren, Patchen, Speichern
├── EcuDetector.cs                 Prüfkette: welches Steuergerät liegt vor, mit Belegliste
├── EcuProfile.cs                  Gerätefamilie, Baustein, Bytereihenfolge, Container
├── FlashFormat.cs                 TRW-Formatkonstanten und Sektortabellen
├── TrwContainer.cs                Das TRW-Sektorformat: finden, zuordnen, lesen
├── FlashRegion.cs                 FlashRegion und RegionScanner mit den Heuristiken
├── PhysicalLayout.cs              Physische Blöcke, Datei-Offset ↔ CPU-Adresse
├── Mpc5777cLayout.cs              Die MPC5777C-Karte nach NXP-Referenzhandbuch
├── SectorInfo.cs                  Sektormodell samt Anzeigetexten
├── VolvoEcuBlock.cs               Parser für VOLVOECU-Module
├── ByteOrder.cs                   Wortzugriffe mit ausdrücklicher Bytereihenfolge
├── BinaryHeuristics.cs            Entropie, Wiederholung, Monotonie, belegte Bereiche
├── Hex.cs                         Adressdarstellung
├── SRecord.cs                     Motorola-S-Record-Ausgabe
├── EcuReport.cs                   Protokolldatei des Auslesegeräts
├── DumpReport.cs                  Textbefund für Oberfläche und CLI
├── Crc32.cs                       CRC32, Polynom 0xEDB88320
└── TriCore/
    ├── TriCoreDevice.cs           TC1796 und TC1797 mit Sektorkarte, übrige nur benannt
    ├── TriCoreLayout.cs           PMU0/PMU1, externer Flash, DFLASH auf die Datei abgebildet
    ├── BoschBlockChain.cs         Blockkopf lesen und prüfen, Kette laufen, Lücken, CVN
    ├── BoschChecksum.cs           Die drei Prüfalgorithmen und die CVN-CRC32
    ├── BoschIdentity.cs           Kennungen samt Fundort
    └── VagEcuCatalog.cs           Steuergerätetyp → Mikrocontroller (Nutzerangabe)

VolvoSplitter.Core.Tests/          126 xUnit-Tests
├── TestDump.cs                    Synthetische TRW-Abbilder
└── TriCoreDump.cs                 Synthetische TriCore-Abbilder mit gültiger Blockkette
```

---

## Selbst bauen

Voraussetzung ist das **.NET 10 SDK**.

```bash
dotnet build VolvoSplitter.slnx -c Release
dotnet test  VolvoSplitter.Core.Tests/VolvoSplitter.Core.Tests.csproj
```

Starten:

```bash
dotnet run --project VolvoSplitter.csproj                        # Oberfläche (nur Windows)
dotnet run --project VolvoSplitter.Cli -- ~/dumps --out ~/ausgabe
```

Nur die Oberfläche zielt auf `net10.0-windows` mit `UseWPF` und verlangt deshalb Windows.
`VolvoSplitter.Core`, `VolvoSplitter.Cli` und die Tests zielen auf `net10.0` und bauen auf jeder
Plattform — die gesamte Projektmappe auf einmal baut allerdings nur unter Windows.

Die Tests kommen ohne echte Steuergerätedaten aus: `TestDump.cs` erzeugt gültige TRW-Abbilder mit
Kopf, Endadresse und passender Prüfsumme, `TriCoreDump.cs` vollständige TriCore-Abbilder mit
gültiger Blockkette. Deren Prüfsummen stimmen wirklich — je Struktur wird ein Stellwort an das
Bereichsende gerechnet; für die CRC32 geht das ohne Suche, weil die 32 Schiebeschritte über vier
angehängte Bytes umkehrbar sind.

Geprüft werden unter anderem die CRC32 gegen einen Referenzalgorithmus und den Vektor
`123456789`, das Auffinden verschobener Sektoren, die Reparatur und ihre Nebenwirkungen, das
Sektorersetzen mit abweichendem CPU-Offset, die Containerabbildung des MPC5777C, der
VOLVOECU-Parser, die S-Record-Zeilenprüfsummen, die Sektorsummen von TC1796 und TC1797, die
komplette Blockkarte des ausgewerteten MED17-Abbilds und die drei Bosch-Prüfalgorithmen.

Und ausdrücklich auch, was das Werkzeug **nicht** behauptet: dass der Regionsscanner keine
Verschlüsselung behauptet, dass ein Bereich hinter dem Large Flash nicht automatisch EEPROM ist,
dass ein TriCore-Anhang nicht automatisch DFLASH ist, dass ohne geprüfte Kette kein Block
`Verified` wird, dass ein Kalibrierungskandidat ein Kandidat bleibt, und dass bei 2 MiB ohne
Steuergerätekennung kein Baustein gewählt wird.

**Grenze der Prüfung, ausdrücklich:** im Repository liegen keine echten Steuergeräte-Abbilder,
weder Volvo noch VAG. Alles oben Genannte prüft die Mechanik gegen synthetische Dateien, nicht
die Trefferquote in der Wirklichkeit. Ein Ordner `fixtures/` steht in `.gitignore`, damit echte
Abbilder — sie enthalten eine VIN — nicht versehentlich eingecheckt werden.

---

## Referenzunterlagen

Die Unterlagen liegen nicht im Repository — es sind Herstellerdokumente, die NXP und
Infineon selbst veröffentlichen. Hier stehen die Fundstellen, auf denen die Interpretation
beruht:

| Quelle | Verwendet für |
| --- | --- |
| [NXP MPC5777C Reference Manual](https://www.nxp.com/docs/en/reference-manual/MPC5777CRM.pdf) | Kapitel 4, Tabellen 4-2 und 4-3 — physisches Speicherlayout, UTEST-Inhalt |
| [NXP MPC5777C Factsheet](https://www.nxp.com/docs/en/fact-sheet/MPC5777CFS.pdf) | Datenblatt des MPC5777C |
| [NXP AN4868](https://www.nxp.com/docs/en/application-note/AN4868.pdf) | EEPROM-Emulation — Blockstatus-Doppelwörter, angehängte Records |
| Infineon TC1796 Data Sheet | PFLASH- und DFLASH-Größen, Sektoreinteilung, Adressen |
| Infineon TC1797 Data Sheet | PMU0/PMU1, Sektoreinteilung, DFLASH, EBU |
| [`fanyi3315/bosch-med17-block-reader`](https://github.com/fanyi3315/bosch-med17-block-reader) | Blockkopf und Verkettung — Community-Reverse-Engineering an *einem* Abbild |
| „MEDC17 Checksum Analyzer & Corrector v1.1“ | Prüfsummenstrukturen, die drei Algorithmen, OTP-Flag, CVN |

Die beiden letzten sind **keine Herstellerdokumente**. Ihre Nutzung wurde ausdrücklich
freigegeben; sie werden hier als Herkunftsnachweis genannt, nicht als Lizenzbedingung. Wie mit
dieser Unsicherheit umgegangen wird, steht unter
[Herkunft dieser Formatkenntnis](#herkunft-dieser-formatkenntnis).

---

## Änderungen

Siehe [CHANGELOG.md](CHANGELOG.md).

---

## Lizenz

[MIT](LICENSE) — © 2026 ubbg. Die Software wird ohne Gewähr bereitgestellt.

Ausgenommen sind die NXP-Dokumente im Repository (`AN4868.pdf`, `MPC5777CFS.pdf`,
`MPC5777CRM, MPC5777C Reference Manual.pdf`): sie stammen von NXP Semiconductors, unterliegen
deren Bedingungen und werden von der MIT-Lizenz nicht erfasst.

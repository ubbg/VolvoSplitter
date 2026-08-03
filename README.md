# Volvo File Splitter

[![CI](https://github.com/ubbg/VolvoSplitter/actions/workflows/ci.yml/badge.svg)](https://github.com/ubbg/VolvoSplitter/actions/workflows/ci.yml)

Zerlegt Flash-Abbilder von Volvo-Motorsteuergeräten (TRW **EMS2.3** und **EMS2.4**) in ihre
Sektoren, prüft die CRC32-Prüfsummen, ordnet ein, was zwischen den Sektoren liegt, und schreibt
geänderte Abbilder wieder zurück.

Drei Wege in dieselbe Analyse:

* **Oberfläche** — WPF-Anwendung mit Adresskarte, Sektorkarten und Textbefund
* **Kommandozeile** — `volvosplit` für den Stapelbetrieb über ganze Ordner
* **Bibliothek** — `VolvoSplitter.Core`, ohne WPF-Abhängigkeit und plattformunabhängig

---

## Hinweis

Das Werkzeug liest, zerlegt und verändert Steuergeräte-Abbilder. Es ist für Analyse, Diagnose
und Instandsetzung gedacht; was mit den Ergebnissen geschieht, liegt in deiner Verantwortung.

Die Originaldatei wird **nie überschrieben**: geladen wird in eine Arbeitskopie im Speicher,
und der Speichern-Dialog schlägt grundsätzlich `<name>_mod<endung>` vor.

---

## Überblick

Ein ausgelesenes Steuergerät liegt als eine einzige große `.mpc`- oder `.bin`-Datei vor — bei
EMS2.4 rund 8,5 MiB. Darin stecken mehrere unabhängige Datensätze mit eigenem Kopf und eigener
Prüfsumme, dazwischen Programmcode ohne Kopf, laufzeitveränderte NVM-Daten und gelöschtes Flash.

Der Volvo File Splitter macht daraus:

* **Sektoren** — Parameter, ASW und Kalibrierung, jeweils als eigene Rohdatei mit der Teilenummer
  im Dateinamen, dazu `CRC ok` oder die Abweichung
* **Bereiche ohne Sektorkopf** — eingeordnet nach Inhalt, mit Angabe, wie belastbar die
  Einordnung ist
* **Physische Blöcke** — beim MPC5777C die Zuordnung Datei-Offset → CPU-Adresse für Large Flash,
  Low/Mid-Blöcke und UTEST
* **Fahrzeugdaten** — VIN und Fahrgestellnummer aus dem Parameter-Sektor
* **Textbefund** — alles davon als `bericht.txt` bzw. in der Zwischenablage

---

## Funktionen

### Oberfläche

* Adresskarte des gesamten Abbilds, maßstäblich — Sektoren, Code, opake Blöcke und NVM-Daten
  farblich unterschieden (`Controls/FlashMap.cs`)
* Je Sektor eine Karte mit Teilenummer, Datei- und CPU-Adressbereich, Größe, Prüfsummenstand,
  Kopfdaten (`p=`, `d=`, `t=`, `f=`, `b=`) und Hinweis, falls der Sektor nicht an der
  Standardadresse liegt
* Einzelne oder alle Prüfsummen korrigieren, inklusive Nachfrage zu veralteten Prüfwertkopien
* Sektor extrahieren, als Motorola-S-Record exportieren oder durch eine Datei ersetzen
* Bereiche ohne Kopf einzeln herausschreiben
* Befund in die Zwischenablage oder als Textdatei
* Dunkles Thema samt dunkler Titelleiste (`Native/WindowTheme.cs`)

### Kommandozeile

* Ganze Ordner in einem Lauf, alle Sektoren als Rohdateien plus `bericht.txt`
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

Der Dateidialog filtert auf `*.mpc;*.bin`.

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
Volvo File Splitter — Stapelbetrieb

  volvosplit <Datei|Ordner> [weitere...] [Optionen]

Zerlegt jedes Flash-Abbild in seine Sektoren, schreibt sie als Rohdateien
und legt einen Textbefund daneben.

Optionen:
  -f, --fixed         Nur die fest verdrahteten Standardadressen lesen
  -o, --out <Ordner>  Zielordner (Standard: neben dem Abbild)
  -h, --help          Diese Hilfe

Beispiele:
  volvosplit C:\Dumps\ecu_Micro.mpc
  volvosplit C:\Dumps --out C:\Ausgabe
```

| Option | Wirkung |
| --- | --- |
| `-f`, `--fixed` | Nur die Standardadressen lesen, kein Suchlauf über das ganze Abbild |
| `-o`, `--out <Ordner>` | Gemeinsamer Zielordner statt eines Ordners neben jedem Abbild |
| `-h`, `--help`, `/?` | Hilfe ausgeben |

**Ordner als Argument** werden **nicht rekursiv** nach `*.mpc` und `*.bin` durchsucht — nur die
oberste Ebene. Mehrfach genannte Dateien werden ohne Rücksicht auf Groß- und Kleinschreibung
entdoppelt.

**Zielordner:** ohne `--out` entsteht `<abbildname>_sektoren/` neben dem Abbild, mit `--out`
stattdessen `<zielordner>/<abbildname>/`. In beiden Fällen landet dort zusätzlich `bericht.txt`.

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

Console.WriteLine($"{FlashFormat.FamilyName(dump.Family)} · {FlashFormat.MicroName(dump.Family)}");
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
| `FlashDump.Load(path, fixedAddressesOnly = false)` | Abbild aus einer Datei laden und analysieren; sucht auch die Protokolldatei daneben |
| `FlashDump.FromBytes(data, name, fixedAddressesOnly = false)` | Abbild aus Bytes im Speicher — für Aufrufer, die die Daten schon halten, und für Tests |
| `dump.Sectors` | Gefundene und fehlende Sektoren (`SectorInfo`) |
| `dump.Regions` | Belegte Bereiche ohne Sektorkopf (`FlashRegion`) |
| `dump.Partitions` | Physische Blöcke mit Zustand (`PartitionInfo`) |
| `dump.Vehicle` | VIN, Fahrgestellnummer, Hersteller — oder `null` |
| `dump.Report` | Protokolldatei des Auslesegeräts (`EcuReport`) — oder `null` |
| `dump.ExtractSector(sector, dir)` | Sektor als Rohdatei schreiben |
| `dump.ExportSRecord(sector, path)` | Sektor als Motorola-S-Record mit echten CPU-Adressen |
| `dump.RepairCrc(sector)` | Prüfsumme neu berechnen; meldet die verbliebenen alten Kopien zurück |
| `dump.ReplaceSector(sector, bytes, repairCrc)` | Sektor durch eine Datei ersetzen |
| `dump.Save(path)` | Arbeitskopie schreiben |
| `DumpReport.Build(dump)` | Vollständiger Textbefund |

---

## Sektorformat

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

Die Familie wird allein an der Dateigröße erkannt: bis `0x500000` EMS2.3, darüber EMS2.4.

| Familie | Micro | Erkennung | Parameter | ASW | Kalibrierung |
| --- | --- | --- | --- | --- | --- |
| EMS2.3 | MPC5674F | Abbild ≤ `0x500000` | `0x060000` | `0x100000` | `0x380000` |
| EMS2.4 | MPC5777C | Abbild > `0x500000` | `0x7C0000` → CPU `0xFC0000` | `0x200000` → CPU `0xA00000` | `0x740000` → CPU `0xF40000` |

Bei EMS2.3 sind Datei-Offset und CPU-Adresse identisch; bei EMS2.4 liegt der Large Flash im
CPU-Adressraum ab `0x800000`, weshalb sich beide Spalten unterscheiden.

Fehlt eine Rolle im Abbild, wird sie trotzdem aufgeführt — mit der Begründung, was an der
Adresse tatsächlich steht, statt eines pauschalen „leer oder verschlüsselt“:

```
Kein Sektorkopf bei 0x200000 — dort steht Programmcode im Klartext, aber ohne Sektorkopf
(0x200000 – 0x33F000, 1.306.624 B)
```

---

## Physisches MPC5777C-Layout

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
* **Quelldateipfade** (`src/`, `../`) aus Assert-Strings — belegen unverschlüsselten Code
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

---

## VOLVOECU-Blöcke

Im Low-Flash liegen ausführbare Module in einem eigenen Format — weder Sektor noch
EEPROM-Datensatz:

```
+0x000  ASCII „VOLVOECU"                        Magic
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

VolvoSplitter.Cli/                 Stapelbetrieb, erzeugt „volvosplit"
└── Program.cs                     Argumentauswertung, Ordnerdurchlauf, Konsolenausgabe

VolvoSplitter.Core/                Analyse, ohne WPF — von Oberfläche und CLI gemeinsam genutzt
├── FlashDump.cs                   Laden, Analysieren, Extrahieren, Patchen, Speichern
├── FlashFormat.cs                 Formatkonstanten, Sektortabellen, Familienerkennung
├── FlashRegion.cs                 FlashRegion und RegionScanner mit den Heuristiken
├── Mpc5777cLayout.cs              Physische Blöcke, Datei-Offset → CPU-Adresse
├── SectorInfo.cs                  Sektormodell samt Anzeigetexten
├── VolvoEcuBlock.cs               Parser für VOLVOECU-Module
├── SRecord.cs                     Motorola-S-Record-Ausgabe
├── EcuReport.cs                   Protokolldatei des Auslesegeräts
├── DumpReport.cs                  Textbefund für Oberfläche und CLI
└── Crc32.cs                       CRC32, Polynom 0xEDB88320

VolvoSplitter.Core.Tests/          49 xUnit-Tests
└── TestDump.cs                    Synthetische Abbilder — keine echten Dumps nötig
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

Die Tests kommen ohne echte Steuergerätedaten aus: `TestDump.cs` erzeugt gültige Abbilder mit
Kopf, Endadresse und passender Prüfsumme. Geprüft werden unter anderem die CRC32 gegen einen
Referenzalgorithmus und den Vektor `123456789`, das Auffinden verschobener Sektoren, die
Reparatur und ihre Nebenwirkungen, das Sektorersetzen mit abweichendem CPU-Offset, die
Containerabbildung des MPC5777C, der VOLVOECU-Parser, die S-Record-Zeilenprüfsummen — und
ausdrücklich auch, dass der Regionsscanner **keine** Verschlüsselung behauptet und einen Bereich
hinter dem Large Flash **nicht** automatisch als EEPROM einordnet.

---

## Referenzunterlagen

Im Repository liegen die NXP-Dokumente, auf denen die Interpretation beruht:

| Datei | Verwendet für |
| --- | --- |
| `MPC5777CRM, MPC5777C Reference Manual.pdf` | Kapitel 4, Tabellen 4-2 und 4-3 — physisches Speicherlayout, UTEST-Inhalt |
| `MPC5777CFS.pdf` | Datenblatt des MPC5777C |
| `AN4868.pdf` | EEPROM-Emulation — Blockstatus-Doppelwörter, angehängte Records |

---

## Änderungen

Siehe [CHANGELOG.md](CHANGELOG.md).

---

## Lizenz

[MIT](LICENSE) — © 2026 ubbg. Die Software wird ohne Gewähr bereitgestellt.

Ausgenommen sind die NXP-Dokumente im Repository (`AN4868.pdf`, `MPC5777CFS.pdf`,
`MPC5777CRM, MPC5777C Reference Manual.pdf`): sie stammen von NXP Semiconductors, unterliegen
deren Bedingungen und werden von der MIT-Lizenz nicht erfasst.

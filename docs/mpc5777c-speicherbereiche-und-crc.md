# MPC5777C-MPC-Dumps: Speicherbereiche, NVM und CRC-Beziehungen

**Status:** technische Untersuchung für das Dev-Team  
**Stand:** 2026-08-03  
**Betroffene Komponente:** `VolvoSplitter.Core` / `RegionScanner`  
**Codeänderungen aus diesem Bericht:** keine

## Kurzfassung

Die aktuelle Anwendung klassifiziert bei EMS2.4 pauschal jeden belegten Bereich ab Datei-Offset `0x800000` als „angehängtes EEPROM“. Diese Einordnung ist zu grob.

Die untersuchten MPC5777C-Dumps bestehen sehr wahrscheinlich aus drei hintereinander abgelegten physischen Adressräumen:

1. 8 MiB Large Flash aus dem CPU-Adressraum `0x00800000–0x00FFFFFF`,
2. vier 64-KiB-Low/Mid-Flash-Blöcke aus `0x00000000–0x0003FFFF`,
3. ein 16-KiB-UTEST-Abbild aus `0x00400000–0x00403FFF`.

Die Zuordnung wird durch die Dateigröße `0x844000`, die NXP-Speicherkarte und interne Adressfelder im Dump stark gestützt. Sie ist dennoch eine Interpretation des proprietären `.MPC`-Containerlayouts; NXP dokumentiert dieses Dateiformat nicht.

Der Bereich bei Datei-Offset `0x810000` ist kein gewöhnlicher EEPROM-Datensatz. Er ist ein ausführbares `VOLVOECU`-Modul mit eigenem Header, PowerPC-Code, Teilenummer und gültiger CRC32. Der Bereich bei `0x820000` enthält dagegen tatsächlich laufzeitveränderte NVM-Daten, Prüfsummenkopien und angehängte `UPTIME`-Records.

Damit ist die Vermutung zum „Verschieben“ teilweise bestätigt: Logische EEPROM-Records können neu angehängt und bei einem Block-Swap in einen anderen physischen Block kopiert werden. Die Flash-Sektoren selbst verschieben sich nicht. Der fehlerhafte Parameter-CRC im D16-Dump liegt außerdem in einem anderen, nicht überlappenden Bereich und wird durch die `UPTIME`-Records nicht unmittelbar erklärt.

## Untersuchungsgrundlage

| Datei | Größe | SHA-256 |
|---|---:|---|
| `FF605001091_Micro.MPC` | `0x844000` | `06490344AAF2B5D3BEEF343375453C2B05CEBFB21883AF10AC99C6B7ED795AD4` |
| `FL442011391_Micro.MPC` | `0x844000` | `283F565342546899A971640CD7F6A186EE2D73BFE93602297DAA04FA6D5A26E8` |

Analysiert wurden Header, belegte Bereiche, Entropie, ASCII-Strings, PowerPC/VLE-Muster, Sektor-CRCs, CRC-Kopien, interne Adressfelder und Unterschiede zwischen beiden Dumps. Alle Binärprüfungen waren read-only.

## Primärquellen

- [NXP MPC5777C Factsheet, Rev. 1](https://www.nxp.com/docs/en/fact-sheet/MPC5777CFS.pdf), Seite 1: 8,25 MiB Flash, darunter vier 64-KiB-Blöcke für EEPROM.
- [NXP MPC5777C Reference Manual](https://www.nxp.com/docs/en/reference-manual/MPC5777CRM.pdf), Kapitel 4, Tabelle 4-2, Seiten 115–116: vier Low/Mid-Blöcke bei `0x00000000–0x0003FFFF`, UTEST bei `0x00400000–0x00403FFF` und 8 MiB Large Flash bei `0x00800000–0x00FFFFFF`.
- [NXP MPC5777C Reference Manual Addendum](https://www.nxp.com/docs/en/reference-manual/MPC5777CRMAD.pdf), aktualisierte UTEST-/DCF-Tabelle: UTEST enthält Test-, Security-, Konfigurations- und Customer-OTP-Daten.
- [NXP AN4868: EEPROM Emulation with MPC55xx, MPC56xx and MPC57xx](https://www.nxp.com/docs/en/application-note/AN4868.pdf), Kapitel 3: Records werden angehängt; bei vollem aktivem Block werden die neuesten gültigen Records in einen anderen Block kopiert und der alte Block gelöscht.

AN4868 beschreibt ein NXP-Referenzverfahren. Ohne vollständige Identifikation der Volvo-spezifischen Record- und Statusfelder ist nicht bewiesen, dass Volvo dieses Layout unverändert verwendet.

## Rekonstruiertes MPC-Dateilayout

### Container-zu-CPU-Abbildung

| Datei-Offset | Wahrscheinlicher CPU-Adressraum | NXP-Blocktyp | Sicherheit |
|---|---|---|---|
| `0x000000–0x7FFFFF` | `0x00800000–0x00FFFFFF` | 8 MiB Large Flash | hoch |
| `0x800000–0x80FFFF` | `0x00000000–0x0000FFFF` | Low, Partition 0 | hoch, aber Containerabbildung ist Inferenz |
| `0x810000–0x81FFFF` | `0x00010000–0x0001FFFF` | Low, Partition 1 | hoch, durch interne Adressen bestätigt |
| `0x820000–0x82FFFF` | `0x00020000–0x0002FFFF` | Mid, Partition 2 | hoch, aber Containerabbildung ist Inferenz |
| `0x830000–0x83FFFF` | `0x00030000–0x0003FFFF` | Mid, Partition 3 | hoch, aber Containerabbildung ist Inferenz |
| `0x840000–0x843FFF` | `0x00400000–0x00403FFF` | UTEST/DCF/OTP | mittel bis hoch; im Beispiel vollständig `0xFF` |

Die erste Zeile wird bereits indirekt vom Projekt verwendet: Ein Sektor bei Datei-Offset `0x740000` nennt beispielsweise den CPU-Offset `0xF40000`, also genau `+0x800000`.

### Belegte kopflose Bereiche in `FF605001091`

| Datei-Offset | CPU-Adresse | Bisherige Anzeige | Neue Einordnung |
|---|---|---|---|
| `0x000000–0x00234D` | `0x800000–0x80234D` | verschlüsselt | opaker Boot-/Security-Bereich; Verschlüsselung oder Kompression nicht abschließend unterscheidbar |
| `0x200000–0x21F81D` | `0xA00000–0xA1F81D` | verschlüsselt / ASW fehlt | opaker ASW-Präfix am erwarteten ASW-Start |
| `0x240000–0x477F40` | `0xA40000–0xC77F40` | Programmcode | PowerPC/VLE-ASW-Code ohne `v=1;a=`-Sektorkopf |
| `0x810000–0x81E27C` | `0x010000–0x01E27C` | EEPROM | `VOLVOECU`-Codeblock mit eigenem Format und gültiger CRC |
| `0x820000–0x82C6B8` | `0x020000–0x02C6B8` | EEPROM | aktiver NVM-/Datenblock mit Prüfsummenkopien und Laufzeit-Records |

Partition 0 (`0x800000–0x80FFFF`), Partition 3 (`0x830000–0x83FFFF`) und der vermutete UTEST-Bereich (`0x840000–0x843FFF`) sind in beiden Beispielen gelöscht (`0xFF`).

## Detailbefunde

### 1. Opaque Bereiche im Large Flash

Die Blöcke bei `0x000000` und `0x200000` haben eine Entropie von ungefähr 7,98 beziehungsweise 8,00 Bit pro Byte, enthalten keine belastbaren Klartextstrukturen und beginnen mit denselben 16 Bytes:

```text
72 0C 19 EB F9 6F 17 92 EA A7 10 27 05 59 F2 35
```

Das spricht für ein gemeinsames proprietäres Container-, Kompressions- oder Verschlüsselungsformat. Hohe Entropie allein beweist keine Verschlüsselung. Die jeweiligen letzten vier Bytes sind keine Standard-CRC32 über den davorliegenden vollständigen Bereich.

Empfohlene UI-Bezeichnung: **„Opaker/geschützter Block“** mit Begründung „hohe Entropie, kein bekannter Header“. „Verschlüsselt“ sollte nur als Möglichkeit, nicht als gesicherte Tatsache erscheinen.

### 2. Klarer ASW-Code ab `0x240000`

Der Bereich enthält PowerPC/VLE-Instruktionsmuster, Quellpfade und Assert-Strings. Im Trailer stehen mehrere referenzierte Prüfwerte:

- PBC-CRC `0xAFC31623` zusätzlich bei Datei-Offset `0x477F24`.
- Wert `0xFEA03A59` am Blockende bei `0x477F3C` und als Kopie bei `0x820008`.

`0xFEA03A59` verhält sich wie ein ASW-Prüfwert oder eine Image-ID, entspricht aber keiner getesteten Standardvariante CRC32, JAMCRC, CRC32/MPEG-2, CRC32/BZIP2 oder CRC32C über die naheliegenden Codebereiche. Der Algorithmus und der exakte Schutzbereich bleiben offen.

### 3. `VOLVOECU`-Block bei `0x810000`

Der Block besitzt ein eigenes, reproduzierbar interpretierbares Format:

| Relativer Offset | Wert | Bedeutung |
|---:|---|---|
| `+0x000` | ASCII `VOLVOECU` | Magic |
| `+0x008` | `0x00010400` | Startadresse des ausführbaren Inhalts |
| `+0x00C` | `0x0001E278` | Endadresse / Position des CRC-Trailers |
| `+0x400` | PowerPC/VLE-Code | Beginn ausführbarer Inhalt |
| `+0x4F0` | `23310625P01` | Teilenummer in `FF605001091` |
| `+0xE278` | `0xAE07B7BC` | gespeicherte CRC32 |

Die Standard-CRC32 über `[0x810000, 0x81E278)` ergibt exakt `0xAE07B7BC`. Im D16-Dump liegt dasselbe Format an denselben Offsets; dort lautet die Teilenummer `23504791P02` und die gültige CRC `0xA4475CA6`.

Zusätzliche Strings wie `ISO_DIAG_INFO_FOR_APPL` und `ISO_DIAG_INFO_FOR_BOOT` bestätigen ausführbaren Diagnose-/Boot-Code. Dieser Block darf nicht pauschal als EEPROM-Datenbank behandelt werden.

### 4. Laufzeitveränderter NVM-Block bei `0x820000`

Der Block enthält statische Tabellen, sektorbezogene Prüfwerte und am belegten Ende fortlaufende Betriebsrecords:

- `FF605001091`: belegt bis `0x82C6B8`; Strings `UPTIME 20210414`, `UPTIME 20210415`, `UPTIME 20210416`.
- `FL442011391`: belegt bis `0x82ED68`; Strings `UPTIME 20260731`, `UPTIME 20260801`.

Die ersten vier Big-Endian-Wörter lauten:

| Datei | `+0x00` | `+0x04` | `+0x08` | `+0x0C` |
|---|---:|---:|---:|---:|
| `FF605001091` | `00000F53` | `F1C259BC` | `FEA03A59` | `8F20D490` |
| `FL442011391` | `0000012D` | `1E004F7F` | `97DE0AE4` | `9F7AE692` |

Gesichert sind folgende Beziehungen:

- `FF605001091 +0x0C = 0x8F20D490`: CRC des CAL-Sektors.
- `FL442011391 +0x08 = 0x97DE0AE4`: CRC des ASW-Sektors.
- `FL442011391 +0x0C = 0x9F7AE692`: CRC des CAL-Sektors.
- `FF605001091 +0x08 = 0xFEA03A59`: identisch mit dem letzten Wort des klaren Programmcodeblocks.

Die Bedeutungen der Wörter bei `+0x00` und `+0x04` sind noch nicht geklärt. Eine CRC32 über naheliegende Bereiche des NVM-Blocks reproduziert `+0x04` nicht.

Die angehängten `UPTIME`-Einträge und die unterschiedlich weit belegten Blöcke sind direkter Beleg für Laufzeitänderungen. Sie sind außerdem mit dem von NXP beschriebenen Append-/Block-Swap-Modell vereinbar. Für eine vollständige Record-Dekodierung fehlen jedoch noch die Volvo-spezifischen Status-, ID-, Längen- und CRC-Felder.

## Auswirkungen auf das D16-Parameter-CRC-Problem

Für `FL442011391`, Parameter `26153871P01`, wurde reproduziert:

```text
Sektor:              0x7C0000–0x7C7AB0
CRC-Eingabebereich:  0x7C00F8–0x7C7AAB
gespeichert:         0x43A3B7ED
berechnet:           0x474CB9D4
```

Der NVM-Block beginnt erst bei `0x820000` und liegt damit außerhalb des Parameter-CRC-Bereichs. Das Anhängen von `UPTIME`-Records kann diese CRC nicht direkt verändern.

Zusätzliche Ausschlüsse aus der Diagnose:

- Kein anderer CRC-Start im Parametersektor ergibt mit dem deklarierten Ende `0x43A3B7ED`.
- Keine plausible nahe Endgrenze ergibt den gespeicherten Wert.
- Eine einzelne veränderte Byteposition oder zwei benachbarte Bytes können die CRC-Differenz mathematisch nicht erklären.
- Der gespeicherte Parameter-CRC erscheint nirgends ein zweites Mal im Dump.
- Derselbe CRC-Algorithmus validiert PBC, ASW und CAL im D16-Dump sowie den EMS2.4-Parametersektor in `FF605001091`.

Wahrscheinlich bleiben damit mehrere nachträglich geänderte Parameterbytes, ein dauerhaft veralteter Trailerwert oder ein inkonsistenter Read während eines Schreibvorgangs. Für eine Entscheidung ist mindestens eine zweite Auslesung derselben ECU erforderlich.

## Problem im aktuellen Code

`FlashFormat.FlashSizeFor(Ems24)` liefert `0x800000`. `RegionScanner.Describe` klassifiziert anschließend jeden Bereich mit `start >= flashSize` sofort als `RegionKind.Eeprom`, ohne Inhalt oder physische Partition zu prüfen. Der Test `BeyondFlashSize_IsClassifiedAsEeprom` schreibt genau diese Annahme fest.

Konsequenzen:

- Der ausführbare `VOLVOECU`-Block wird falsch als EEPROM angezeigt.
- Low/Mid-Partitionen und UTEST können nicht getrennt dargestellt werden.
- Die UI zeigt Datei-Offsets, aber keine rekonstruierte CPU-Adresse dieser angehängten Partitionen.
- Ein zukünftiger belegter UTEST-/OTP-Bereich würde ebenfalls fälschlich als laufzeitverändertes EEPROM erscheinen.

## Empfohlene Umsetzung

### Priorität 0: korrektes physisches Layout

1. Ein explizites MPC5777C-Layoutmodell einführen, getrennt nach Large Flash, vier Low/Mid-Partitionen und UTEST.
2. Datei-Offset und rekonstruierte CPU-Adresse separat speichern und anzeigen.
3. Die Containerabbildung nur bei passender Familie und plausibler Dateigröße aktivieren; Unsicherheit im Modell ausdrücken.
4. `RegionKind.Eeprom` nicht mehr allein aus `start >= 0x800000` ableiten.

### Priorität 0: `VOLVOECU`-Parser

1. Magic `VOLVOECU` erkennen.
2. Start- und Endadresse bei `+0x08/+0x0C` validieren.
3. Teilenummern und klare Strings extrahieren.
4. Trailer-CRC32 über Header und Body prüfen.
5. UI-Bezeichnung beispielsweise „Low-Flash-Code / VOLVOECU-Modul“.

### Priorität 1: NVM-Analyse

1. Low/Mid-Blöcke zunächst als physische Partitionen anzeigen: gelöscht, Code, NVM-Daten oder unbekannt.
2. Bekannte Sektor-CRC-Kopien als Referenzen modellieren, statt lediglich gleiche 32-Bit-Werte global zu suchen.
3. Append-Records und mögliche Blockstatusfelder explorativ erkennen; Ergebnisse mit Confidence-Level versehen.
4. Eine Dump-Vergleichsfunktion ergänzen, die geänderte Bytebereiche und neue Records zwischen zwei Auslesungen derselben ECU meldet.

### Priorität 1: verständliche Evidenz in der UI

Die Anzeige sollte zwischen **gesichert**, **stark gestützt** und **unbekannt** unterscheiden. Beispiel:

- „PowerPC-Code erkannt; `VOLVOECU`-Header und CRC gültig“ — gesichert.
- „NVM-Block; Laufzeit-Records und CRC-Referenzen vorhanden“ — stark gestützt.
- „Opaker Block; hohe Entropie, Format unbekannt“ — gesichert bezüglich Beobachtung, offen bezüglich Verschlüsselung.

## Vorgeschlagene Regressionstests

Die Tests sollten kleine synthetische Fixtures oder freigegebene Ausschnitte verwenden, nicht zwingend vollständige proprietäre Dumps.

1. `Mpc5777cLayout_MapsLargeFlashAndLowMidPartitions`
2. `RegionBeyondLargeFlash_IsNotAutomaticallyEeprom`
3. `VolvoEcuHeader_MapsStartAndEndAddresses`
4. `VolvoEcuBlock_ValidatesWholeBlockCrc32`
5. `VolvoEcuBlock_IsClassifiedAsCode`
6. `LowMidErasedPartition_IsReportedAsErased`
7. `UtestPartition_IsDistinctFromEepromEmulation`
8. `NvmBlock_ReportsKnownSectorChecksumReferences`
9. `DumpComparison_ReportsAppendedRuntimeRecords`
10. `OpaqueRegion_DoesNotClaimEncryptionAsFact`

## Offene Fragen

1. Welches genaue Format verwenden die opaken Blöcke bei `0x000000` und `0x200000`?
2. Wie wird `0xFEA03A59` über den ASW-/Codebereich berechnet?
3. Was bedeuten die NVM-Headerwörter bei `0x820000` und `0x820004`?
4. Welche Recordstatus-, ID-, Längen- und Prüfwortfelder verwendet Volvo im NVM-Bereich?
5. Ändert sich der D16-Parametersektor zwischen zwei Reads derselben ECU oder bleibt nur sein Trailer dauerhaft veraltet?
6. Enthalten andere `.MPC`-Varianten die beiden CSE-High-Blöcke, oder lässt das Auslesegerät diese grundsätzlich aus?

## Empfohlener nächster Messversuch

Dieselbe D16-ECU dreimal auslesen:

1. zweimal unmittelbar hintereinander bei ruhender ECU,
2. einmal nach Motorbetrieb und sauberem ECU-Shutdown.

Anschließend getrennt vergleichen:

- Parametersektor `0x7C0000–0x7C7AAF`,
- Low/Mid-Partitionen `0x800000–0x83FFFF`,
- insbesondere das belegte Record-Ende in Partition 2.

Damit lässt sich unterscheiden zwischen instabilem Read, echten Parameteränderungen, Append-Records und einem Block-Swap.

## Sicherheitsnotiz

Low/Mid-Flash, UTEST und CSE-nahe Daten können Boot-, Security-, Diagnose- und Betriebszustand beeinflussen. Erkennen und Extrahieren ist unkritisch; automatisches Patchen oder Zurückschreiben sollte erst nach geklärtem Format, Schutzbereich und Recovery-Verfahren angeboten werden.

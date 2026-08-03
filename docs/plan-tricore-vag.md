# Plan: VAG-Steuergeräte (Infineon TriCore) lesen

## Kontext

`VolvoSplitter` zerlegt heute genau zwei Abbildarten: Volvo/TRW **EMS2.3** (MPC5674F) und
**EMS2.4** (MPC5777C). Beide sind PowerPC, big-endian, und tragen ein TRW-eigenes
ASCII-Sektorformat (`v=1;a=…`, Endadresse bei 0x0F8, CRC32 im Trailer). Die Analyse hängt
an diesen Annahmen: `DetectFamily` entscheidet allein an der Dateigröße, `FlashDump.Layout`
ist wörtlich auf `Mpc5777cLayout` typisiert, alle Wortzugriffe sind fest big-endian.

Ziel ist, **zusätzlich Abbilder von VAG-Steuergeräten auf Infineon-TriCore-MCU zu lesen**
(Bosch EDC17-Diesel- und MED17/ME17-Benzin-Familie): little-endian, ohne TRW-Sektorköpfe,
ohne festen CRC-Trailer.

Der entscheidende Punkt ist die Trennung zwischen **belegt** und **gedeutet**. Belegbar
sind drei Dinge, und alle drei sind es unabhängig voneinander:

1. Die **physische Sektorkarte** der MCU aus den Infineon-Datenblättern.
2. Eine **Bosch-Blockkopfstruktur**, die als verkettete Liste im Abbild steht — quelloffen
   dokumentiert und im Abbild selbst nachprüfbar (siehe unten).
3. **Kennungsstrings** im Abbild.

Nicht belegbar ist alles darüber hinaus. Das Repository hat dafür schon eine Kultur
(`OpaqueRegion_DoesNotClaimEncryptionAsFact`, `RegionBeyondLargeFlash_IsNotAutomaticallyEeprom`);
dieser Plan setzt sie fort und erfindet keine Bosch-Blocktabelle.

### Festgelegter Rahmen

| Frage | Entscheidung |
| --- | --- |
| Umfang | **Nur lesen und zerlegen.** Kein Schreiben, kein Prüfsummen-Korrigieren, kein Blockersatz für TriCore-Abbilder. |
| Definitionen | **Fest in C#**, wie heute. Keine JSON-Dateien, Core bleibt ohne NuGet-Pakete. |
| Zielbausteine | **TC1796 und TC1797 zuerst.** Übrige TriCore werden benannt, aber ohne Sektorkarte. |
| Umbenennung | **Später**, gestaffelt (§9). Namensraum und Assemblynamen bleiben unangetastet. |

---

## Belegte Grundlagen

### A. Physische Karte (Infineon-Datenblätter, im Plan-Lauf ausgewertet)

| | TC1796 | TC1797 |
| --- | --- | --- |
| PFLASH gesamt | 2 MiB (ein PMU) | **4 oder 3 MiB — „derivative dependent"**: PMU0 2 MiB + PMU1 2 MiB |
| Sektoren je 2-MiB-Bank | 8 × 16 KiB, 1 × 128 KiB, 1 × 256 KiB, 3 × 512 KiB | 8 × 16 KiB, 1 × 128 KiB, 7 × 256 KiB |
| DFLASH | 128 KiB, 2 Bänke à 64 KiB | 64 KiB, 2 Bänke à 32 KiB (= 16 KiB EEPROM-Emulation), nur PMU0 |
| BROM | 16 KiB | 16 KiB (nur PMU0) |
| Externer Bus | EBU vorhanden | EBU vorhanden |

Beide Sektorsummen ergeben je Bank exakt 2048 KiB (128 + 128 + 256 + 1536 bzw.
128 + 128 + 1792) — das ist die Prüfbedingung der Tabellen, analog zum bestehenden
`ContainerSize_AccountsForEveryByte`. Für die 3-MiB-Ausführung des TC1797 nennt das
Datenblatt **keine** Sektoraufteilung der dann 1 MiB großen PMU1; dieser Fall wird benannt,
aber nicht mit einer geratenen Karte gefüllt.

Adressen: PFLASH cached `0x80000000` / uncached `0xA0000000`; PMU1 des TC1797 bei
`0x80800000` / `0xA0800000`; DFLASH `0xAF000000`; BROM `0xAFFFC000`; externer Flash über
die EBU ab `0x84000000`. **Cached und uncached zeigen auf denselben Flash** — die
Adressabbildung muss beide Formen annehmen (Bit `0x20000000` beim Umrechnen maskieren).
Das ist keine Feinheit: die Infineon-Unterlagen nennen `0xA…`, die Bosch-Blockköpfe im
Abbild nennen `0x8…`.

### B. Bosch-Blockstruktur — Kopf, Prüfsummenstrukturen, CVN

**Zwei unabhängige Quellen**, beide vom Auftraggeber zur Nutzung bereitgestellt:

* **Q1** — `github.com/fanyi3315/bosch-med17-block-reader` (JavaScript, ~130 Zeilen).
  Läuft die Blockkette ab. Ausgewertet wurden Quelltext **und die vollständige
  Beispielausgabe über alle acht Blöcke** eines 8-MiB-MED17-Abbilds.
* **Q2** — „MEDC17 Checksum Analyzer & Corrector v1.1" (Python). Kennt zusätzlich die
  Prüfsummenstrukturen, die drei Prüfalgorithmen, den `0xDEADBEEF`-Abschluss, das
  OTP-Flag und die CVN.

Sie überschneiden sich im Blockkopf und **stimmen dort exakt überein** (§B4). Das ist der
wichtigste Umstand an diesem Material: zwei getrennt entstandene Werkzeuge, dieselbe
Struktur.

#### B1 — Blockkopf, little-endian, feste Länge 0x34 bis zu den Strukturen

| Versatz | Breite | Feld | Anmerkung |
| --- | --- | --- | --- |
| +0x00 | u32 | `blockIdentifier` | **unteres Byte** = Blockart; **Bit `0x00800000` = OTP** (One-Time Programmable) |
| +0x04 | u32 | `size` | Blocklänge |
| +0x08 | u32 | `nextSector` | CPU-Adresse des nächsten Blockkopfes — die Verkettung (nur Q1) |
| +0x0C | u32 | `blockEnd` | Endadresse; `= blockStart + size − 4` |
| +0x10 | u32 | `table1Pointer` | CPU-Adresse einer Wortliste |
| +0x14 | u32 | `table2Pointer` | dito |
| +0x18 | u8 | `table1Size` | Anzahl Einträge |
| +0x19 | u8 | `table2Size` | Anzahl Einträge |
| +0x1A | 10 B | `swIdentifier` | ASCII, z. B. `10SW008917` |
| +0x24 | 8 B | — | im Beispiel `0xFF`-Füllung; Bedeutung unbekannt |
| +0x2C | u32 | `numChecksumStructures` | Anzahl der Prüfsummenstrukturen |
| +0x30 | u32 | `checksumAdjust` | Abgleichwort |
| +0x34 | n × 32 B | Prüfsummenstrukturen | siehe B2 |
| +0x34 + n·32 | u32 | Prüfwort des Blocks | danach beginnt der Blockinhalt |

Q1 las die 10 Byte Kennung plus die 8 Folgebytes als eine 18-Byte-Kennung mit
`0xFF`-Füllung — dieselben Bytes, gröbere Deutung. Q2 ist hier genauer.

**Blockepilog**, relativ zum Blockende (aus Q2):

| Lage | Inhalt |
| --- | --- |
| `start + size − 136` | RSA-Signatur, 128 B (RIPEMD-160, e=3) |
| `start + size − 8` | `dCSAdjust`, 4 B — das Stellwort der CRC32 |
| `start + size − 4` | **`0xDEADBEEF`** — Blockabschluss |

#### B2 — Prüfsummenstruktur, 32 Byte

| Versatz | Breite | Feld |
| --- | --- | --- |
| +0x00 | u8 | `csBlockId` |
| +0x04 | u32 | `csStart` — CPU-Adresse, Beginn des geprüften Bereichs |
| +0x08 | u32 | `csEnd` — CPU-Adresse, **einschließlich** |
| +0x0C | u32 | `csStartVal` — Startwert, im Regelfall `0xFADECAFE` |
| +0x10 | u32 | `csExpectedVal` — Sollwert, im Regelfall `0xCAFEAFFE` |
| +0x14 | u32 | `blockIdRef` |
| +0x18 | u32 | `blockIdAddr` |
| +0x1C | u16 | `csAlgorithm` — unteres Byte zählt |

**Die drei Algorithmen** — alle mit Startwert `0xFADECAFE`:

| ID | Name | Verfahren | Sollergebnis |
| --- | --- | --- | --- |
| `0x00` | `SB_CRC32_ALGO_E` | CRC32 bitweise, Polynom `0xEDB88320`, little-endian-Doppelworte | **`0x35015001`** |
| `0x01` | `SB_ADD32_ALGO_E` | Summe der u32-Doppelworte, Überlauf verworfen | `0xCAFEAFFE` |
| `0x10` | `SB_ADD16_ALGO_E` | Summe der u16-Worte | `0xCAFEAFFE` |

`0x35015001` ist exakt das Einerkomplement von `0xCAFEAFFE` — nachgerechnet. Alle drei
Algorithmen lassen sich mit dem vorhandenen `Crc32` bzw. zwei Dutzend Zeilen Summenbildung
umsetzen. **Damit wird aus „Blockkette gefunden" ein rechnerisch bestätigter Prüfwert** —
die stärkste Belegform, die dieses Werkzeug kennt.

Adressumrechnung je Struktur:
`Datei-Offset = csStart − blockStartCpu + blockStartDatei`.

#### B3 — Zwei weitere lesbare Angaben

**Steuergerätevariante.** Im Dataset-Block (`0x60`) steht bei Blockversatz **+0x78** eine
schrägstrichgetrennte Zeichenkette, z. B. `34/1/EDC17_C46/5/P643//C643X5L8///`. Das Feld
mit `EDC17`/`MED17`/`MEDC17` benennt die Variante. Das ist eine **feste Fundstelle** und
damit deutlich belastbarer als freies Durchsuchen nach Zeichenketten — es löst zugleich
die MCU-Mehrdeutigkeit aus §C.

**CVN** (Calibration Verification Number). Eine CRC32 (tabellengetrieben, Start
`0xFFFFFFFF`, Endabgleich `0xFFFFFFFF`) über mehrere Speicherbereiche. Die Bereiche stehen
nicht fest im Werkzeug, sondern werden aus einer Konfigurationsstruktur im Abbild gelesen:
gesucht wird das Muster `{Zeiger, DS_START, DS_WOCS_END, Anzahl}`, wobei `DS_START` die
Startadresse des Dataset-Blocks ist. Der Zeiger führt auf eine Tabelle aus
`(Start, Ende)`-Paaren. Die CVN wird **gelesen und ausgewiesen** — sie ist die Zahl, die
die OBD-Diagnose zur Prüfung der Kalibrierung meldet.

#### B4 — Extended Blockarten (Q2)

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

Einstiegspunkt im Vorbild: `0x80018000` (dort „alternative bootloader"), danach jeweils
`nextSector` folgen. Die Beispielausgabe zeigt eine Kette über **acht** Blöcke, die von
PMU0 nach PMU1 und weiter in den externen Flash läuft — die Kette ist **nicht**
adressgeordnet (Block 4 liegt bei `0x80000000`, Block 5 bei `0x80020000`).

Das Speichermodell jenes Dumps (8 MiB):

```
Datei 0x000000–0x1FFFFF -> CPU 0x80000000   PMU0, 2 MiB   intern
Datei 0x200000–0x3FFFFF -> CPU 0x80800000   PMU1, 2 MiB   intern
Datei 0x400000–0x7FFFFF -> CPU 0x84000000   externer Flash über EBU, 4 MiB
```

Das ist unabhängig von den Datenblättern **die Bestätigung** für PMU1 bei `0x…800000`
beim TC1797 — und der Beleg dafür, dass MED17.1-Geräte externen Flash über die EBU haben.
Ein 8-MiB-MED17-Abbild ist also kein Fehler, sondern ein eigener Containerfall.

#### B5 — Nachgerechnete Invarianten und der Abgleich beider Quellen

Zuerst die Kreuzprüfung. Q1 und Q2 kennen einander nicht und leiten die Blockgrenze
verschieden her — Q1 aus `blockEnd = blockStart + size − 4`, Q2 aus
`block_start = ((block_end + 5) − size − 1)`. Ausgerechnet über alle acht Blöcke ergeben
**beide Formeln dasselbe Ergebnis**, ohne Abweichung. Es ist dieselbe Struktur.

Der zweite Abgleich betrifft die Prüfsummenstrukturen. Q2 sagt: 32 Byte je Struktur ab
+0x34, danach ein Prüfwort. Rechnet man das auf die von Q1 beobachteten Tabellenzeiger um,
trifft es bei den beiden Blöcken, deren Tabellen unmittelbar folgen, **punktgenau**:

| Block | Anzahl Strukturen | Ende + Prüfwort | beobachteter `table2Pointer` |
| --- | --- | --- | --- |
| 3 Customer tuning prot. | 1 | +0x58 | **+0x58** |
| 5 ASW #0 | 4 | +0xB8 | **+0xB8** |

Zwei verschiedene Strukturanzahlen, beide exakt — das ist kein Zufall. **Damit ist meine
frühere Aussage widerlegt, Lage und Größe der Prüfsummenstrukturen seien aus dem Material
nicht ableitbar.** Sie sind es: 32 Byte, ab +0x34, gefolgt von einem Prüfwort.

Aus der Beispielausgabe folgen weiter diese Regeln — jede 8 von 8 mal:

**(I1) `blockEnd == blockStart + size - 4`** — ohne eine einzige Abweichung:

| # | Art | `blockStart` | `size` | `+size` | `blockEnd` | Δ |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Startup | 80018000 | 7f00 | 8001ff00 | 8001fefc | 4 |
| 2 | Tuning protection | 80014000 | 3f00 | 80017f00 | 80017efc | 4 |
| 3 | Cust. tuning prot. | 80010000 | 2000 | 80012000 | 80011ffc | 4 |
| 4 | Customer | 80000000 | fd04 | 8000fd04 | 8000fd00 | 4 |
| 5 | ASW #0 | 80020000 | 1e0000 | 80200000 | 801ffffc | 4 |
| 6 | ASW #1 | 80800000 | 200000 | 80a00000 | 809ffffc | 4 |
| 7 | ASW #2 | 84100000 | 280000 | 84380000 | 8437fffc | 4 |
| 8 | Dataset #0 | 84002000 | fe000 | 84100000 | 840ffffc | 4 |

`blockEnd` zeigt also auf das **letzte Wort**, nicht hinter das Blockende. Das ist die
schärfste einzelne Prüfregel, die sich aus dem Material gewinnen lässt.

**(I2) `table1Pointer` und `table2Pointer` liegen im eigenen Block** — bei allen sechs
Blöcken, die überhaupt Tabellen haben. Blöcke ohne Tabellen tragen Zeiger `0` und Anzahl `0`.

**(I3) Kettenende ist `nextSector == 0`** — Block 8 beendet die Kette so, keine
Sonderbehandlung nötig.

**(I4) `identifier` sind 18 Byte: ASCII, mit `0xFF` aufgefüllt.** Er gruppiert die Blöcke
zu Softwareeinheiten — im Beispiel `10SW008917` (Blöcke 1–4), `10SW026798` (5–7),
`10SW028601` (8). Die Form `\d{2}SW\d{6}` ist damit belegt und gehört in den Kennungsleser.

**(I5) Blockgrößen sind nicht sektorbündig.** Block 4 ist `0xFD04` groß. Eine Prüfung auf
Sektorausrichtung wäre falsch.

**(I7) `0xDEADBEEF` steht bei `blockStart + size − 4`** (aus Q2). Ein Magiewert am
Blockende — die schärfste Einzelprüfung überhaupt, weil sie unabhängig von jeder
Adressrechnung ist. In Q1s Beispielausgabe nicht sichtbar, weil dort nur Kopffelder
ausgegeben werden; sie widerspricht ihr aber auch nicht.

**(I6) Die Blöcke überlappen sich nicht** und decken den Flash bis auf sechs kleine Lücken
ab (764 B, 2 × 256 B, 8 KiB in PMU0; 8 KiB und 512 KiB im externen Flash). Der Parser
meldet Lücken, behauptet aber nichts über ihren Inhalt.

Die Blockkarte des Beispiels — zugleich die Vorlage für das synthetische Testabbild:

```
Block                 CPU-Bereich          Datei-Offset     Bank     Größe
4 Customer            80000000-8000fd04    000000-00fd04    PMU0        64.772 B
3 Cust. tuning prot.  80010000-80012000    010000-012000    PMU0         8.192 B
2 Tuning protection   80014000-80017f00    014000-017f00    PMU0        16.128 B
1 Startup             80018000-8001ff00    018000-01ff00    PMU0        32.512 B
5 ASW #0              80020000-80200000    020000-200000    PMU0     1.966.080 B
6 ASW #1              80800000-80a00000    200000-400000    PMU1     2.097.152 B
8 Dataset #0          84002000-84100000    402000-500000    Extern   1.040.384 B
7 ASW #2              84100000-84380000    500000-780000    Extern   2.621.440 B
```

Das Abbild ist damit ein **TC1797** (2 MiB PMU0 + 2 MiB PMU1) mit 4 MiB externem Flash —
unabhängig von den Datenblättern die Bestätigung für PMU1 bei `0x…800000`.

#### Zwei Fallen im Vorbild, die nicht mitgenommen werden

**(F1) Zeigertabellen sind keine Adresstabellen.** Ihre Einträge sind gemischt: PFLASH
(`80141f04`), externer Flash (`808084ac`), **LDRAM `0xC0000000`** (`c0000070`, `c0000280`),
Wächterwerte `0` und `ffffffff` — und schlichte Zahlen ohne Adresscharakter (`f7c`). Die
Tabellen werden roh ausgegeben, **ohne Deutung**, und kein Eintrag wird als Adresse geprüft.

**(F2) Das Vorbild löst Zeiger gegen die falsche Bank auf.** `readTable` bekommt den
`pmuName` des *Blocks* und zieht davon die Basis ab. Block 5 liegt in PMU0, seine Tabelle
enthält aber `808084ac` (PMU1) — `0x808084ac − 0x80000000 = 0x8084ac` liegt jenseits des
2-MiB-Puffers. Die C#-Umsetzung löst **jeden** Zeiger über `PhysicalLayout.ToFile` global
auf, nie relativ zur Bank des Blocks.

Dazu die bekannten Schnittgrenzen des Vorbilds: `slice(0, 0x3FFFFC)`, `0x1FFFFF`,
`0x7FFFFB` schneiden ein bis vier Byte ab, wodurch Block 5 um ein Byte gekürzt extrahiert
wird. Wird nicht übernommen.

#### Einordnung dieser Quelle — ausdrücklich

- **Nutzung ist freigegeben.** Der Auftraggeber hat die freie Nutzung erklärt. Das
  Repository selbst trägt keinen Lizenztext; die Quelle wird deshalb in Quelltextkommentar
  und README genannt — als Herkunftsnachweis, nicht als Lizenzbedingung.
- **Kein Herstellerdokument.** Community-Reverse-Engineering, an *einem* Abbild
  entwickelt. Die Blockarten-Tabelle ist eine Deutung, keine Bosch-Angabe.
- **Nur MED17 belegt.** Ob EDC17 denselben Kopf trägt, ist plausibel (gleiche
  Bosch-Softwarearchitektur), aber unbelegt. Der Parser prüft das an der Struktur selbst
  über I1–I3, statt es am Steuergerätetyp vorauszusetzen.
- **Das Flagbit ist geklärt.** Block 2 trägt `0x00800020` statt `0x20`; laut Q2 ist Bit
  `0x00800000` das **OTP-Kennzeichen** (One-Time Programmable). Für einen
  Tuning-Schutz-Block ist das schlüssig. Wird als Merkmal ausgewiesen.
- **Zwei Deutungen bleiben Deutung.** Die Namen der Blockarten und der Algorithmen
  (`SB_CRC32_ALGO_E` usw.) stammen aus den Werkzeugen, nicht aus einer Bosch-Unterlage.
  Sie werden übernommen, aber als Herkunft gekennzeichnet.
- **Die acht Bytes bei +0x24 sind unerklärt.** Im Beispiel `0xFF`-Füllung. Werden roh
  ausgewiesen, nicht gedeutet.

**Das ändert die Ausrichtung des Vorhabens.** Statt Blockgrenzen aus Entropie zu *raten*,
wird eine Struktur *gelesen und geprüft*. Die Entropieanalyse bleibt, rutscht aber vom
Haupt- zum Rückfallverfahren.

### C. Steuergerät → MCU (Tabelle aus der Aufgabenstellung)

Diesel: EDC17U01/U05→TC1766 · CP04/CP14→TC1796 · CP20→TC1796/TC1797 · C46→TC1767 ·
C54→TC1797 · CP44→TC1797 · C64→TC1767/TC1782 · C74→TC1793/TC1797.
Benzin: ME17.5→TC1762/TC1766 · MED17.5.2/.5.5→TC1766/TC1767 · MED17.5.21/.25→TC1782 ·
MED17.5.26/.27→TC1782/TC1793 · MED17.1→TC1796 · MED17.1.1→TC1796 · MED17.1.6→TC1797 ·
MED17.1.21/.1.27→TC1793.

Diese Tabelle ist kein Layout, sondern der **Entkoppler der Mehrdeutigkeit**: eine
2-MiB-Datei kann TC1796 oder TC1797-PMU0 sein, und die Sektorkarten ihrer oberen Hälften
unterscheiden sich. Findet der Kennungsleser `EDC17CP44`, ist es TC1797; findet er
`EDC17CP14`, ist es TC1796. Ohne Fund bleibt es mehrdeutig und wird so gemeldet.

---

## Phase 1 — Nahtstellen einziehen, ohne Verhalten zu ändern

Abnahmekriterium: `dotnet test` 49/49, und `git diff --stat VolvoSplitter.Core.Tests/`
zeigt **genau eine geänderte Zeile** — den Typnamen in `Mpc5777cLayoutTests.cs:7`
(`VolvoEcuBlockTests.cs:63` nutzt `var` und bleibt unberührt). Kein `Assert` wird angefasst.

### 1.1 `PhysicalLayout.cs` — Daten, keine Schnittstelle

`Mpc5777cLayout` hat kein gerätespezifisches *Verhalten*, nur gerätespezifische *Daten*.
Ein Interface wäre hier Ballast. Stattdessen eine `sealed class` mit statischen Erbauern —
das passt zur Hausordnung des Repos (kein einziges `interface`, kein `abstract`, kein
`virtual` in Core).

```csharp
public sealed class PhysicalLayout
{
    public IReadOnlyList<FlashPartition> Partitions { get; }   // grob: Bänke
    public IReadOnlyList<FlashPartition> EraseSectors { get; } // fein: Löschsektoren, nur Anzeige
    public bool   Complete { get; }
    public string SourceNote { get; }      // "MPC5777C, Referenzhandbuch Tabelle 4-2"
    public FlashPartition? PartitionAt(long fileOffset);
    public long? ToCpu(long fileOffset);
    public long? ToFile(long cpuAddress);  // neu — die Blockkette braucht die Rückrichtung
}
```

`ToFile` maskiert `0x20000000` heraus, damit `0x80…` und `0xA0…` gleich behandelt werden.

`Mpc5777cLayout` wird zur statischen Erbauerklasse und behält **alle** Konstanten
(`LargeFlashSize`, `LowMidBlockSize`, `LowMidSize`, `UtestSize`, `ContainerSize`) und
`For(EcuFamily, long) → PhysicalLayout?`. Der Rumpf von `For` ändert sich nicht.

`FlashPartition` bekommt **eine** neue Eigenschaft:

```csharp
public bool EmulatedEeprom { get; init; }   // Vorgabe false
```

Damit werden die beiden gerätespezifischen Klauseln generisch:
`FlashRegion.cs:150` und `FlashRegion.cs:262` prüfen statt
`Type: FlashBlockType.Low or FlashBlockType.Mid` künftig `EmulatedEeprom: true`.

Der AN4868-Blockstatus-Dekoder `BlockStatus` (`FlashRegion.cs:360-375`) bleibt **bewusst**
an `Type is Low or Mid` gebunden — das sind NXP-Vorschlagswerte, für Infineon-DFLASH gilt
davon nichts.

Umtypisierungen `Mpc5777cLayout?` → `PhysicalLayout?`: `FlashDump.Layout` (Z. 45) und
`RegionScanner.Scan/MergePerEepromBlock/Describe/Classify` (Z. 99, 137, 222, 244).

Ebenfalls in `Classify`: der Aufruf `VolvoEcuBlock.TryParse` (Z. 249) läuft heute
bedingungslos über jeden Bereich. Er wird an den Herstellerzweig des Profils gebunden,
damit ein TriCore-Abbild gar nicht erst gegen ein Volvo-Containerformat geprüft wird.

### 1.2 `TrwContainer.cs` — das TRW-Format aus `FlashDump` herauslösen

Unverändert verschoben: `DiscoverSlots` (Z. 232–252), `FindHeaders` (255–271),
`MatchesMagic` (274–275), `MatchSlot` (281–305), `KindFromHeader` (307–314),
`LabelFromHeader` (316–324), `ReadSector` (326–395), `ParseHeaderFields` (554–571),
`Ascii` (573–577), `ComputeSectorCrc` (584–588), `ReadCpuOffset` (591–597).

`FlashDump` behält Laden, Orchestrierung, Mutation und Ausgabe und fällt von 610 auf
≈ 330 Zeilen; `TrwContainer.cs` liegt bei ≈ 270. Das passt zur Dateigrößenkultur des Repos.
`RepairCrc` und `ReplaceSector` rufen `TrwContainer.SectorCrc` / `.CpuOffsetAt` auf —
keine Verhaltensänderung.

### 1.3 `EcuProfile.cs` — Ersatz der vier Ternärausdrücke

```csharp
public enum Endianness    { Big, Little }
public enum ContainerKind { TrwSector, BoschBlockChain, None }

public sealed record EcuProfile(
    string Key,             // "ems23" | "ems24" | "tricore" | "unknown"
    string FamilyName,      // "EMS2.3" | "EDC17 / MED17 (TriCore)" | "unbekannt"
    string MicroName,       // "MPC5674F" | "TC1797" | "—"
    string Manufacturer,    // "Volvo / TRW" | "VAG / Bosch"
    Endianness Endianness,
    long FlashSize,
    ContainerKind Container,
    bool SupportsWriteBack)             // Volvo: true, TriCore: false
{
    public EcuFamily?     Family { get; init; }   // null bei TriCore/unbekannt
    public TriCoreDevice? Device { get; init; }
    public IReadOnlyList<SectorSlot> Slots { get; init; } = [];
}
```

`FlashFormat` behält **alle** öffentlichen Member; `SlotsFor`, `FlashSizeFor`,
`FamilyName`, `MicroName` werden Einzeiler über `EcuProfiles.ForFamily(f)`, `DetectFamily`
bleibt wortgleich erhalten und wird künftig nur noch **innerhalb** des TRW-Zweigs der
Erkennung benutzt. Damit bleibt `FlashFormatTests.cs` unangetastet.

`SupportsWriteBack` verankert die Festlegung „nur lesen" technisch — nicht durch
weggelassenen Code, sondern durch ein Feld, das Oberfläche und
`Save`/`RepairCrc`/`ReplaceSector` abfragen.

### 1.4 `ByteOrder.cs` — reine Extraktion

Aus `FlashDump.ReadUInt32Be`/`WriteUInt32Be` (Z. 599–609), `VolvoEcuBlock.ReadBe32`
(Z. 79–80), `RegionScanner.IndexOfUInt32Be` (Z. 390–397):
`ReadUInt32(span, off, Endianness)`, `WriteUInt32`, `IndexOfUInt32`. Die bisherigen
Aufrufer delegieren mit `Endianness.Big` — kein Verhaltensunterschied.
`FlashDump.FindUInt32Be` bleibt öffentlich erhalten (steht im README) und bekommt eine
Überladung mit Bytefolge.

### 1.5 `BinaryHeuristics.cs` — generische Helfer freilegen

Aus `FlashRegion.cs` unverändert herausgezogen: `Entropy` (Z. 403), `DuplicateRatio`
(Z. 424), `OccupiedRuns` (Z. 177), `IsErased` (Z. 195), `Subtract` (Z. 203), `TrimErased`
(Z. 169), `CountOccurrences` (Z. 377). Löschbyte `0xFF` und Seitengröße `0x1000` werden
Parameter statt Literale.

### 1.6 `Hex.cs` — Adressdarstellung

```csharp
public static string Addr(long v) => v <= 0xFFFFFF ? $"0x{v:X6}" : $"0x{v:X8}";
```

**Präzisierung:** `X6` schneidet *nicht* ab — `$"{0xA0000000:X6}"` liefert `A0000000`. Die
Änderung ist reine Ausrichtung und Lesbarkeit, keine Korrektheitsfrage. Betroffen:
`SectorInfo.cs:79,80,98,113`, `FlashRegion.cs:40,55`, `Mpc5777cLayout.cs:38,40`,
`DumpReport.cs`, `MainWindow.xaml.cs`, `Controls/FlashMap.cs:181,184`.

---

## Phase 2 — Erkennung: Prüfkette statt Dateigröße

`FlashFormat.DetectFamily(long)` reicht nicht mehr: eine 2-MiB-Datei kann heute nur EMS2.3
sein, künftig auch ein TC1796-Abbild.

`VolvoSplitter.Core/EcuDetector.cs`:

```csharp
public sealed record Detection(EcuProfile Profile, int Score, int Runner,
                               IReadOnlyList<string> Evidence, bool Ambiguous);

public static Detection Identify(byte[] data, EcuReport? report = null);
```

**TRW-Sonden**

| Beleg | Punkte |
| --- | --- |
| `FlashFormat.HeaderMagic` (`v=1;a=`) mit lesbarem `o=`-Feld | +50 |
| Fundadresse oder `o=`-Wert deckt sich mit `Ems23Slots`/`Ems24Slots` | +30, **entscheidet zugleich 2.3 gegen 2.4** |
| `VolvoEcuBlock.Magic` parsebar | +20 |
| `"VOLVO"` bei Parametersektor +0x10B | +20 |
| Dateigröße == `Mpc5777cLayout.ContainerSize` | +15 |
| Größe ≤ `Ems23MaxSize` (die alte Regel) | +5, **nur noch Stichentscheid innerhalb TRW** |

Ein EMS-Abbild nennt seine Rolle selbst — damit ist der 2-MiB-Konflikt sauber gelöst.
Weil die synthetischen Testabbilder aus `TestDump` immer `v=1;a=` oder `VOLVOECU` tragen,
bleiben die bestehenden Tests grün.

**TriCore-Sonden**

| Beleg | Punkte |
| --- | --- |
| Mindestens ein Block mit **rechnerisch stimmender Prüfsumme** (§B2) | **+80** |
| Gültiger **Bosch-Blockkopf** samt `0xDEADBEEF`-Abschluss (§4) | **+60** |
| Variantenstring `EDC17…`/`MED17…` im Dataset-Block bei +0x78 (§B3) | +50, benennt zugleich die Variante |
| Kennungen `0281`/`0261`+6 Ziffern, `1037`/`1039`+6 Ziffern, `EDC17…`, `MED17…`, `ME17…` | +40, benennt zugleich den Steuergerätetyp |
| `EcuReport.Micro` aus der Beidatei nennt `TC17xx` | +45 |
| **Zeigerdichte:** Anteil 4-Byte-*ausgerichteter* LE-Wörter mit oberem Byte `0x80`/`0xA0`/`0x84` und zweitem Byte < 0x10 über Schwelle | +30 |
| Dateigröße ∈ {0x200000, 0x300000, 0x400000, 0x800000} | +10 |
| Größe == PFLASH + 0x10000 bzw. + 0x20000 (DFLASH-Anhang) | +10 |

`EcuReport` liest den Schlüssel `Micro` bereits (`EcuReport.cs:19`) und entfernt die
Dateinamensanhängsel `_Micro`/`_ExtFlash`/`_Eeprom` schon (Z. 50) — für die stärkste
Sonde ist nichts Neues nötig. Der Anhang `_ExtFlash` ist zudem ein direkter Hinweis auf
den externen Flash der MED17.1-Geräte.

**Entscheidungsregel**

1. Höchste Punktzahl gewinnt.
2. Vorsprung < 20 → `Ambiguous = true`; Profil wird gesetzt, aber beide Kandidaten werden
   in Oberfläche und Bericht gezeigt.
3. Sieger < 30 → `EcuProfiles.Unknown`: kein Container, kein Layout, `FlashSize` = Dateigröße.
   `RegionScanner` liefert dann genau das, was das Werkzeug heute schon ehrlich kann —
   belegte Bereiche mit Entropieeinordnung und Konfidenz „unbekannt".
4. **Die Dateigröße darf nie zwischen Herstellern entscheiden**, nur innerhalb eines
   Herstellers Gleichstände brechen.

`FlashDump` bekommt `public Detection Detection { get; }`; `Load`/`FromBytes` bekommen
`string? forceProfile = null` zum Übersteuern.

---

## Phase 3 — TriCore-Bausteinmodell

`VolvoSplitter.Core/TriCore/TriCoreDevice.cs`:

```csharp
public sealed record FlashBank(string Label, long CpuStart, long Size,
                               IReadOnlyList<long> SectorSizes, bool EmulatedEeprom);

public sealed record TriCoreDevice(string Name, IReadOnlyList<FlashBank> Program,
                                   FlashBank? Data, FlashBank? External,
                                   string SourceNote)
{
    public bool SectorMapKnown => Program.All(b => b.SectorSizes.Count > 0);
    public static readonly TriCoreDevice Tc1796, Tc1797;
    public static TriCoreDevice Generic(long imageSize);
    public static TriCoreDevice? ByName(string micro);   // für EcuReport.Micro
}
```

**Ausgeliefert wird nur TC1796 und TC1797** — genau die beiden Datenblätter. Sie decken
EDC17CP04/CP14/CP20/C54/CP44/C74 und MED17.1/.1.1/.1.6 ab, also den Großteil der Tabelle.

**Zurückgestellt: TC1762, TC1766, TC1767, TC1782, TC1791, TC1793.** Diese laufen über
`TriCoreDevice.Generic(size)`: eine einzige PFLASH-Partition, `SectorSizes` leer,
`SourceNote = "Sektoreinteilung für diesen Baustein nicht hinterlegt"`,
`PhysicalLayout.Complete = false`. Jede echte Karte kommt später als eigener kleiner
Commit mit Datenblattzitat. Eine geratene Karte auszuliefern wäre genau die Erfindung, die
die Ehrlichkeitstests dieses Repos verbieten.

`VolvoSplitter.Core/TriCore/TriCoreLayout.cs` → `PhysicalLayout For(TriCoreDevice, long imageSize)`:

| Dateigröße | Deutung |
| --- | --- |
| 0x200000 | eine PFLASH-Bank (TC1796 vollständig, oder TC1797-PMU0) |
| 0x400000 | TC1797 PMU0 (`0x80000000`) + PMU1 (`0x80800000`) |
| 0x300000 | 3-MiB-Ausführung des TC1797 — PMU1 als ein Block ohne Sektorgliederung |
| **0x800000** | **4 MiB intern + 4 MiB externer Flash ab `0x84000000`** — der MED17.1-Fall aus §B |
| oben + 0x10000 (TC1797) bzw. + 0x20000 (TC1796) | zusätzlich DFLASH ab `0xAF000000`, `EmulatedEeprom = true` |
| sonst | Rest als „Anhang" ohne CPU-Adresse — wie `Mpc5777cLayout` es Z. 146–148 schon tut |

Die **groben** Partitionen bleiben PMU0/PMU1/Extern/DFLASH, damit
`FlashRegion.PartitionLabel` und `DescribePartitions` sinnvolle Aussagen liefern. Die
Löschsektoren landen in `EraseSectors` und dienen nur der Anzeige — feingranulare
Partitionen würden eine 1-MiB-Coderegion mit dem Label des ersten berührten 16-KiB-Sektors
versehen, was irreführend wäre.

**Bekannte Grenze:** Auslesewerkzeuge legen DFLASH und externen Flash oft als *eigene*
Dateien ab (`…_Eeprom.bin`, `…_ExtFlash.bin`). `FlashDump` hält genau ein `byte[]`; ein
Mehrdateimodell ist nicht Teil dieses Vorhabens. Eine allein geladene 0x20000-Datei ohne
Zeigerdichte muss als `Unknown` erkannt werden, nicht als geschrumpftes PFLASH — dafür
eine eigene Testzusicherung.

---

## Phase 4 — Bosch-Blockkette lesen (das Kernstück)

`VolvoSplitter.Core/TriCore/BoschBlockChain.cs`. Eigenständige C#-Umsetzung der in §B
beschriebenen Struktur.

```csharp
public sealed record BoschBlock(
    long CpuStart, long FileStart, long Size, long CpuEnd,
    byte Id, string IdName, uint Flags,
    long? NextCpu,
    long Table1Cpu, IReadOnlyList<uint> Table1,
    long Table2Cpu, IReadOnlyList<uint> Table2,
    string Identifier, int ChecksumStructureCount);

public static IReadOnlyList<BoschBlock> Walk(byte[] data, PhysicalLayout layout,
                                             out IReadOnlyList<string> evidence);
```

**Zwei Suchverfahren, die einander bestätigen.** Q1 läuft die Kette über `nextSector`;
Q2 sucht stattdessen ab dem ersten Nicht-Null-Byte, prüft dort einen Kopf und springt
hinter den Block zum nächsten Nicht-Null-Byte. Beide werden umgesetzt:

* **Abtastung** (Q2) als Hauptverfahren — sie findet auch Blöcke, deren Kette gerissen ist
  oder deren Kopf nicht bei `0x18000` beginnt.
* **Kettenlauf** (Q1) als Gegenprobe — stimmen beide Ergebnismengen überein, ist das ein
  eigener Beleg und wandert in `Detection.Evidence`. Weichen sie ab, wird die Differenz
  gemeldet, nicht stillschweigend vereinigt.

**Prüfung eines Kopfes** — erst wenn *alle* zutreffen, gilt er als gültig. Die Regeln
stammen aus §B und sind dort nachgerechnet:

1. **`0xDEADBEEF` bei `blockStart + size − 4`** (I7). Die schärfste Einzelprüfung,
   unabhängig von jeder Adressrechnung — sie steht deshalb an erster Stelle.
2. Unteres Byte von Wort 0 steht in der Blockarten-Tabelle (B4).
3. `size` zwischen 0x40 und der Dateigröße, und der Block passt vollständig hinein;
   **keine** Prüfung auf Sektorausrichtung (I5).
4. **`blockEnd == blockStart + size − 4`** — exakt, ohne Toleranz (I1).
5. `blockStart` und `blockEnd` sind gültige TriCore-Flashadressen.
6. `numChecksumStructures` ist plausibel (Q2 deckelt bei 100).
7. `table1Pointer`/`table2Pointer` liegen im eigenen Block (I2), sofern die Anzahl > 0 ist.
8. `swIdentifier` ist druckbares ASCII (I4).
9. Für den Kettenlauf zusätzlich: `nextSector` ist `0` (I3) **oder** bildet über
   `PhysicalLayout.ToFile` ab und trägt dort selbst einen gültigen Kopf.

**Prüfsummen nachrechnen — das ist der eigentliche Beleg.** Für jede Struktur des Blocks:
Bereich `[csStart, csEnd]` über die Adressumrechnung auf Datei-Offsets bringen, mit
`csStartVal` als Startwert den Algorithmus aus `csAlgorithm` rechnen und gegen den
Sollwert prüfen — `0x35015001` bei CRC32, `csExpectedVal` bei ADD32 und ADD16.

Erst **das** rechtfertigt `SectorStatus.Verified`. Ein Block, dessen Kopf aufgeht, dessen
Prüfsummen aber nicht stimmen, bekommt `SectorStatus.CrcMismatch` — den Zustand, den
`SectorInfo` für den Volvo-Pfad schon kennt und den die Oberfläche schon anzeigt. Ein
Abbild mit bearbeiteter Kalibrierung sieht damit sofort so aus, wie es ist.

Umsetzung: `Crc32.Compute` deckt CRC32 nicht ab — der Bosch-Algorithmus verarbeitet
little-endian-Doppelworte bitweise mit freiem Startwert. Das ist eine eigene, rund
15-zeilige Routine neben dem vorhandenen `Crc32`; ADD32 und ADD16 sind je fünf Zeilen.

**Zeigerauflösung.** Jeder Zeiger — `nextSector`, `table1Pointer`, `table2Pointer` — wird
über `PhysicalLayout.ToFile` **global** aufgelöst, nie relativ zur Bank des Blocks. Siehe
F2 in §B: die Tabellen eines PMU0-Blocks enthalten nachweislich PMU1-Adressen.

**Tabelleninhalte** werden roh übernommen und ungedeutet ausgegeben (F1) — sie enthalten
Flash-, Extern- und LDRAM-Adressen, Wächterwerte und schlichte Zahlen nebeneinander.

**Kettenlauf.** Höchstens 64 Schritte, besuchte Adressen in einem `HashSet` — eine
zyklische oder überlange Kette bricht ab und wird als solche gemeldet.

**Lückenbericht.** Nach dem Kettenlauf werden die von keinem Block belegten Bereiche
aufgeführt (im Beispiel sechs, von 256 B bis 512 KiB) — als Beobachtung, ohne Deutung.

**Ergebnis.**
- Jeder **geprüfte** Block wird ein `SectorInfo` mit `Kind` aus der Blockart, `Label` aus
  `IdName`, `PartNumber` aus `Identifier`, `Start`/`Length` aus dem Kopf und
  `Status = Verified`. Damit ist er sofort extrahierbar und S-Record-fähig — `SRecord`
  schreibt bereits 32-Bit-S3-Adressen, `0x80000000` passt ohne Änderung.
- Blöcke, deren Kopf nur teilweise aufgeht, erscheinen **nicht** als Sektor, sondern als
  Beleg in `Detection.Evidence` („Blockkopfkandidat bei 0x… nicht bestätigt").
- Jede **Prüfsummenstruktur** wird mit Algorithmus, Bereich, Startwert, Sollwert und
  gerechnetem Wert ausgewiesen — samt Ergebnis. Das ist die Kernaussage des Befunds.
- Blöcke mit gesetztem **OTP-Kennzeichen** werden als solche markiert.
- Die **CVN** wird gerechnet und ausgewiesen, sofern die Konfigurationsstruktur gefunden
  wird; sonst „nicht gefunden". Nur lesend.
- Die **Steuergerätevariante** aus dem Dataset-Block (+0x78) wird ausgegeben.
- `Table1`/`Table2` werden als Wortlisten ausgegeben, ohne Deutung; ebenso die acht
  unerklärten Bytes bei +0x24.

`SectorKind` bekommt die Werte `Bootloader` und `Dataset`. Geprüft: kein `switch`-Ausdruck
im Core ist über `SectorKind` erschöpfend, das Hinzufügen ist gefahrlos.

**Warum das der richtige Weg ist:** die Kette ist selbstprüfend. Acht Blöcke, die
lückenlos aufeinander zeigen, deren Größen zu den Endadressen passen und deren Kennungen
lesbar sind, sind ein Beleg, kein Verdacht. Genau das rechtfertigt `Status = Verified` —
und genau das ist der Unterschied zu allem, was Phase 5 macht.

---

## Phase 5 — Rückfall: Entropie, wenn keine Kette gefunden wird

Findet Phase 4 keine gültige Kette (anderer EDC17-Aufbau, gekürztes Abbild, verschlüsselt
ausgelesen), bleibt das, was das Werkzeug schon kann. `RegionScanner` läuft mit der
TriCore-Sektorkarte statt der MPC5777C-Karte. Drei kleine Ergänzungen:

* `ContainsSourcePaths` (Z. 446–455) bekommt `"@(#)"u8` als dritte Nadel — die übliche
  SCCS-Kennung in Bosch-Ständen.
* Neue Heuristik `MonotonicRunRatio(data, start, length)`: Anteil der 64-Byte-Fenster, die
  als u8- oder u16le-Folge monoton nicht fallend sind. Kennfeldachsen und Stützstellen
  sind genau das — ein echtes, billiges Signal.
* `Classify` bekommt **einen** neuen Zweig, der `RegionKind.Data` **beibehält** und
  lediglich betitelt: `Title = "Datenbereich — Kalibrierungskandidat"`,
  `Confidence = Strong`. Nur wenn **alle** zutreffen: Entropie 3,0–5,5; Länge ≥ 128 KiB;
  `MonotonicRunRatio` über Schwelle; Beginn auf einer Löschsektorgrenze. Trifft nur ein
  Teil zu, bleibt es schlicht „Daten".

**Kein neuer `RegionKind.Calibration`** — das wäre eine Behauptung im Typsystem.

Die bestehenden Schwellen (7,9 / 4,5 / 0,02) wurden an PowerPC-Code gemessen; die
öffentliche EDC17-Faustregel (Code 5,5–7,0 bit/Byte, Daten 3,0–5,5) ist damit verträglich.
Sie bleiben unverändert, bis echte Abbilder etwas anderes zeigen — Nachjustieren ohne
Messung wäre Raten.

---

## Phase 6 — Kennungen aus dem Abbild

`VolvoSplitter.Core/TriCore/BoschIdentity.cs`. **Ein** linearer Durchlauf sammelt
druckbare ASCII-Läufe ab 6 Zeichen samt Offset; die Muster laufen anschließend über die
Läufe, nicht über die Rohbytes. Billiger, und der Fundort fällt gratis ab — ein Fundort
ist ein Beleg, eine Zeichenkette allein nur eine Beobachtung.

```csharp
public sealed record IdentityHit(string Kind, string Value, long Offset);

public sealed record BoschIdentity(IReadOnlyList<IdentityHit> Hits)
{
    public static BoschIdentity? Scan(ReadOnlySpan<byte> data);
}
```

**Vorrang hat die feste Fundstelle.** Steht im Dataset-Block bei +0x78 ein Variantenstring
(§B3), wird die Steuergerätevariante von dort genommen — mit `RegionConfidence.Confirmed`.
Das freie Durchsuchen unten ist nur der Rückfall für Abbilder ohne lesbaren Dataset-Block
und liefert nie mehr als `Strong`.

Strenge Formen — lockeres Matchen hieße behaupten:

* Hardware `^0(?:281|261)\d{6}$`, Software `^10(?:37|39)\d{6}$`
* Blockkennung aus §B: `^\d{2}SW\d{6}$` (z. B. `10SW008917`) — die verbindet den
  Kennungsleser direkt mit der Blockkette
* Steuergerätetyp `EDC17[A-Z]?\d{2}`, `MED?17(\.\d+)*`, `ME17(\.\d+)*`
* VAG-Teilenummer: strenge Form **und** Präfix-Whitelist (03L, 06K, 04L, 03G, …) **und**
  in der Nähe eines `1037`-/`0281`-Treffers — sonst gar nicht melden
* VIN: 17 Zeichen aus `[A-HJ-NPR-Z0-9]` **und** WMI aus {WVW, WAU, TMB, VSS, TRU, WV1, WV2}

`VolvoSplitter.Core/TriCore/VagEcuCatalog.cs` hält die Steuergerätetabelle aus §C:
Typ → Kandidaten-MCU, Kraftstoffart, Kurzbeschreibung. Zwei Aufgaben: **Bausteinwahl**
bei mehrdeutiger Größe, und die Anzeigezeile „EDC17C54 · TC1797 · Diesel · erste
Euro-6-Generation 2.0 TDI". Die Herkunft der Tabelle (Nutzerangabe, kein Herstellerdokument)
steht als Kommentar darüber und als Fußnote im Befund.

`VehicleInfo(Vin, ChassisNumber, Maker)` bleibt unangetastet — das ist ein
Volvo-Parametersektor-Record mit festen Offsets. `FlashDump` bekommt stattdessen
`public BoschIdentity? Identity { get; }`, gefüllt nur bei `Container == BoschBlockChain`.

---

## Phase 7 — Oberfläche, Karte, Bericht, Kommandozeile

| Ort | Heute | Künftig |
| --- | --- | --- |
| `MainWindow.xaml:5` | `Title="Volvo File Splitter"` | Produktname aus `Directory.Build.props` (§9) |
| `MainWindow.xaml:20` | `Text="TRW EMS2.3 · EMS2.4"` | benannt, im Leerzustand beide Familien, nach dem Laden `Detection.Profile.FamilyName`; bei `Ambiguous` ein Hinweisabzeichen |
| `MainWindow.xaml:34` und `.cs:237` | „MPC-Dump hierher ziehen" | „Flash-Abbild hierher ziehen" — an **beiden** Stellen, `ResetDropZone` setzt den Text zurück |
| `MainWindow.xaml:42` | „.MPC · .BIN · Rohabbild des Flash" | zusätzlich `.ORI` |
| `MainWindow.xaml:366-370` | feste Zeile „laut NXP-Referenzhandbuch, Tabelle 4-2" | benannt, gebunden an `PhysicalLayout.SourceNote` |
| `MainWindow.xaml.cs:84-85` | `FlashFormat.FamilyName/MicroName(_dump.Family)` | `_dump.Profile.FamilyName` / `.MicroName` |
| `MainWindow.xaml.cs:113-149` `UpdateIdentity` | nur `VehicleInfo` + `EcuReport` | zusätzlicher Zweig für `_dump.Identity`, füllt dieselben TextBlöcke |
| `MainWindow.xaml.cs:212` | Filter `*.mpc;*.bin` | zusätzlich `*.ori` |
| `MainWindow.xaml.cs` | Aktionen immer aktiv | Speichern / CRC reparieren / Sektor ersetzen ausgegraut, wenn `!Profile.SupportsWriteBack`, mit Hinweistext |
| `Controls/FlashMap.cs:108` | festes 1-MiB-Raster | Schrittweite aus `ImageSize` abgeleitet (Zweierpotenz, geklemmt 0x10000…0x100000, Ziel 8–16 Linien) |
| `Controls/FlashMap.cs:181,184` | `X6` | `Hex.Addr` |
| `DumpReport.cs:16-17` | `FlashFormat.FamilyName/MicroName` | `dump.Profile.*`, plus Zeile „Erkennung:" mit `Detection.Evidence` |
| `DumpReport.cs:62` | `"Physische Blöcke (MPC5777C, Referenzhandbuch Tabelle 4-2):"` | `$"Physische Blöcke ({layout.SourceNote}):"` |
| `DumpReport.cs` neu | — | Abschnitte „Kennungen" (mit Fundorten), „Blockkette" (Reihenfolge, Art, Kennung, Zeigertabellen), „Löschsektoren" wenn vorhanden |
| `Program.cs:55` | `FlashFormat.FamilyName(dump.Family)` | `dump.Profile.FamilyName` |
| `Program.cs:78-79` | `*.mpc`, `*.bin` | zusätzlich `*.ori` |
| `Program.cs:123-139` | „Volvo File Splitter — Stapelbetrieb" | Produktname, unterstützte Familien, neue Schalter `--profile <name>`, `--list-profiles` |

`FlashMap` funktioniert für TriCore ohne weitere Änderung: liegen keine `Sectors` vor,
zeichnet es die `Regions` — genau das gewünschte Bild.

---

## Phase 8 — Tests

`TestDump.cs` bleibt unangetastet bis auf zwei additive Helfer `WriteLe`/`ReadLe`.
Neu `VolvoSplitter.Core.Tests/TriCoreDump.cs`:

```csharp
public static byte[] Pflash(long size, params (long Start, byte[] Bytes)[] parts);
public static byte[] CodeBlock(int length, long cpuBase, int pointerEvery = 64);
public static byte[] CalibrationBlock(int length);      // monotone u16le-Achsen, Entropie ≈ 4
// setzt blockEnd = cpuStart + size - 4 (I1) und legt die Tabellen im Block ab (I2)
public static byte[] BoschBlockHeader(byte id, long cpuStart, long size, long nextCpu,
                                      string identifier, uint[]? table1, uint[]? table2,
                                      int checksumStructureCount, uint flags = 0);
public static byte[] BlockChain(TriCoreDevice device, params (byte Id, long Cpu, long Size)[] chain);
public static byte[] Tc1797Container(byte[] pmu0, byte[] pmu1, byte[]? external = null);
/// Die Blockkarte aus §B als fertiges 8-MiB-Abbild — Grundlage der Positivkontrolle.
public static byte[] SampleMed17Image();
```

Zusicherungen, in der Handschrift von `OpaqueRegion_DoesNotClaimEncryptionAsFact`:

1. `Tc1796SectorMap_AccountsForEveryByte` / `Tc1797SectorMap_AccountsForEveryByte` — je Bank exakt 2 MiB.
2. `BlockChain_WalksTheSampleLayout` — Positivkontrolle mit **genau der Blockkarte aus §B**: acht Blöcke über PMU0/PMU1/Extern, alle `Verified`, Reihenfolge und Größen wie dort.
3. `BlockEnd_MustBeStartPlusSizeMinusFour` — ein Kopf mit `blockEnd == start + size` (statt `− 4`) wird **verworfen**. Das schützt die tragende Regel I1 gegen unbemerktes Aufweichen.
4. `TablePointerOutsideOwnBlock_RejectsHeader` — Regel I2.
5. `NextSectorZero_EndsChainCleanly` — Regel I3, kein Fehlerzustand.
6. `BrokenChainLink_StopsWalkWithoutClaimingBlocks` — `nextSector` zeigt ins Leere; kein erfundener Block.
7. `CyclicChain_TerminatesAndIsReported`.
8. `UnalignedBlockSize_IsAccepted` — Größe `0xFD04` wie Block 4; keine Sektorbündigkeitsprüfung (I5).
9. `TableEntriesAreNotValidatedAsAddresses` — eine Tabelle mit `0xC0000070`, `0`, `0xFFFFFFFF` und `0xF7C` wird unverändert übernommen (F1).
10. `TablePointerIntoOtherBank_ResolvesGlobally` — ein PMU0-Block mit `0x808084AC` in der Tabelle liest aus PMU1, nicht ins Leere (F2).
11. `MissingDeadbeefMarker_RejectsBlock` — Regel I7; ein sonst tadelloser Kopf ohne `0xDEADBEEF` am Blockende wird verworfen.
12. `BlockGaps_AreReportedWithoutInterpretation` — die sechs Lücken der Beispielkarte tauchen als Beobachtung auf, ohne Deutung.
13. `Crc32Algo_MatchesExpectedValue` — Bosch-CRC32 über einen bekannten Bereich mit Startwert `0xFADECAFE` ergibt `0x35015001`. Dazu die Gegenprobe `~0xCAFEAFFE == 0x35015001`.
14. `Add32AndAdd16_MatchExpectedValue` — beide ergeben `0xCAFEAFFE`.
15. `TamperedCalibration_YieldsCrcMismatch_NotVerified` — ein Byte im geprüften Bereich verändert → der Block bekommt `SectorStatus.CrcMismatch`, **nicht** `Verified`. Der wichtigste Fall überhaupt.
16. `UnknownAlgorithmId_IsReportedNotGuessed` — ein `csAlgorithm` außerhalb {0x00, 0x01, 0x10} führt zu „unbekannt", nicht zu einem geratenen Verfahren.
17. `ChecksumStructures_AreThirtyTwoBytesFromOffset34` — Positivkontrolle des Abgleichs aus §B5: bei 1 und bei 4 Strukturen landet das Prüfwort auf +0x54 bzw. +0xB4.
18. `OtpFlag_IsReported` — Bit `0x00800000` wird als OTP ausgewiesen.
19. `ScanAndChainWalk_AgreeOnTheSampleImage` — beide Suchverfahren liefern dieselben acht Blöcke; die Übereinstimmung erscheint als Beleg.
20. `ChainBroken_ScanStillFindsBlocks` — bei gerissener Kette findet die Abtastung die Blöcke weiterhin, und die Abweichung wird gemeldet.
21. `EcuVariantString_IsReadFromDatasetBlock` — `34/1/EDC17_C46/5/…` bei Dataset +0x78 ergibt `EDC17_C46` mit `Confirmed`.
22. `CvnConfigNotFound_IsReportedAsNotFound` — kein Ratewert, keine Null.
23. `SwIdentifier_IsTenBytesFollowedByEightUnknown` — `10SW008917` bei +0x1A, danach acht Bytes, die roh ausgewiesen und nicht gedeutet werden; die Form `\d{2}SW\d{6}` wird erkannt (I4).
24. `BlocksAreGroupedBySoftwareUnit` — die drei Kennungen des Beispiels gruppieren die acht Blöcke wie dort.
25. `TriCoreImage_WithoutChain_ClaimsNoVerifiedBlocks` — plausibler Code und Kennfelder, aber keine Kette → **kein** `SectorStatus.Verified`.
26. `CalibrationCandidate_IsNotCalledCalibration` — `Kind == RegionKind.Data`, `Confidence != Confirmed`, Beschreibung enthält „Kandidat", nicht „Kalibrierungssektor".
27. `TwoMegabyteImage_WithoutEcuString_DoesNotPickASingleMcu` — Mehrdeutigkeit TC1796 / TC1797-PMU0 wird gemeldet, nicht aufgelöst.
28. `EcuTypeString_SelectsTheMatchingMcu` — `EDC17CP44` führt zur TC1797-Karte.
29. `UnknownTriCoreDevice_ClaimsNoSectorMap` — `Generic`, `Complete == false`, `EraseSectors` leer, `SourceNote` sagt es.
30. `TriCoreDflashTail_IsNotAutomaticallyEeprom` — direkte Entsprechung zu `RegionBeyondLargeFlash_IsNotAutomaticallyEeprom`.
31. `DflashOnlyFile_IsNotMistakenForPflash` — 0x20000-Datei ohne Zeigerdichte → `Unknown`.
32. `CachedAndUncachedAddresses_MapToTheSameOffset` — `0x80020000` und `0xA0020000` ergeben denselben Datei-Offset.
33. `BoschIdentity_ReportsOffsets_NotJustValues`.
34. `VagPartNumberCandidate_RequiresStrictShape` — beliebige 11 Ziffern werden nicht als VAG-Teilenummer gemeldet.
35. `Detection_IsAmbiguous_WhenBothFamiliesScoreClose`.
36. `TriCoreProfile_DoesNotOfferWriteBack` — `SupportsWriteBack == false`, `Save`/`RepairCrc`/`ReplaceSector` verweigern.
37. `EmsImage_StillDetectedAfterTriCoreSupport` — Regression gegen Erkennungsschäden.

**Echte Abbilder:** die synthetischen Dateien prüfen die Mechanik, nicht die Trefferquote
in der Wirklichkeit. Vor der Veröffentlichung von Phase 4 mindestens je ein echtes
MED17- und EDC17-Abbild manuell gegenprüfen. Diese Dateien **nicht** einchecken — sie
enthalten eine VIN. Ein `fixtures/`-Ordner in `.gitignore` plus Tests, die bei Abwesenheit
übersprungen werden (`Skip`), ist der richtige Kompromiss.

---

## Phase 9 — Dokumentation und Benennung

### Dokumentation

| Abschnitt in `README.md` | Änderung |
| --- | --- |
| `## Überblick` (Z. 27) | zweite Gerätefamilie nennen |
| `## Sektorformat` (Z. 229) | klarstellen, dass es das TRW-Format beschreibt und für TriCore nicht gilt |
| `## Unterstützte Steuergeräte` (Z. 269) | Satz Z. 271 „Die Familie wird allein an der Dateigröße erkannt" stimmt nach Phase 2 nicht mehr — durch die Prüfkette ersetzen; Tabelle EDC17/MED17/ME17 → MCU aufnehmen, mit Kennzeichnung, welche Sektorkarte hinterlegt ist |
| `## Physisches MPC5777C-Layout` (Z. 291) | zu „Physische Layouts" verallgemeinern, TC1796-/TC1797-Karten daneben |
| **neu** `## Bosch-Blockkette` | die Kopfstruktur aus §B samt der nachgerechneten Invarianten I1–I6 und der Beispiel-Blockkarte; **Quellenangabe** auf `fanyi3315/bosch-med17-block-reader` mit dem Hinweis, dass es Community-Reverse-Engineering an einem einzelnen Abbild ist |
| `### Was das Werkzeug bewusst nicht behauptet` (Z. 359) | erweitern: keine Blöcke ohne geprüfte Kette; keine Deutung der Prüfsummenstrukturen; keine geratene MCU bei 2 MiB; kein bestätigter Kalibrierbereich aus Entropie allein |
| `## Referenzunterlagen` (Z. 507) | die beiden Infineon-Datenblätter mit Version und Link; das genannte GitHub-Repository als Formatquelle |

`CHANGELOG.md` bekommt einen Eintrag, der die Grenze ausdrücklich nennt: gelesen, nicht
geschrieben; gegen synthetische Abbilder geprüft, an echten nur stichprobenweise.

### Benennung — Empfehlung

Namensraum `VolvoSplitter`, Assemblys `VolvoSplitter`/`VolvoSplitter.Core`/`volvosplit`
und Repository-Name bleiben durch alle Phasen **unverändert**. v1.0.0 ist mit
Release-Artefakten veröffentlicht; der Namensraum steht in rund 25 Dateien und jedem
Testkopf, die Artefaktnamen sind in `release.yml` hartkodiert. Ein Rename bricht jede
Verknüpfung und jedes Skript der Nutzer — für null Funktionsgewinn.

Stattdessen mit Phase 7 nur die **nutzersichtbaren Produktzeichenketten** ändern:
`Directory.Build.props` `<Product>`, `VolvoSplitter.csproj` `<ApplicationTitle>`,
`MainWindow.xaml:5` `Title=`, `Program.cs:124` Banner. Vorschlag **„Flash File Splitter"** —
behält die Wortmarke, verliert den Herstellerbezug. Rund sechs Zeilen, und damit der
größte Teil des Ehrlichkeitsnutzens. Die vollständige Umbenennung auf `EcuSplitter` /
`ecusplit` bleibt ein v2.0.0-Ereignis, nach Bewährung an echten Abbildern.

Nicht umbenennen: `VolvoEcuBlock` — die Klasse parst ein Format mit dem Magic `VOLVOECU`,
der Name ist korrekt. `FlashFormat` — eine Zeile Klassendoku „TRW-spezifisch" reicht,
eine Umbenennung kostet ~30 Testreferenzen für null Funktion.

---

## Verifikation

1. `dotnet test` nach **jeder** Phase; Phase 1 muss die 49 bestehenden Fälle unverändert
   grün lassen, mit genau einer geänderten Testzeile.
2. `dotnet build` für alle vier Projekte. Unter Linux sind Core, CLI und Tests baubar;
   die GUI ist `net10.0-windows` — das entspricht dem CI-Aufbau auf `windows-latest`.
3. CLI gegen ein synthetisches TriCore-Abbild mit vollständiger Blockkette:
   `dotnet run --project VolvoSplitter.Cli -- <abbild.bin> --out /tmp/out` — erwartet:
   erkanntes Profil mit Belegliste, physische Bankliste aus dem Datenblatt, gelesene
   Blockkette mit Art und Kennung, Kennungen mit Offsets, exportierte Blöcke, `bericht.txt`.
4. Gegenprobe: dieselbe CLI auf ein synthetisches EMS2.4-Abbild — die Ausgabe muss Zeichen
   für Zeichen der heutigen entsprechen.
5. Gegenprobe ohne Kette: TriCore-Abbild ohne gültigen Blockkopf — es darf **kein**
   `Verified`-Sektor entstehen, nur Regionen mit Konfidenzangabe.
6. Oberfläche (Windows): TriCore-Abbild laden, Speichern/Reparieren ausgegraut, Raster der
   Adressleiste bei 8 MiB sinnvoll, keine MPC5777C-Texte mehr.
7. **Freigabebedingung Phase 4:** je ein echtes MED17- und EDC17-Abbild manuell
   gegengeprüft — Kette läuft durch, Kennungen stimmen mit dem Steuergeräteaufkleber
   überein.

**Grenze der Verifikation, ausdrücklich:** im Repository liegen keine echten
Steuergeräte-Abbilder, weder Volvo noch VAG. Punkte 1–6 prüfen gegen synthetische Dateien.
Das gehört in `CHANGELOG.md` und in den Befundtext.

---

## Risiken und Nicht-Ziele

**Risiken**

| Risiko | Gegenmaßnahme |
| --- | --- |
| Die Struktur stammt aus **einem** Abbild und zwei Werkzeugen. Andere EDC17/MED17-Stände können abweichen. | Der Parser prüft über I1–I7 und rechnet die Prüfsummen nach, statt etwas vorauszusetzen. Ohne gültigen Block gibt es keine Blöcke, nur Regionen. Das Werkzeug wird dadurch schlechter, nicht falsch. |
| Herkunft der Formatkenntnis. | Nutzung ist freigegeben; beide Quellen werden in Quelltextkommentar und README genannt. Die zwei Fehler von Q1 (bankrelative Zeigerauflösung, abschneidende Puffergrenzen) werden nicht mitgenommen. |
| Die Prüfsummen*korrektur* ist mit diesem Material technisch möglich, aber nicht Auftrag. | Bleibt außerhalb (§Nicht-Ziele). Die Prüfung ist davon unberührt und wird vollständig umgesetzt. |
| Rechenaufwand: bitweise CRC32 über mehrere MiB. | Das Verfahren ist bitweise definiert; eine Tabellenvariante muss zeichengenau gleich rechnen, sonst wird sie nicht benutzt. Erst messen, dann optimieren. |
| Mehrdeutigkeit bei 2 MiB (TC1796 gegen TC1797-PMU0). | Melden statt raten; auflösbar über Kennungsstring und Steuergerätetabelle. |
| Falsch-positive Erkennung — ein EMS-Abbild enthält zufällig `0x80`-Muster. | TRW-Sonden punkten höher; die Zeigerdichte verlangt 4-Byte-Ausrichtung und eine Schwelle; bei knappem Vorsprung `Ambiguous` statt stiller Wahl. |
| Regression im Volvo-Pfad durch die Umtypisierung von `Mpc5777cLayout`. | Phase 1 als reiner Umbau mit unveränderter Testmenge und genau einer geänderten Testzeile. |
| Fehlende Sektorkarten für sechs der acht MCU. | `Generic` mit `Complete = false` und ehrlicher `SourceNote`; je Baustein ein späterer Commit mit Datenblattzitat. |
| Getrennte DFLASH-/ExtFlash-Dateien. | Als Grenze dokumentiert; eine Testzusicherung verhindert Fehlerkennung. Mehrdateimodell bewusst außerhalb dieses Vorhabens. |
| Entropieschwellen wurden an PowerPC gemessen. | Unverändert lassen, bis echte TriCore-Abbilder eine Messung erlauben. |
| Neuere MED17/EDC17 mit RSA-Signatur. | Nur lesbar, nicht prüfbar — wird als solches benannt, nicht als Fehler. |

**Nicht-Ziele**

- Kein Schreiben, kein Prüfsummen-Korrigieren, kein Blockersatz für TriCore-Abbilder.
  Prüfsummen werden **gerechnet und gemeldet**, nicht gestellt.
- Keine CVN-Korrektur — die CVN wird gelesen und ausgewiesen.
- Kein GF(2)-Löser, keine `dCSAdjust`-Berechnung.
- Kein Entpacken von FRF, ODX-F, SGO oder anderen Auslieferungscontainern; kein
  Entschlüsseln von irgendetwas.
- Kein Seed/Key, keine OBD-/Bench-Kommunikation, kein Flashen.
- Kein A2L-/DAMOS-Einlesen, keine Kennfeldbenennung.
- Kein universelles Steuergeräte-Framework: keine Plugin-Schnittstelle, keine
  Konfigurationsdateien, keine Assembly-Suche, keine JSON-Definitionen.
- Keine erfundene Blocktabelle und keine Übertragung des Simos18/TC1791-Layouts auf
  MEDC17 — jenes wird allenfalls als veröffentlichtes Analogon erwähnt.
- Keine Sektorkarten für TC1762/66/67/82/91/93 — nur Benennung.
- Keine vollständige Umbenennung des Projekts in diesem Vorhaben.

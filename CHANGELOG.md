# Änderungen

Das Format folgt lose [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
die Versionsnummern [Semantic Versioning](https://semver.org/lang/de/).

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

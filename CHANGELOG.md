# Änderungen

Das Format folgt lose [Keep a Changelog](https://keepachangelog.com/de/1.1.0/),
die Versionsnummern [Semantic Versioning](https://semver.org/lang/de/).

## Unveröffentlicht

> Die Zahlen der folgenden Abschnitte sind an **1516 echten VAG-EDC17-Abbildern** gemessen, nicht
> geschätzt. Dieser Bestand ist privat und liegt dem Repository **nicht** bei; im Repository liegen
> weiterhin keine Steuergeräte-Abbilder, weder Volvo noch VAG. Ausgangsstand der Messung: **243**
> Abbilder ohne Blöcke, **7 826** Blöcke.

### Der Nullpunkt eines Abbilds wird gemessen, nicht gesetzt

Datei-Offset 0 lag bedingungslos auf `0x80000000`. Damit hing jede Kopfprüfung an einer
Annahme, die nie geprüft wurde — und **227 von 1516** echten VAG-EDC17-Abbildern verloren
dadurch *sämtliche* Blöcke, obwohl jeder ihrer Blockköpfe seine Lage selbst nennt
(`blockStart = blockEnd − size + 4`). Betroffen war alles, was nicht an der PFLASH-Basis
beginnt: Teilauslesungen ab `0x80180000`, reine PMU1-Abzüge ab `0x80800000`, herausgelöste
Einzelblöcke.

`BoschBlockChain.MeasureWindowStarts` sucht Kopfkandidaten jetzt **layoutfrei** — nur mit den
Regeln, die ohne Basis auskommen — und leitet aus ihnen die CPU-Adresse des Datei-Offsets 0 ab.
Die Kandidaten laufen als weitere Layout-Kandidaten durch dieselbe Zählung bestätigter Köpfe,
mit der schon die Bankaufteilung entschieden wird; `TriCoreLayout.For` nimmt den Nullpunkt als
`windowStart` entgegen, ohne Angabe gilt weiterhin der Anfang des Bausteins.

Zwei Sperren gegen den Zufallstreffer, beide nachgemessen:

* Eine gemessene Basis muss die Vorgabe **echt schlagen**, nicht bloß einholen. Ein Kopf
  bestätigt die aus ihm selbst abgeleitete Basis zwangsläufig — Gleichstand ist deshalb kein
  Beleg. Die naive Fassung mit einer gemeinsamen Rangliste ließ zwei EDC17CP74-Abbilder von
  einem Block auf null fallen.
* Ein Fensteranfang, der in keiner Bank des Bausteins liegt, wird nicht stillschweigend auf die
  nächste Bank aufgerundet, sondern als eine Partition ohne Bankgliederung ausgewiesen.

Am Bestand: **243 → 2** Abbilder ohne Blöcke, **7 826 → 8 441** Blöcke, **kein einziges**
vorher gelesenes Abbild verschlechtert. Alle 8 441 herausgelösten Sektordateien beginnen mit
einer bekannten Blockart, tragen die Länge aus ihrem Größenfeld, enden auf `0xDEADBEEF` und
sind byteweise im Quellabbild enthalten; 8 362 tragen eine nachgerechnete, aufgehende
Prüfsumme (vorher 7 800). Am schärfsten zeigt es sich an den kleinen Dateien: **alle 136**
Abbilder unter 1 MiB waren stumm, ausnahmslos, und alle 136 liefern jetzt Blöcke. 227 Abbilder
lesen ihr Layout aus einem gemessenen Nullpunkt, verteilt auf neun verschiedene Basen — die
häufigste ist `0x80180000` (110 Abbilder).

Die gemeldeten **Prüfsummenabweichungen steigen dabei von 26 auf 79**, und das ist kein
Rückschritt: kein vorher bestätigter Block ist darunter. Alle 53 hängen an neu gefundenen
Blöcken, 43 davon sind Tuning-protection- und Emulation-extension-Blöcke — genau die Blockarten,
deren Abweichung das Werkzeug zeigen soll. Elf der 79 sind allerdings gar keine Abweichungen —
siehe „Ein nie gestelltes Stellwort ist keine Abweichung“ weiter unten.

Nebenwirkung mitbehoben: der Bericht wies für eine Teilauslesung `0x000000–0x080000 →
0x80000000–0x80080000` aus — eine Aussage, der der Blockkopf im selben Abbild widersprach.

### Ein unlesbares Kennungsfeld verwirft den Blockkopf nicht mehr

`swIdentifier` war die achte Regel der Kopfprüfung: ein einziges Byte außerhalb des
Textbereichs ließ den ganzen Kopf durchfallen, obwohl `0xDEADBEEF`, `blockEnd`, Größe und
Strukturzahl längst zutrafen. 25 Abbilder des Bestands füllen das Feld mit `0xAF` und verloren
dadurch zusammen **123 Blöcke**, 14 davon restlos alle.

Ein unlesbares Kennungsfeld ist eine **fehlende Kennung**, kein ungültiger Kopf: es wird gelesen
statt geprüft und ergibt dann die leere Kennung. `0xAF` als drittes Füllbyte zu benennen wäre
dagegen nicht gedeckt — der Wert kommt in keiner der ausgewerteten Unterlagen als
Bosch-Wächterwert vor, er steht nur in diesen Abbildern. Gelesen wird alles oder nichts; aus
Binärrauschen den druckbaren Teil herauszuklauben erfände eine Teilenummer.

### „Nichts gefunden“ und „nicht verstanden“ sind zwei verschiedene Aussagen

Die Belegliste des Kettenlesers wurde vollständig gerechnet und dann weggeworfen: `ProbeTriCore`
übernahm aus dem Ergebnis nur Blöcke und Variante. In **keinem** der erzeugten Berichte stand
je einer der Sätze „Kein bestätigter Bosch-Blockkopf gefunden“, „Blockkopfkandidat bei …
nicht bestätigt“ oder „Kette bricht ab“ — obwohl der Quelltext über die verworfenen Kandidaten
selbst schreibt „Wird gemeldet, aber nicht als Block ausgegeben“.

Sie steht jetzt in den Erkennungsbelegen und damit in Bericht und Stapelausgabe. Der Deckel von
fünf aufgezählten Kandidaten bleibt: der Befund eines normal gelesenen Abbilds wächst dadurch im
Mittel um 2,8 Zeilen. Genau diese Sätze hätten den Nullpunktfehler oben sofort sichtbar gemacht.

Am Bestand: jedes der 1516 Abbilder trägt jetzt mindestens einen Beleg des Kettenlesers, 366
nennen ausdrücklich **verworfene** Kopfkandidaten (28 davon mit der Zusammenfassung „… und N
weitere“). „Kette läuft im Kreis“ bleibt bei null — das ist kein Fehlen, sondern ein Befund:
keine Kette im Bestand hat einen Zyklus.

### „gesichert“ trägt nur noch, was nachgerechnet ist

Die Bereichseinordnung vergab ihre höchste Beleglage — laut eigener Definition „durch Kopf,
Prüfsumme oder eindeutigen Inhalt belegt“ — an drei Aussagen, die an den Bytes falsch waren.
Gemessen an den 975 Bereichen, die der Bestand meldet: **15 Zeilen falsch, alle 15
nachgerechnet.** (Gezählt wird hier auf dem Stand *nach* der Nullpunkt-Berichtigung oben, denn
die entscheidet mit, wieviele kopflose Bereiche es überhaupt gibt.)

* **Drei Bytes belegen keinen Klartext.** Neben `src/` und `@(#)` wurde auch `../` gesucht, und
  ein Fund hebelte gleich dreifach aus: die Opak-Erkennung, die Bedingung aus Wiederholungsanteil
  und Entropie, und die Beleglage. Im ganzen Bestand kommen `src/` und `@(#)` in **null**
  Abbildern vor, `../` in 59 — ausgelöst hat die Heuristik also ausschließlich der Zufallstreffer,
  fünfmal, und war fünfmal falsch, viermal davon in einem Bereich mit **Entropie 8,00**, also
  mitten im Rauschen. Entscheidend ist jetzt nicht die Nadel, sondern dass sie *in einer
  Zeichenkette* steht: der druckbare ASCII-Lauf um den Fund muss 16 Byte erreichen. Gerechnet ist
  das (95/256)¹⁶, unter einem erwarteten Zufallstreffer je 4 MiB; gemessen ist der längste
  druckbare Lauf, der im Bestand eine der drei Nadeln enthält, **8 Byte** lang (`VV=VV../`) — ein
  echter Pfad wie `../src/appl/main.c` hat 18. Der gefundene Text steht jetzt im Befund, damit
  der Beleg zu sehen ist statt behauptet zu werden. Und die Opak-Erkennung geht vor: „Entropie
  8,00“ und „Programmcode im Klartext“ schließen einander aus. Verloren geht dabei kein echter
  Codebereich — es gab keinen.
* **Geringe Entropie ist keine Konstanz.** „Konstantes Füllbyte 0x??“ hing an `entropy < 0.5`,
  und das heißt „stark ungleichverteilt“; genannt wurde `data[start]`, also schlicht das erste
  Byte des Bereichs. Zehn der 879 so gemeldeten Bereiche waren nicht konstant, und in fünf davon
  kam das genannte Byte im ganzen Bereich **genau einmal** vor, während 99,94 % auf `0x00`
  standen. Geprüft wird die Konstanz jetzt an den Bytes, benannt wird das häufigste Byte mit
  seinem Anteil, und „gesichert“ trägt nur der nachgerechnete Fall: aus „Konstantes Füllbyte
  0x1D · gesichert“ wird „Überwiegend 0x00 (99,94 %), 5 abweichende Bytes · stark gestützt“.
* **Der Kalibrierungskandidat ist gestrichen.** Er verlangte vier Merkmale zugleich und griff in
  **0 von 1516** Abbildern — und er kann es auch nicht: der Anteil monotoner Fenster erreicht
  höchstens 0,0292, und zwar in den Dataset-Blöcken, in denen die Kalibrierdaten wirklich liegen,
  während die kopflosen Restbereiche schon 0,0220 erreichen. Die beiden Mengen überlappen, es gibt
  also keine Schwelle, die sie trennt — auch keine am Bestand kalibrierte. Ein Zweig, der nur auf
  synthetischen Rampen anspricht, steht im Bericht als Möglichkeit, die es nicht gibt.

Am Bestand: 975 Bereiche, davon 960 Zeilen unverändert und 15 geändert; „gesichert“ 884 → 869.
Von den verbliebenen 869 „Konstantes Füllbyte“ und den 10 neuen „Überwiegend 0x…“ ist keine
einzige an den Bytes falsch — vorher waren es 10 von 879.

### Ein nie gestelltes Stellwort ist keine Abweichung

Der Eigentümer des Bestands rechnet damit, dass ein Teil seiner Abbilder vorab verändert wurde;
48 der 1516 melden mindestens eine Prüfsummenabweichung, bei Dateien mit Tuning-Hinweis im Namen
sind es 10 %. Eine Blockart fällt aus diesem Bild heraus: **„Emulation extension chip“ (`0xD0`)
meldete in 11 von 11 Fällen eine Abweichung** — ausnahmslos. Eine Blockart, deren Prüfsumme nie
aufgeht, ist keine Manipulation.

An den Bytes ist die Sache eindeutig. Im Kopf eines solchen Blocks steht `0xAF` — das Füllbyte
dieser Gerätefamilie für „nicht gesetzt“, dasselbe, das im Kennungsfeld schon einen eigenen
Abschnitt oben hat — gleich viermal: in `nextSector`, in der Kennung, in den acht unerklärten
Bytes bei +0x24 **und im Stellwort `checksumAdjust` bei +0x30**. Der Rumpf trägt dagegen echte
Daten. Und die CRC32 läuft ohne Schlussabgleich über einen Bereich, der das Stellwort
**einschließt**, damit das Register am Ende auf `0x35015001` steht — das stand als Begründung
schon im Kopfkommentar von `BoschChecksum`. Ist das Stellwort nie gestellt worden, *kann* die
Rechnung nicht aufgehen. Gemeldet wurde also eine Abweichung, wo es gar keine gestellte
Prüfsumme gibt.

„Weicht ab“ und „nicht gestellt“ sind deshalb jetzt zwei Zustände, und „in Ordnung“ ist der
neue ausdrücklich auch nicht — es gibt drei:

* `BoschChecksumStructure.NotStamped` trägt die Unterscheidung, `BoschBlock.ChecksumMismatch`
  schließt sie aus, `ChecksumNotStamped` und `ChecksumsVerified` stehen daneben. `VerifiedCount`
  und die Erkennungsbelege zählen einen ungestellten Block nicht mehr als bestätigt — er ist
  ungeprüft, nicht bestätigt.
* `SectorStatus.ChecksumNotStamped` steht zwischen `Verified` und `CrcMismatch`; `CrcOk` bleibt
  für ihn falsch.
* **Die Oberfläche sagt denselben dritten Satz.** Die Sektorkarte hat einen eigenen Zweig für
  den neuen Zustand — ohne ihn fiele er in die Vorgabe der Karte, und die lautet „Prüfsumme
  stimmt“: aus der Überbezichtigung wäre eine Falschbestätigung geworden, das schlechtere
  Ergebnis von beiden. „Prüfsumme korrigieren“ bleibt dabei ausgeblendet, denn es gibt nichts
  zu korrigieren. Vermerk und Zähler nach dem Herausschreiben gehen nicht mehr über die
  Verneinung von `CrcOk`, sondern über den Zustand selbst — sonst meldete die Statuszeile „mit
  abweichender Prüfsumme“ für einen Block, dessen Prüfsumme nie gestellt wurde.
* Der Befund schreibt „nicht gestellt: Stellwort steht auf 0xAFAFAFAF, gerechnet 0xD519CB36“
  statt „weicht ab: gerechnet …, erwartet …“, führt das Stellwort als eigenes Kopffeld und
  färbt die Zeile nicht mehr als Warnung. Die Stapelausgabe bekommt eine dritte, ebenfalls
  vierstellige Marke: `n.g.` neben `ok` und `CRC!`.

Zwei Sperren gegen ein zu weites Netz, beide nachgemessen:

* **Das Stellwort erklärt nur die Bereiche, die es einschließen.** Über die 8 441 Blöcke des
  Bestands schließt genau **eine** Struktur je Block den eigenen Kopf ein; die übrigen 5 973
  liegen daneben und werden vom Stellwort nicht berührt. Läge dort eine Abweichung, bliebe sie
  eine — sonst entschuldigte ein einziges Füllwort im Kopf jede Abweichung im ganzen Block.
* **`0x00000000` gilt nicht als Füllmuster.** Es kommt im Bestand als Stellwort null Mal vor —
  wie `0xFFFFFFFF`, das trotzdem zählt, weil ein nie beschriebenes Wort im NOR-Flash genau so
  aussieht. Die Null ist dagegen ein Wert, den ein wirklich gestelltes Stellwort annehmen kann;
  sie mitzuzählen verschluckte echte Abweichungen ohne jeden Beleg.

Am Bestand, gemessen über alle 1516 Abbilder, Blockzahl und Blockgrenzen unverändert (8 441
Blöcke, 2 Abbilder ohne Blöcke): **8 362 Blöcke „ok“ · 68 „weicht ab“ · 11 „nicht gestellt“**,
vorher 8 362 · 79 · —. Umgestuft wurden **genau** die 11 `0xD0`-Blöcke, und zwar nicht bloß der
Zahl nach: die Menge der abweichenden Blöcke nachher ist die Menge vorher minus diese 11, kein
Block ist neu dazugekommen und keiner der echten Abweichungen ist verschwunden. Die Zahl der
Abbilder mit mindestens einer Abweichung fällt von 48 auf 37.

### Was ausdrücklich nicht geändert wurde

* **Zwei Abbilder bleiben ohne Blöcke** (von vorher 243). Beide sagen jetzt wenigstens, was sie
  verworfen haben: 155 bzw. 1576 Kopfkandidaten, keiner bestätigt. An den Bytes nachgesehen ist
  das die richtige Antwort. Im ersten kommt `0xDEADBEEF` im **ganzen Abbild kein einziges Mal**
  vor; die beiden strukturell plausiblen Köpfe bei `0x180000` und `0x1A0000` tragen am
  errechneten Blockende `0xFFFFFFFF`. Das zweite nennt sich `EDC17 XY01`, steht damit in keiner
  Zeile der Steuergerätetabelle, und keiner seiner Kandidaten hält der `blockEnd`-Regel stand.
* **Eine layoutfreie Rohsuche findet 79 Kopfkandidaten mehr**, als das Werkzeug meldet — 8 520
  gegen 8 441, verteilt auf 22 Abbilder. 77 davon nennen eine Basis *unterhalb* von
  `0x80000000`: das sind aneinandergehängte Mehrfach-Auslesungen, deren zweite und dritte Kopie
  liegen bleibt. Das ist Untermeldung, keine Erfindung — und ein Mehrfenstermodell ist nicht Teil
  dieses Werkzeugs, `FlashDump` hält genau ein Abbild. Der Punkt bleibt offen.
* **Die CVN wird in keinem einzigen der 1516 Abbilder gefunden.** Ob `FindCvn` ein falsches
  Zeigermodell benutzt oder VAG-EDC17-Stände sie schlicht anders ablegen, lässt sich ohne eine
  unabhängige Referenz-CVN — etwa aus einem OBD-Auslesegerät — nicht entscheiden. Ein Ratewert
  wäre schlechter als keiner; der offene Punkt aus v1.1.1 bleibt bestehen.
* **Die Farbe des Abbildstreifens bleibt zweiwertig.** `Controls/FlashMap.cs` malt
  `CrcOk ? signal : warn`; ein nie gestellter Block bekommt damit den Warnton. Das ist die
  ungefährliche Seite — der Signalton behauptete eine Bestätigung —, und es ist eine Farbe, kein
  Satz. Die Karte daneben nennt den Zustand beim Namen. Die WPF-Anwendung zielt auf
  `net10.0-windows` und lässt sich hier nicht bauen; für eine dritte Farbe ohne Übersetzer ist
  das zu wenig Gewinn. Der Punkt steht als solcher da.

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

### Die Kommandozeile ersetzt kein Ergebnis mehr durch ein anderes

Zwei gleichnamige Abbilder in **einem** Aufruf teilten sich stillschweigend einen Ausgabeordner.
`volvosplit a/gleich.bin b/gleich.bin --out out` meldete „2/2 Abbilder verarbeitet“ mit
Rückgabewert 0 und legte die Sektoren beider Steuergeräte nebeneinander in `out/gleich/` — dazu
**eine** `bericht.txt`, die nur das zuletzt zerlegte beschrieb. Wer sie las, sah zwei Blöcke und
fand neun Dateien. Bei gleicher Teilenummer wurde byteweise überschrieben, ohne jede Meldung. Im
gelieferten Bestand löst das niemand aus (kein doppelter Basisname unter 1516); erreichbar ist es,
sobald zwei Auslesungen `Original.bin` heißen, und das ist der Regelfall, nicht die Ausnahme.

Das zweite Abbild bekommt jetzt einen eigenen Ordner, benannt nach seinem **Elternordner**
(`out/b_gleich/`), notfalls durchnummeriert, und die Ausweichung wird gemeldet. Eine laufende
Nummer sagte nur „das zweite“, der Elternordner sagt „welches“ — für eine Ablage, die als Beleg
dienen soll, ist das der Unterschied zwischen einer Kennung und einem Namen. Abgebrochen wird
nicht: ein Stapellauf über hunderte Abbilder darf nicht am zweiten Fund sterben. Belegt heißt
dabei „in diesem Lauf vergeben“ und nicht „liegt schon auf der Platte“, sonst wüchse bei jedem
Durchgang ein weiterer Satz Ordner heran.

### Ein fehlender Optionswert ist ein Vertipper, keine Vorgabe

Die Argumentzerlegung prüfte nur, *ob* nach einer Option noch ein Argument kommt, und übersprang
sie sonst wortlos. Gemessen: `volvosplit probe.bin -o` legte `probe_sektoren/` neben dem Abbild an
statt im angegebenen Ordner, `--out o13 -p` ließ genau die Erkennung laufen, die übersteuert
werden sollte, und `-o -f` legte einen Ordner namens `-f` an und ließ `--fixed` fallen — alle drei
mit Rückgabewert 0. Bei **falsch geschriebenen** Werten hielt sich dasselbe Programm längst an die
richtige Regel (`-p tc1798` → 1, `-r pdf` → 1); sie gilt jetzt auch für den fehlenden.

Ebenso ist eine **unbekannte** Option ein Aufruffehler statt eines Dateinamens: `--fixd` erzeugte
„Nicht gefunden: --fixd“ auf der Fehlerausgabe, Rückgabewert 0 — und der Lauf lief ohne die Option
weiter, die der Nutzer gesetzt zu haben glaubte. Neu ist `--` als Trenner; danach ist alles ein
Dateiname. Ohne ihn wäre die Prüfung eine Sackgasse für Abbilder, die wie eine Option heißen.

### Entdoppelt wird, wie das Dateisystem es sieht

Die Dateisammlung faltete Pfade ohne Rücksicht auf Groß- und Kleinschreibung zusammen. Unter
Windows ist das richtig, unter **Linux** ist es Datenverlust: ein Ordner mit `A.bin` und `a.bin`
meldete „1/1 Abbilder verarbeitet“, und eine der beiden Dateien wurde nie gelesen. Verglichen wird
jetzt nach der Vorgabe des jeweiligen Systems, entdoppelt über den vollen Pfad statt über die
Schreibweise (`probe.bin` und `./probe.bin` sind ein Abbild), und **jede verworfene Nennung wird
gemeldet**.

Argumentzerlegung, Dateisammlung und Zielordnerwahl stehen dafür konsolenfrei in
`VolvoSplitter.Cli/CommandLine.cs`; der Programmrumpf hält Ein- und Ausgabe. Die Kommandozeile war
bislang ungetestet und hat jetzt 23 Tests.

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

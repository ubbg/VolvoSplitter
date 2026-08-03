using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core;

/// <summary>
/// Ergebnis der Erkennung: das gewählte Profil, seine Punktzahl, die des
/// Zweitplatzierten und die Belege, die dazu geführt haben.
/// </summary>
/// <param name="Ambiguous">
/// Der Vorsprung war zu knapp. Das Profil ist trotzdem gesetzt, aber beide
/// Kandidaten werden gezeigt.
/// </param>
public sealed record Detection(EcuProfile Profile, int Score, int Runner,
                               IReadOnlyList<string> Evidence, bool Ambiguous)
{
    /// <summary>Name des Zweitplatzierten, falls es einen gab.</summary>
    public string? RunnerName { get; init; }

    /// <summary>
    /// Der Baustein ließ sich nicht auf einen festlegen — bei 2 MiB etwa
    /// TC1796 gegen die PMU0 eines TC1797. Gemeldet, nicht geraten.
    /// </summary>
    public bool DeviceAmbiguous { get; init; }

    public IReadOnlyList<string> DeviceCandidates { get; init; } = [];

    /// <summary>Ergebnis des Blockkettenlaufs, falls das Profil eine Kette vorsieht.</summary>
    public BoschChainResult Chain { get; init; } = BoschChainResult.Empty;

    public BoschIdentity? Identity { get; init; }

    public string Summary => Ambiguous && RunnerName is { } runner
        ? $"{Profile.Headline} ({Score} Punkte) — knapp vor {runner} ({Runner})"
        : $"{Profile.Headline} ({Score} Punkte)";
}

/// <summary>
/// Erkennt, welches Steuergerät vorliegt — an Belegen im Abbild, nicht an der
/// Dateigröße. Bis Version 1 genügte die Größe: eine Datei bis 0x500000 war
/// EMS2.3, darüber EMS2.4. Mit den TriCore-Abbildern stimmt das nicht mehr,
/// denn eine 2-MiB-Datei kann beides sein.
///
/// Jeder Beleg gibt Punkte, der höchste Stand gewinnt. Zwei Regeln halten das
/// ehrlich:
///
/// * <strong>Die Dateigröße entscheidet nie zwischen Herstellern</strong>, sie
///   bricht nur Gleichstände innerhalb eines Herstellers. Ausnahme ist die
///   exakte Containergröße des MPC5777C — die ist kein Größenmaß, sondern die
///   auf das Byte genaue Summe seiner Blockkarte.
/// * <strong>Ein knapper Vorsprung heißt mehrdeutig</strong>, nicht „gewonnen".
/// </summary>
public static class EcuDetector
{
    /// <summary>Unter dieser Punktzahl wird nichts behauptet.</summary>
    public const int MinimumScore = 30;

    /// <summary>Ist der Vorsprung kleiner, gilt das Ergebnis als mehrdeutig.</summary>
    public const int AmbiguityMargin = 20;

    private sealed record Candidate(EcuProfile Profile, int Score, List<string> Evidence)
    {
        public BoschChainResult Chain { get; init; } = BoschChainResult.Empty;
        public BoschIdentity? Identity { get; init; }
        public bool DeviceAmbiguous { get; init; }
        public IReadOnlyList<string> DeviceCandidates { get; init; } = [];
    }

    public static Detection Identify(byte[] data, EcuReport? report = null)
    {
        var candidates = new List<Candidate>
        {
            ProbeTrw(data, EcuProfiles.Ems23),
            ProbeTrw(data, EcuProfiles.Ems24),
            ProbeTriCore(data, report)
        };

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var best = candidates[0];
        var runner = candidates[1];

        if (best.Score < MinimumScore)
            return new Detection(EcuProfiles.Unknown(data.LongLength), best.Score, runner.Score,
                [$"Kein Profil erreicht die Mindestpunktzahl {MinimumScore} — " +
                 "es werden nur Bereiche gemeldet, kein Container",
                 .. best.Evidence],
                Ambiguous: false);

        bool ambiguous = best.Score - runner.Score < AmbiguityMargin;

        var evidence = new List<string>(best.Evidence);
        if (ambiguous)
            evidence.Add($"Vorsprung nur {best.Score - runner.Score} Punkte vor " +
                         $"{runner.Profile.Headline} ({runner.Score}) — Zuordnung nicht eindeutig");

        return new Detection(best.Profile, best.Score, runner.Score, evidence, ambiguous)
        {
            RunnerName = runner.Profile.Headline,
            DeviceAmbiguous = best.DeviceAmbiguous,
            DeviceCandidates = best.DeviceCandidates,
            Chain = best.Chain,
            Identity = best.Identity
        };
    }

    // ==================================================================
    // TRW — EMS2.3 und EMS2.4
    // ==================================================================

    private static Candidate ProbeTrw(byte[] data, EcuProfile profile)
    {
        var evidence = new List<string>();
        int score = 0;

        var headers = TrwContainer.FindHeaders(data).ToList();
        if (headers.Count > 0)
        {
            score += 50;
            evidence.Add($"{headers.Count} × Sektorkopf „v=1;a=" + "\" mit lesbarem o=-Feld");
        }

        var slots = profile.Slots;
        var matching = headers.Where(h => slots.Any(s => s.Start == h.Position ||
                                                        s.CpuOffset == h.CpuOffset)).ToList();
        if (matching.Count > 0)
        {
            score += 30;
            evidence.Add($"{matching.Count} Sektorkopf/-köpfe decken sich mit der " +
                         $"{profile.FamilyName}-Tabelle");
        }

        if (FindVolvoEcuBlock(data) is { } ecuAt)
        {
            score += 20;
            evidence.Add($"VOLVOECU-Modul bei {Hex.Addr(ecuAt)} parsebar");
        }

        foreach (var header in headers)
        {
            long makerAt = header.Position + FlashFormat.MakerOffset;
            if (makerAt + FlashFormat.MakerLength > data.LongLength) continue;
            if (TrwContainer.Ascii(data, makerAt, FlashFormat.MakerLength) != "VOLVO") continue;

            score += 20;
            evidence.Add($"Herstellerkennung „VOLVO\" im Parametersektor bei {Hex.Addr(makerAt)}");
            break;
        }

        // Die exakte Containergröße ist kein Größenmaß, sondern die auf das Byte
        // genaue Summe der MPC5777C-Blockkarte: 8 MiB + 4 × 64 KiB + 16 KiB.
        if (profile.Family == EcuFamily.Ems24 && data.LongLength == Mpc5777cLayout.ContainerSize)
        {
            score += 30;
            evidence.Add($"Dateigröße 0x{Mpc5777cLayout.ContainerSize:X} ist exakt die Summe der " +
                         "MPC5777C-Blockkarte (Large Flash + Low/Mid + UTEST)");
        }

        // Die alte Regel — nur noch Stichentscheid innerhalb von TRW.
        bool small = data.LongLength <= FlashFormat.Ems23MaxSize;
        if (small == (profile.Family == EcuFamily.Ems23)) score += 5;

        return new Candidate(profile, score, evidence);
    }

    private static long? FindVolvoEcuBlock(byte[] data)
    {
        var magic = VolvoEcuBlock.Magic;

        for (int at = 0; at + magic.Length <= data.Length; at++)
        {
            if (data[at] != magic[0]) continue;
            if (!data.AsSpan(at, magic.Length).SequenceEqual(magic)) continue;
            if (VolvoEcuBlock.TryParse(data, at, out _)) return at;
        }
        return null;
    }

    // ==================================================================
    // TriCore — EDC17 / MED17
    // ==================================================================

    private static Candidate ProbeTriCore(byte[] data, EcuReport? report)
    {
        var evidence = new List<string>();
        int score = 0;

        // 1. Baustein bestimmen, soweit es geht: Protokolldatei vor Kennung.
        var identity = BoschIdentity.Scan(data);
        var (device, deviceEvidence, deviceScore, ambiguous, candidates) =
            ChooseDevice(data, report, identity);

        score += deviceScore;
        evidence.AddRange(deviceEvidence);

        // 2. Blockkette lesen — der stärkste Beleg, den es hier gibt.
        var layout = TriCoreLayout.For(device, data.LongLength);
        var chain = BoschBlockChain.Read(data, layout);

        if (chain.Blocks.Count > 0)
        {
            score += 60;
            evidence.Add($"{chain.Blocks.Count} Bosch-Blockkopf/-köpfe bestätigt " +
                         $"(blockEnd-Regel und 0x{BoschBlockChain.EndMarker:X8} am Blockende)");

            int verified = chain.Blocks.Count(b => b.ChecksumsComputed && !b.ChecksumMismatch);
            if (verified > 0)
            {
                score += 80;
                evidence.Add($"{verified} Block/Blöcke mit rechnerisch bestätigter Prüfsumme");
            }
        }

        // 3. Variantenkennung aus dem Dataset-Block, feste Fundstelle.
        if (chain.Variant is { } variant)
        {
            score += 50;
            evidence.Add($"Variantenkennung „{variant}\" im Dataset-Block bei " +
                         $"{Hex.Addr(chain.VariantOffset ?? 0)}");
        }

        // Kennungen erneut lesen, jetzt mit der festen Fundstelle als Vorrang.
        identity = BoschIdentity.Scan(data, chain.Variant, chain.VariantOffset ?? 0) ?? identity;

        if (identity is { } found && found.Hits.Count > 0)
        {
            var strong = found.Hits.Where(h => h.Kind is "Hardware" or "Software" or "Steuergerät"
                                                     or "Blockkennung").ToList();
            if (strong.Count > 0)
            {
                score += 40;
                evidence.Add("Bosch-Kennungen im Abbild: " +
                             string.Join(", ", strong.Take(4).Select(h => h.Value)));
            }
        }

        // 4. Zeigerdichte — 4-Byte-ausgerichtete little-endian-Wörter, die auf
        //    den TriCore-Adressraum zeigen.
        double density = PointerDensity(data);
        if (density >= PointerDensityThreshold)
        {
            score += 30;
            evidence.Add($"{density:P1} der ausgerichteten Wörter zeigen in den " +
                         "TriCore-Adressraum (0x80…, 0xA0…, 0x84…)");
        }

        // 5. Größen — nur Stichentscheid, nie ausschlaggebend.
        if (data.LongLength is 0x200000 or 0x300000 or 0x400000 or 0x800000) score += 10;
        if (device.ProgramSize > 0 && device.Data is { } dflash &&
            data.LongLength == device.ProgramSize + dflash.Size)
        {
            score += 10;
            evidence.Add($"Größe entspricht {device.Name}-PFLASH plus angehängtem DFLASH");
        }

        return new Candidate(EcuProfiles.ForTriCore(device, data.LongLength, FamilyNameFor(identity)),
                             score, evidence)
        {
            Chain = chain,
            Identity = identity,
            DeviceAmbiguous = ambiguous,
            DeviceCandidates = candidates
        };
    }

    /// <summary>Anteil, ab dem die Zeigerdichte als Beleg zählt.</summary>
    private const double PointerDensityThreshold = 0.02;

    private static string FamilyNameFor(BoschIdentity? identity)
    {
        string? type = identity?.EcuType;
        if (type is null) return "EDC17 / MED17 (TriCore)";

        var entry = VagEcuCatalog.Find(type);
        return entry is null ? $"{type} (TriCore)" : $"{entry.Type} · {entry.Fuel}";
    }

    /// <summary>
    /// Wählt den Baustein. Reihenfolge der Belege: Protokolldatei, dann
    /// Steuergerätetyp aus dem Abbild, dann — nur wenn die Blockkette es
    /// entscheidet — die Bank-Aufteilung. Bleibt es offen, wird nichts gewählt.
    /// </summary>
    private static (TriCoreDevice Device, List<string> Evidence, int Score,
                    bool Ambiguous, IReadOnlyList<string> Candidates)
        ChooseDevice(byte[] data, EcuReport? report, BoschIdentity? identity)
    {
        var evidence = new List<string>();

        if (report is not null && TriCoreDevice.ByName(report.Micro) is { } fromReport)
        {
            evidence.Add($"Protokolldatei nennt Micro „{report.Micro}\" → {fromReport.Name}");
            return (fromReport, evidence, 45, false, []);
        }

        string? ecuType = identity?.EcuType;
        var entry = VagEcuCatalog.Find(ecuType);

        if (entry is { Ambiguous: false } && TriCoreDevice.ByName(entry.Micros[0]) is { } fromType)
        {
            evidence.Add($"Steuergerätetyp {entry.Type} im Abbild → {fromType.Name}");
            return (fromType, evidence, 0, false, []);
        }

        if (entry is { Ambiguous: true })
            evidence.Add($"Steuergerätetyp {entry.Type} lässt {string.Join(" und ", entry.Micros)} " +
                         "zu — nicht entschieden");

        // Ohne Kennung: die Blockkette befragen. Nur wenn genau eine Aufteilung
        // Blöcke bestätigt, ist das ein Beleg — sonst bleibt es mehrdeutig.
        var decided = DecideByChain(data, evidence);
        if (decided is not null)
            return (decided, evidence, 0, false, []);

        var open = TriCoreDevice.Mapped.Select(d => d.Name).ToList();
        evidence.Add($"Baustein nicht bestimmt — {string.Join(" oder ", open)} kommen in Frage; " +
                     "die Löschsektorkarte bleibt deshalb offen");

        return (TriCoreDevice.Generic(data.LongLength), evidence, 0, true, open);
    }

    /// <summary>
    /// Probiert die hinterlegten Bausteine und nimmt den, dessen Bankaufteilung
    /// mehr Blockköpfe bestätigt. Das ist ein Beleg aus dem Abbild, kein Raten:
    /// nur eine richtige Bankgrenze lässt <c>blockEnd</c> und <c>0xDEADBEEF</c>
    /// zusammenpassen. Bei Gleichstand wird nichts gewählt.
    /// </summary>
    private static TriCoreDevice? DecideByChain(byte[] data, List<string> evidence)
    {
        var scored = TriCoreDevice.Mapped
            .Select(device => (device,
                               count: BoschBlockChain.ScanHeaders(
                                   data, TriCoreLayout.For(device, data.LongLength)).Count))
            .OrderByDescending(x => x.count)
            .ToList();

        if (scored.Count < 2 || scored[0].count == 0 || scored[0].count == scored[1].count)
            return null;

        evidence.Add($"{scored[0].device.Name} bestätigt {scored[0].count} Blockköpfe, " +
                     $"{scored[1].device.Name} nur {scored[1].count} — die Bankaufteilung " +
                     "entscheidet der Abbildinhalt");
        return scored[0].device;
    }

    /// <summary>
    /// Anteil der 4-Byte-ausgerichteten little-endian-Wörter, die wie eine
    /// TriCore-Adresse aussehen: oberes Byte 0x80, 0xA0 oder 0x84 und zweites
    /// Byte unter 0x10. Die Ausrichtung ist wesentlich — ohne sie zählt jedes
    /// zufällige 0x80-Byte mit.
    /// </summary>
    public static double PointerDensity(byte[] data)
    {
        long words = Math.Min(data.LongLength, 1 << 22) / 4;
        if (words < 256) return 0;

        long hits = 0;
        for (long w = 0; w < words; w++)
        {
            long at = w * 4;
            byte high = data[at + 3];        // little endian: oberstes Byte zuletzt
            byte second = data[at + 2];

            if (high is 0x80 or 0xA0 or 0x84 && second < 0x10) hits++;
        }
        return (double)hits / words;
    }
}

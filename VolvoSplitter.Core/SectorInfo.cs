using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VolvoSplitter.Core;

public enum SectorStatus
{
    /// <summary>Sektor gelesen, Prüfsumme stimmt.</summary>
    Verified,

    /// <summary>Sektor gelesen, Prüfsumme weicht ab — wird trotzdem geschrieben.</summary>
    CrcMismatch,

    /// <summary>
    /// Sektor gelesen, aber es wurde nie eine Prüfsumme gestellt — das Stellwort
    /// des Blocks steht auf dem Füllmuster. Der dritte Zustand: die Rechnung
    /// kann nicht aufgehen, und trotzdem ist nichts abweichend. Ihn mit
    /// <see cref="CrcMismatch"/> zusammenzulegen hieße, jedem Besitzer eines
    /// solchen Abbilds eine Manipulation zu melden, die es nicht gibt.
    /// </summary>
    ChecksumNotStamped,

    /// <summary>An dieser Adresse steht kein lesbarer Sektor.</summary>
    Missing
}

/// <summary>Ein im Abbild gefundener (oder fehlender) Sektor.</summary>
public sealed class SectorInfo : INotifyPropertyChanged
{
    public required SectorKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Prefix { get; init; }
    public required long Start { get; init; }
    public required long CpuOffset { get; init; }
    public required SectorStatus Status { get; init; }

    /// <summary>
    /// Grund, warum kein Sektor gelesen werden konnte. Wird nach der
    /// Bereichsanalyse präzisiert, sobald bekannt ist, was dort wirklich liegt.
    /// </summary>
    public string? MissingReason { get; set; }

    public string PartNumber { get; init; } = "";
    public long Length { get; init; }
    public long End => Start + Length;
    public long CpuEnd => CpuOffset + Length;

    public uint CrcStored { get; init; }
    public uint CrcComputed { get; init; }

    /// <summary>Sektor reichte über das Dateiende hinaus und wurde gekürzt.</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Der Block trägt das OTP-Kennzeichen: Bit <c>0x00800000</c> im
    /// Kennungswort der Bosch-Blockkette. Einmal programmiert, nicht mehr
    /// löschbar — beim Tuning-Schutz-Block ist das der Sinn der Sache.
    ///
    /// Stand bis v1.3.0 als „ · OTP" im <see cref="Label"/>. Als eigenes Feld
    /// kann die Oberfläche es färben, statt es nur zu buchstabieren; für
    /// Textausgaben schreibt <see cref="LabelText"/> es weiterhin aus.
    /// </summary>
    public bool Otp { get; init; }

    /// <summary>
    /// Adresse laut Tabelle, wenn der Sektor woanders gefunden wurde.
    /// Null, solange er dort liegt, wo er hingehört.
    /// </summary>
    public long? ExpectedStart { get; init; }

    public bool Relocated => ExpectedStart is not null;

    // --- Felder aus dem ASCII-Kopf ---
    public string Project { get; init; } = "";      // p=
    public string BuildDate { get; init; } = "";    // d=
    public string BuildTime { get; init; } = "";    // t=
    public string SourceFile { get; init; } = "";   // f=
    public string Baseline { get; init; } = "";     // b=
    public IReadOnlyDictionary<string, string> HeaderFields { get; init; } =
        new Dictionary<string, string>();

    public bool Present => Status != SectorStatus.Missing;
    public bool CrcOk => Status == SectorStatus.Verified;

    /// <summary>
    /// Für diesen Sektor wurde nie eine Prüfsumme gestellt. <see cref="CrcOk"/>
    /// bleibt dabei falsch — bestätigt ist er nicht —, aber abweichend ist er
    /// eben auch nicht.
    /// </summary>
    public bool ChecksumNotStamped => Status == SectorStatus.ChecksumNotStamped;

    /// <summary>
    /// Für diesen Sektor gibt es Vorgänge, die das Abbild verändern. Bei den
    /// Blöcken der Bosch-Blockkette false: dort werden Prüfsummen gerechnet und
    /// gemeldet, aber nicht gestellt.
    /// </summary>
    public bool Writable { get; init; } = true;

    public string OutputName => Prefix + PartNumber;

    /// <summary>
    /// Bezeichnung samt OTP-Vermerk — für Bericht und Befehlszeile, die kein
    /// Farbmittel haben. Die Oberfläche nimmt <see cref="Label"/> und setzt das
    /// Kennzeichen daneben.
    /// </summary>
    public string LabelText => Otp ? $"{Label} · OTP" : Label;

    /// <summary>Art des Datensatzes aus dem Dateinamen im Kopf (dst1 / dst2 / pbc).</summary>
    public string DataSetTag
    {
        get
        {
            if (SourceFile.Contains(".dst1")) return "dst1";
            if (SourceFile.Contains(".dst2")) return "dst2";
            if (SourceFile.Contains(".pbc")) return "pbc";
            return "";
        }
    }

    public string AddressRange => $"0x{Start:X6} – 0x{End:X6}";
    public string CpuAddressRange => $"0x{CpuOffset:X6} – 0x{CpuEnd:X6}";
    public string SizeText => $"{Length:N0} B";

    /// <summary>Hinweistext bei abweichender Prüfsumme. Der Sektor bleibt nutzbar.</summary>
    public string CrcAlert
    {
        get
        {
            string text = $"Datei 0x{CrcStored:X8}   berechnet 0x{CrcComputed:X8}   " +
                          "— wird trotzdem geschrieben";
            if (Truncated)
                text += "\nSektor reicht über das Dateiende hinaus und wurde gekürzt.";
            return text;
        }
    }

    /// <summary>Hinweis, wenn der Sektor nicht an der Standardadresse liegt.</summary>
    public string RelocationNote => ExpectedStart is { } expected
        ? $"Gefunden bei 0x{Start:X6} statt 0x{expected:X6} — Adresse weicht von der Tabelle ab"
        : "";

    /// <summary>
    /// Weitere Fundstellen dieses Prüfwerts im Abbild, außerhalb des eigenen
    /// Trailers. Das Steuergerät hält Kopien in Tabellen — eine nur im Sektor
    /// korrigierte Prüfsumme passt dann nicht mehr zur Kopie.
    /// </summary>
    public IReadOnlyList<long> ChecksumCopies { get; set; } = [];

    public bool HasChecksumCopies => ChecksumCopies.Count > 0;

    public string ChecksumCopyNote => ChecksumCopies.Count == 0
        ? ""
        : $"Prüfwert 0x{CrcStored:X8} steht auch bei " +
          string.Join(", ", ChecksumCopies.Select(a => $"0x{a:X6}")) +
          " — beim Korrigieren dort ebenfalls anpassen";

    private string? _note;
    /// <summary>Letzte Rückmeldung zu diesem Sektor (extrahiert, korrigiert, ersetzt).</summary>
    public string? Note
    {
        get => _note;
        set { _note = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

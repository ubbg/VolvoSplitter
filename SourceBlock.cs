using VolvoSplitter.Core;

namespace VolvoSplitter;

/// <summary>
/// Ein Block der Quelle, wie ihn die Leiste zeigt: der gelesene Block selbst und
/// das Ergebnis der Prüfung gegen das Ziel.
///
/// Ein eigener Typ und nicht <see cref="SectorInfo"/> — damit ein Block der
/// Quelle gar nicht erst durch <c>SectorOf()</c> in einen der Zielpfade geraten
/// kann. Alle bestehenden Handler holen ihren Sektor über diesen einen Weg und
/// steigen für die Leiste an ihrer ersten Zeile aus; die Trennung steht damit im
/// Typsystem und nicht in der Aufmerksamkeit des nächsten Lesers.
///
/// Der Plan kommt aus <see cref="BlockTransfer"/>, also aus derselben Prüfung,
/// die auch der Vorgang selbst benutzt. Die Karte kann dadurch nichts anderes
/// behaupten, als der Knopf tut.
/// </summary>
public sealed record SourceBlock(SectorInfo Sector, BlockTransferPlan Plan)
{
    /// <summary>Der Übertrag ist zulässig — der Knopf erscheint.</summary>
    public bool CanSend => Plan.Possible;

    /// <summary>
    /// Was die Prüfung ergeben hat: das gefundene Gegenstück oder der Grund,
    /// warum es keines gibt. Steht dauerhaft auf der Karte, damit die Ablehnung
    /// vor dem Klick sichtbar ist und nicht erst danach.
    /// </summary>
    public string MatchNote => Plan switch
    {
        { Possible: false } => Plan.RejectionText,
        { TargetBlock: { } hit } => $"Ziel: {hit.Label} bei {Hex.Addr(hit.Start)}",
        _ => ""
    };

    /// <summary>Warnungen und Feststellungen, die den Übertrag nicht verhindern.</summary>
    public string WarningNote => string.Join("  ",
        Plan.Findings.Where(f => f.Severity == TransferSeverity.Warning).Select(f => f.Text));

    public bool HasWarning => Plan.Possible && Plan.Warnings.Any();

    /// <summary>
    /// Teilenummer und Prüfwert stimmen bereits überein. Eine Beobachtung, keine
    /// Behauptung von Bytegleichheit — sie beantwortet die Frage, ob der
    /// Übertrag überhaupt etwas ändern würde.
    /// </summary>
    public bool LooksIdentical =>
        Plan.TargetBlock is { } hit &&
        hit.PartNumber == Sector.PartNumber &&
        hit.CrcStored == Sector.CrcStored &&
        hit.Length == Sector.Length;
}

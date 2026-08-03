namespace VolvoSplitter.Core.Reporting;

/// <summary>In welchem Format ein Bericht geschrieben wird.</summary>
public enum ReportFormat
{
    /// <summary>Reiner Text, Spalten mit Leerzeichen ausgerichtet.</summary>
    Text,

    /// <summary>Markdown mit Überschriften und Pipe-Tabellen.</summary>
    Markdown,

    /// <summary>Eigenständige HTML-Seite mit eingebettetem Stil.</summary>
    Html
}

/// <summary>
/// Gewicht einer Tabellenzeile. Sie sagt <em>was der Befund ist</em>, nicht wie
/// er aussehen soll — die Farbe wählt allein der HTML-Ausgeber, Text und
/// Markdown ignorieren sie.
/// </summary>
public enum RowMood
{
    Normal,

    /// <summary>Nachgerechnet und in Ordnung.</summary>
    Good,

    /// <summary>Weicht ab — das ist die Aussage, die zuerst auffallen soll.</summary>
    Warning
}

/// <summary>Ein Baustein des Berichts. Jeder Ausgeber setzt alle Arten um.</summary>
public abstract record ReportNode;

/// <summary>Abschnittsüberschrift.</summary>
public sealed record Heading(string Text) : ReportNode;

/// <summary>Freie Zeile.</summary>
public sealed record Paragraph(string Text) : ReportNode;

/// <summary>Aufzählung — Belege, Lücken, Anmerkungen.</summary>
public sealed record Bullets(IReadOnlyList<string> Items) : ReportNode;

/// <summary>Name-Wert-Paare, etwa die Kopfdaten eines Blocks.</summary>
public sealed record Fields(IReadOnlyList<(string Name, string Value)> Rows) : ReportNode;

/// <summary>Eine Tabellenzeile samt Gewicht.</summary>
public sealed record ReportRow(IReadOnlyList<string> Cells, RowMood Mood = RowMood.Normal);

/// <summary>Tabelle mit Kopfzeile.</summary>
public sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<ReportRow> Rows) : ReportNode;

/// <summary>
/// Roher Text, der so bleiben soll, wie er ist — Bytefolgen und Zeigertabellen.
/// Sie sind ungedeutet und sollen auch ungedeutet aussehen.
/// </summary>
public sealed record Preformatted(string Text) : ReportNode;

/// <summary>
/// Ein benannter Unterabschnitt mit eigenen Bausteinen. Damit lässt sich die
/// Blockkette abbilden, ohne dass die Ausgeber Verschachtelungstiefen zählen
/// müssen: Text rückt ein, Markdown setzt eine tiefere Überschrift, HTML eine
/// eigene Sektion.
/// </summary>
public sealed record Group(string Title, IReadOnlyList<ReportNode> Children) : ReportNode;

/// <summary>Der fertige Bericht, unabhängig vom Ausgabeformat.</summary>
public sealed record ReportDocument(string Title, IReadOnlyList<ReportNode> Nodes);

/// <summary>
/// Sammelt Bausteine der Reihe nach ein. Nur Bequemlichkeit — er hält
/// <see cref="DumpReport"/> lesbar, damit der Aufbau des Berichts weiterhin von
/// oben nach unten gelesen werden kann.
/// </summary>
public sealed class ReportBuilder
{
    private readonly List<ReportNode> _nodes = [];

    public ReportBuilder Add(ReportNode node)
    {
        _nodes.Add(node);
        return this;
    }

    public ReportBuilder Heading(string text) => Add(new Heading(text));

    public ReportBuilder Paragraph(string text) => Add(new Paragraph(text));

    public ReportBuilder Bullets(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count > 0 ? Add(new Bullets(list)) : this;
    }

    public ReportBuilder Fields(IEnumerable<(string, string)> rows)
    {
        var list = rows.ToList();
        return list.Count > 0 ? Add(new Fields(list)) : this;
    }

    public ReportBuilder Table(IReadOnlyList<string> headers, IEnumerable<ReportRow> rows)
    {
        var list = rows.ToList();
        return list.Count > 0 ? Add(new Table(headers, list)) : this;
    }

    public ReportBuilder Preformatted(string text) => Add(new Preformatted(text));

    public ReportBuilder Group(string title, Action<ReportBuilder> children)
    {
        var inner = new ReportBuilder();
        children(inner);
        return inner._nodes.Count > 0 ? Add(new Group(title, inner._nodes)) : this;
    }

    public IReadOnlyList<ReportNode> Nodes => _nodes;

    public ReportDocument Build(string title) => new(title, _nodes);
}

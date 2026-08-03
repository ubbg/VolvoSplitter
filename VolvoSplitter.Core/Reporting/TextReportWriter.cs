using System.Text;

namespace VolvoSplitter.Core.Reporting;

/// <summary>
/// Schreibt den Bericht als reinen Text. Spaltenbreiten ergeben sich aus dem
/// breitesten Eintrag, statt fest verdrahtet zu sein — damit läuft keine Spalte
/// mehr über, wenn eine Bezeichnung länger ausfällt als erwartet.
/// </summary>
public static class TextReportWriter
{
    private const string Indent = "   ";

    /// <summary>Abstand zwischen zwei Tabellenspalten.</summary>
    private const string Gap = "  ";

    public static string Write(ReportDocument document)
    {
        var text = new StringBuilder();
        text.AppendLine(document.Title);

        WriteNodes(text, document.Nodes, "");
        return text.ToString();
    }

    private static void WriteNodes(StringBuilder text, IReadOnlyList<ReportNode> nodes, string prefix)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Heading heading:
                    text.AppendLine();
                    text.AppendLine(prefix + heading.Text);
                    break;

                case Paragraph paragraph:
                    text.AppendLine(prefix + paragraph.Text);
                    break;

                case Bullets bullets:
                    foreach (string item in bullets.Items)
                        text.AppendLine($"{prefix}{Indent}· {item}");
                    break;

                case Fields fields:
                    WriteFields(text, fields, prefix);
                    break;

                case Table table:
                    WriteTable(text, table, prefix);
                    break;

                case Preformatted pre:
                    foreach (string line in pre.Text.Split('\n'))
                        text.AppendLine(prefix + line.TrimEnd('\r'));
                    break;

                case Group group:
                    text.AppendLine();
                    text.AppendLine(prefix + group.Title);
                    WriteNodes(text, group.Children, prefix + Indent);
                    break;
            }
        }
    }

    private static void WriteFields(StringBuilder text, Fields fields, string prefix)
    {
        int width = fields.Rows.Max(r => r.Name.Length);
        foreach (var (name, value) in fields.Rows)
            text.AppendLine($"{prefix}{name.PadRight(width)}{Gap}{value}");
    }

    /// <summary>
    /// Tabelle mit Kopfzeile. Die letzte Spalte wird nicht aufgefüllt, damit
    /// keine unsichtbaren Leerzeichen am Zeilenende stehen.
    /// </summary>
    private static void WriteTable(StringBuilder text, Table table, string prefix)
    {
        int columns = table.Headers.Count;
        var widths = new int[columns];

        for (int i = 0; i < columns; i++)
        {
            widths[i] = table.Headers[i].Length;
            foreach (var row in table.Rows)
                if (i < row.Cells.Count) widths[i] = Math.Max(widths[i], row.Cells[i].Length);
        }

        text.AppendLine(prefix + Join(table.Headers, widths));
        foreach (var row in table.Rows)
            text.AppendLine(prefix + Join(row.Cells, widths));
    }

    private static string Join(IReadOnlyList<string> cells, int[] widths)
    {
        var line = new StringBuilder();

        for (int i = 0; i < widths.Length; i++)
        {
            string cell = i < cells.Count ? cells[i] : "";
            if (i > 0) line.Append(Gap);
            line.Append(i == widths.Length - 1 ? cell : cell.PadRight(widths[i]));
        }

        return line.ToString().TrimEnd();
    }
}

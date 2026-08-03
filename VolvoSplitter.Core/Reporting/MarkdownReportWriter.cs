using System.Text;

namespace VolvoSplitter.Core.Reporting;

/// <summary>
/// Schreibt den Bericht als Markdown: echte Tabellen statt ausgerichteter
/// Leerzeichen, damit er sich in ein Ticket oder eine Dokumentation einfügt.
/// </summary>
public static class MarkdownReportWriter
{
    public static string Write(ReportDocument document)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {Escape(document.Title)}");

        WriteNodes(text, document.Nodes, level: 2);
        return text.ToString();
    }

    private static void WriteNodes(StringBuilder text, IReadOnlyList<ReportNode> nodes, int level)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Heading heading:
                    text.AppendLine();
                    text.AppendLine($"{new string('#', level)} {Escape(heading.Text)}");
                    text.AppendLine();
                    break;

                case Paragraph paragraph:
                    text.AppendLine(Escape(paragraph.Text));
                    text.AppendLine();
                    break;

                case Bullets bullets:
                    foreach (string item in bullets.Items)
                        text.AppendLine($"* {Escape(item)}");
                    text.AppendLine();
                    break;

                case Fields fields:
                    foreach (var (name, value) in fields.Rows)
                        text.AppendLine($"* **{Escape(name)}** — {Escape(value)}");
                    text.AppendLine();
                    break;

                case Table table:
                    WriteTable(text, table);
                    break;

                case Preformatted pre:
                    text.AppendLine("```");
                    text.AppendLine(pre.Text.TrimEnd());
                    text.AppendLine("```");
                    text.AppendLine();
                    break;

                case Group group:
                    text.AppendLine();
                    text.AppendLine($"{new string('#', Math.Min(level + 1, 6))} {Escape(group.Title)}");
                    text.AppendLine();
                    WriteNodes(text, group.Children, Math.Min(level + 2, 6));
                    break;
            }
        }
    }

    private static void WriteTable(StringBuilder text, Table table)
    {
        text.AppendLine("| " + string.Join(" | ", table.Headers.Select(Cell)) + " |");
        text.AppendLine("|" + string.Concat(table.Headers.Select(_ => " --- |")));

        foreach (var row in table.Rows)
        {
            var cells = Enumerable.Range(0, table.Headers.Count)
                                  .Select(i => Cell(i < row.Cells.Count ? row.Cells[i] : ""));
            text.AppendLine("| " + string.Join(" | ", cells) + " |");
        }

        text.AppendLine();
    }

    /// <summary>
    /// Tabellenzelle: Ein senkrechter Strich im Inhalt würde die Tabelle
    /// zerlegen, ein Zeilenumbruch sie beenden.
    /// </summary>
    private static string Cell(string value) =>
        Escape(value).Replace("|", "\\|").ReplaceLineEndings(" ");

    /// <summary>
    /// Nur maskieren, was wirklich als Auszeichnung gelesen würde. Der
    /// Unterstrich gehört ausdrücklich nicht dazu: CommonMark wertet ihn
    /// innerhalb eines Wortes nicht als Betonung, und die Werte dieses Berichts
    /// sind voll davon — <c>EV_ECM30TDI011</c>, <c>SB_CRC32_ALGO_E</c>,
    /// <c>EDC17_CP44</c>. Maskiert man ihn, wird die Rohdatei unlesbar, ohne
    /// dass die gerenderte Fassung gewinnt.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("*", "\\*")
             .Replace("`", "\\`");
}

using System.Text;

namespace VolvoSplitter.Core.Reporting;

/// <summary>
/// Schreibt den Bericht als eigenständige HTML-Seite: eingebetteter Stil, keine
/// Verweise nach außen, damit die Datei allein weitergegeben werden kann.
///
/// Der Stil folgt der Anwendung — dunkel als Grundton, hell über
/// <c>prefers-color-scheme</c>. Zeilen mit <see cref="RowMood.Warning"/> werden
/// abgesetzt: eine abweichende Prüfsumme ist die Aussage, die zuerst auffallen
/// soll.
/// </summary>
public static class HtmlReportWriter
{
    public static string Write(ReportDocument document)
    {
        var text = new StringBuilder();

        text.AppendLine("<!DOCTYPE html>");
        text.AppendLine("<html lang=\"de\">");
        text.AppendLine("<head>");
        text.AppendLine("<meta charset=\"utf-8\">");
        text.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        text.AppendLine($"<title>{Escape(document.Title)}</title>");
        text.AppendLine($"<style>{Style}</style>");
        text.AppendLine("</head>");
        text.AppendLine("<body>");
        text.AppendLine($"<h1>{Escape(document.Title)}</h1>");

        WriteNodes(text, document.Nodes, level: 2);

        text.AppendLine("</body>");
        text.AppendLine("</html>");
        return text.ToString();
    }

    private static void WriteNodes(StringBuilder text, IReadOnlyList<ReportNode> nodes, int level)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Heading heading:
                    text.AppendLine($"<h{level}>{Escape(heading.Text)}</h{level}>");
                    break;

                case Paragraph paragraph:
                    text.AppendLine($"<p>{Escape(paragraph.Text)}</p>");
                    break;

                case Bullets bullets:
                    text.AppendLine("<ul>");
                    foreach (string item in bullets.Items)
                        text.AppendLine($"<li>{Escape(item)}</li>");
                    text.AppendLine("</ul>");
                    break;

                case Fields fields:
                    text.AppendLine("<dl>");
                    foreach (var (name, value) in fields.Rows)
                        text.AppendLine($"<dt>{Escape(name)}</dt><dd>{Escape(value)}</dd>");
                    text.AppendLine("</dl>");
                    break;

                case Table table:
                    WriteTable(text, table);
                    break;

                case Preformatted pre:
                    text.AppendLine($"<pre>{Escape(pre.Text.TrimEnd())}</pre>");
                    break;

                case Group group:
                    int inner = Math.Min(level + 1, 6);
                    text.AppendLine("<section class=\"group\">");
                    text.AppendLine($"<h{inner}>{Escape(group.Title)}</h{inner}>");
                    WriteNodes(text, group.Children, Math.Min(inner + 1, 6));
                    text.AppendLine("</section>");
                    break;
            }
        }
    }

    private static void WriteTable(StringBuilder text, Table table)
    {
        text.AppendLine("<div class=\"scroll\"><table>");
        text.AppendLine("<thead><tr>");
        foreach (string header in table.Headers)
            text.AppendLine($"<th>{Escape(header)}</th>");
        text.AppendLine("</tr></thead>");

        text.AppendLine("<tbody>");
        foreach (var row in table.Rows)
        {
            string css = row.Mood switch
            {
                RowMood.Warning => " class=\"warn\"",
                RowMood.Good => " class=\"good\"",
                _ => ""
            };

            text.AppendLine($"<tr{css}>");
            for (int i = 0; i < table.Headers.Count; i++)
                text.AppendLine($"<td>{Escape(i < row.Cells.Count ? row.Cells[i] : "")}</td>");
            text.AppendLine("</tr>");
        }
        text.AppendLine("</tbody></table></div>");
    }

    /// <summary>
    /// Maskiert alles, was sonst als Markup gelesen würde. Ein Dateiname darf
    /// beliebige Zeichen tragen — er landet als Text auf der Seite, nie als Tag.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;");

    private const string Style = """

        :root { color-scheme: dark light; }
        body {
          margin: 0 auto; padding: 2rem 1.25rem; max-width: 78rem;
          font-family: "Segoe UI", system-ui, sans-serif; line-height: 1.5;
          background: #14161a; color: #e6e8ec;
        }
        h1 { font-size: 1.5rem; margin: 0 0 .25rem; }
        h2 { font-size: 1.15rem; margin: 2rem 0 .5rem; padding-bottom: .3rem;
             border-bottom: 1px solid #2c313a; }
        h3, h4, h5, h6 { font-size: 1rem; margin: 1.1rem 0 .4rem; color: #cfd4dd; }
        p { margin: .35rem 0; }
        ul { margin: .35rem 0; padding-left: 1.3rem; }
        li { margin: .15rem 0; }
        dl { display: grid; grid-template-columns: max-content 1fr; gap: .2rem .9rem; margin: .4rem 0; }
        dt { color: #98a0ae; }
        dd { margin: 0; font-variant-numeric: tabular-nums; }
        .group { margin: .6rem 0 .6rem 1rem; padding-left: .9rem; border-left: 2px solid #2c313a; }
        .scroll { overflow-x: auto; margin: .5rem 0 1rem; }
        table { border-collapse: collapse; width: 100%; font-size: .92rem; }
        th, td { text-align: left; padding: .34rem .7rem; border-bottom: 1px solid #262b33;
                 white-space: nowrap; font-variant-numeric: tabular-nums; }
        th { color: #98a0ae; font-weight: 600; border-bottom: 1px solid #39404b; }
        tr.warn td { background: #3a1f22; color: #ffb4b4; }
        tr.good td { color: #b7e3bd; }
        pre { margin: .4rem 0; padding: .6rem .8rem; overflow-x: auto;
              background: #1b1e24; border-radius: 4px; font-size: .86rem; }
        code, pre { font-family: Consolas, "Cascadia Mono", monospace; }

        @media (prefers-color-scheme: light) {
          body { background: #ffffff; color: #1b1f26; }
          h2 { border-bottom-color: #dfe3ea; }
          h3, h4, h5, h6 { color: #333a45; }
          dt, th { color: #5c6675; }
          th { border-bottom-color: #c9cfd9; }
          th, td { border-bottom-color: #e8ebf0; }
          .group { border-left-color: #dfe3ea; }
          tr.warn td { background: #fdecec; color: #8e2020; }
          tr.good td { color: #1f6b30; }
          pre { background: #f4f6f9; }
        }

        """;
}

using VolvoSplitter.Core;
using VolvoSplitter.Core.Reporting;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Der Bericht wird einmal aufgebaut und dreimal ausgegeben. Diese Tests fragen
/// deshalb zweierlei: Sagen alle drei Formate dasselbe? Und hält jedes seine
/// eigene Zeichensyntax ein, wenn im Inhalt Sonderzeichen stehen?
/// </summary>
public class DumpReportTests
{
    /// <summary>Ein Abbild mit Blockkette, Prüfsummen und Kennungen.</summary>
    private static FlashDump SampleDump() =>
        FlashDump.FromBytes(TriCoreDump.SampleMed17Image(), "probe.mpc");

    [Fact]
    public void AllThreeFormats_CarryTheSameFacts()
    {
        var dump = SampleDump();

        string text = DumpReport.Build(dump, ReportFormat.Text);
        string markdown = DumpReport.Build(dump, ReportFormat.Markdown);
        string html = DumpReport.Build(dump, ReportFormat.Html);

        // Was im Text steht, darf in den anderen beiden nicht fehlen: Dateiname,
        // Blockbezeichnungen, Prüfverfahren, Variantenkennung.
        string[] facts =
        [
            "probe.mpc", "Startup Block", "Dataset #0", "SB_CRC32_ALGO_E", "MED17.1.6"
        ];

        foreach (string fact in facts)
        {
            Assert.Contains(fact, text);
            Assert.Contains(fact, markdown);
            Assert.Contains(fact, html);
        }
    }

    [Fact]
    public void BuildWithoutFormat_StillYieldsText()
    {
        // Die alte Signatur hält Aufrufer heraus, die kein Format kennen.
        var dump = SampleDump();

        Assert.Equal(DumpReport.Build(dump, ReportFormat.Text), DumpReport.Build(dump));
    }

    [Fact]
    public void Html_EscapesMarkupInContent()
    {
        // Ein Dateiname darf beliebige Zeichen tragen. Er landet als Text auf
        // der Seite, nie als Tag — sonst schriebe der Bericht fremdes Markup.
        // (Ohne Schrägstrich im Namen: den würde Path.GetFileName abschneiden.)
        var dump = FlashDump.FromBytes(TriCoreDump.SampleMed17Image(), "<script>&böse.mpc");

        string html = DumpReport.Build(dump, ReportFormat.Html);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;böse", html);
    }

    [Fact]
    public void Markdown_EscapesPipesSoTablesSurvive()
    {
        // Ein senkrechter Strich in einer Zelle würde die Tabelle zerlegen.
        var document = new ReportDocument("Probe",
        [
            new Table(["Feld", "Wert"], [new ReportRow(["a|b", "c|d"])])
        ]);

        string markdown = MarkdownReportWriter.Write(document);

        Assert.Contains(@"a\|b", markdown);
        Assert.Contains(@"c\|d", markdown);
    }

    [Fact]
    public void Markdown_LeavesUnderscoresAlone()
    {
        // Bezeichner wie SB_CRC32_ALGO_E sind der Normalfall. CommonMark liest
        // den Unterstrich im Wortinneren nicht als Betonung — ihn zu maskieren
        // machte nur die Rohdatei unlesbar.
        var document = new ReportDocument("Probe",
        [
            new Paragraph("SB_CRC32_ALGO_E")
        ]);

        Assert.Contains("SB_CRC32_ALGO_E", MarkdownReportWriter.Write(document));
    }

    [Theory]
    [InlineData("bericht.txt", ReportFormat.Text)]
    [InlineData("bericht.md", ReportFormat.Markdown)]
    [InlineData("bericht.MD", ReportFormat.Markdown)]
    [InlineData("bericht.html", ReportFormat.Html)]
    [InlineData("bericht.HTM", ReportFormat.Html)]
    [InlineData("md", ReportFormat.Markdown)]
    [InlineData(".html", ReportFormat.Html)]
    [InlineData("bericht.fremd", ReportFormat.Text)]
    [InlineData("", ReportFormat.Text)]
    public void FormatFor_ReadsTheExtension(string input, ReportFormat expected) =>
        Assert.Equal(expected, DumpReport.FormatFor(input));

    [Theory]
    [InlineData(ReportFormat.Text, "txt")]
    [InlineData(ReportFormat.Markdown, "md")]
    [InlineData(ReportFormat.Html, "html")]
    public void Extension_MatchesTheFormat(ReportFormat format, string expected) =>
        Assert.Equal(expected, DumpReport.Extension(format));

    [Fact]
    public void ImageWithoutChainOrIdentity_StillYieldsEveryFormat()
    {
        // Ein Abbild ohne Blockkette, ohne Kennungen, ohne Layout. Erwartet wird
        // ein Bericht — kein leerer String und vor allem keine Ausnahme.
        var dump = FlashDump.FromBytes(new byte[0x2000], "leer.bin");

        foreach (var format in Enum.GetValues<ReportFormat>())
        {
            string report = DumpReport.Build(dump, format);
            Assert.NotEmpty(report);
            Assert.Contains("leer.bin", report);
        }
    }

    [Fact]
    public void FailingChecksum_IsMarkedAsWarningForHtml()
    {
        // Die Stimmung einer Zeile ist die Aussage, nicht die Farbe: Eine
        // abweichende Prüfsumme muss als Warnung durchkommen, damit HTML sie
        // hervorheben kann.
        var document = new ReportDocument("Probe",
        [
            new Table(["Sektor", "Prüfsumme"],
            [
                new ReportRow(["gut", "ok"], RowMood.Good),
                new ReportRow(["schlecht", "weicht ab"], RowMood.Warning)
            ])
        ]);

        string html = HtmlReportWriter.Write(document);

        Assert.Contains("<tr class=\"warn\">", html);
        Assert.Contains("<tr class=\"good\">", html);
    }

    [Fact]
    public void Html_IsSelfContained()
    {
        // Die Datei soll allein weitergegeben werden können: Stil eingebettet,
        // kein Verweis nach außen.
        string html = DumpReport.Build(SampleDump(), ReportFormat.Html);

        Assert.Contains("<style>", html);
        Assert.Contains("charset=\"utf-8\"", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<script", html);
    }
}

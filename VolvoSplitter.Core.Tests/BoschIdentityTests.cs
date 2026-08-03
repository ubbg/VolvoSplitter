using System.Text;
using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

public class BoschIdentityTests
{
    private static byte[] WithText(params (long At, string Text)[] strings)
    {
        var data = new byte[0x10000];
        Array.Fill(data, (byte)0xFF);
        foreach (var (at, text) in strings)
            Encoding.ASCII.GetBytes(text).CopyTo(data, (int)at);
        return data;
    }

    [Fact]
    public void BoschIdentity_ReportsOffsets_NotJustValues()
    {
        // Ein Fundort ist ein Beleg, eine Zeichenkette allein nur eine Beobachtung.
        var identity = BoschIdentity.Scan(WithText(
            (0x1000, "0281020088 Bosch"),
            (0x2000, "1037529914 SW")))!;

        var hardware = Assert.Single(identity.Hits, h => h.Kind == "Hardware");
        var software = Assert.Single(identity.Hits, h => h.Kind == "Software");

        Assert.Equal("0281020088", hardware.Value);
        Assert.Equal(0x1000, hardware.Offset);
        Assert.Equal("1037529914", software.Value);
        Assert.Equal(0x2000, software.Offset);
    }

    [Fact]
    public void BlockIdentifierShape_IsRecognised()
    {
        var identity = BoschIdentity.Scan(WithText((0x400, "10SW008917")))!;

        var hit = Assert.Single(identity.Hits, h => h.Kind == "Blockkennung");
        Assert.Equal("10SW008917", hit.Value);
        Assert.Equal(0x400, hit.Offset);
    }

    [Fact]
    public void VagPartNumberCandidate_RequiresStrictShape()
    {
        // Elf beliebige Ziffern sind keine VAG-Teilenummer — und ohne Nähe zu
        // einer Software- oder Hardwarenummer wird gar nichts gemeldet.
        Assert.Null(BoschIdentity.Scan(WithText((0x1000, "Zahl 12345678912 Ende")))
                        ?.Hits.FirstOrDefault(h => h.Kind == "Teilenummer"));

        // Strenge Form, bekanntes Präfix — aber allein stehend.
        Assert.Null(BoschIdentity.Scan(WithText((0x1000, "03L906022AG")))
                        ?.Hits.FirstOrDefault(h => h.Kind == "Teilenummer"));

        // Erst zusammen mit einer Softwarenummer in der Nähe.
        var identity = BoschIdentity.Scan(WithText((0x1000, "1037529914 03L906022AG")))!;
        var part = Assert.Single(identity.Hits, h => h.Kind == "Teilenummer");
        Assert.Equal("03L906022AG", part.Value);
    }

    [Fact]
    public void UnknownPartNumberPrefix_IsNotReported()
    {
        // Die Präfixliste ist bewusst kurz: lieber eine Kennung zu wenig als
        // eine erfundene.
        var identity = BoschIdentity.Scan(WithText((0x1000, "1037529914 09Z906022AG")))!;
        Assert.DoesNotContain(identity.Hits, h => h.Kind == "Teilenummer");
    }

    [Fact]
    public void Vin_RequiresVagWorldManufacturerCode()
    {
        var vag = BoschIdentity.Scan(WithText((0x1000, "WVWZZZ1KZAW123456")))!;
        Assert.Equal("WVWZZZ1KZAW123456", Assert.Single(vag.Hits, h => h.Kind == "VIN").Value);

        // Ein Volvo-WMI ist in einem Bosch-Abbild kein VIN-Fund.
        var volvo = BoschIdentity.Scan(WithText((0x1000, "YV1AB1234C5678901")));
        Assert.DoesNotContain(volvo?.Hits ?? [], h => h.Kind == "VIN");
    }

    [Fact]
    public void VariantFromDatasetBlock_OutranksFreeSearch()
    {
        // Die feste Fundstelle gilt als gesichert, das freie Durchsuchen nie
        // mehr als „stark gestützt".
        var identity = BoschIdentity.Scan(WithText((0x1000, "irgendwo MED17.5.25 im Abbild")),
                                          "34/1/EDC17_C46/5/P643//C643X5L8///", 0x8000)!;

        var confirmed = identity.Hits.Where(h => h.Kind == "Steuergerät" &&
                                                 h.Confidence == RegionConfidence.Confirmed)
                                     .ToList();

        Assert.Equal("EDC17_C46", Assert.Single(confirmed).Value);
        Assert.Equal("EDC17_C46", identity.EcuType);
        Assert.Contains(identity.Hits, h => h.Kind == "Variante" && h.Offset == 0x8000);
    }

    [Fact]
    public void EcuTypeFromVariant_PicksTheFieldThatNamesTheDevice()
    {
        Assert.Equal("EDC17_C46",
                     BoschIdentity.EcuTypeFromVariant("34/1/EDC17_C46/5/P643//C643X5L8///"));
        Assert.Equal("MED17.1.6",
                     BoschIdentity.EcuTypeFromVariant("34/1/MED17.1.6/5/P643//C643X5L8///"));
        Assert.Null(BoschIdentity.EcuTypeFromVariant("34/1/2/5/P643//C643X5L8///"));
    }

    [Fact]
    public void EmptyImage_YieldsNoIdentityAtAll()
    {
        var blank = new byte[0x1000];
        Array.Fill(blank, (byte)0xFF);

        Assert.Null(BoschIdentity.Scan(blank));
    }

    // ==================================================================
    // Steuergerätetabelle
    // ==================================================================

    [Fact]
    public void VagEcuCatalog_ResolvesUnambiguousTypes()
    {
        Assert.Same(TriCoreDevice.Tc1797, VagEcuCatalog.DeviceFor("EDC17CP44"));
        Assert.Same(TriCoreDevice.Tc1796, VagEcuCatalog.DeviceFor("EDC17CP14"));
        Assert.Same(TriCoreDevice.Tc1797, VagEcuCatalog.DeviceFor("MED17.1.6"));
    }

    [Fact]
    public void VagEcuCatalog_RefusesToChooseWhenTwoMcusFit()
    {
        Assert.Null(VagEcuCatalog.DeviceFor("EDC17CP20"));
        Assert.Null(VagEcuCatalog.DeviceFor("ME17.5"));

        var entry = VagEcuCatalog.Find("EDC17CP20");
        Assert.NotNull(entry);
        Assert.True(entry!.Ambiguous);
        Assert.Equal(["TC1796", "TC1797"], entry.Micros);
    }

    [Fact]
    public void VagEcuCatalog_MatchesTheLongestEntryAndIgnoresSeparators()
    {
        // MED17.1.6 darf nicht als MED17.1 durchgehen …
        Assert.Equal("MED17.1.6", VagEcuCatalog.Find("MED17.1.6")!.Type);
        Assert.Equal("MED17.1", VagEcuCatalog.Find("MED17.1")!.Type);

        // … und EDC17_C46 ist derselbe Eintrag wie EDC17C46.
        Assert.Equal("EDC17C46", VagEcuCatalog.Find("EDC17_C46")!.Type);
        Assert.Equal("EDC17C46", VagEcuCatalog.Find("EDC17 C46")!.Type);

        Assert.Null(VagEcuCatalog.Find("MPC5777C"));
        Assert.Null(VagEcuCatalog.Find(null));
    }
}

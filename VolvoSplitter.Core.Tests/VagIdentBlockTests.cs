using VolvoSplitter.Core;
using VolvoSplitter.Core.TriCore;

namespace VolvoSplitter.Core.Tests;

/// <summary>
/// Das VAG-Identifikationsfeld ist eine feste Struktur — entsprechend scharf
/// sind die Prüfungen. Jeder Test hier fragt dieselbe Frage von einer anderen
/// Seite: Wird ein halb passender Fund abgelehnt, statt geraten zu werden?
/// </summary>
public class VagIdentBlockTests
{
    /// <summary>Die Werte des ausgewerteten Audi-Abbilds.</summary>
    private static byte[] AudiField() => TriCoreDump.VagIdentField(
        "04L907309L", "EV_ECM20TDI011", "04L906027BB", "005001", "7155", "R4 2.0l TDI");

    [Fact]
    public void CompleteField_IsReadInFull()
    {
        var image = TriCoreDump.Pflash(0x1000, (0x200, AudiField()));

        var vag = VagIdentBlock.Find(image);

        Assert.NotNull(vag);
        Assert.Equal("04L907309L", vag!.HardwarePartNumber);
        Assert.Equal("EV_ECM20TDI011", vag.SystemName);
        Assert.Equal("005001", vag.Index);
        Assert.Equal("04L906027BB", vag.SoftwarePartNumber);
        Assert.Equal("7155", vag.SoftwareLevel);
        Assert.Equal("R4 2.0l TDI", vag.EngineText);
        Assert.Equal("04L906027BB 7155", vag.SoftwareText);
    }

    [Fact]
    public void SoftwareLevel_MustBeFourDigits()
    {
        // Buchstaben statt Ziffern im Standfeld: dann gibt es keine Angabe.
        // Das ist die schärfste Einzelprüfung — an ihr scheitert ein
        // Zufallstreffer, bevor irgendetwas Erfundenes im Bericht landet.
        var image = TriCoreDump.Pflash(0x1000, (0x200, TriCoreDump.VagIdentField(
            "04L907309L", "EV_ECM20TDI011", "04L906027BB", "005001", "71X5")));

        Assert.Null(VagIdentBlock.Find(image));
    }

    [Fact]
    public void PartNumberOutsideTheVagScheme_IsRejected()
    {
        // 1037540589 ist eine Bosch-Softwarenummer, keine VAG-Teilenummer:
        // gleich lang, aber ohne 906/907/910/997 an der Baugruppenstelle.
        var image = TriCoreDump.Pflash(0x1000, (0x200, TriCoreDump.VagIdentField(
            "04L907309L", "EV_ECM20TDI011", "1037540589", "005001", "7155")));

        Assert.Null(VagIdentBlock.Find(image));
    }

    [Fact]
    public void PartNumberWithoutChangeIndex_IsAccepted()
    {
        // 298907401 hat keinen Buchstabenindex — kommt so im Porsche-Abbild vor.
        var image = TriCoreDump.Pflash(0x1000, (0x200, TriCoreDump.VagIdentField(
            "298907401", "EV_ECM30TDI011", "298907401F", "001003", "0001", "3.0TDI EDC17")));

        var vag = VagIdentBlock.Find(image);

        Assert.NotNull(vag);
        Assert.Equal("298907401", vag!.HardwarePartNumber);
        Assert.Equal("298907401F", vag.SoftwarePartNumber);
    }

    [Fact]
    public void DisagreeingPartNumberCopies_RejectTheField()
    {
        // Die Software-Teilenummer steht zweimal. Weichen die Ablagen ab, ist
        // die Deutung falsch — dann lieber keine Angabe.
        var field = AudiField();
        field[-VagIdentBlock.HardwareOffset + VagIdentBlock.SoftwareFirstOffset] = (byte)'9';

        Assert.Null(VagIdentBlock.Find(TriCoreDump.Pflash(0x1000, (0x200, field))));
    }

    [Fact]
    public void MissingAnchor_YieldsNothing()
    {
        var image = TriCoreDump.Pflash(0x1000, (0x200, TriCoreDump.CodeBlock(0x400, 0x80000000)));

        Assert.Null(VagIdentBlock.Find(image));
    }

    [Fact]
    public void FieldCutOffAtTheEnd_YieldsNothingWithoutOverrun()
    {
        // Das Feld ragt über das Abbildende hinaus. Erwartet wird kein Fund —
        // und vor allem keine Ausnahme.
        var full = AudiField();
        var image = TriCoreDump.Pflash(full.Length - 20, (0, full[..(full.Length - 20)]));

        Assert.Null(VagIdentBlock.Find(image));
    }

    [Fact]
    public void EngineCodes_DropPlaceholdersAndDuplicates()
    {
        // „----" ist ein belegter Leerplatz, und dieselbe Kennung steht über
        // mehrere Varianten-Plätze hinweg mehrfach.
        var image = TriCoreDump.Pflash(0x1000, (0x200, TriCoreDump.VagIdentField(
            "04L907309P", "EV_ECM20TDI011", "04L906026GP", "004002", "3021", "R4 2.0l TDI",
            "----", "DFFA", "----", "DFFA", "DFGA")));

        var vag = VagIdentBlock.Find(image);

        Assert.NotNull(vag);
        Assert.Equal(["DFFA", "DFGA"], vag!.EngineCodes);
        Assert.Equal("DFFA, DFGA", vag.EngineCodeText);
    }

    [Fact]
    public void FieldWithoutEngineCodes_IsStillRead()
    {
        // Im Audi-Abbild stehen hinter der Motorbezeichnung Binärdaten statt
        // Kennbuchstaben. Das macht den übrigen Fund nicht ungültig.
        var image = TriCoreDump.Pflash(0x1000, (0x200, AudiField()));

        var vag = VagIdentBlock.Find(image);

        Assert.NotNull(vag);
        Assert.Empty(vag!.EngineCodes);
        Assert.Equal("7155", vag.SoftwareLevel);
    }

    [Fact]
    public void Identity_ReportsTheFieldAsConfirmed()
    {
        // Aus dem Feld werden Kennungen — und zwar gesicherte, weil sie aus
        // einer geprüften Struktur stammen und nicht aus einem Mustertreffer.
        var image = TriCoreDump.Pflash(0x1000, (0x200, AudiField()));

        var identity = BoschIdentity.Scan(image);

        Assert.NotNull(identity);
        Assert.NotNull(identity!.Vag);

        var level = Assert.Single(identity.Hits, h => h.Kind == "Softwarestand");
        Assert.Equal("7155", level.Value);
        Assert.Equal(RegionConfidence.Confirmed, level.Confidence);

        var software = Assert.Single(identity.Hits, h => h.Kind == "VAG-Software");
        Assert.Equal("04L906027BB", software.Value);

        // Die Nummer darf nicht zusätzlich als „Teilenummer" auftauchen —
        // einmal gesichert genügt.
        Assert.DoesNotContain(identity.Hits,
                              h => h.Kind == "Teilenummer" && h.Value == "04L906027BB");
    }
}

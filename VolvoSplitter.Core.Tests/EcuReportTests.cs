using VolvoSplitter.Core;

namespace VolvoSplitter.Core.Tests;

public class EcuReportTests
{
    [Fact]
    public void Parse_ExtractsFields()
    {
        string path = TestDump.TempPath(".TXT");
        try
        {
            File.WriteAllLines(path,
            [
                "Plugin : MPC5674",
                "Hardware Nr. : 31399478",
                "Software Nr. : 31399480",
                "Software Upgrade Nr. : 111 222 333",
                "leere Zeile ohne Doppelpunkt"
            ]);

            var report = EcuReport.Parse(path);

            Assert.NotNull(report);
            Assert.Equal("MPC5674", report!.Plugin);
            Assert.Equal("31399478", report.HardwareNumber);
            Assert.Equal("31399480", report.SoftwareNumber);
            Assert.Equal(["111", "222", "333"], report.UpgradeNumbers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindFor_StripsMicroSuffix()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vs_report_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Dump heißt "..._Micro.mpc", das Protokoll trägt den Namen ohne Suffix.
            string dump = Path.Combine(dir, "ECU123_Micro.mpc");
            File.WriteAllBytes(dump, new byte[16]);
            File.WriteAllLines(Path.Combine(dir, "ECU123.TXT"), ["Plugin : X"]);

            var report = EcuReport.FindFor(dump);

            Assert.NotNull(report);
            Assert.Equal("X", report!.Plugin);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindFor_NoReport_ReturnsNull()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vs_report_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dump = Path.Combine(dir, "lonely.mpc");
            File.WriteAllBytes(dump, new byte[16]);

            Assert.Null(EcuReport.FindFor(dump));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

using Verify11683A.App.Reports;
using Verify11683A.Results;

namespace Verify11683A.Tests;

public class PdfReportTests
{
    [Fact]
    public void A_complete_run_renders_to_a_pdf()
    {
        var pdf = PdfReport.Generate(ResultsFileTests.SampleRun(), "11683A_SG52060005_20261017-1430.json");
        Assert.True(pdf.Length > 10_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Failing_incomplete_and_simulated_runs_render_too()
    {
        var failing = ResultsFileTests.SampleRun((_, v) => v * 0.98);
        var partial = failing with
        {
            Part1 = failing.Part1! with { Points = failing.Part1.Points.Where(p => p.RangeId != "100mW").ToList(), Missing = ["100mW"] },
            Part2 = null,
            Conditions = failing.Conditions with { SimulationScenario = "supply-low", WarmupCutShort = true },
            Notes = [],
        };
        Assert.True(PdfReport.Generate(failing).Length > 10_000);
        Assert.True(PdfReport.Generate(partial).Length > 10_000);
    }

    [Fact]
    public void Regeneration_from_a_saved_results_file_writes_the_pdf_beside_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var json = ResultsFile.Save(ResultsFileTests.SampleRun(), directory);
            var pdf = PdfReport.WriteBeside(ResultsFile.Load(json), json);
            Assert.Equal(Path.ChangeExtension(json, ".pdf"), pdf);
            Assert.True(new FileInfo(pdf).Length > 10_000);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

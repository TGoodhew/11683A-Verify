using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using Verify11683A.App;
using Verify11683A.App.Reports;
using Verify11683A.Results;

namespace Verify11683A.Tests;

/// <summary>Printing the report: page placement, the rendered pages, finding a saved file's report, and a real print to a file.</summary>
[SupportedOSPlatform("windows6.1")]
public class PrintingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));

    public PrintingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_letter_page_prints_at_its_own_size_on_letter_paper()
    {
        var at = ReportPrinter.Fit(new SizeF(850, 1100), new RectangleF(0, 0, 850, 1100));
        Assert.Equal(new RectangleF(0, 0, 850, 1100), at);
    }

    [Fact]
    public void On_a4_the_page_is_scaled_down_to_fit_and_centred()
    {
        var a4 = new RectangleF(0, 0, 827, 1169);
        var at = ReportPrinter.Fit(new SizeF(850, 1100), a4);
        Assert.Equal(827, at.Width, 0.5);
        Assert.Equal(1100 * 827f / 850, at.Height, 0.5);
        Assert.Equal(0, at.X, 0.5);
        Assert.Equal((1169 - at.Height) / 2, at.Y, 0.5);
    }

    [Fact]
    public void Pages_are_rendered_at_300_dpi_on_letter()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var pages = ReportPrinter.Pages(PdfReport.Build(ResultsFileTests.SampleRun(), "x.json"));
        Assert.NotEmpty(pages);
        // PNG: width and height are big-endian in the IHDR chunk at bytes 16–23.
        var png = pages[0];
        Assert.Equal(0x89, png[0]);
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        // Within a pixel: QuestPDF rounds the page down.
        Assert.InRange(width, 8.5 * ReportPrinter.Dpi - 1, 8.5 * ReportPrinter.Dpi);
        Assert.InRange(height, 11 * ReportPrinter.Dpi - 1, 11 * ReportPrinter.Dpi);
    }

    [Fact]
    public void A_saved_verification_or_meter_run_gives_its_report()
    {
        var verification = ResultsFile.Save(ResultsFileTests.SampleRun(), _dir);
        var meter = MeterRunTests.Sample().Save(_dir);
        Assert.NotNull(Printing.DocumentFor(verification)());
        Assert.NotNull(Printing.DocumentFor(meter)());

        var other = Path.Combine(_dir, "other.json");
        File.WriteAllText(other, """{ "schema": "something-else" }""");
        Assert.Throws<OperatorException>(() => Printing.DocumentFor(other));
    }

    [Fact]
    public void Saved_results_are_newest_first_without_the_actuals_file()
    {
        var older = Path.Combine(_dir, "a.json");
        var newer = Path.Combine(_dir, "b.json");
        File.WriteAllText(older, "{}");
        File.WriteAllText(newer, "{}");
        File.WriteAllText(Path.Combine(_dir, CalibratorActuals.FileName), "{}");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        Assert.Equal([newer, older], Printing.SavedResults(_dir));
        Assert.Empty(Printing.SavedResults(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Print_and_printer_options_are_parsed()
    {
        var options = CommandLine.Parse(["--print", "r.json", "--printer", "Brother"]);
        Assert.Equal("r.json", options.Print);
        Assert.Equal("Brother", options.Printer);
    }

    [Fact]
    public void Prints_to_a_file_with_microsoft_print_to_pdf()
    {
        const string printer = "Microsoft Print to PDF";
        if (!OperatingSystem.IsWindows() || !PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer))
            return;
        var path = Path.Combine(_dir, "printed.pdf");
        var pages = ReportPrinter.Print(PdfReport.Build(ResultsFileTests.SampleRun(), "x.json"), printer, "test", path);
        Assert.True(pages >= 1);
        // The spooler writes the file asynchronously.
        for (var i = 0; i < 100 && !(File.Exists(path) && new FileInfo(path).Length > 0); i++)
            Thread.Sleep(100);
        Assert.True(new FileInfo(path).Length > 10_000);
        if (Environment.GetEnvironmentVariable("KEEP_PRINTED_PDF") is { } keep)
            File.Copy(path, keep, overwrite: true);
    }
}

using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Verify11683A.App.Reports;

/// <summary>
/// Prints a report on a Windows printer. QuestPDF renders each page to an image and System.Drawing prints the
/// images, so no PDF reader has to be installed. The page is printed at its own size (the report has 0.6 in
/// margins), scaled down to fit if the paper is smaller, such as A4.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ReportPrinter
{
    /// <summary>Raster resolution of the printed pages: sharp text on a laser printer, quick to spool.</summary>
    public const int Dpi = 300;

    /// <summary>The installed printers, the Windows default first.</summary>
    public static IReadOnlyList<string> Printers()
    {
        var all = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        var preferred = DefaultPrinter();
        return [.. all.Where(p => p == preferred), .. all.Where(p => p != preferred)];
    }

    /// <summary>The Windows default printer, or null if there is none.</summary>
    public static string? DefaultPrinter()
    {
        var settings = new PrinterSettings();
        return settings.IsDefaultPrinter && settings.PrinterName != "" ? settings.PrinterName : null;
    }

    /// <summary>Renders the pages of <paramref name="document"/> at <see cref="Dpi"/>.</summary>
    public static IReadOnlyList<byte[]> Pages(IDocument document) =>
        document.GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = Dpi }).ToList();

    /// <summary>Prints <paramref name="document"/> on <paramref name="printer"/>. Returns the number of pages sent.</summary>
    /// <param name="toFile">Print to this file instead, e.g. with Microsoft Print to PDF (used by the tests).</param>
    public static int Print(IDocument document, string printer, string jobName, string? toFile = null)
    {
        var pages = Pages(document);
        using var print = new PrintDocument { DocumentName = jobName };
        print.PrinterSettings.PrinterName = printer;
        if (!print.PrinterSettings.IsValid)
            throw new InvalidOperationException($"No printer named '{printer}'.");
        if (toFile is not null)
        {
            print.PrinterSettings.PrintToFile = true;
            print.PrinterSettings.PrintFileName = toFile;
        }
        // No "Printing page n" dialog: the console says what is happening.
        print.PrintController = new StandardPrintController();

        var next = 0;
        print.PrintPage += (_, e) =>
        {
            using var stream = new MemoryStream(pages[next]);
            using var image = System.Drawing.Image.FromStream(stream);
            var graphics = e.Graphics!;
            // Graphics start at the printable area's corner; move back to the paper's corner so the page lands where
            // its own margins put it.
            graphics.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            // Page bounds are in hundredths of an inch.
            var size = new SizeF(image.Width / image.HorizontalResolution * 100, image.Height / image.VerticalResolution * 100);
            graphics.DrawImage(image, Fit(size, e.PageBounds));
            next++;
            e.HasMorePages = next < pages.Count;
        };
        print.Print();
        return pages.Count;
    }

    /// <summary>
    /// Where to draw a page of <paramref name="page"/> size on <paramref name="paper"/>: at its own size if it fits,
    /// otherwise scaled down to fit, keeping its shape; centred either way.
    /// </summary>
    public static RectangleF Fit(SizeF page, RectangleF paper)
    {
        var scale = Math.Min(1f, Math.Min(paper.Width / page.Width, paper.Height / page.Height));
        var size = new SizeF(page.Width * scale, page.Height * scale);
        return new RectangleF(paper.X + (paper.Width - size.Width) / 2, paper.Y + (paper.Height - size.Height) / 2, size.Width, size.Height);
    }
}

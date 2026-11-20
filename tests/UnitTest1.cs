using Microsoft.VisualStudio.TestTools.UnitTesting;
using MultiCopyPrint.Models;
using MultiCopyPrint.Services;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.IO;
using System.Text;

namespace MultiCopyPrint.Tests;

[TestClass]
public class MultiCopyPrintTests
{
    private string _testDir = null!;
    private string _sample5PagePdf = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "MultiCopyPrint_UnitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _sample5PagePdf = Path.Combine(_testDir, "test_5pages.pdf");

        // Generate 5-page test PDF
        using var doc = new PdfDocument();
        for (int i = 1; i <= 5; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 24, XFontStyle.Bold);
            gfx.DrawString($"Test Page {i}", font, XBrushes.Black, new XRect(0, 0, page.Width.Point, page.Height.Point), XStringFormats.Center);
        }
        doc.Save(_sample5PagePdf);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Test_PdfLoadingAndThumbnails()
    {
        var service = new PdfPrintService();
        var pages = await service.LoadPagesAsync(_sample5PagePdf);

        Assert.AreEqual(5, pages.Count, "Should load exactly 5 pages.");
        Assert.AreEqual(1, pages[0].PageNumber);
        Assert.AreEqual(5, pages[4].PageNumber);
        Assert.IsNotNull(pages[0].Thumbnail, "Thumbnail for page 1 should be generated.");
    }

    [TestMethod]
    public async Task Test_PageMultiplicationExactUserSpecification()
    {
        var service = new PdfPrintService();
        var pages = await service.LoadPagesAsync(_sample5PagePdf);

        pages[0].CopyCount = 3;
        pages[1].CopyCount = 2;
        pages[2].CopyCount = 4;
        pages[3].CopyCount = 6;
        pages[4].CopyCount = 8;

        string multipliedPdf = service.GenerateMultipliedPdf(_sample5PagePdf, pages);

        Assert.IsTrue(File.Exists(multipliedPdf), "Multiplied PDF file should exist.");

        using var resultDoc = PdfReader.Open(multipliedPdf, PdfDocumentOpenMode.Import);
        Assert.AreEqual(23, resultDoc.PageCount, "Total pages in output must be exactly 23.");

        try { File.Delete(multipliedPdf); } catch { }
    }

    [TestMethod]
    public async Task Test_ZeroCopiesSkipsPage()
    {
        var service = new PdfPrintService();
        var pages = await service.LoadPagesAsync(_sample5PagePdf);

        pages[0].CopyCount = 2;
        pages[1].CopyCount = 0;
        pages[2].CopyCount = 1;
        pages[3].CopyCount = 0;
        pages[4].CopyCount = 3;

        string multipliedPdf = service.GenerateMultipliedPdf(_sample5PagePdf, pages);

        using var resultDoc = PdfReader.Open(multipliedPdf, PdfDocumentOpenMode.Import);
        Assert.AreEqual(6, resultDoc.PageCount, "Total pages should be 2 + 0 + 1 + 0 + 3 = 6.");

        try { File.Delete(multipliedPdf); } catch { }
    }

    [TestMethod]
    public void Test_PagePrintItemNegativeClamping()
    {
        var item = new PagePrintItem { PageNumber = 1, CopyCount = 5 };
        item.CopyCount = -3;
        Assert.AreEqual(0, item.CopyCount, "Negative copy counts must be clamped to 0.");
    }

    [TestMethod]
    public void Test_SumatraPathResolutionBundled()
    {
        string path = PdfPrintService.FindSumatraPath();
        string expectedBundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "SumatraPDF.exe");

        Assert.IsTrue(File.Exists(path), $"Resolved Sumatra path must exist on disk: {path}");
        Assert.AreEqual(expectedBundled.ToLowerInvariant(), path.ToLowerInvariant(),
            "PdfPrintService must prioritize the bundled tools\\SumatraPDF.exe to ensure zero client-machine dependencies.");
    }

    [TestMethod]
    public void Test_SpoolDirectoryPointsToCommonApplicationData()
    {
        using var watcher = new SpoolWatcherService();
        string expectedBase = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        StringAssert.StartsWith(watcher.SpoolDirectory, expectedBase, "Spool folder must be under CommonApplicationData (C:\\ProgramData) for SYSTEM/User sharing.");
        Assert.IsTrue(Directory.Exists(watcher.SpoolDirectory), "Spool directory must be created automatically.");
    }

    [TestMethod]
    public async Task Test_SpoolWatcherWaitsForFileLockRelease()
    {
        string testSpoolFile = Path.Combine(_testDir, "locked_job.ps");

        var lockTask = Task.Run(async () =>
        {
            using var file = File.Open(testSpoolFile, FileMode.Create, FileAccess.Write, FileShare.None);
            await Task.Delay(600);
            byte[] dummyData = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\n%%Pages: 1\n");
            file.Write(dummyData, 0, dummyData.Length);
            file.Flush();
        });

        bool ready = await Task.Run(() => SpoolWatcherService.WaitForFileReady(testSpoolFile, timeoutMs: 3000, pollIntervalMs: 100));

        await lockTask;
        Assert.IsTrue(ready, "WaitForFileReady must successfully wait and acquire the file once lock is released.");
    }

    [TestMethod]
    public void Test_CmykPreservationAfterMultiplication()
    {
        // 1. Synthesize pure CMYK PDF (100% Magenta: C:0, M:1, Y:0, K:0)
        string cmykSourcePdf = Path.Combine(_testDir, "source_cmyk.pdf");
        using (var doc = new PdfDocument())
        {
            doc.Options.ColorMode = PdfColorMode.Cmyk;
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var pureMagenta = XColor.FromCmyk(0.0, 1.0, 0.0, 0.0);
            gfx.DrawRectangle(new XSolidBrush(pureMagenta), new XRect(0, 0, page.Width.Point, page.Height.Point));
            doc.Save(cmykSourcePdf);
        }

        // 2. Multiply page 3 times
        var service = new PdfPrintService();
        var items = new List<PagePrintItem> { new() { PageNumber = 1, CopyCount = 3 } };
        string multipliedPdf = service.GenerateMultipliedPdf(cmykSourcePdf, items);

        Assert.IsTrue(File.Exists(multipliedPdf));

        // 3. Verify output document structure
        using (var outputDoc = PdfReader.Open(multipliedPdf, PdfDocumentOpenMode.Import))
        {
            Assert.AreEqual(3, outputDoc.PageCount, "Multiplied CMYK document must contain 3 pages.");
        }

        // 4. Assert native CMYK color space preservation without RGB flattening
        string pdfText = File.ReadAllText(multipliedPdf);
        Assert.IsTrue(pdfText.Contains("DeviceCMYK"), "Multiplied PDF stream must preserve native CMYK (/DeviceCMYK).");
        Assert.IsFalse(pdfText.Contains("DeviceRGB"), "Multiplied PDF must not flatten CMYK into sRGB (/DeviceRGB).");

        try { File.Delete(multipliedPdf); } catch { }
    }

    [TestMethod]
    public async Task Test_InstructionParserComplexWhatsAppInput()
    {
        string input = "Pg1 - 10 copies, pg 2-4 - 5 each, last page 100";
        int totalPages = 5;

        var result = await InstructionParserService.ParseAsync(input, totalPages);

        Assert.IsFalse(result.UsedAi, "Standard regex patterns should parse offline without AI.");
        Assert.AreEqual(10, result.PageCopies[1], "Page 1 should have 10 copies.");
        Assert.AreEqual(5, result.PageCopies[2], "Page 2 should have 5 copies.");
        Assert.AreEqual(5, result.PageCopies[3], "Page 3 should have 5 copies.");
        Assert.AreEqual(5, result.PageCopies[4], "Page 4 should have 5 copies.");
        Assert.AreEqual(100, result.PageCopies[5], "Last page (5) should have 100 copies.");
    }

    [TestMethod]
    public async Task Test_InstructionParserOutOfBoundsRejection()
    {
        string input = "pg 1: 5, pg 8: 20";
        int totalPages = 5;

        var result = await InstructionParserService.ParseAsync(input, totalPages);

        Assert.AreEqual(5, result.PageCopies[1], "Page 1 should be set.");
        Assert.IsFalse(result.PageCopies.ContainsKey(8), "Page 8 must be rejected since document has only 5 pages.");
        Assert.IsTrue(result.Warnings.Count > 0, "Warnings should report ignored page 8.");
        StringAssert.Contains(result.Warnings[0], "Ignored Page 8");
    }

    [TestMethod]
    public async Task Test_InstructionParserAllPagesPattern()
    {
        string input = "all pages = 50";
        int totalPages = 4;

        var result = await InstructionParserService.ParseAsync(input, totalPages);

        Assert.AreEqual(4, result.PageCopies.Count);
        for (int p = 1; p <= totalPages; p++)
        {
            Assert.AreEqual(50, result.PageCopies[p]);
        }
    }

    [TestMethod]
    public async Task Test_InstructionParserBareNumbersWithoutPrefix()
    {
        // Testing user's exact specification without 'p' or 'pg' prefix:
        // 1- 3, 2- 2, 3-4, 4-6, 5-8
        string input = "1- 3, 2- 2, 3-4, 4-6, 5-8";
        int totalPages = 5;

        var result = await InstructionParserService.ParseAsync(input, totalPages);

        Assert.IsFalse(result.UsedAi, "Bare numbers with operators should parse offline via Layer 1.");
        Assert.AreEqual(3, result.PageCopies[1]);
        Assert.AreEqual(2, result.PageCopies[2]);
        Assert.AreEqual(4, result.PageCopies[3]);
        Assert.AreEqual(6, result.PageCopies[4]);
        Assert.AreEqual(8, result.PageCopies[5]);
    }

    [TestMethod]
    public void Test_InstructionParserCleanJsonFences()
    {
        string rawWithFences = "```json\n[{\"page\": 1, \"copies\": 10}]\n```";
        string cleaned = InstructionParserService.CleanJsonFences(rawWithFences);
        Assert.AreEqual("[{\"page\": 1, \"copies\": 10}]", cleaned);
    }

    [TestMethod]
    public async Task Test_InstructionParserWhatsAppHeaderStripping()
    {
        // Real-world WhatsApp forward containing timestamp [10:30 AM, 15/11/2026] and phone number
        string input = "[10:30 AM, 15/11/2026] +91 98765-43210: 1- 3, 2- 2, 3- 4, 4- 6, 5- 8";
        int totalPages = 5;

        var result = await InstructionParserService.ParseAsync(input, totalPages);

        Assert.IsFalse(result.UsedAi, "Should parse via offline regex without triggering AI.");
        Assert.IsFalse(result.PageCopies.ContainsKey(10), "Timestamp 10:30 must be stripped and not matched as Page 10.");
        Assert.AreEqual(3, result.PageCopies[1]);
        Assert.AreEqual(2, result.PageCopies[2]);
        Assert.AreEqual(4, result.PageCopies[3]);
        Assert.AreEqual(6, result.PageCopies[4]);
        Assert.AreEqual(8, result.PageCopies[5]);
    }

    [TestMethod]
    public async Task Test_InstructionParserPatternBTextAndRanges()
    {
        // Pattern B: Pg 1: 5, Pg 2-4: 10, All: 20
        string input = "All: 20, Pg 1: 5, Pg 2-4: 10";
        int totalPages = 5;

        var result = await InstructionParserService.ParseAsync(input, totalPages);

        Assert.AreEqual(5, result.PageCopies[1]);
        Assert.AreEqual(10, result.PageCopies[2]);
        Assert.AreEqual(10, result.PageCopies[3]);
        Assert.AreEqual(10, result.PageCopies[4]);
        Assert.AreEqual(20, result.PageCopies[5]);
    }

    [TestMethod]
    public async Task Test_InstructionParserBareColonsWithoutSpaces()
    {
        // Ensure 1:10 is not accidentally stripped as a timestamp and correctly parses as Page 1 = 10
        string input = "1:10, 2:20";
        int totalPages = 2;

        var result = await InstructionParserService.ParseAsync(input, totalPages);

        Assert.AreEqual(10, result.PageCopies[1]);
        Assert.AreEqual(20, result.PageCopies[2]);
    }
}

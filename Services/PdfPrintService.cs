using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using MultiCopyPrint.Models;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace MultiCopyPrint.Services;

public class PdfPrintService
{
    #region Win32 Spooler Interop for Direct RAW Printing
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string? pDataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
    #endregion

    public static string FindSumatraPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string bundled = Path.Combine(baseDir, "tools", "SumatraPDF.exe");
        if (File.Exists(bundled)) return bundled;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(localAppData, "SumatraPDF", "SumatraPDF.exe"),
            @"C:\Program Files\SumatraPDF\SumatraPDF.exe",
            @"C:\Program Files (x86)\SumatraPDF\SumatraPDF.exe"
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return "SumatraPDF.exe";
    }

    public static string FindGhostscriptPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string bundled = Path.Combine(baseDir, "tools", "gswin64c.exe");
        if (File.Exists(bundled)) return bundled;

        string[] candidates =
        [
            @"C:\Program Files\gs\gs10.04.0\bin\gswin64c.exe",
            @"C:\Program Files\gs\gs10.03.1\bin\gswin64c.exe",
            @"C:\Program Files\gs\gs10.02.0\bin\gswin64c.exe"
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        try
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string gsFolder = Path.Combine(programFiles, "gs");
            if (Directory.Exists(gsFolder))
            {
                var match = Directory.GetFiles(gsFolder, "gswin64c.exe", SearchOption.AllDirectories);
                if (match.Length > 0) return match[0];
            }
        }
        catch { }

        return "gswin64c.exe";
    }

    public static bool TryConvertPostScriptToPdf(string psPath, out string pdfPath)
    {
        pdfPath = Path.Combine(Path.GetTempPath(), "MultiCopyPrint", $"{Path.GetFileNameWithoutExtension(psPath)}_{Guid.NewGuid():N}.pdf");
        string gsExe = FindGhostscriptPath();

        string tempDir = Path.GetDirectoryName(pdfPath)!;
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gsExe,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-dNOPAUSE");
            psi.ArgumentList.Add("-dBATCH");
            psi.ArgumentList.Add("-dSAFER");
            psi.ArgumentList.Add("-sDEVICE=pdfwrite");
            psi.ArgumentList.Add("-sColorConversionStrategy=LeaveColorUnchanged");
            psi.ArgumentList.Add("-dDownsampleColorImages=false");
            psi.ArgumentList.Add($"-sOutputFile={pdfPath}");
            psi.ArgumentList.Add(psPath);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(30000);
            return proc.ExitCode == 0 && File.Exists(pdfPath);
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<PagePrintItem>> LoadPagesAsync(string pdfPath)
    {
        return await Task.Run(() =>
        {
            var items = new List<PagePrintItem>();
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            int pageCount = doc.PageCount;

            // Stream each page without loading entire PDF into byte[] in Large Object Heap
            for (int i = 0; i < pageCount; i++)
            {
                BitmapSource? thumb = null;
                try
                {
                    using var stream = File.OpenRead(pdfPath);
                    using var skBitmap = PDFtoImage.Conversion.ToImage(stream, page: i, options: new PDFtoImage.RenderOptions(Dpi: 72));
                    thumb = ConvertSkBitmapToBitmapSource(skBitmap);
                }
                catch
                {
                    // Fallback to null thumbnail if render fails
                }

                items.Add(new PagePrintItem
                {
                    PageNumber = i + 1,
                    CopyCount = 1,
                    Thumbnail = thumb
                });
            }

            return items;
        });
    }

    public string GenerateMultipliedPdf(string sourcePdfPath, IReadOnlyList<PagePrintItem> pageItems)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MultiCopyPrint");
        if (!Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }

        string outputPath = Path.Combine(tempDir, $"print_{Guid.NewGuid():N}.pdf");

        using var sourceDoc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
        using var outputDoc = new PdfDocument();

        // Set CMYK color mode unconditionally so both flat pre-press PDFs (PDF/X-1a, CorelDRAW)
        // and transparency-grouped documents preserve native CMYK without sRGB flattening.
        outputDoc.Options.ColorMode = PdfColorMode.Cmyk;

        foreach (var item in pageItems)
        {
            if (item.CopyCount <= 0) continue;
            var sourcePage = sourceDoc.Pages[item.PageNumber - 1];

            for (int c = 0; c < item.CopyCount; c++)
            {
                outputDoc.AddPage(sourcePage);
            }
        }

        outputDoc.Save(outputPath);
        return outputPath;
    }

    public bool PrintWithSumatra(string pdfPath, string printerName)
    {
        string sumatraExe = FindSumatraPath();
        var psi = new ProcessStartInfo
        {
            FileName = sumatraExe,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-print-to");
        psi.ArgumentList.Add(printerName);
        psi.ArgumentList.Add("-print-settings");
        psi.ArgumentList.Add("noscale");
        psi.ArgumentList.Add("-silent");
        psi.ArgumentList.Add(pdfPath);

        using var proc = Process.Start(psi);
        if (proc == null) return false;

        proc.WaitForExit(30000);
        return proc.ExitCode == 0;
    }

    /// <summary>
    /// Sends a raw PostScript / PRN stream directly to a printer port via Win32 spooler,
    /// bypassing GDI/sRGB rasterization for 100% CMYK and vector fidelity on PostScript/RIP hardware.
    /// </summary>
    public static bool SendRawBytesToPrinter(string printerName, byte[] bytes, string documentName = "MultiCopy Job")
    {
        var di = new DOCINFOA { pDocName = documentName, pDataType = "RAW" };
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero)) return false;

        try
        {
            if (!StartDocPrinter(hPrinter, 1, di)) return false;
            try
            {
                if (!StartPagePrinter(hPrinter)) return false;
                try
                {
                    IntPtr pUnmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, pUnmanagedBytes, bytes.Length);
                        return WritePrinter(hPrinter, pUnmanagedBytes, bytes.Length, out int _);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pUnmanagedBytes);
                    }
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    private static BitmapSource ConvertSkBitmapToBitmapSource(SKBitmap skBitmap)
    {
        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 85);
        using var stream = new MemoryStream(data.ToArray());

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MultiCopyPrint.Models;
using MultiCopyPrint.Services;

namespace MultiCopyPrint;

public partial class MainWindow : Window
{
    private readonly PdfPrintService _printService = new();
    private readonly SpoolWatcherService _spoolWatcher = new();
    private readonly ObservableCollection<PagePrintItem> _pageItems = [];
    private string? _currentPdfPath;
    private bool _isSyncingGlobalCopies;

    public MainWindow()
    {
        InitializeComponent();
        LstPages.ItemsSource = _pageItems;
        LoadInstalledPrinters();

        _spoolWatcher.JobArrived += OnJobArrivedFromSpool;

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]) && Path.GetExtension(args[1]).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadDocumentAsync(args[1]);
        }
    }

    private void LoadInstalledPrinters()
    {
        CmbPrinters.Items.Clear();
        string defaultPrinter = "";
        try
        {
            var printDoc = new PrintDocument();
            defaultPrinter = printDoc.PrinterSettings.PrinterName;

            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                CmbPrinters.Items.Add(printer);
            }
        }
        catch
        {
            // Fallback
        }

        if (CmbPrinters.Items.Count > 0)
        {
            CmbPrinters.SelectedItem = !string.IsNullOrEmpty(defaultPrinter) && CmbPrinters.Items.Contains(defaultPrinter)
                ? defaultPrinter
                : CmbPrinters.Items[0];
        }
    }

    public async Task LoadDocumentAsync(string pdfPath)
    {
        try
        {
            TxtDocumentStatus.Text = $"Loading {Path.GetFileName(pdfPath)}...";
            TxtJobStatus.Text = "Rendering thumbnails...";
            _currentPdfPath = pdfPath;

            var items = await _printService.LoadPagesAsync(pdfPath);

            _pageItems.Clear();
            foreach (var item in items)
            {
                item.PropertyChanged += (s, e) => UpdateSummary();
                _pageItems.Add(item);
            }

            TxtDocumentStatus.Text = $"{Path.GetFileName(pdfPath)} ({_pageItems.Count} pages)";
            TxtJobStatus.Text = "Document ready";

            if (ChkSameForAll.IsChecked == true && int.TryParse(TxtGlobalCopies.Text, out int globalCount))
            {
                ApplyGlobalCopies(globalCount);
            }

            UpdateSummary();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load PDF:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtDocumentStatus.Text = "Error loading document.";
            TxtJobStatus.Text = "Ready";
        }
    }

    private async void OnJobArrivedFromSpool(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string pdfToLoad = filePath;

        if (ext == ".ps" || ext == ".prn")
        {
            if (PdfPrintService.TryConvertPostScriptToPdf(filePath, out string convertedPdf))
            {
                pdfToLoad = convertedPdf;
            }
            else
            {
                MessageBox.Show($"Received print spool file '{Path.GetFileName(filePath)}', but Ghostscript is required to convert PostScript to PDF.\n\nPlease install Ghostscript or drag-and-drop your PDF directly.", "PostScript Spool", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        await Dispatcher.InvokeAsync(async () =>
        {
            Activate();
            WindowState = WindowState.Normal;
            await LoadDocumentAsync(pdfToLoad);
        });
    }

    private async void BtnApplyInstructions_Click(object sender, RoutedEventArgs e)
    {
        if (_pageItems.Count == 0)
        {
            MessageBox.Show("Please load a PDF document first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string rawText = TxtInstructions.Text;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            MessageBox.Show("Please paste or type instructions first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TxtJobStatus.Text = "Parsing instructions...";

        var parsed = await InstructionParserService.ParseAsync(rawText, _pageItems.Count);

        if (parsed.PageCopies.Count == 0 && parsed.Warnings.Count == 0)
        {
            MessageBox.Show("Could not detect page numbers or copy counts. Please verify instructions format.",
                            "Parse Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtJobStatus.Text = "Ready";
            return;
        }

        // Reset all to 0 first so only requested pages print, then apply parsed counts
        _isSyncingGlobalCopies = true;
        foreach (var item in _pageItems)
        {
            item.CopyCount = parsed.PageCopies.TryGetValue(item.PageNumber, out int count) ? count : 0;
        }
        _isSyncingGlobalCopies = false;
        UpdateSummary();

        TxtJobStatus.Text = parsed.UsedAi ? "Applied instructions (via AI fallback)" : "Applied instructions (via Offline Parser)";

        if (parsed.Warnings.Count > 0)
        {
            MessageBox.Show(string.Join("\n", parsed.Warnings), "Out-of-Range Pages Ignored",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateSummary()
    {
        int totalPrints = _pageItems.Sum(p => p.CopyCount);
        TxtTotalSummary.Text = $"Total prints: {totalPrints} sheets ({_pageItems.Count} pages)";
        BtnPrint.Content = totalPrints > 0 ? $"🖨️ Print ({totalPrints} sheets)" : "🖨️ Print Now";
    }

    private void BtnDriverSettings_Click(object sender, RoutedEventArgs e)
    {
        string? selectedPrinter = CmbPrinters.SelectedItem as string;
        if (string.IsNullOrEmpty(selectedPrinter))
        {
            MessageBox.Show("Please select a printer first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /e /n \"{selectedPrinter}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open printer driver settings: {ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnQuickFill_All1_Click(object sender, RoutedEventArgs e)
    {
        _isSyncingGlobalCopies = true;
        foreach (var item in _pageItems) item.CopyCount = 1;
        _isSyncingGlobalCopies = false;
        UpdateSummary();
    }

    private void BtnQuickFill_All0_Click(object sender, RoutedEventArgs e)
    {
        _isSyncingGlobalCopies = true;
        foreach (var item in _pageItems) item.CopyCount = 0;
        _isSyncingGlobalCopies = false;
        UpdateSummary();
    }

    private void BtnQuickFill_Odd_Click(object sender, RoutedEventArgs e)
    {
        _isSyncingGlobalCopies = true;
        foreach (var item in _pageItems) item.CopyCount = (item.PageNumber % 2 != 0) ? 1 : 0;
        _isSyncingGlobalCopies = false;
        UpdateSummary();
    }

    private void BtnQuickFill_Even_Click(object sender, RoutedEventArgs e)
    {
        _isSyncingGlobalCopies = true;
        foreach (var item in _pageItems) item.CopyCount = (item.PageNumber % 2 == 0) ? 1 : 0;
        _isSyncingGlobalCopies = false;
        UpdateSummary();
    }

    private void TxtCopyCount_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.SelectAll();
        }
    }

    private void TxtCopyCount_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Down)
        {
            e.Handled = true;
            (sender as TextBox)?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
        }
        else if (e.Key == System.Windows.Input.Key.Up)
        {
            e.Handled = true;
            (sender as TextBox)?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Previous));
        }
    }

    private void ApplyGlobalCopies(int count)
    {
        _isSyncingGlobalCopies = true;
        foreach (var item in _pageItems)
        {
            item.CopyCount = count;
        }
        _isSyncingGlobalCopies = false;
        UpdateSummary();
    }

    private void ChkSameForAll_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkSameForAll.IsChecked == true && int.TryParse(TxtGlobalCopies.Text, out int count))
        {
            ApplyGlobalCopies(count);
        }
    }

    private void TxtGlobalCopies_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ChkSameForAll.IsChecked == true && int.TryParse(TxtGlobalCopies.Text, out int count))
        {
            ApplyGlobalCopies(count);
        }
    }

    private void TxtCopyCount_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isSyncingGlobalCopies)
        {
            UpdateSummary();
        }
    }

    private void BtnInc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PagePrintItem item })
        {
            item.CopyCount++;
        }
    }

    private void BtnDec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PagePrintItem item } && item.CopyCount > 0)
        {
            item.CopyCount--;
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PDF Documents (*.pdf)|*.pdf|All Files (*.*)|*.*",
            Title = "Select PDF Document"
        };

        if (dlg.ShowDialog() == true)
        {
            _ = LoadDocumentAsync(dlg.FileName);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) is string pdfPath)
        {
            await LoadDocumentAsync(pdfPath);
        }
    }

    private async void BtnPrint_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pageItems.Count == 0)
        {
            MessageBox.Show("Please load a document before printing.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? selectedPrinter = CmbPrinters.SelectedItem as string;
        if (string.IsNullOrEmpty(selectedPrinter))
        {
            MessageBox.Show("Please select a destination printer.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int totalCopies = _pageItems.Sum(p => p.CopyCount);
        if (totalCopies == 0)
        {
            MessageBox.Show("Total copy count is 0. Please set copy counts greater than 0.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (totalCopies > 500)
        {
            var confirm = MessageBox.Show(
                $"This job will print {totalCopies:N0} total sheets. Are you sure you want to proceed?",
                "High Sheet Count Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;
        }

        TxtJobStatus.Text = "Assembling multiplied print job...";

        try
        {
            string multipliedPdf = await Task.Run(() => _printService.GenerateMultipliedPdf(_currentPdfPath, _pageItems));

            TxtJobStatus.Text = $"Dispatching to {selectedPrinter} via vector engine...";

            bool success = await Task.Run(() => _printService.PrintWithSumatra(multipliedPdf, selectedPrinter));

            if (success)
            {
                TxtJobStatus.Text = "Print job dispatched successfully!";
                MessageBox.Show($"Successfully dispatched {totalCopies} sheets to {selectedPrinter}!", "Print Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtJobStatus.Text = "Printing failed.";
                MessageBox.Show($"Print failed. Ensure printer '{selectedPrinter}' is online and accessible.", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            TxtJobStatus.Text = "Error during print assembly.";
            MessageBox.Show($"Print error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pageItems.Count == 0)
        {
            MessageBox.Show("Please load a document first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sfd = new SaveFileDialog
        {
            Filter = "PDF Document (*.pdf)|*.pdf",
            FileName = $"{Path.GetFileNameWithoutExtension(_currentPdfPath)}_multiplied.pdf",
            Title = "Save Multiplied PDF Document"
        };

        if (sfd.ShowDialog() == true)
        {
            try
            {
                TxtJobStatus.Text = "Exporting multiplied PDF...";
                string tempPdf = await Task.Run(() => _printService.GenerateMultipliedPdf(_currentPdfPath, _pageItems));
                File.Copy(tempPdf, sfd.FileName, true);
                TxtJobStatus.Text = "PDF exported successfully!";
                MessageBox.Show($"Saved multiplied PDF to:\n{sfd.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _spoolWatcher.Dispose();
        base.OnClosed(e);
    }
}

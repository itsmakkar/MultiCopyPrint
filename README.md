# MultiCopyPrint — Windows Multi-Copy Print Manager

A high-productivity Windows desktop application and pre-press utility designed for commercial print shops, copy centers, and digital printing studios.

Standard Windows print dialogs only allow printing identical copies of an entire document or forcing operators to manually submit separate jobs for each page. **MultiCopyPrint** gives you an interactive visual matrix to set custom copy counts for each individual page in a single unified print dispatch.

---

## Key Features

* **Visual Page Matrix**: Interactive thumbnails for every page in your PDF with individual copy counters, +/- increment buttons, and visual skipped-page badges.
* **Instant WhatsApp / Email Parser**: Paste customer instructions directly into the top bar. Automatically cleans WhatsApp timestamps, headers, and phone numbers:
  * **Quick Numbers:** `1- 3, 2- 2, 3- 4, 4- 6, 5- 8` *(Page - Copies)*
  * **Ranges & Bulk:** `Pg 1: 5, Pg 2-4: 10, All: 20, Last: 50`
* **100% CMYK Vector Fidelity**: Preserves native vector linework, hairlines, and CMYK color plates (`/DeviceCMYK`). Never flattens vectors to low-res RGB bitmaps.
* **True-to-Scale (`noscale`)**: Automatically disables printer driver auto-shrinking to maintain exact 1:1 commercial print dimensions.
* **Windows Explorer Right-Click Menu**: Right-click any `.pdf` in Windows Explorer $\rightarrow$ **Print with Multi-Copy Dialog**.
* **Pre-Press RIP Export**: Click **💾 Save CMYK PDF (RIP / Hotfolder)** to export the expanded, multiplied PDF directly into your RIP workflow (Fiery, VersaWorks, Caldera).
* **Watched Hotfolder Integration**: Monitors `C:\ProgramData\MultiCopyPrint\Spool\` for dropped PDFs (ideal for CorelDRAW `Publish to PDF` or Illustrator exports).
* **Operator Safety Guards**: Visual confirmation and warnings when total print volume exceeds 500 sheets to prevent accidental paper/toner waste.
* **Fully Standalone**: Built with self-contained .NET 8 (x64) and bundled runtime dependencies. End users do not need .NET or C++ runtimes installed.

---

## Quick Start for End Users

1. Download the latest release: **`MultiCopyPrint-v1.0.0-windows-x64.zip`** from the [Releases](../../releases) tab.
2. Extract the ZIP to your preferred location (e.g., `C:\Program Files\MultiCopyPrint` or `Documents`).
3. Right-click **`Install.bat`** $\rightarrow$ select **Run as administrator**.
   * *This registers the Windows right-click menu and creates Desktop shortcuts.*
4. **Print:** Right-click any PDF file $\rightarrow$ click **Print with Multi-Copy Dialog**.

---

## Instruction Formats Supported

Operators can paste customer messages directly into the **📋 Instructions** bar:

| Pattern Type | Input Example | Result |
| :--- | :--- | :--- |
| **Quick Numbers** | `1- 3, 2- 2, 3- 4, 4- 6, 5- 8` | Page 1 gets 3, Page 2 gets 2, Page 3 gets 4, etc. |
| **Ranges** | `Pg 1: 10, Pg 2-4: 5 each` | Page 1 gets 10; Pages 2, 3, 4 each get 5 copies. |
| **All / Bulk** | `All: 25` or `All pages = 50` | Sets every page to the specified quantity. |
| **Combined** | `All: 20, Pg 1: 5, Last: 100` | Base 20 for all, Page 1 overridden to 5, Last page to 100. |
| **Raw WhatsApp** | `[10:30 AM, 15/11/2026] +91 98765-43210: 1- 5, 2- 10` | Timestamps & phones stripped automatically; parses cleanly. |

---

## Quick Fill Presets

* **All = 1**: Resets all pages to 1 copy.
* **All = 0**: Clears all counts (skips all pages).
* **Odd Only**: Selects odd-numbered pages (1, 3, 5...).
* **Even Only**: Selects even-numbered pages (2, 4, 6...).
* **Same prints for all**: Locks all pages to synchronize with a global copy count.

---

## Building from Source

### Prerequisites
* Windows 10 / 11 (x64)
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build & Run Tests
```powershell
# Clone the repository
git clone https://github.com/itsmakkar/MultiCopyPrint.git
cd MultiCopyPrint

# Run unit tests
dotnet test tests/MultiCopyPrint.Tests.csproj

# Run the app
dotnet run
```

### Publish Self-Contained Release
To compile a single, zero-dependency release folder for distribution:
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

---

## Architecture Overview

```
MultiCopyPrint/
├── Models/
│   └── PagePrintItem.cs          # Page model, thumbnail, copy count & skip state
├── Services/
│   ├── PdfPrintService.cs        # PdfSharpCore CMYK multiplication & SumatraPDF dispatch
│   ├── InstructionParserService.cs# Regex parser & WhatsApp header noise filter
│   └── SpoolWatcherService.cs    # File lock-release polling & spool folder watcher
├── tests/
│   ├── MultiCopyPrint.Tests.csproj
│   └── UnitTest1.cs              # 16 unit tests (CMYK integrity, parser, lock polling)
├── tools/
│   └── SumatraPDF.exe            # Bundled vector print engine (noscale mode)
├── MainWindow.xaml / .cs         # Visual WPF grid, hotkeys, watermark placeholder
├── App.xaml / .cs                # Application entry point
├── install-all.ps1               # Automated installer (Program Files, HKLM, ACLs)
└── Install.bat                   # 1-click elevation wrapper for end users
```

---

## License

This project is licensed under the [MIT License](LICENSE).

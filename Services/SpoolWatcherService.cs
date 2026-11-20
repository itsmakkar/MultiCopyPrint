using System.IO;

namespace MultiCopyPrint.Services;

public class SpoolWatcherService : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly string _spoolDir;

    public event Action<string>? JobArrived;

    public SpoolWatcherService()
    {
        // Use CommonApplicationData (C:\ProgramData) so both SYSTEM (spoolsv.exe)
        // and interactive user session can access the same spool folder.
        _spoolDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MultiCopyPrint",
            "Spool"
        );

        if (!Directory.Exists(_spoolDir))
        {
            Directory.CreateDirectory(_spoolDir);
        }

        try
        {
            _watcher = new FileSystemWatcher(_spoolDir)
            {
                Filter = "*.*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFileCreated;
        }
        catch
        {
            // If watcher cannot start, app continues in drag-and-drop mode
        }
    }

    public string SpoolDirectory => _spoolDir;

    private async void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (ext != ".pdf" && ext != ".ps" && ext != ".prn") return;

        bool ready = await Task.Run(() => WaitForFileReady(e.FullPath));
        if (ready)
        {
            JobArrived?.Invoke(e.FullPath);
        }
    }

    public static bool WaitForFileReady(string filePath, int timeoutMs = 15000, int pollIntervalMs = 150)
    {
        int elapsed = 0;

        while (elapsed < timeoutMs)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    if (stream.Length > 0)
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                // File is still locked or being written by spooler
            }

            Thread.Sleep(pollIntervalMs);
            elapsed += pollIntervalMs;
        }

        return false;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}

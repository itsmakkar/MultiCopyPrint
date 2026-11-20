using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace MultiCopyPrint.Models;

public class PagePrintItem : INotifyPropertyChanged
{
    private int _copyCount = 1;
    private BitmapSource? _thumbnail;

    public int PageNumber { get; init; }

    public int CopyCount
    {
        get => _copyCount;
        set
        {
            if (_copyCount != value)
            {
                _copyCount = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSkipped));
            }
        }
    }

    public bool IsSkipped => _copyCount == 0;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace NMKRevit.NMKPrint.Models
{
  public partial class PrintPreviewRow : ObservableObject
  {
    public int Index { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Number { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Revision { get; init; } = "-";
    public string RevisionDate { get; init; } = "-";
    public string Size { get; init; } = "-";
    public string Format { get; init; } = string.Empty;
    public string Orientation { get; init; } = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsProgressTextVisible))]
    private double _progressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsProgressTextVisible))]
    private string _progressText = "0 %";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsProgressTextVisible))]
    private bool _isPrinting;

    [ObservableProperty]
    private MediaBrush _progressBrush = MediaBrushes.Transparent;

    public bool IsProgressBarVisible => IsPrinting && !string.Equals(ProgressText, "Error", System.StringComparison.OrdinalIgnoreCase);
    public bool IsProgressTextVisible => !IsProgressBarVisible;

    public MediaBrush Foreground => Format == "DWG" ? MediaBrushes.SeaGreen : MediaBrushes.Black;
  }
}

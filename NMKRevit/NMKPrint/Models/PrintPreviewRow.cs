namespace NMKRevit.NMKPrint.Models
{
  public class PrintPreviewRow
  {
    public int Index { get; init; }
    public string Number { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Revision { get; init; } = "-";
    public string RevisionDate { get; init; } = "-";
    public string Size { get; init; } = "-";
    public string Format { get; init; } = string.Empty;
    public string Orientation { get; init; } = "-";
    public System.Windows.Media.Brush Foreground => Format == "DWG"
      ? System.Windows.Media.Brushes.SeaGreen
      : System.Windows.Media.Brushes.Black;
  }
}

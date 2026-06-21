namespace NMKRevit.NMKPrint.Models
{
  public class PrintJob
  {
    public PrintItem Item { get; init; } = null!;
    public PrintFormat Format { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
  }
}

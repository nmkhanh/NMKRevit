using System;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace NMKRevit.FloorTool.Models
{
  public sealed class FloorToolLogEntry
  {
    private static readonly MediaBrush InfoBrush = CreateBrush("#4B5563");
    private static readonly MediaBrush SuccessBrush = CreateBrush("#138A3D");
    private static readonly MediaBrush WarningBrush = CreateBrush("#FF7A00");
    private static readonly MediaBrush ErrorBrush = CreateBrush("#FF4500");

    public FloorToolLogEntry(FloorToolLogLevel level, string tool, string elementId, string message)
    {
      Time = DateTime.Now;
      Level = level;
      Tool = tool;
      ElementId = elementId;
      Message = message;
    }

    public DateTime Time { get; }
    public FloorToolLogLevel Level { get; }
    public string Tool { get; }
    public string ElementId { get; }
    public string Message { get; }
    public MediaBrush Foreground => Level switch
    {
      FloorToolLogLevel.Success => SuccessBrush,
      FloorToolLogLevel.Warning => WarningBrush,
      FloorToolLogLevel.Error => ErrorBrush,
      _ => InfoBrush
    };

    private static MediaBrush CreateBrush(string color)
    {
      var brush = new MediaSolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color));
      brush.Freeze();
      return brush;
    }
  }
}

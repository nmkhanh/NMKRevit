using System;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace NMKRevit.Rebar.Models
{
  public sealed class RebarToolLogEntry
  {
    private static readonly MediaBrush InfoBrush = CreateBrush("#4B5563");
    private static readonly MediaBrush SuccessBrush = CreateBrush("#138A3D");
    private static readonly MediaBrush WarningBrush = CreateBrush("#FF7A00");
    private static readonly MediaBrush ErrorBrush = CreateBrush("#FF4500");

    public RebarToolLogEntry(RebarToolLogLevel level, string tool, string elementId, string message)
    {
      Time = DateTime.Now;
      Level = level;
      Tool = tool;
      ElementId = elementId;
      Message = message;
    }

    public DateTime Time { get; }
    public RebarToolLogLevel Level { get; }
    public string Tool { get; }
    public string ElementId { get; }
    public string Message { get; }
    public MediaBrush Foreground => Level switch
    {
      RebarToolLogLevel.Success => SuccessBrush,
      RebarToolLogLevel.Warning => WarningBrush,
      RebarToolLogLevel.Error => ErrorBrush,
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

using System.Collections.Generic;

namespace NMKRevit.FloorTool.Models
{
  public sealed class FloorToolResult
  {
    public string Summary { get; set; } = string.Empty;
    public List<FloorToolResultItem> Items { get; } = new();
  }

  public sealed class FloorToolResultItem
  {
    public FloorToolResultItem(FloorToolLogLevel level, string elementId, string message)
    {
      Level = level;
      ElementId = elementId;
      Message = message;
    }

    public FloorToolLogLevel Level { get; }
    public string ElementId { get; }
    public string Message { get; }
  }

  public enum JoinFloorScope
  {
    Selected,
    ActiveView,
    AllModel
  }
}

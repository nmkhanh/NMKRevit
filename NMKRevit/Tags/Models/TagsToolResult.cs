using System.Collections.Generic;

namespace NMKRevit.Tags.Models
{
  public sealed class TagsToolResult
  {
    public string Summary { get; set; } = string.Empty;
    public List<TagsToolResultItem> Items { get; } = new();
  }

  public sealed class TagsToolResultItem
  {
    public TagsToolResultItem(TagsToolLogLevel level, string elementId, string message)
    {
      Level = level;
      ElementId = elementId;
      Message = message;
    }

    public TagsToolLogLevel Level { get; }
    public string ElementId { get; }
    public string Message { get; }
  }

  public enum TagTargetKind
  {
    Unknown,
    Column,
    Floor,
    Wall
  }
}

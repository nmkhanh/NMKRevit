using System.Collections.Generic;
using System.Linq;

namespace NMKRevit.Rebar.Models
{
  public sealed class RebarPlacementResult
  {
    public int CreatedCount { get; set; }
    public Dictionary<string, int> CountsByBarId { get; } = new();

    public string Message
    {
      get
      {
        string detail = string.Join(", ", CountsByBarId.Select(pair => $"{pair.Key}: {pair.Value}"));
        return string.IsNullOrWhiteSpace(detail)
          ? $"Created {CreatedCount} bars."
          : $"Created {CreatedCount} bars.\n{detail}";
      }
    }
  }
}

using Newtonsoft.Json;
using System.Collections.Generic;

namespace NMKRevit.Rebar.Models
{
  public sealed class AiRebarConfig
  {
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonProperty("units")] public string Units { get; set; } = "mm";
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("distribution")] public AiRebarDistributionConfig Distribution { get; set; } = new();
    [JsonProperty("bars")] public List<AiRebarBarConfig> Bars { get; set; } = new();
  }

  public sealed class AiRebarBarConfig
  {
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("mark")] public string Mark { get; set; } = string.Empty;
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
    [JsonProperty("diameterMm")] public double DiameterMm { get; set; }
    [JsonProperty("typeName")] public string? TypeName { get; set; }
    [JsonProperty("side")] public string? Side { get; set; } = "auto";
    [JsonProperty("distribution")] public AiRebarDistributionConfig? Distribution { get; set; }
    [JsonProperty("shape")] public AiRebarShapeConfig Shape { get; set; } = new();
  }

  public sealed class AiRebarDistributionConfig
  {
    [JsonProperty("offsetsMm")] public List<double>? OffsetsMm { get; set; }
    [JsonProperty("spacingsMm")] public List<double>? SpacingsMm { get; set; }
    [JsonProperty("count")] public int? Count { get; set; }
    [JsonProperty("spacingMm")] public double? SpacingMm { get; set; }
  }

  public sealed class AiRebarShapeConfig
  {
    [JsonProperty("points")] public List<AiRebarPointConfig> Points { get; set; } = new();
  }

  public sealed class AiRebarPointConfig
  {
    [JsonProperty("xMm")] public double XMm { get; set; }
    [JsonProperty("zMm")] public double ZMm { get; set; }
  }
}

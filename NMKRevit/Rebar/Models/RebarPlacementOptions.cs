namespace NMKRevit.Rebar.Models
{
  public sealed class RebarPlacementOptions
  {
    public bool Accepted { get; set; }
    public string JsonPath { get; set; } = string.Empty;
    public string TypeNamePrefix { get; set; } = string.Empty;
    public double LeftOffsetMm { get; set; } = 150;
    public double RightOffsetMm { get; set; } = 150;
    public double FaceOffsetMm { get; set; } = 150;
    public bool AlternateRotate180 { get; set; }
  }
}

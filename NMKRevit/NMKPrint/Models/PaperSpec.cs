namespace NMKRevit.NMKPrint.Models
{
  public class PaperSpec
  {
    public PaperSpec(string name, double widthMm, double heightMm)
    {
      Name = name;
      WidthMm = widthMm;
      HeightMm = heightMm;
    }

    public string Name { get; }
    public double WidthMm { get; }
    public double HeightMm { get; }
    public string Orientation => WidthMm >= HeightMm ? "Landscape" : "Portrait";
  }
}

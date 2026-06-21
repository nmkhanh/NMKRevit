using Autodesk.Revit.DB;

namespace NMKRevit.NMKPrint.Models
{
  public enum SelectionSourceKind
  {
    User,
    Schedule,
    ViewSheetSet
  }

  public class SelectionSource
  {
    public string Name { get; init; } = string.Empty;
    public SelectionSourceKind Kind { get; init; }
    public ElementId Id { get; init; } = ElementId.InvalidElementId;
    public override string ToString() => Name;
  }
}

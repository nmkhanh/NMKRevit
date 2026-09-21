using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace NMKRebar.Services
{
  public sealed class NmkRebarArrayTypeSelectionFilter : ISelectionFilter
  {
    private readonly string _typeName;

    public NmkRebarArrayTypeSelectionFilter(string typeName)
    {
      _typeName = typeName;
    }

    public bool AllowElement(Element elem)
    {
      return CreateRebarByLineService.IsRebarArrayInstance(elem)
        && elem is FamilyInstance instance
        && instance.Symbol.Name.Equals(_typeName, StringComparison.OrdinalIgnoreCase);
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }
}

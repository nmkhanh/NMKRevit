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
      return elem is FamilyInstance instance
        && instance.Symbol?.Family?.Name.Equals(RebarTypeCreateService.ArrayFamilyName, StringComparison.OrdinalIgnoreCase) == true
        && instance.Symbol.Name.Equals(_typeName, StringComparison.OrdinalIgnoreCase);
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }
}

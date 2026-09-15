using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace NMKRebar.Services
{
  public sealed class NmkRebarArraySelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return elem is FamilyInstance instance
        && instance.Symbol?.Family?.Name.Equals(RebarTypeCreateService.ArrayFamilyName, StringComparison.OrdinalIgnoreCase) == true;
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }
}

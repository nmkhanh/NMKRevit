using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace NMKRebar.Services
{
  public sealed class NmkRebarArraySelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return CreateRebarByLineService.IsRebarArrayInstance(elem);
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }
}

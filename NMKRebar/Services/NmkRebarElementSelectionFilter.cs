using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI.Selection;

namespace NMKRebar.Services
{
  public sealed class NmkRebarElementSelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return elem is Rebar;
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace NMKRebar.Services
{
  public sealed class NmkRebarArrayFilteredTypesSelectionFilter : ISelectionFilter
  {
    private readonly HashSet<string> _typeNames;

    public NmkRebarArrayFilteredTypesSelectionFilter(IEnumerable<string> typeNames)
    {
      _typeNames = new HashSet<string>(
        typeNames.Where(name => !string.IsNullOrWhiteSpace(name)),
        StringComparer.OrdinalIgnoreCase);
    }

    public bool AllowElement(Element elem)
    {
      return CreateRebarByLineService.IsRebarArrayInstance(elem)
        && elem is FamilyInstance instance
        && instance.Symbol.Name != null
        && _typeNames.Contains(instance.Symbol.Name);
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }
}

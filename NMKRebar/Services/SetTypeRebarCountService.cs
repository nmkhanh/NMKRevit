using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace NMKRebar.Services
{
  public static class SetTypeRebarCountService
  {
    public static Dictionary<string, int> CountBarsByBarTypeName(Document doc)
    {
      var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (RevitRebar rebar in new FilteredElementCollector(doc)
        .OfClass(typeof(RevitRebar))
        .Cast<RevitRebar>())
      {
        if (doc.GetElement(rebar.GetTypeId()) is not RebarBarType barType)
        {
          continue;
        }

        int bars = rebar.NumberOfBarPositions;
        if (bars <= 0)
        {
          bars = 1;
        }

        counts.TryGetValue(barType.Name, out int sum);
        counts[barType.Name] = sum + bars;
      }

      return counts;
    }
  }
}

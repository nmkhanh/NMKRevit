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

    public static Dictionary<string, int> CountShapesByRebarTypeName(Document doc)
    {
      var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (FamilyInstance instance in new FilteredElementCollector(doc)
        .OfClass(typeof(FamilyInstance))
        .Cast<FamilyInstance>())
      {
        if (!CreateRebarByLineService.IsRebarShapeInstance(instance))
        {
          continue;
        }

        string typeName = ReadRebarTypeName(doc, instance);
        if (string.IsNullOrWhiteSpace(typeName))
        {
          continue;
        }

        counts.TryGetValue(typeName, out int sum);
        counts[typeName] = sum + 1;
      }

      return counts;
    }

    private static string ReadRebarTypeName(Document doc, FamilyInstance instance)
    {
      Parameter? parameter = instance.LookupParameter(CreateRebarByLineService.RebarTypeParameterName);
      if (parameter == null || !parameter.HasValue)
      {
        return string.Empty;
      }

      if (parameter.StorageType == StorageType.ElementId)
      {
        Element? element = doc.GetElement(parameter.AsElementId());
        return element?.Name?.Trim() ?? string.Empty;
      }

      string text = parameter.AsString();
      if (string.IsNullOrWhiteSpace(text))
      {
        text = parameter.AsValueString();
      }

      return text?.Trim() ?? string.Empty;
    }
  }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.FloorTool.Models;
using System.Collections.Generic;
using System.Linq;

namespace NMKRevit.FloorTool.Services
{
  public sealed class FloorSelectionService
  {
    public FloorToolResult SelectMultiIslandFloors(UIApplication uiapp)
    {
      UIDocument uidoc = uiapp.ActiveUIDocument;
      Document document = uidoc.Document;
      List<Floor> floors = GetSelectedOrActiveViewFloors(uidoc);
      var matchingIds = new List<ElementId>();
      var result = new FloorToolResult();
      int nonFlat = 0;
      int singleIsland = 0;

      foreach (Floor floor in floors)
      {
        Level? level = GetLevel(document, floor);
        FloorProfileAnalysis? analysis = level == null ? null : GeometryUtilities.AnalyzeFlatTop(floor, level.Elevation);
        if (analysis == null)
        {
          nonFlat++;
          result.Items.Add(new FloorToolResultItem(FloorToolLogLevel.Warning, floor.Id.ToString(), "Skipped: sloped or unsupported top geometry."));
        }
        else if (analysis.Profiles.Count > 1)
        {
          matchingIds.Add(floor.Id);
          result.Items.Add(new FloorToolResultItem(FloorToolLogLevel.Success, floor.Id.ToString(), $"Selected: {analysis.Profiles.Count} top islands."));
        }
        else
        {
          singleIsland++;
        }
      }

      uidoc.Selection.SetElementIds(matchingIds);
      result.Summary = $"Checked {floors.Count}; selected {matchingIds.Count}; single-island {singleIsland}; skipped non-flat {nonFlat}.";
      return result;
    }

    internal static List<Floor> GetSelectedOrActiveViewFloors(UIDocument uidoc)
    {
      Document document = uidoc.Document;
      List<Floor> selected = uidoc.Selection.GetElementIds()
        .Select(id => document.GetElement(id) as Floor)
        .Where(floor => floor != null)
        .Cast<Floor>()
        .ToList();
      return selected.Count > 0
        ? selected
        : new FilteredElementCollector(document, document.ActiveView.Id)
          .OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>().ToList();
    }

    internal static Level? GetLevel(Document document, Floor floor)
    {
      Level? level = document.GetElement(floor.LevelId) as Level;
      if (level != null)
      {
        return level;
      }
      Parameter parameter = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM);
      return parameter?.StorageType == StorageType.ElementId
        ? document.GetElement(parameter.AsElementId()) as Level
        : null;
    }
  }
}

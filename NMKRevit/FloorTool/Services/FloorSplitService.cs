using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.FloorTool.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NMKRevit.FloorTool.Services
{
  public sealed class FloorSplitService
  {
    public FloorToolResult SplitMultiIslandFloors(UIApplication uiapp)
    {
      UIDocument uidoc = uiapp.ActiveUIDocument;
      Document document = uidoc.Document;
      List<Floor> floors = FloorSelectionService.GetSelectedOrActiveViewFloors(uidoc);
      var result = new FloorToolResult();
      var createdIds = new List<ElementId>();
      int split = 0;
      int created = 0;
      int single = 0;
      int nonFlat = 0;
      int failed = 0;

      using var transaction = new Transaction(document, "Split multi-island floors");
      transaction.Start();
      foreach (Floor source in floors)
      {
        Level? level = FloorSelectionService.GetLevel(document, source);
        FloorProfileAnalysis? analysis = level == null ? null : GeometryUtilities.AnalyzeFlatTop(source, level.Elevation);
        if (analysis == null)
        {
          nonFlat++;
          result.Items.Add(new FloorToolResultItem(FloorToolLogLevel.Warning, source.Id.ToString(), "Skipped: sloped or unsupported top geometry."));
          continue;
        }
        if (analysis.Profiles.Count <= 1)
        {
          single++;
          continue;
        }

        using var subTransaction = new SubTransaction(document);
        subTransaction.Start();
        try
        {
          var newFloors = new List<Floor>();
          foreach (IList<CurveLoop> profile in analysis.Profiles)
          {
            newFloors.Add(CreateFloor(document, source, level!, profile));
          }
          document.Delete(source.Id);
          subTransaction.Commit();
          split++;
          created += newFloors.Count;
          createdIds.AddRange(newFloors.Select(floor => floor.Id));
          result.Items.Add(new FloorToolResultItem(
            FloorToolLogLevel.Success,
            source.Id.ToString(),
            $"Split into {newFloors.Count} Floors. New IDs: {string.Join(", ", newFloors.Select(floor => floor.Id.ToString()))}."));
        }
        catch (Exception ex)
        {
          subTransaction.RollBack();
          failed++;
          result.Items.Add(new FloorToolResultItem(FloorToolLogLevel.Error, source.Id.ToString(), $"Rolled back; original Floor kept. {ex.Message}"));
        }
      }
      transaction.Commit();
      uidoc.Selection.SetElementIds(createdIds);

      result.Summary = $"Checked {floors.Count}; split {split}; created {created}; single-island {single}; skipped non-flat {nonFlat}; failed {failed}.";
      return result;
    }

    private static Floor CreateFloor(Document document, Floor source, Level level, IList<CurveLoop> sourceProfile)
    {
      IList<CurveLoop> profile = GeometryUtilities.NormalizeProfile(sourceProfile);
      Floor? floor = TryCreateWithFullProfile(document, source, level, profile);
      if (floor != null)
      {
        return floor;
      }

      IList<CurveLoop>? cleanProfile = GeometryUtilities.BuildCleanPolylineProfile(profile);
      if (cleanProfile != null)
      {
        floor = TryCreateWithFullProfile(document, source, level, cleanProfile);
        if (floor != null)
        {
          return floor;
        }
        profile = cleanProfile;
      }

      floor = Floor.Create(document, new List<CurveLoop> { GeometryUtilities.CloneLoop(profile[0]) }, source.GetTypeId(), level.Id);
      CopySourceProperties(source, floor);
      for (int i = 1; i < profile.Count; i++)
      {
        try
        {
          document.Create.NewOpening(floor, GeometryUtilities.ToCurveArray(profile[i]), true);
        }
        catch (Exception ex)
        {
          throw new InvalidOperationException($"Opening {i} could not be recreated: {ex.Message}", ex);
        }
      }
      return floor;
    }

    private static Floor? TryCreateWithFullProfile(Document document, Floor source, Level level, IList<CurveLoop> profile)
    {
      if (!BoundaryValidation.IsValidHorizontalBoundary(profile))
      {
        return null;
      }
      using var subTransaction = new SubTransaction(document);
      subTransaction.Start();
      try
      {
        Floor floor = Floor.Create(document, profile, source.GetTypeId(), level.Id);
        CopySourceProperties(source, floor);
        subTransaction.Commit();
        return floor;
      }
      catch
      {
        subTransaction.RollBack();
        return null;
      }
    }

    private static void CopySourceProperties(Floor source, Floor target)
    {
      Parameter sourceOffset = source.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
      Parameter targetOffset = target.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
      if (sourceOffset != null && targetOffset != null && !targetOffset.IsReadOnly)
      {
        targetOffset.Set(sourceOffset.AsDouble());
      }
      GeometryUtilities.CopyWritableParameters(source, target);
    }
  }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.FloorTool.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NMKRevit.FloorTool.Services
{
  public sealed class FloorJoinService
  {
    public FloorToolResult JoinFloors(UIApplication uiapp, JoinFloorScope scope)
    {
      UIDocument uidoc = uiapp.ActiveUIDocument;
      Document document = uidoc.Document;
      List<Floor> floors = GetFloors(uidoc, scope);
      var result = new FloorToolResult();
      int candidates = 0;
      int joined = 0;
      int alreadyJoined = 0;
      int orderChanged = 0;
      int failed = 0;

      if (floors.Count < 2)
      {
        result.Summary = $"Scope {scope}: need at least two Floors; found {floors.Count}.";
        return result;
      }

      using var transaction = new Transaction(document, "Join floors");
      transaction.Start();
      for (int i = 0; i < floors.Count; i++)
      {
        for (int j = i + 1; j < floors.Count; j++)
        {
          Floor first = floors[i];
          Floor second = floors[j];
          if (!BoundingBoxesTouch(first, second))
          {
            continue;
          }
          candidates++;
          string ids = $"{first.Id}, {second.Id}";
          try
          {
            if (JoinGeometryUtils.AreElementsJoined(document, first, second))
            {
              alreadyJoined++;
            }
            else
            {
              JoinGeometryUtils.JoinGeometry(document, first, second);
              joined++;
            }
            if (EnsureThickerCuts(document, first, second))
            {
              orderChanged++;
            }
          }
          catch (Exception ex)
          {
            failed++;
            result.Items.Add(new FloorToolResultItem(FloorToolLogLevel.Error, ids, ex.Message));
          }
        }
      }
      transaction.Commit();
      result.Summary = $"Scope {scope}: Floors {floors.Count}; candidates {candidates}; joined {joined}; already joined {alreadyJoined}; order changed {orderChanged}; failed {failed}.";
      return result;
    }

    private static List<Floor> GetFloors(UIDocument uidoc, JoinFloorScope scope)
    {
      Document document = uidoc.Document;
      if (scope == JoinFloorScope.Selected)
      {
        return uidoc.Selection.GetElementIds().Select(id => document.GetElement(id) as Floor)
          .Where(floor => floor != null).Cast<Floor>().ToList();
      }
      FilteredElementCollector collector = scope == JoinFloorScope.ActiveView
        ? new FilteredElementCollector(document, document.ActiveView.Id)
        : new FilteredElementCollector(document);
      return collector.OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>().ToList();
    }

    private static bool BoundingBoxesTouch(Element first, Element second)
    {
      BoundingBoxXYZ firstBox = first.get_BoundingBox(null);
      BoundingBoxXYZ secondBox = second.get_BoundingBox(null);
      if (firstBox == null || secondBox == null)
      {
        return false;
      }
      const double tolerance = 1d / 304.8d;
      return firstBox.Min.X <= secondBox.Max.X + tolerance && firstBox.Max.X + tolerance >= secondBox.Min.X
        && firstBox.Min.Y <= secondBox.Max.Y + tolerance && firstBox.Max.Y + tolerance >= secondBox.Min.Y
        && firstBox.Min.Z <= secondBox.Max.Z + tolerance && firstBox.Max.Z + tolerance >= secondBox.Min.Z;
    }

    private static bool EnsureThickerCuts(Document document, Floor first, Floor second)
    {
      double firstThickness = GetThickness(first);
      double secondThickness = GetThickness(second);
      if (Math.Abs(firstThickness - secondThickness) < 1e-9)
      {
        return false;
      }
      Floor thicker = firstThickness > secondThickness ? first : second;
      Floor thinner = firstThickness > secondThickness ? second : first;
      if (JoinGeometryUtils.IsCuttingElementInJoin(document, thicker, thinner))
      {
        return false;
      }
      JoinGeometryUtils.SwitchJoinOrder(document, thicker, thinner);
      return true;
    }

    private static double GetThickness(Floor floor)
    {
      Parameter? parameter = floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
      if (parameter?.StorageType == StorageType.Double)
      {
        return parameter.AsDouble();
      }
      Element? type = floor.Document.GetElement(floor.GetTypeId());
      parameter = type?.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
      return parameter?.StorageType == StorageType.Double ? parameter.AsDouble() : 0;
    }
  }
}

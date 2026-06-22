using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using RevitView = Autodesk.Revit.DB.View;

namespace NMKRevit.FilledRegions.Services
{
  public sealed class FilledRegionSplitResult
  {
    public bool Cancelled { get; set; }
    public int Checked { get; set; }
    public int Split { get; set; }
    public int Created { get; set; }
    public int Skipped { get; set; }
    public List<string> Failures { get; } = new();
  }

  public sealed class FilledRegionSplitService
  {
    public FilledRegionSplitResult Execute(UIDocument uidoc)
    {
      Document document = uidoc.Document;
      RevitView view = document.ActiveView;
      List<FilledRegion>? regions = GetTargetRegions(uidoc, view);
      var result = new FilledRegionSplitResult { Cancelled = regions == null };
      if (regions == null)
      {
        return result;
      }

      result.Checked = regions.Count;
      using var transaction = new Transaction(document, "Split FilledRegion loops");
      transaction.Start();
      foreach (FilledRegion source in regions)
      {
        List<IList<CurveLoop>> groups;
        try
        {
          groups = FilledRegionBoundaryUtility.Split(source.GetBoundaries(), view);
        }
        catch (Exception ex)
        {
          result.Failures.Add($"{source.Id}: boundary analysis failed - {ex.Message}");
          continue;
        }

        if (groups.Count <= 1)
        {
          result.Skipped++;
          continue;
        }

        using var subTransaction = new SubTransaction(document);
        subTransaction.Start();
        try
        {
          var created = new List<FilledRegion>();
          foreach (IList<CurveLoop> group in groups)
          {
            FilledRegion region = FilledRegion.Create(document, source.GetTypeId(), view.Id, group);
            CopyWritableParameters(source, region);
            created.Add(region);
          }
          document.Delete(source.Id);
          subTransaction.Commit();
          result.Split++;
          result.Created += created.Count;
        }
        catch (Exception ex)
        {
          subTransaction.RollBack();
          result.Failures.Add($"{source.Id}: rolled back - {ex.Message}");
        }
      }
      transaction.Commit();
      return result;
    }

    private static List<FilledRegion>? GetTargetRegions(UIDocument uidoc, RevitView view)
    {
      Document document = uidoc.Document;
      List<FilledRegion> selected = uidoc.Selection.GetElementIds()
        .Select(id => document.GetElement(id) as FilledRegion)
        .Where(region => region != null && region.OwnerViewId == view.Id)
        .Cast<FilledRegion>()
        .ToList();
      if (selected.Count > 0)
      {
        return selected;
      }

      try
      {
        return uidoc.Selection.PickObjects(
            ObjectType.Element,
            new FilledRegionSelectionFilter(view.Id),
            "Pick FilledRegions to split")
          .Select(reference => document.GetElement(reference.ElementId) as FilledRegion)
          .Where(region => region != null)
          .Cast<FilledRegion>()
          .ToList();
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        return null;
      }
    }

    private static void CopyWritableParameters(Element source, Element target)
    {
      foreach (Parameter sourceParameter in source.Parameters)
      {
        if (sourceParameter == null || sourceParameter.IsReadOnly)
        {
          continue;
        }
        Parameter targetParameter = target.get_Parameter(sourceParameter.Definition);
        if (targetParameter == null || targetParameter.IsReadOnly || targetParameter.StorageType != sourceParameter.StorageType)
        {
          continue;
        }
        try
        {
          switch (sourceParameter.StorageType)
          {
            case StorageType.Double: targetParameter.Set(sourceParameter.AsDouble()); break;
            case StorageType.Integer: targetParameter.Set(sourceParameter.AsInteger()); break;
            case StorageType.String: targetParameter.Set(sourceParameter.AsString()); break;
            case StorageType.ElementId: targetParameter.Set(sourceParameter.AsElementId()); break;
          }
        }
        catch
        {
          // Ignore system-owned parameters that reject Set.
        }
      }
    }

    private sealed class FilledRegionSelectionFilter : ISelectionFilter
    {
      private readonly ElementId _viewId;

      public FilledRegionSelectionFilter(ElementId viewId)
      {
        _viewId = viewId;
      }

      public bool AllowElement(Element element) => element is FilledRegion region && region.OwnerViewId == _viewId;
      public bool AllowReference(Reference reference, XYZ position) => true;
    }
  }

  internal static class FilledRegionBoundaryUtility
  {
    public static List<IList<CurveLoop>> Split(IList<CurveLoop> loops, RevitView view)
    {
      var infos = loops.Select((loop, index) => new LoopInfo(loop, index, view)).ToList();
      foreach (LoopInfo loop in infos)
      {
        loop.Depth = infos.Count(other => other.Index != loop.Index && other.Area > loop.Area && PointInPolygon(loop.Point, other.Points));
      }

      List<LoopInfo> filledLoops = infos.Where(loop => loop.Depth % 2 == 0).OrderBy(loop => loop.Area).ToList();
      List<LoopInfo> holes = infos.Where(loop => loop.Depth % 2 == 1).ToList();
      var result = new List<IList<CurveLoop>>();
      foreach (LoopInfo outer in filledLoops)
      {
        var group = new List<CurveLoop> { outer.Loop };
        foreach (LoopInfo hole in holes)
        {
          LoopInfo? parent = filledLoops
            .Where(candidate => candidate.Area > hole.Area && PointInPolygon(hole.Point, candidate.Points))
            .OrderBy(candidate => candidate.Area)
            .FirstOrDefault();
          if (parent?.Index == outer.Index)
          {
            group.Add(hole.Loop);
          }
        }
        result.Add(group);
      }
      return result;
    }

    private static IList<UV> ToPolygon(CurveLoop loop, RevitView view)
    {
      var points = new List<UV>();
      foreach (Curve curve in loop)
      {
        foreach (XYZ xyz in curve.Tessellate())
        {
          XYZ offset = xyz - view.Origin;
          var point = new UV(offset.DotProduct(view.RightDirection), offset.DotProduct(view.UpDirection));
          if (points.Count == 0 || !AlmostEqual(points[points.Count - 1], point))
          {
            points.Add(point);
          }
        }
      }
      if (points.Count > 1 && AlmostEqual(points[0], points[points.Count - 1]))
      {
        points.RemoveAt(points.Count - 1);
      }
      return points;
    }

    private static bool PointInPolygon(UV point, IList<UV> polygon)
    {
      bool inside = false;
      for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
      {
        UV a = polygon[i];
        UV b = polygon[j];
        if (((a.V > point.V) != (b.V > point.V)) && point.U < (b.U - a.U) * (point.V - a.V) / (b.V - a.V) + a.U)
        {
          inside = !inside;
        }
      }
      return inside;
    }

    private static double SignedArea(IList<UV> polygon)
    {
      double area = 0;
      for (int i = 0; i < polygon.Count; i++)
      {
        UV first = polygon[i];
        UV second = polygon[(i + 1) % polygon.Count];
        area += first.U * second.V - second.U * first.V;
      }
      return area * 0.5;
    }

    private static UV FindInteriorPoint(IList<UV> polygon)
    {
      var average = new UV(polygon.Average(point => point.U), polygon.Average(point => point.V));
      if (PointInPolygon(average, polygon))
      {
        return average;
      }
      double minU = polygon.Min(point => point.U);
      double maxU = polygon.Max(point => point.U);
      double minV = polygon.Min(point => point.V);
      double maxV = polygon.Max(point => point.V);
      for (int y = 1; y < 20; y++)
      {
        for (int x = 1; x < 20; x++)
        {
          var candidate = new UV(minU + (maxU - minU) * x / 20d, minV + (maxV - minV) * y / 20d);
          if (PointInPolygon(candidate, polygon))
          {
            return candidate;
          }
        }
      }
      return polygon[0];
    }

    private static bool AlmostEqual(UV first, UV second) =>
      Math.Abs(first.U - second.U) < 1e-7 && Math.Abs(first.V - second.V) < 1e-7;

    private sealed class LoopInfo
    {
      public LoopInfo(CurveLoop loop, int index, RevitView view)
      {
        Loop = loop;
        Index = index;
        Points = ToPolygon(loop, view);
        Point = FindInteriorPoint(Points);
        Area = Math.Abs(SignedArea(Points));
      }

      public CurveLoop Loop { get; }
      public int Index { get; }
      public IList<UV> Points { get; }
      public UV Point { get; }
      public double Area { get; }
      public int Depth { get; set; }
    }
  }
}

using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NMKRevit.FloorTool.Services
{
  internal sealed class FloorProfileAnalysis
  {
    public List<IList<CurveLoop>> Profiles { get; } = new();
    public int HoleCount { get; set; }
  }

  internal static class GeometryUtilities
  {
    private const double PointTolerance = 1e-7;

    public static FloorProfileAnalysis? AnalyzeFlatTop(Floor floor, double profileElevation)
    {
      try
      {
        var options = new Options
        {
          ComputeReferences = false,
          DetailLevel = ViewDetailLevel.Fine,
          IncludeNonVisibleObjects = false
        };
        var loops = new List<LoopInfo>();
        double? topElevation = null;

        foreach (Solid solid in GetSolids(floor.get_Geometry(options)))
        {
          if (solid.Volume <= 1e-6)
          {
            continue;
          }

          foreach (Face rawFace in solid.Faces)
          {
            if (rawFace is not PlanarFace face)
            {
              if (HasUpwardSlope(rawFace))
              {
                return null;
              }
              continue;
            }

            XYZ normal = face.FaceNormal.Normalize();
            if (normal.Z > 0.01 && normal.Z < 0.999)
            {
              return null;
            }
            if (normal.Z < 0.999 || face.Area <= 1e-6)
            {
              continue;
            }
            if (topElevation.HasValue && Math.Abs(face.Origin.Z - topElevation.Value) > 1e-4)
            {
              return null;
            }

            topElevation = face.Origin.Z;
            foreach (EdgeArray edges in face.EdgeLoops)
            {
              IList<UV> polygon = ToPolygon(edges);
              CurveLoop? curveLoop = ToCurveLoop(edges, profileElevation);
              double area = Math.Abs(SignedArea(polygon));
              if (polygon.Count >= 3 && area > 1e-6 && curveLoop != null)
              {
                loops.Add(new LoopInfo(loops.Count, curveLoop, polygon, area));
              }
            }
          }
        }

        if (loops.Count == 0)
        {
          return null;
        }

        foreach (LoopInfo loop in loops)
        {
          loop.Depth = loops.Count(other => other.Index != loop.Index && other.Area > loop.Area && PointInPolygon(loop.Point, other.Points));
        }

        List<LoopInfo> outers = loops.Where(loop => loop.Depth % 2 == 0).OrderBy(loop => loop.Area).ToList();
        List<LoopInfo> holes = loops.Where(loop => loop.Depth % 2 == 1).ToList();
        var result = new FloorProfileAnalysis { HoleCount = holes.Count };

        foreach (LoopInfo outer in outers)
        {
          var profile = new List<CurveLoop> { outer.Loop };
          foreach (LoopInfo hole in holes)
          {
            LoopInfo? parent = outers
              .Where(candidate => candidate.Area > hole.Area && PointInPolygon(hole.Point, candidate.Points))
              .OrderBy(candidate => candidate.Area)
              .FirstOrDefault();
            if (parent?.Index == outer.Index)
            {
              profile.Add(hole.Loop);
            }
          }
          result.Profiles.Add(profile);
        }

        return result;
      }
      catch
      {
        return null;
      }
    }

    public static IList<CurveLoop> NormalizeProfile(IList<CurveLoop> profile)
    {
      var result = new List<CurveLoop>();
      for (int i = 0; i < profile.Count; i++)
      {
        CurveLoop loop = CloneLoop(profile[i]);
        bool counterclockwise = loop.IsCounterclockwise(XYZ.BasisZ);
        if ((i == 0 && !counterclockwise) || (i > 0 && counterclockwise))
        {
          loop.Flip();
        }
        result.Add(loop);
      }
      return result;
    }

    public static CurveLoop CloneLoop(CurveLoop source)
    {
      var clone = new CurveLoop();
      foreach (Curve curve in source)
      {
        clone.Append(curve.Clone());
      }
      return clone;
    }

    public static CurveArray ToCurveArray(CurveLoop source)
    {
      var result = new CurveArray();
      foreach (Curve curve in source)
      {
        result.Append(curve.Clone());
      }
      return result;
    }

    public static IList<CurveLoop>? BuildCleanPolylineProfile(IList<CurveLoop> profile)
    {
      var result = new List<CurveLoop>();
      foreach (CurveLoop sourceLoop in profile)
      {
        var points = new List<XYZ>();
        foreach (Curve curve in sourceLoop)
        {
          foreach (XYZ point in curve.Tessellate())
          {
            if (points.Count == 0 || points[points.Count - 1].DistanceTo(point) > PointTolerance)
            {
              points.Add(point);
            }
          }
        }
        if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= PointTolerance)
        {
          points.RemoveAt(points.Count - 1);
        }

        RemoveShortAndCollinearPoints(points);
        if (points.Count < 3)
        {
          return null;
        }
        var cleanLoop = new CurveLoop();
        for (int i = 0; i < points.Count; i++)
        {
          XYZ start = points[i];
          XYZ end = points[(i + 1) % points.Count];
          if (start.DistanceTo(end) <= 1d / 304.8d)
          {
            return null;
          }
          cleanLoop.Append(Line.CreateBound(start, end));
        }
        result.Add(cleanLoop);
      }
      return NormalizeProfile(result);
    }

    public static void CopyWritableParameters(Element source, Element target)
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
          // Revit exposes some system parameters as writable although they reject Set.
        }
      }
    }

    private static IEnumerable<Solid> GetSolids(GeometryElement? geometry)
    {
      if (geometry == null)
      {
        yield break;
      }
      foreach (GeometryObject geometryObject in geometry)
      {
        if (geometryObject is Solid solid)
        {
          yield return solid;
        }
        else if (geometryObject is GeometryInstance instance)
        {
          foreach (Solid nested in GetSolids(instance.GetInstanceGeometry()))
          {
            yield return nested;
          }
        }
      }
    }

    private static void RemoveShortAndCollinearPoints(List<XYZ> points)
    {
      bool changed = true;
      while (changed && points.Count >= 3)
      {
        changed = false;
        for (int i = 0; i < points.Count; i++)
        {
          XYZ previous = points[(i + points.Count - 1) % points.Count];
          XYZ current = points[i];
          XYZ next = points[(i + 1) % points.Count];
          XYZ first = current - previous;
          XYZ second = next - current;
          double lengthProduct = first.GetLength() * second.GetLength();
          bool collinear = lengthProduct < 1e-12 || first.CrossProduct(second).GetLength() / lengthProduct < 1e-6;
          if (previous.DistanceTo(current) <= 1d / 304.8d || current.DistanceTo(next) <= 1d / 304.8d || collinear)
          {
            points.RemoveAt(i);
            changed = true;
            break;
          }
        }
      }
    }

    private static bool HasUpwardSlope(Face face)
    {
      Mesh mesh = face.Triangulate();
      for (int i = 0; i < mesh.NumTriangles; i++)
      {
        MeshTriangle triangle = mesh.get_Triangle(i);
        XYZ normal = (triangle.get_Vertex(1) - triangle.get_Vertex(0))
          .CrossProduct(triangle.get_Vertex(2) - triangle.get_Vertex(0)).Normalize();
        if (normal.Z > 0.01 && normal.Z < 0.999)
        {
          return true;
        }
      }
      return false;
    }

    private static CurveLoop? ToCurveLoop(EdgeArray edges, double elevation)
    {
      var curves = new List<Curve>();
      foreach (Edge edge in edges)
      {
        Curve curve = edge.AsCurve();
        var translation = Transform.CreateTranslation(new XYZ(0, 0, elevation - curve.GetEndPoint(0).Z));
        curves.Add(curve.CreateTransformed(translation));
      }
      return BuildContinuousLoop(curves);
    }

    private static CurveLoop? BuildContinuousLoop(List<Curve> curves)
    {
      if (curves.Count == 0)
      {
        return null;
      }
      var remaining = new List<Curve>(curves);
      var ordered = new List<Curve> { remaining[0] };
      remaining.RemoveAt(0);

      while (remaining.Count > 0)
      {
        XYZ end = ordered[ordered.Count - 1].GetEndPoint(1);
        int bestIndex = -1;
        bool reverse = false;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < remaining.Count; i++)
        {
          double startDistance = end.DistanceTo(remaining[i].GetEndPoint(0));
          double endDistance = end.DistanceTo(remaining[i].GetEndPoint(1));
          if (startDistance < bestDistance)
          {
            bestDistance = startDistance;
            bestIndex = i;
            reverse = false;
          }
          if (endDistance < bestDistance)
          {
            bestDistance = endDistance;
            bestIndex = i;
            reverse = true;
          }
        }
        if (bestIndex < 0 || bestDistance > 1e-6)
        {
          return null;
        }
        Curve next = remaining[bestIndex];
        ordered.Add(reverse ? next.CreateReversed() : next);
        remaining.RemoveAt(bestIndex);
      }

      if (ordered[ordered.Count - 1].GetEndPoint(1).DistanceTo(ordered[0].GetEndPoint(0)) > 1e-6)
      {
        return null;
      }
      var loop = new CurveLoop();
      ordered.ForEach(loop.Append);
      return loop;
    }

    private static IList<UV> ToPolygon(EdgeArray edges)
    {
      var points = new List<UV>();
      foreach (Edge edge in edges)
      {
        foreach (XYZ xyz in edge.AsCurve().Tessellate())
        {
          var point = new UV(xyz.X, xyz.Y);
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
        UV a = polygon[i];
        UV b = polygon[(i + 1) % polygon.Count];
        area += a.U * b.V - b.U * a.V;
      }
      return area * 0.5;
    }

    private static UV InteriorPoint(IList<UV> polygon)
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
      Math.Abs(first.U - second.U) < PointTolerance && Math.Abs(first.V - second.V) < PointTolerance;

    private sealed class LoopInfo
    {
      public LoopInfo(int index, CurveLoop loop, IList<UV> points, double area)
      {
        Index = index;
        Loop = loop;
        Points = points;
        Area = area;
        Point = InteriorPoint(points);
      }

      public int Index { get; }
      public CurveLoop Loop { get; }
      public IList<UV> Points { get; }
      public double Area { get; }
      public UV Point { get; }
      public int Depth { get; set; }
    }
  }
}

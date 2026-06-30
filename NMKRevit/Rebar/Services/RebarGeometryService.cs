using Autodesk.Revit.DB;
using NMKRevit.Rebar.Models;
using System;
using System.Collections.Generic;

namespace NMKRevit.Rebar.Services
{
  public static class RebarGeometryService
  {
    private const double Tolerance = 1e-8;

    public static IList<Curve> BuildCurves(XYZ basePoint, XYZ barDirection, XYZ inward, AiRebarShapeConfig shape)
    {
      var curves = new List<Curve>();
      for (int i = 1; i < shape.Points.Count; i++)
      {
        XYZ start = MapPoint(basePoint, barDirection, inward, shape.Points[i - 1]);
        XYZ end = MapPoint(basePoint, barDirection, inward, shape.Points[i]);
        if (start.DistanceTo(end) <= Tolerance)
        {
          throw new InvalidOperationException("Shape co segment chieu dai bang 0.");
        }
        curves.Add(Line.CreateBound(start, end));
      }
      return curves;
    }

    public static void ValidateContinuous(IList<Curve> curves, string owner)
    {
      for (int i = 1; i < curves.Count; i++)
      {
        if (!curves[i - 1].GetEndPoint(1).IsAlmostEqualTo(curves[i].GetEndPoint(0)))
        {
          throw new InvalidOperationException($"{owner}: shape khong lien tuc.");
        }
      }
    }

    public static XYZ ProjectToPlane(XYZ vector, XYZ normal)
    {
      return vector - normal.Multiply(vector.DotProduct(normal));
    }

    public static XYZ ProjectPointToFacePlane(XYZ point, XYZ planeOrigin, XYZ normal)
    {
      double distance = (point - planeOrigin).DotProduct(normal);
      return point - normal.Multiply(distance);
    }

    public static double Mm(double value)
    {
      return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
    }

    private static XYZ MapPoint(XYZ basePoint, XYZ barDirection, XYZ inward, AiRebarPointConfig point)
    {
      return basePoint
        + barDirection.Multiply(Mm(point.XMm))
        + inward.Multiply(Mm(point.ZMm));
    }
  }
}

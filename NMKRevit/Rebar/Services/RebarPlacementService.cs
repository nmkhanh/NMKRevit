using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using NMKRevit.Rebar.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace NMKRevit.Rebar.Services
{
  public sealed class RebarPlacementService
  {
    private const double Tolerance = 1e-8;
    private readonly AiRebarJsonService _jsonService = new();

    public RebarPlacementResult Place(UIApplication uiapp, RebarPlacementOptions options)
    {
      UIDocument uiDocument = uiapp.ActiveUIDocument;
      Document document = uiDocument.Document;
      AiRebarConfig config = _jsonService.Load(options.JsonPath);

      Reference faceReference = uiDocument.Selection.PickObject(
        ObjectType.Face,
        "Pick face dung lam mat offset thep");
      Element host = document.GetElement(faceReference.ElementId);
      if (RebarHostData.GetRebarHostData(host) == null)
      {
        throw new InvalidOperationException("Element duoc chon khong host duoc rebar.");
      }

      Face face = host.GetGeometryObjectFromReference(faceReference) as Face
        ?? throw new InvalidOperationException("Khong doc duoc face da pick.");
      XYZ pickPoint = faceReference.GlobalPoint ?? face.Evaluate(new UV(0.5, 0.5));
      XYZ faceOrigin = face.Project(pickPoint)?.XYZPoint ?? pickPoint;
      XYZ faceNormal = face.ComputeNormal(face.Project(faceOrigin)?.UVPoint ?? new UV(0.5, 0.5)).Normalize();
      XYZ inward = faceNormal.Negate();

      XYZ point1 = uiDocument.Selection.PickPoint("Pick diem 1 de chon phuong rai");
      XYZ point2 = uiDocument.Selection.PickPoint("Pick diem 2 de chon phuong rai");
      XYZ distributionDirection = RebarGeometryService.ProjectToPlane(point2 - point1, faceNormal).Normalize();
      if (distributionDirection.GetLength() <= Tolerance)
      {
        throw new InvalidOperationException("Hai diem pick khong tao duoc phuong rai hop le tren mat da chon.");
      }

      XYZ barDirection = inward.CrossProduct(distributionDirection).Normalize();
      XYZ planeNormal = distributionDirection;
      return PlaceBars(document, host, point1, point2, faceOrigin, faceNormal, inward, distributionDirection, barDirection, planeNormal, config, options);
    }

    private static RebarPlacementResult PlaceBars(
      Document document,
      Element host,
      XYZ point1,
      XYZ point2,
      XYZ faceOrigin,
      XYZ faceNormal,
      XYZ inward,
      XYZ distributionDirection,
      XYZ barDirection,
      XYZ planeNormal,
      AiRebarConfig config,
      RebarPlacementOptions options)
    {
      var result = new RebarPlacementResult();
      using var transaction = new Transaction(document, "AI Rebar from JSON");
      transaction.Start();

      int enabledPieces = config.Bars.Count(bar => bar.Enabled);
      int pieceIndex = 0;
      foreach (AiRebarBarConfig bar in config.Bars.Where(bar => bar.Enabled))
      {
        string side = ResolveSide(bar.Side, pieceIndex, enabledPieces);
        IReadOnlyList<double> positions = ResolvePositions(bar.Distribution ?? config.Distribution);
        if (positions.Count == 0)
        {
          throw new InvalidOperationException($"{bar.Id}: distribution khong co vi tri rai.");
        }

        RebarBarType barType = ResolveBarType(document, options.TypeNamePrefix, bar.TypeName, bar.Id);
        int localCount = 0;
        for (int i = 0; i < positions.Count; i++)
        {
          XYZ distributionOrigin = side.Equals("right", StringComparison.OrdinalIgnoreCase)
            ? point2 - distributionDirection.Multiply(RebarGeometryService.Mm(options.RightOffsetMm + positions[i]))
            : point1 + distributionDirection.Multiply(RebarGeometryService.Mm(options.LeftOffsetMm + positions[i]));
          XYZ onFace = RebarGeometryService.ProjectPointToFacePlane(distributionOrigin, faceOrigin, faceNormal);
          XYZ basePoint = onFace + inward.Multiply(RebarGeometryService.Mm(options.FaceOffsetMm));

          bool rotate180 = options.AlternateRotate180 && i % 2 == 1;
          IList<Curve> curves = RebarGeometryService.BuildCurves(basePoint, rotate180 ? barDirection.Negate() : barDirection, inward, bar.Shape);
          RebarGeometryService.ValidateContinuous(curves, bar.Id);
          CreateRebar(document, host, barType, planeNormal, curves, bar.Id);
          result.CreatedCount++;
          localCount++;
        }

        result.CountsByBarId[bar.Id] = localCount;
        pieceIndex++;
      }

      transaction.Commit();
      return result;
    }

    private static string ResolveSide(string? side, int index, int enabledPieces)
    {
      if (!string.IsNullOrWhiteSpace(side) && !side.Equals("auto", StringComparison.OrdinalIgnoreCase))
      {
        string normalized = side.Trim().ToLowerInvariant();
        if (normalized is "left" or "right")
        {
          return normalized;
        }
        throw new InvalidOperationException($"side khong hop le: {side}. Dung left, right hoac auto.");
      }
      if (enabledPieces <= 1)
      {
        return "left";
      }
      return index % 2 == 0 ? "left" : "right";
    }

    private static IReadOnlyList<double> ResolvePositions(AiRebarDistributionConfig distribution)
    {
      if (distribution.OffsetsMm?.Count > 0)
      {
        return distribution.OffsetsMm;
      }

      var positions = new List<double> { 0 };
      if (distribution.SpacingsMm?.Count > 0)
      {
        double current = 0;
        foreach (double spacing in distribution.SpacingsMm)
        {
          if (spacing <= 0)
          {
            throw new InvalidOperationException("spacingsMm chi duoc chua so duong.");
          }
          current += spacing;
          positions.Add(current);
        }
        return positions;
      }

      if (distribution.Count.HasValue && distribution.Count.Value > 0 && distribution.SpacingMm.HasValue)
      {
        if (distribution.SpacingMm.Value <= 0)
        {
          throw new InvalidOperationException("spacingMm phai > 0.");
        }
        return Enumerable.Range(0, distribution.Count.Value)
          .Select(index => index * distribution.SpacingMm.Value)
          .ToList();
      }

      throw new InvalidOperationException("distribution can offsetsMm, spacingsMm hoac count + spacingMm.");
    }

    private static RebarBarType ResolveBarType(Document document, string? prefix, string? requestedName, string id)
    {
      if (string.IsNullOrWhiteSpace(requestedName))
      {
        throw new InvalidOperationException($"{id}: JSON phai co typeName vi command chi chon type co san.");
      }
      string typeName = (prefix ?? string.Empty) + requestedName.Trim();
      RebarBarType? existing = new FilteredElementCollector(document)
        .OfClass(typeof(RebarBarType))
        .Cast<RebarBarType>()
        .FirstOrDefault(type => type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
      if (existing != null)
      {
        return existing;
      }

      throw new InvalidOperationException($"Khong tim thay RebarBarType co san '{typeName}' cho {id}.");
    }

    private static RevitRebar CreateRebar(Document document, Element host, RebarBarType barType, XYZ planeNormal, IList<Curve> curves, string context)
    {
#if D2026 || D2027
      using var terminations = new BarTerminationsData(document);
      RevitRebar rebar = RevitRebar.CreateFromCurves(document, RebarStyle.Standard, barType, host, planeNormal, curves, terminations, true, true)
        ?? throw new InvalidOperationException($"{context}: Revit khong tao duoc rebar.");
#else
      RevitRebar rebar = RevitRebar.CreateFromCurves(
        document,
        RebarStyle.Standard,
        barType,
        null,
        null,
        host,
        planeNormal,
        curves,
        RebarHookOrientation.Right,
        RebarHookOrientation.Right,
        true,
        true)
        ?? throw new InvalidOperationException($"{context}: Revit khong tao duoc rebar.");
#endif
      TryShowUnobscured(document, rebar);
      return rebar;
    }

    private static void TryShowUnobscured(Document document, RevitRebar rebar)
    {
      try
      {
        rebar.SetUnobscuredInView(document.ActiveView, true);
      }
      catch (Autodesk.Revit.Exceptions.ArgumentException)
      {
      }
      catch (Autodesk.Revit.Exceptions.InvalidOperationException)
      {
      }
    }
  }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using JsonOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;
using RevitTaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace NMKRevit.Rebar
{
  [Transaction(TransactionMode.Manual)]
  public sealed class RebarFoundation : IExternalCommand
  {
    private const double Tolerance = 1e-8;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      UIDocument uiDocument = commandData.Application.ActiveUIDocument;
      Document document = uiDocument.Document;

      try
      {
        string? jsonPath = SelectJsonFile();
        if (jsonPath == null) return Result.Cancelled;
        FoundationRebarConfig config = LoadConfig(jsonPath);

        Reference picked = uiDocument.Selection.PickObject(
          ObjectType.PointOnElement,
          new FamilyInstanceFilter(),
          "Click a face of a rectangular pile-cap FamilyInstance solid");
        Element host = document.GetElement(picked.ElementId);
        Solid? solid = FindPickedSolid(host, picked.GlobalPoint);
        if (solid == null) throw new InvalidOperationException("Could not resolve the solid that was clicked.");

        RectangularSolid box = RectangularSolid.Create(solid);
        if (!box.IsRectangular)
          throw new InvalidOperationException("The selected solid is not sufficiently close to a rectangular box.");
        if (RebarHostData.GetRebarHostData(host) == null)
          throw new InvalidOperationException("The selected FamilyInstance cannot host rebar. Use a reinforcement-compatible structural category and enable reinforcement.");

        string summary = config.SchemaVersion == 2
          ? CreateFromVersion2(document, host, box, config)
          : CreateFromVersion1(document, host, box, config);

        RevitTaskDialog.Show(
          "RebarFoundation",
          $"JSON: {Path.GetFileName(jsonPath)}\n" + summary);
        return Result.Succeeded;
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        return Result.Cancelled;
      }
      catch (Exception exception)
      {
        message = exception.ToString();
        RevitTaskDialog.Show("RebarFoundation", exception.Message);
        return Result.Failed;
      }
    }

    private static string CreateFromVersion1(Document document, Element host, RectangularSolid box, FoundationRebarConfig config)
    {
      Dictionary<string, LayerConfig> layers = GetLayers(config);
      int layerBars = 0;
      int hairpinBars = 0;
      var layouts = new Dictionary<string, LayerLayout>(StringComparer.OrdinalIgnoreCase);
      var usedTypes = new List<string>();
      var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

      using (var transaction = new Transaction(document, "Rebar foundation from JSON"))
      {
        transaction.Start();
        var layerTypes = new Dictionary<string, RebarBarType>(StringComparer.OrdinalIgnoreCase);
        foreach (LayerConfig layer in config.Layers)
        {
          layerTypes[layer.Id] = ResolveBarType(document, layer.DiameterMm, layer.TypeName, layer.Id);
        }

        Dictionary<string, double> elevations = ResolveLayerElevations(box, layers, layerTypes);
        ValidateLayerBounds(box, layerTypes, elevations);
        foreach (LayerConfig layer in config.Layers)
        {
          string id = layer.Id;
          RebarBarType barType = layerTypes[id];
          LayerLayout layout = CreateLayer(document, host, box, layer, barType, elevations[id], config.SideCenterCoverMm, elevations);
          layouts[id] = layout;
          layerBars += layout.Created;
          layerCounts[id] = layout.Created;
          usedTypes.Add(barType.Name);
        }

        if (config.HookBars?.Enabled == true)
        {
          HookBarConfig hook = config.HookBars;
          RebarBarType hookType = ResolveBarType(document, hook.DiameterMm, hook.TypeName, "Hook");
          LongLegEndConfig longLegEnd = hook.LongLegEnd ?? throw new InvalidOperationException("hookBars.longLegEnd is required.");
          string wrapLayerId = ResolveLayerIdAlias(hook.WrapLayerId, config.Layers, true);
          string endLayerId = ResolveLayerIdAlias(longLegEnd.LayerId, config.Layers, false);
          if (!layouts.TryGetValue(wrapLayerId, out LayerLayout? wrapLayout))
            throw new InvalidOperationException($"hookBars.wrapLayerId '{hook.WrapLayerId}' does not match any layer id.");
          if (!layouts.TryGetValue(endLayerId, out LayerLayout? endLayout))
            throw new InvalidOperationException($"hookBars.longLegEnd.layerId '{longLegEnd.LayerId}' does not match any layer id.");
          hairpinBars = CreateHairpins(document, host, box, hook, hookType, wrapLayout, endLayout);
          usedTypes.Add(hookType.Name);
        }
        transaction.Commit();
      }

      string counts = string.Join(", ", config.Layers.Select(layer =>
      {
        layerCounts.TryGetValue(layer.Id, out int created);
        return $"{layer.Id}: {created}";
      }));
      return
        $"Schema: v1\n" +
        $"Layer bars: {layerBars} ({counts})\n" +
        $"Hairpin bars: {hairpinBars}\n" +
        $"Types: {string.Join(", ", usedTypes.Distinct())}";
    }

    private static string CreateFromVersion2(Document document, Element host, RectangularSolid box, FoundationRebarConfig config)
    {
      var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var usedTypes = new List<string>();
      var warnings = new List<string>();
      int total = 0;

      using (var transaction = new Transaction(document, "Rebar foundation v2 from JSON"))
      {
        transaction.Start();
        foreach (RebarPieceConfig piece in config.BarPieces.Where(piece => piece.Enabled))
        {
          RebarBarType barType = ResolveBarType(document, piece.DiameterMm, piece.TypeName, piece.SourceTypeName, piece.Id);
          int created = CreateBarPiece(document, host, box, piece, barType, config.SideCenterCoverMm, warnings);
          counts[piece.Id] = created;
          total += created;
          usedTypes.Add(barType.Name);
          if (piece.Quantity > 0 && piece.Quantity != created)
            warnings.Add($"{piece.Id}: schedule quantity {piece.Quantity} differs from created count {created}.");
        }
        transaction.Commit();
      }

      string countText = string.Join(", ", config.BarPieces.Where(piece => piece.Enabled).Select(piece =>
      {
        counts.TryGetValue(piece.Id, out int created);
        return $"{piece.Id}: {created}";
      }));
      string warningText = warnings.Count == 0
        ? string.Empty
        : "\nWarnings: " + string.Join("; ", warnings.Take(5)) + (warnings.Count > 5 ? $" (+{warnings.Count - 5} more)" : string.Empty);
      return
        $"Schema: v2\n" +
        $"Bar pieces: {total} ({countText})\n" +
        $"Types: {string.Join(", ", usedTypes.Distinct())}" +
        warningText;
    }

    private static int CreateBarPiece(
      Document document,
      Element host,
      RectangularSolid box,
      RebarPieceConfig piece,
      RebarBarType barType,
      double defaultSideCoverMm,
      List<string> warnings)
    {
      ShapeConfig shape = piece.Shape ?? throw new InvalidOperationException($"barPieces.{piece.Id}: shape is required.");
      int count = 0;
      foreach (PiecePlacementConfig placement in piece.Placements)
      {
        bool alongX = placement.Direction.Equals("X", StringComparison.OrdinalIgnoreCase);
        string kind = shape.Kind ?? string.Empty;
        if (kind.Equals("hairpin", StringComparison.OrdinalIgnoreCase))
        {
          count += CreateHairpinPiece(document, host, box, piece, barType, placement, shape, warnings);
          continue;
        }

        double z = ResolvePlacementZ(box, placement.Z);
        IList<double> positions = BuildPlacementPositions(
          alongX ? box.MinY : box.MinX,
          alongX ? box.MaxY : box.MaxX,
          placement.Distribution);
        foreach (double position in positions)
        {
          IList<Curve> curves;
          BarTerminationSpec terminations = BarTerminationSpec.None;
          XYZ planeNormal = alongX ? box.YAxis : box.XAxis;

          if (kind.Equals("straight", StringComparison.OrdinalIgnoreCase))
          {
            curves = BuildStraightPieceCurves(box, alongX, position, z, placement, shape, defaultSideCoverMm);
          }
          else if (kind.Equals("straightWithNativeHooks", StringComparison.OrdinalIgnoreCase))
          {
            double angle = shape.HookAngleDegrees <= 0 ? 90 : shape.HookAngleDegrees;
            if (Math.Abs(angle - 180) <= 0.5 && TryBuildNativeHookSpec(document, piece, barType, shape, warnings, out terminations))
              curves = BuildStraightPieceCurves(box, alongX, position, z, placement, shape, defaultSideCoverMm);
            else
            {
              if (Math.Abs(angle - 180) > 0.5)
                warnings.Add($"{piece.Id}: hook angle {angle:0.###} uses explicit line legs; only 180-degree hooks use RebarHookType.");
              curves = BuildStraightWithExplicitLegs(box, alongX, position, z, placement, shape, defaultSideCoverMm);
            }
          }
          else if (kind.Equals("uBar", StringComparison.OrdinalIgnoreCase))
          {
            curves = BuildStraightWithExplicitLegs(box, alongX, position, z, placement, shape, defaultSideCoverMm);
          }
          else if (kind.Equals("piecewiseLine", StringComparison.OrdinalIgnoreCase))
          {
            curves = BuildPiecewiseLineCurves(box, alongX, position, shape);
          }
          else
          {
            throw new InvalidOperationException($"barPieces.{piece.Id}: unsupported shape.kind '{kind}'.");
          }

          ValidateCurvesInsideBox(curves, box, $"barPieces.{piece.Id}");
          CreateRebar(document, host, barType, planeNormal, curves, terminations, $"barPieces.{piece.Id}");
          count++;
        }
      }
      return count;
    }

    private static int CreateHairpinPiece(
      Document document,
      Element host,
      RectangularSolid box,
      RebarPieceConfig piece,
      RebarBarType barType,
      PiecePlacementConfig placement,
      ShapeConfig shape,
      List<string> warnings)
    {
      bool alongX = placement.Direction.Equals("X", StringComparison.OrdinalIgnoreCase);
      IList<double> longitudinalPositions = BuildPlacementPositions(alongX ? box.MinX : box.MinY, alongX ? box.MaxX : box.MaxY, placement.Distribution);
      IList<double> rowPositions = BuildPlacementPositions(alongX ? box.MinY : box.MinX, alongX ? box.MaxY : box.MaxX, placement.SecondaryDistribution);
      double bottomZ = ResolvePlacementZ(box, placement.Z);
      int count = 0;

      foreach (double longitudinal in longitudinalPositions)
      {
        foreach (double row in rowPositions)
        {
          IList<Curve> curves = BuildHairpinPieceCurves(box, alongX, longitudinal, row, bottomZ, shape);
          ValidateCurvesInsideBox(curves, box, $"barPieces.{piece.Id}");
          CreateRebar(document, host, barType, alongX ? box.XAxis : box.YAxis, curves, BarTerminationSpec.None, $"barPieces.{piece.Id}");
          count++;
        }
      }

      if (shape.BendRadiusMm.HasValue)
        warnings.Add($"{piece.Id}: bend radius {shape.BendRadiusMm.Value:0.###} mm is stored as metadata; Revit controls the physical bend radius from the bar type.");
      return count;
    }

    private static IList<double> BuildPlacementPositions(double min, double max, DistributionConfig? distribution)
    {
      return distribution == null ? new List<double> { (min + max) * 0.5 } : BuildPositions(min, max, distribution);
    }

    private static double ResolvePlacementZ(RectangularSolid box, PlacementZConfig? z)
    {
      z ??= new PlacementZConfig();
      string reference = z.Reference ?? "bottomFace";
      if (reference.Equals("topFace", StringComparison.OrdinalIgnoreCase))
        return box.MaxZ - Mm(z.OffsetMm);
      if (reference.Equals("bottomFace", StringComparison.OrdinalIgnoreCase))
        return box.MinZ + Mm(z.OffsetMm);
      if (reference.Equals("absoluteFromBottom", StringComparison.OrdinalIgnoreCase))
        return box.MinZ + Mm(z.OffsetMm);
      if (reference.Equals("absoluteFromTop", StringComparison.OrdinalIgnoreCase))
        return box.MaxZ - Mm(z.OffsetMm);
      throw new InvalidOperationException($"Unsupported placement z.reference '{reference}'.");
    }

    private static IList<Curve> BuildStraightPieceCurves(
      RectangularSolid box,
      bool alongX,
      double position,
      double z,
      PiecePlacementConfig placement,
      ShapeConfig shape,
      double defaultSideCoverMm)
    {
      ResolveLineRange(box, alongX, placement, shape, defaultSideCoverMm, out double u0, out double u1);
      return new List<Curve>
      {
        alongX
          ? Line.CreateBound(box.ToWorld(u0, position, z), box.ToWorld(u1, position, z))
          : Line.CreateBound(box.ToWorld(position, u0, z), box.ToWorld(position, u1, z))
      };
    }

    private static IList<Curve> BuildStraightWithExplicitLegs(
      RectangularSolid box,
      bool alongX,
      double position,
      double z,
      PiecePlacementConfig placement,
      ShapeConfig shape,
      double defaultSideCoverMm)
    {
      ResolveLineRange(box, alongX, placement, shape, defaultSideCoverMm, out double u0, out double u1);
      double startLeg = Mm(shape.HookStartMm ?? shape.LegLengthMm ?? 0);
      double endLeg = Mm(shape.HookEndMm ?? shape.LegLengthMm ?? 0);
      if (startLeg <= Tolerance && endLeg <= Tolerance)
        throw new InvalidOperationException("Explicit hook fallback requires hookStartMm/hookEndMm or legLengthMm.");
      double sign = (shape.HookLegDirection ?? "down").Equals("up", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
      var points = new List<XYZ>();
      if (startLeg > Tolerance) points.Add(PointOnLayer(box, alongX, position, u0, z + sign * startLeg));
      points.Add(PointOnLayer(box, alongX, position, u0, z));
      points.Add(PointOnLayer(box, alongX, position, u1, z));
      if (endLeg > Tolerance) points.Add(PointOnLayer(box, alongX, position, u1, z + sign * endLeg));
      return ConsecutiveLines(points);
    }

    private static IList<Curve> BuildPiecewiseLineCurves(RectangularSolid box, bool alongX, double position, ShapeConfig shape)
    {
      return BuildSegments(shape, point =>
      {
        double u = (alongX ? box.MinX : box.MinY) + Mm(point.Umm);
        double z = box.MinZ + Mm(point.Zmm);
        return PointOnLayer(box, alongX, position, u, z);
      });
    }

    private static IList<Curve> BuildHairpinPieceCurves(
      RectangularSolid box,
      bool alongX,
      double longitudinal,
      double row,
      double bottomZ,
      ShapeConfig shape)
    {
      double halfWidth = Mm((shape.CrownWidthMm ?? 0) * 0.5);
      double longLeg = Mm(shape.LongLegMm ?? 0);
      double shortLeg = Mm(shape.ShortLegMm ?? 0);
      if (halfWidth <= Tolerance || longLeg <= Tolerance || shortLeg <= Tolerance)
        throw new InvalidOperationException("hairpin requires positive crownWidthMm, longLegMm, and shortLegMm.");
      double topZ = bottomZ + longLeg;
      double shortBottomZ = topZ - shortLeg;
      return new List<Curve>
      {
        Line.CreateBound(HairpinPoint(box, alongX, longitudinal, row, -halfWidth, bottomZ), HairpinPoint(box, alongX, longitudinal, row, -halfWidth, topZ)),
        Line.CreateBound(HairpinPoint(box, alongX, longitudinal, row, -halfWidth, topZ), HairpinPoint(box, alongX, longitudinal, row, halfWidth, topZ)),
        Line.CreateBound(HairpinPoint(box, alongX, longitudinal, row, halfWidth, topZ), HairpinPoint(box, alongX, longitudinal, row, halfWidth, shortBottomZ))
      };
    }

    private static IList<Curve> ConsecutiveLines(IList<XYZ> points)
    {
      if (points.Count < 2) throw new InvalidOperationException("At least two points are required to create rebar.");
      var curves = new List<Curve>();
      for (int index = 1; index < points.Count; index++)
        curves.Add(Line.CreateBound(points[index - 1], points[index]));
      return curves;
    }

    private static void ResolveLineRange(
      RectangularSolid box,
      bool alongX,
      PiecePlacementConfig placement,
      ShapeConfig shape,
      double defaultSideCoverMm,
      out double u0,
      out double u1)
    {
      double min = alongX ? box.MinX : box.MinY;
      double max = alongX ? box.MaxX : box.MaxY;
      bool hasStart = placement.LineStartOffsetMm.HasValue;
      bool hasEnd = placement.LineEndOffsetMm.HasValue;
      u0 = min + Mm(placement.LineStartOffsetMm ?? defaultSideCoverMm);
      u1 = max - Mm(placement.LineEndOffsetMm ?? defaultSideCoverMm);

      if (shape.StraightLengthMm.HasValue)
      {
        double length = Mm(shape.StraightLengthMm.Value);
        if (hasStart && !hasEnd)
          u1 = u0 + length;
        else if (!hasStart && hasEnd)
          u0 = u1 - length;
        else if (!hasStart && !hasEnd)
        {
          double center = (min + max) * 0.5;
          u0 = center - length * 0.5;
          u1 = center + length * 0.5;
        }
      }

      if (u1 <= u0)
        throw new InvalidOperationException("Bar line length is not positive.");
    }

    private static bool TryBuildNativeHookSpec(
      Document document,
      RebarPieceConfig piece,
      RebarBarType barType,
      ShapeConfig shape,
      List<string> warnings,
      out BarTerminationSpec specification)
    {
      specification = BarTerminationSpec.None;
      try
      {
        double angle = shape.HookAngleDegrees <= 0 ? 90 : shape.HookAngleDegrees;
        if (Math.Abs(angle - 180) > 0.5)
          throw new InvalidOperationException("Only 180-degree hooks are created with RebarHookType; other hooked shapes must be explicit line geometry.");
        RebarHookType? startHook = (shape.HookStartMm ?? shape.LegLengthMm ?? 0) > 0
          ? ResolveHookType(document, barType, shape.StartHookTypeName ?? shape.HookTypeName, angle)
          : null;
        RebarHookType? endHook = (shape.HookEndMm ?? shape.LegLengthMm ?? 0) > 0
          ? ResolveHookType(document, barType, shape.EndHookTypeName ?? shape.HookTypeName, angle)
          : null;
        if (startHook == null && endHook == null)
          throw new InvalidOperationException("No hook length was specified.");
        specification = new BarTerminationSpec(
          startHook,
          endHook,
          ParseHookOrientation(shape.StartHookOrientation, RebarTerminationOrientation.Right),
          ParseHookOrientation(shape.EndHookOrientation, RebarTerminationOrientation.Left));
        return true;
      }
      catch (Exception exception) when (shape.FallbackToLine)
      {
        warnings.Add($"{piece.Id}: native hook unavailable, used explicit line legs instead ({exception.Message})");
        return false;
      }
    }

    private static RebarTerminationOrientation ParseHookOrientation(string? value, RebarTerminationOrientation fallback)
    {
      if (string.IsNullOrWhiteSpace(value)) return fallback;
      return Enum.TryParse(value, true, out RebarTerminationOrientation result)
        ? result
        : throw new InvalidOperationException($"Unsupported hook orientation '{value}'. Use Left or Right.");
    }

    private static string? SelectJsonFile()
    {
      string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
      string defaultPath = Path.Combine(folder, "RebarFoundation.json");
      var dialog = new JsonOpenFileDialog
      {
        Title = "Select foundation reinforcement JSON",
        Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        InitialDirectory = folder,
        FileName = File.Exists(defaultPath) ? Path.GetFileName(defaultPath) : string.Empty,
        CheckFileExists = true
      };
      return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static FoundationRebarConfig LoadConfig(string path)
    {
      var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Ignore };
      FoundationRebarConfig? config = JsonConvert.DeserializeObject<FoundationRebarConfig>(File.ReadAllText(path), settings);
      if (config == null) throw new InvalidOperationException("The JSON file is empty.");
      if (config.SchemaVersion != 1 && config.SchemaVersion != 2) throw new InvalidOperationException("Only schemaVersion 1 and 2 are supported.");
      if (!config.Units.Equals("mm", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Only millimetres (units = mm) are supported.");
      if (!config.CoordinateSystem.Equals("selectedSolidLocalXYZ", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Only coordinateSystem selectedSolidLocalXYZ is supported.");
      if (config.SchemaVersion == 2)
      {
        ValidateVersion2Config(config);
        return config;
      }
      ValidateVersion1Config(config);
      return config;
    }

    private static void ValidateVersion1Config(FoundationRebarConfig config)
    {
      if (config.Layers == null || config.Layers.Count == 0)
        throw new InvalidOperationException("JSON must contain layers.");

      foreach (LayerConfig layer in config.Layers)
      {
        if (string.IsNullOrWhiteSpace(layer.Id)) throw new InvalidOperationException("Every layer requires id.");
        if (layer.DiameterMm <= 0) throw new InvalidOperationException($"Layer {layer.Id}: diameterMm must be positive.");
        if (!IsDirection(layer.Direction)) throw new InvalidOperationException($"Layer {layer.Id}: direction must be X or Y.");
        ValidateDistribution(layer.Distribution, $"Layer {layer.Id}");
        ValidateLayerShape(layer.Shape, $"Layer {layer.Id}");
      }

      if (config.HookBars?.Enabled == true)
      {
        if (config.HookBars.DiameterMm <= 0) throw new InvalidOperationException("hookBars.diameterMm must be positive.");
        ValidateDistribution(config.HookBars.LongitudinalDistribution, "hookBars");
        if (config.HookBars.RowDistribution != null) ValidateDistribution(config.HookBars.RowDistribution, "hookBars.rowDistribution");
        ValidateHookShape(config.HookBars.Shape, "hookBars");
      }
    }

    private static void ValidateVersion2Config(FoundationRebarConfig config)
    {
      if (config.BarPieces == null || config.BarPieces.Count == 0)
        throw new InvalidOperationException("schemaVersion 2 JSON must contain barPieces.");
      var duplicate = config.BarPieces.GroupBy(piece => piece.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
      if (duplicate != null) throw new InvalidOperationException($"Duplicate bar piece id: {duplicate.Key}.");

      foreach (RebarPieceConfig piece in config.BarPieces)
      {
        if (string.IsNullOrWhiteSpace(piece.Id)) throw new InvalidOperationException("Every bar piece requires id.");
        if (piece.DiameterMm <= 0) throw new InvalidOperationException($"barPieces.{piece.Id}: diameterMm must be positive.");
        if (piece.Quantity < 0) throw new InvalidOperationException($"barPieces.{piece.Id}: quantity cannot be negative.");
        if (piece.Shape == null) throw new InvalidOperationException($"barPieces.{piece.Id}: shape is required.");
        ValidatePieceShape(piece.Shape, $"barPieces.{piece.Id}");
        if (piece.Placements == null || piece.Placements.Count == 0)
          throw new InvalidOperationException($"barPieces.{piece.Id}: placements must contain at least one placement.");
        foreach (PiecePlacementConfig placement in piece.Placements)
        {
          if (!IsDirection(placement.Direction))
            throw new InvalidOperationException($"barPieces.{piece.Id}: placement.direction must be X or Y.");
          if (placement.Distribution != null) ValidateDistribution(placement.Distribution, $"barPieces.{piece.Id}.placement");
          if (placement.SecondaryDistribution != null) ValidateDistribution(placement.SecondaryDistribution, $"barPieces.{piece.Id}.placement.secondaryDistribution");
          ValidatePlacementZ(placement.Z, $"barPieces.{piece.Id}.placement.z");
        }
      }
    }

    private static void ValidatePlacementZ(PlacementZConfig? z, string owner)
    {
      z ??= new PlacementZConfig();
      string reference = z.Reference ?? "bottomFace";
      string[] allowed = { "topFace", "bottomFace", "absoluteFromBottom", "absoluteFromTop" };
      if (!allowed.Contains(reference, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{owner}: unsupported reference '{reference}'.");
      if (z.OffsetMm < 0) throw new InvalidOperationException($"{owner}: offsetMm cannot be negative.");
    }

    private static Dictionary<string, LayerConfig> GetLayers(FoundationRebarConfig config)
    {
      var duplicate = config.Layers.GroupBy(layer => layer.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
      if (duplicate != null) throw new InvalidOperationException($"Duplicate layer id: {duplicate.Key}.");
      Dictionary<string, LayerConfig> result = config.Layers.ToDictionary(layer => layer.Id, StringComparer.OrdinalIgnoreCase);
      if (result.Count == 0) throw new InvalidOperationException("JSON must contain at least one layer.");
      return result;
    }

    private static string ResolveLayerIdAlias(string? id, IReadOnlyList<LayerConfig> layers, bool first)
    {
      if (layers.Count == 0) throw new InvalidOperationException("JSON must contain at least one layer.");
      string token = id?.Trim() ?? string.Empty;
      if (string.IsNullOrWhiteSpace(token) ||
          token.Equals(first ? "firstLayer" : "lastLayer", StringComparison.OrdinalIgnoreCase) ||
          token.Equals(first ? "first" : "last", StringComparison.OrdinalIgnoreCase) ||
          token.Equals(first ? "$first" : "$last", StringComparison.OrdinalIgnoreCase))
        return first ? layers[0].Id : layers[layers.Count - 1].Id;
      return token;
    }

    private static Dictionary<string, double> ResolveLayerElevations(
      RectangularSolid box,
      Dictionary<string, LayerConfig> layers,
      Dictionary<string, RebarBarType> layerTypes)
    {
      var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
      foreach (string id in layers.Keys)
        ResolveElevation(id, box, layers, layerTypes, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
      return result;
    }

    private static double ResolveElevation(
      string id,
      RectangularSolid box,
      Dictionary<string, LayerConfig> layers,
      Dictionary<string, RebarBarType> layerTypes,
      Dictionary<string, double> resolved,
      HashSet<string> resolving)
    {
      if (resolved.TryGetValue(id, out double existing)) return existing;
      if (!resolving.Add(id)) throw new InvalidOperationException($"Circular layer position reference at {id}.");
      LayerConfig layer = layers[id];
      PositionConfig position = layer.Position ?? throw new InvalidOperationException($"Layer {id}: position is required.");
      double z;
      if (position.Reference.Equals("topFace", StringComparison.OrdinalIgnoreCase))
        z = box.MaxZ - Mm(position.OffsetMm);
      else if (position.Reference.Equals("bottomFace", StringComparison.OrdinalIgnoreCase))
        z = box.MinZ + Mm(position.OffsetMm);
      else if (position.Reference.Equals("aboveLayer", StringComparison.OrdinalIgnoreCase) || position.Reference.Equals("belowLayer", StringComparison.OrdinalIgnoreCase))
      {
        if (string.IsNullOrWhiteSpace(position.LayerId) || !layers.ContainsKey(position.LayerId))
          throw new InvalidOperationException($"Layer {id}: position.layerId is invalid.");
        double referenceZ = ResolveElevation(position.LayerId, box, layers, layerTypes, resolved, resolving);
        double centerDistance =
          layerTypes[id].BarModelDiameter * 0.5 +
          layerTypes[position.LayerId].BarModelDiameter * 0.5 +
          Mm(position.ClearanceMm + position.OffsetMm);
        z = position.Reference.Equals("aboveLayer", StringComparison.OrdinalIgnoreCase)
          ? referenceZ + centerDistance
          : referenceZ - centerDistance;
      }
      else
        throw new InvalidOperationException($"Layer {id}: unsupported position.reference '{position.Reference}'.");
      resolving.Remove(id);
      resolved[id] = z;
      return z;
    }

    private static void ValidateLayerBounds(
      RectangularSolid box,
      Dictionary<string, RebarBarType> layerTypes,
      Dictionary<string, double> z)
    {
      foreach (string id in z.Keys)
      {
        double radius = layerTypes[id].BarModelDiameter * 0.5;
        if (z[id] - radius <= box.MinZ || z[id] + radius >= box.MaxZ)
          throw new InvalidOperationException($"Layer {id} lies outside the selected solid.");
      }
    }

    private static LayerLayout CreateLayer(
      Document document,
      Element host,
      RectangularSolid box,
      LayerConfig layer,
      RebarBarType barType,
      double z,
      double defaultSideCoverMm,
      IReadOnlyDictionary<string, double> elevations)
    {
      bool alongX = layer.Direction.Equals("X", StringComparison.OrdinalIgnoreCase);
      double sideCover = Mm(layer.SideCenterCoverMm ?? defaultSideCoverMm);
      double lineStart = (alongX ? box.MinX : box.MinY) + sideCover;
      double lineEnd = (alongX ? box.MaxX : box.MaxY) - sideCover;
      double distributionMin = alongX ? box.MinY : box.MinX;
      double distributionMax = alongX ? box.MaxY : box.MaxX;
      IList<double> positions = BuildPositions(distributionMin, distributionMax, layer.Distribution);
      if (lineEnd <= lineStart) throw new InvalidOperationException($"Layer {layer.Id}: side cover is too large.");

      string shapeKind = NormalizeLayerShapeKind(layer);
      RebarHookType? hook90 = null;
      if (shapeKind.Equals("straightNativeHooks", StringComparison.OrdinalIgnoreCase) && !layer.Shape.LegLengthMm.HasValue)
      {
        double angle = layer.Shape.HookAngleDegrees <= 0 ? 90 : layer.Shape.HookAngleDegrees;
        if (Math.Abs(angle - 180) <= 0.5)
          hook90 = ResolveHookType(document, barType, layer.Shape.HookTypeName, angle);
      }

      int count = 0;
      foreach (double position in positions)
      {
        IList<Curve> curves;
        BarTerminationSpec terminations = BarTerminationSpec.None;
        if (shapeKind.Equals("polycurve", StringComparison.OrdinalIgnoreCase))
          curves = BuildLayerPolycurve(box, alongX, position, layer.Shape);
        else if (shapeKind.Equals("uToLayer", StringComparison.OrdinalIgnoreCase))
        {
          string targetLayerId = string.IsNullOrWhiteSpace(layer.Shape.TargetLayerId) ? "L2" : layer.Shape.TargetLayerId.Trim();
          if (!elevations.TryGetValue(targetLayerId, out double targetZ))
            throw new InvalidOperationException($"Layer {layer.Id}: shape.targetLayerId '{targetLayerId}' does not match any layer id.");
          curves = BuildUToLayerCurves(box, alongX, position, lineStart, lineEnd, z, targetZ);
        }
        else if (shapeKind.Equals("straightNativeHooks", StringComparison.OrdinalIgnoreCase))
          curves = BuildDownHookedCurves(box, alongX, position, lineStart, lineEnd, z, barType, layer.Shape, hook90);
        else
        {
          curves = new List<Curve>
          {
            alongX
              ? Line.CreateBound(box.ToWorld(lineStart, position, z), box.ToWorld(lineEnd, position, z))
              : Line.CreateBound(box.ToWorld(position, lineStart, z), box.ToWorld(position, lineEnd, z))
          };
        }
        ValidateCurvesInsideBox(curves, box, $"Layer {layer.Id}");
        CreateRebar(document, host, barType, alongX ? box.YAxis : box.XAxis, curves, terminations, $"Layer {layer.Id}");
        count++;
      }

      return new LayerLayout(layer, barType, alongX, z, lineStart, lineEnd, positions, count);
    }

    private static string NormalizeLayerShapeKind(LayerConfig layer)
    {
      string kind = layer.Shape?.Kind ?? "auto";
      if (!kind.Equals("auto", StringComparison.OrdinalIgnoreCase)) return kind;
      return layer.Id.Equals("L1", StringComparison.OrdinalIgnoreCase) || layer.Id.Equals("L2", StringComparison.OrdinalIgnoreCase)
        ? "straightNativeHooks"
        : "uToLayer";
    }

    private static IList<Curve> BuildUToLayerCurves(
      RectangularSolid box,
      bool alongX,
      double position,
      double u0,
      double u1,
      double bottomZ,
      double topZ)
    {
      if (topZ <= bottomZ) throw new InvalidOperationException("U-to-layer bar top must be above its layer elevation.");
      XYZ leftTop = PointOnLayer(box, alongX, position, u0, topZ);
      XYZ leftBottom = PointOnLayer(box, alongX, position, u0, bottomZ);
      XYZ rightBottom = PointOnLayer(box, alongX, position, u1, bottomZ);
      XYZ rightTop = PointOnLayer(box, alongX, position, u1, topZ);

      return new List<Curve>
      {
        Line.CreateBound(leftTop, leftBottom),
        Line.CreateBound(leftBottom, rightBottom),
        Line.CreateBound(rightBottom, rightTop)
      };
    }

    private static IList<Curve> BuildDownHookedCurves(
      RectangularSolid box,
      bool alongX,
      double position,
      double u0,
      double u1,
      double mainZ,
      RebarBarType barType,
      ShapeConfig shape,
      RebarHookType? hook90)
    {
      double tangentLength = shape.LegLengthMm.HasValue ? Mm(shape.LegLengthMm.Value) : 0;
      if (tangentLength <= Tolerance)
      {
        if (hook90 == null) throw new InvalidOperationException("straightNativeHooks without legLengthMm is only supported for 180-degree hooks. For 90-degree hooks, provide legLengthMm so the tool can draw explicit line legs.");
        tangentLength = barType.GetHookTangentLength(hook90.Id);
        if (tangentLength <= Tolerance) tangentLength = hook90.GetHookExtensionLength(barType);
      }
      if (tangentLength <= 0) throw new InvalidOperationException("The hook type does not provide a valid automatic length.");

      XYZ leftBottom = PointOnLayer(box, alongX, position, u0, mainZ - tangentLength);
      XYZ leftTop = PointOnLayer(box, alongX, position, u0, mainZ);
      XYZ rightTop = PointOnLayer(box, alongX, position, u1, mainZ);
      XYZ rightBottom = PointOnLayer(box, alongX, position, u1, mainZ - tangentLength);

      return new List<Curve>
      {
        Line.CreateBound(leftBottom, leftTop),
        Line.CreateBound(leftTop, rightTop),
        Line.CreateBound(rightTop, rightBottom)
      };
    }

    private static int CreateHairpins(
      Document document,
      Element host,
      RectangularSolid box,
      HookBarConfig hook,
      RebarBarType hairpinType,
      LayerLayout l2,
      LayerLayout l3)
    {
      LongLegEndConfig longLegEnd = hook.LongLegEnd ?? throw new InvalidOperationException("hookBars.longLegEnd is required.");
      if (!longLegEnd.Reference.Equals("bottomOfLayer", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("hookBars.longLegEnd.reference must be bottomOfLayer.");
      if (longLegEnd.OffsetMm < 0) throw new InvalidOperationException("hookBars.longLegEnd.offsetMm cannot be negative.");
      double shortLeg = hook.ShortLegLengthMm.HasValue
        ? Mm(hook.ShortLegLengthMm.Value)
        : ResolveHookType(document, hairpinType, hook.HookTypeName, 180).GetHookExtensionLength(hairpinType);
      double minimumHalfWidth = hairpinType.StandardBendDiameter * 0.5 + hairpinType.BarModelDiameter * 0.5;
      double wrapHalfWidth = Math.Max(
        minimumHalfWidth,
        l2.BarType.BarModelDiameter * 0.5 + hairpinType.BarModelDiameter * 0.5 + Mm(hook.WrapClearanceMm));
      double wrapTopOffset = l2.BarType.BarModelDiameter * 0.5
        + hairpinType.BarModelDiameter * 0.5
        + Mm(hook.WrapClearanceMm);
      double longBottomZ = l3.Z - l3.BarType.BarModelDiameter * 0.5 - Mm(longLegEnd.OffsetMm);
      ShapeConfig hookShape = hook.Shape ?? throw new InvalidOperationException("hookBars.shape is required.");
      int count = 0;

      bool hasExplicitDistribution = hook.LongitudinalDistribution.OffsetsMm?.Count > 0
        || hook.LongitudinalDistribution.SpacingPattern?.Count > 0
        || hook.LongitudinalDistribution.SpacingsMm?.Count > 0
        || hook.LongitudinalDistribution.Zones?.Count > 0;
      bool autoLongitudinal = !hasExplicitDistribution
        && hook.LongitudinalDistribution.Layout.Equals("autoFromL2Staggered", StringComparison.OrdinalIgnoreCase);
      DistributionConfig? rowDistribution = hook.RowDistribution;
      bool explicitRows = rowDistribution != null
        && (!rowDistribution.Layout.Equals("autoFromL2Staggered", StringComparison.OrdinalIgnoreCase)
          || rowDistribution.OffsetsMm?.Count > 0
          || rowDistribution.SpacingPattern?.Count > 0
          || rowDistribution.SpacingsMm?.Count > 0
          || rowDistribution.Zones?.Count > 0);
      IList<double> rowPositions = explicitRows
        ? BuildPositions(l2.AlongX ? box.MinY : box.MinX, l2.AlongX ? box.MaxY : box.MaxX, rowDistribution!)
        : BuildAutoHairpinRows(l2.Positions, hook.DefaultSkipL2BarsAtEdges, hook.DefaultSpacingMultiplier);
      double l2Spacing = autoLongitudinal ? ResolveTypicalSpacing(l2.Positions) : 0;

      for (int rowIndex = 0; rowIndex < rowPositions.Count; rowIndex++)
      {
        double l2BarPosition = rowPositions[rowIndex];
        IList<double> longitudinalPositions = autoLongitudinal
          ? BuildAutoHairpinPositions(l2.LineStart, l2.LineEnd, l2Spacing, hook.DefaultSpacingMultiplier, hook.DefaultSkipL2BarsAtEdges, Mm(hook.DefaultLongitudinalShiftMm), hook.DefaultStaggerRows && rowIndex % 2 == 1)
          : BuildPositions(l2.AlongX ? box.MinX : box.MinY, l2.AlongX ? box.MaxX : box.MaxY, hook.LongitudinalDistribution);
        foreach (double longitudinal in longitudinalPositions)
        {
          IList<Curve> curves = hookShape.Kind.Equals("polycurve", StringComparison.OrdinalIgnoreCase)
            ? BuildHairpinPolycurve(box, l2.AlongX, l2BarPosition, longitudinal, l2.Z, hookShape)
            : BuildDefaultHairpinCurves(box, l2.AlongX, l2BarPosition, longitudinal, l2.Z, longBottomZ, shortLeg, wrapHalfWidth, wrapTopOffset);
          ValidateCurvesInsideBox(curves, box, "hookBars");
          CreateRebar(document, host, hairpinType, l2.AlongX ? box.XAxis : box.YAxis, curves, BarTerminationSpec.None, "Hairpin");
          count++;
        }
      }
      return count;
    }

    private static IList<double> BuildAutoHairpinRows(IList<double> positions, int skip, double multiplier)
    {
      if (skip < 0) throw new InvalidOperationException("hookBars.defaultSkipL2BarsAtEdges cannot be negative.");
      if (positions.Count <= skip * 2)
        throw new InvalidOperationException("Not enough wrapped-layer bars remain after skipping the requested bars at both edges.");
      if (multiplier <= 0) throw new InvalidOperationException("hookBars.defaultSpacingMultiplier must be positive.");
      int barStep = Math.Max(1, (int)Math.Round(multiplier));
      var rows = new List<double>();
      for (int index = skip; index < positions.Count - skip; index += barStep) rows.Add(positions[index]);
      return rows;
    }

    private static double ResolveTypicalSpacing(IList<double> positions)
    {
      if (positions.Count < 2) throw new InvalidOperationException("At least two wrapped-layer bars are required to derive the hairpin spacing.");
      List<double> gaps = positions.Zip(positions.Skip(1), (left, right) => right - left).Where(gap => gap > Tolerance).OrderBy(gap => gap).ToList();
      if (gaps.Count == 0) throw new InvalidOperationException("Could not derive a valid spacing from the wrapped layer.");
      return gaps[gaps.Count / 2];
    }

    private static IList<double> BuildAutoHairpinPositions(double start, double end, double l2Spacing, double multiplier, int skip, double longitudinalShift, bool stagger)
    {
      if (multiplier <= 0) throw new InvalidOperationException("hookBars.defaultSpacingMultiplier must be positive.");
      double pitch = l2Spacing * multiplier;
      double first = start + skip * l2Spacing + longitudinalShift + (stagger ? pitch * 0.5 : 0);
      double last = end - skip * l2Spacing;
      var positions = new List<double>();
      for (double value = first; value <= last + Tolerance; value += pitch) positions.Add(Math.Min(value, last));
      return positions;
    }

    private static IList<Curve> BuildDefaultHairpinCurves(
      RectangularSolid box,
      bool l2AlongX,
      double l2BarPosition,
      double longitudinal,
      double l2Z,
      double longBottomZ,
      double shortLeg,
      double halfWidth,
      double topOffset)
    {
      if (l2Z - shortLeg <= longBottomZ)
        throw new InvalidOperationException("Hairpin short leg reaches below the long-leg bottom; reduce the hook extension or adjust the layers.");
      double topZ = l2Z + topOffset;
      XYZ longBottom = HairpinPoint(box, l2AlongX, longitudinal, l2BarPosition, -halfWidth, longBottomZ);
      XYZ longTop = HairpinPoint(box, l2AlongX, longitudinal, l2BarPosition, -halfWidth, topZ);
      XYZ shortTop = HairpinPoint(box, l2AlongX, longitudinal, l2BarPosition, halfWidth, topZ);
      XYZ shortBottom = HairpinPoint(box, l2AlongX, longitudinal, l2BarPosition, halfWidth, l2Z - shortLeg);
      return new List<Curve>
      {
        Line.CreateBound(longBottom, longTop),
        Line.CreateBound(longTop, shortTop),
        Line.CreateBound(shortTop, shortBottom)
      };
    }

    private static IList<Curve> BuildLayerPolycurve(RectangularSolid box, bool alongX, double position, ShapeConfig shape)
    {
      return BuildSegments(shape, point =>
      {
        double u = (alongX ? box.MinX : box.MinY) + Mm(point.Umm);
        double z = box.MinZ + Mm(point.Zmm);
        return PointOnLayer(box, alongX, position, u, z);
      });
    }

    private static IList<Curve> BuildHairpinPolycurve(RectangularSolid box, bool l2AlongX, double l2Position, double longitudinal, double l2Z, ShapeConfig shape)
    {
      return BuildSegments(shape, point => HairpinPoint(box, l2AlongX, longitudinal, l2Position, Mm(point.Umm), l2Z + Mm(point.Zmm)));
    }

    private static IList<Curve> BuildSegments(ShapeConfig shape, Func<ShapePoint, XYZ> map)
    {
      if (shape.Segments == null || shape.Segments.Count == 0)
        throw new InvalidOperationException("polycurve requires shape.segments.");
      var curves = new List<Curve>();
      foreach (ShapeSegment segment in shape.Segments)
      {
        XYZ start = map(segment.Start ?? throw new InvalidOperationException("Shape segment start is required."));
        XYZ end = map(segment.End ?? throw new InvalidOperationException("Shape segment end is required."));
        if (!segment.Type.Equals("line", StringComparison.OrdinalIgnoreCase))
          throw new InvalidOperationException($"Unsupported shape segment type '{segment.Type}'. Only straight line segments are allowed.");
        curves.Add(Line.CreateBound(start, end));
      }
      for (int index = 1; index < curves.Count; index++)
        if (!curves[index - 1].GetEndPoint(1).IsAlmostEqualTo(curves[index].GetEndPoint(0)))
          throw new InvalidOperationException("polycurve segments are not continuous.");
      return curves;
    }

    private static XYZ PointOnLayer(RectangularSolid box, bool alongX, double position, double u, double z) =>
      alongX ? box.ToWorld(u, position, z) : box.ToWorld(position, u, z);

    private static XYZ HairpinPoint(RectangularSolid box, bool l2AlongX, double longitudinal, double l2Position, double crossOffset, double z) =>
      l2AlongX ? box.ToWorld(longitudinal, l2Position + crossOffset, z) : box.ToWorld(l2Position + crossOffset, longitudinal, z);

    private static RevitRebar CreateRebar(
      Document document,
      Element host,
      RebarBarType barType,
      XYZ planeNormal,
      IList<Curve> curves,
      BarTerminationSpec specification,
      string context = "Rebar")
    {
#if D2026 || D2027
      using var terminations = new BarTerminationsData(document);
      if (specification.StartHook != null)
      {
        terminations.HookTypeIdAtStart = specification.StartHook.Id;
        terminations.TerminationOrientationAtStart = specification.StartOrientation;
      }
      if (specification.EndHook != null)
      {
        terminations.HookTypeIdAtEnd = specification.EndHook.Id;
        terminations.TerminationOrientationAtEnd = specification.EndOrientation;
      }
      RevitRebar rebar = RevitRebar.CreateFromCurves(document, RebarStyle.Standard, barType, host, planeNormal, curves, terminations, true, true)
        ?? throw new InvalidOperationException($"{context}: Revit did not create the requested rebar shape.");
#else
      RevitRebar rebar = RevitRebar.CreateFromCurves(
        document,
        RebarStyle.Standard,
        barType,
        specification.StartHook,
        specification.EndHook,
        host,
        planeNormal,
        curves,
        RebarHookOrientation.Right,
        RebarHookOrientation.Right,
        true,
        true)
        ?? throw new InvalidOperationException($"{context}: Revit did not create the requested rebar shape.");
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
        // Some view types do not support per-view rebar visibility overrides.
      }
      catch (Autodesk.Revit.Exceptions.InvalidOperationException)
      {
        // Keep the created bar even when the active view cannot show it unobscured.
      }
    }

    private static IList<double> BuildPositions(double min, double max, DistributionConfig distribution)
    {
      distribution ??= new DistributionConfig();
      if (distribution.Zones?.Count > 0)
      {
        var all = new List<double>();
        foreach (DistributionZoneConfig zone in distribution.Zones)
        {
          double zoneStart = min + Mm(zone.StartOffsetMm);
          double zoneEnd = min + Mm(zone.EndOffsetMm);
          if (zoneStart < min - Tolerance || zoneEnd > max + Tolerance || zoneEnd < zoneStart)
            throw new InvalidOperationException("Distribution zone lies outside its available dimension.");
          all.AddRange(BuildRange(zoneStart, zoneEnd, zone.Layout, zone.MaximumSpacingMm, zone.OffsetsMm, zone.SpacingPattern, zone.SpacingsMm, zone.IncludeEnd));
        }
        return all.Distinct(new DoubleToleranceComparer()).OrderBy(value => value).ToList();
      }
      double start = min + Mm(distribution.EdgeStartMm);
      double end = max - Mm(distribution.EdgeEndMm);
      if (end < start) throw new InvalidOperationException("Distribution edge offsets exceed the available dimension.");
      return BuildRange(start, end, distribution.Layout, distribution.MaximumSpacingMm, distribution.OffsetsMm, distribution.SpacingPattern, distribution.SpacingsMm, distribution.IncludeEnd, min);
    }

    private static IList<double> BuildRange(
      double start,
      double end,
      string layout,
      double spacingMm,
      List<double>? offsetsMm,
      List<SpacingPatternItemConfig>? spacingPattern,
      List<double>? spacingsMm,
      bool includeEnd,
      double? offsetOrigin = null)
    {
      if (offsetsMm?.Count > 0)
      {
        double origin = offsetOrigin ?? start;
        List<double> positions = offsetsMm.Select(value => origin + Mm(value)).ToList();
        if (positions.Any(value => value < start - Tolerance || value > end + Tolerance))
          throw new InvalidOperationException("An offsetsMm value violates the distribution limits.");
        return positions;
      }
      if (spacingPattern?.Count > 0)
      {
        var positions = new List<double> { start };
        double current = start;
        foreach (SpacingPatternItemConfig item in spacingPattern)
        {
          for (int index = 0; index < item.Count; index++)
          {
            current += Mm(item.SpacingMm);
            if (current > end + Tolerance) throw new InvalidOperationException("The sum of spacingPattern exceeds the distribution end.");
            positions.Add(current);
          }
        }
        if (includeEnd && end - positions[positions.Count - 1] > Tolerance) positions.Add(end);
        return positions;
      }
      if (spacingsMm?.Count > 0)
      {
        var positions = new List<double> { start };
        double current = start;
        foreach (double spacing in spacingsMm)
        {
          current += Mm(spacing);
          if (current > end + Tolerance) throw new InvalidOperationException("The sum of spacingsMm exceeds the distribution end.");
          positions.Add(current);
        }
        if (includeEnd && end - positions[positions.Count - 1] > Tolerance) positions.Add(end);
        return positions;
      }
      double spacingInternal = Mm(spacingMm);
      if (spacingInternal <= 0) throw new InvalidOperationException("maximumSpacingMm must be positive.");
      if (Math.Abs(end - start) < Tolerance) return new List<double> { start };
      if (layout.Equals("fixedSpacingWithRemainderAtEnd", StringComparison.OrdinalIgnoreCase))
      {
        var positions = new List<double>();
        for (double value = start; value <= end + Tolerance; value += spacingInternal) positions.Add(Math.Min(value, end));
        if (includeEnd && end - positions[positions.Count - 1] > Tolerance) positions.Add(end);
        return positions;
      }
      if (!layout.Equals("maximumSpacingEven", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Unsupported distribution layout '{layout}'.");
      int spaces = Math.Max(1, (int)Math.Ceiling((end - start) / spacingInternal));
      double actual = (end - start) / spaces;
      return Enumerable.Range(0, spaces + 1).Select(index => start + index * actual).ToList();
    }

    private static RebarBarType ResolveBarType(Document document, double diameterMm, string? requestedName, string layerId)
    {
      return ResolveBarType(document, diameterMm, requestedName, null, layerId);
    }

    private static RebarBarType ResolveBarType(Document document, double diameterMm, string? requestedName, string? sourceTypeName, string layerId)
    {
      string typeName = string.IsNullOrWhiteSpace(requestedName) ? $"F{layerId}_D{diameterMm:0.###}" : requestedName.Trim();
      List<RebarBarType> types = new FilteredElementCollector(document).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().OrderBy(type => type.Name).ToList();
      RebarBarType? named = types.FirstOrDefault(type => type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
      if (named != null) return named;

      string diameterToken = diameterMm.ToString("0.###", CultureInfo.InvariantCulture);
      string sourceName = string.IsNullOrWhiteSpace(sourceTypeName) ? $"CSS{diameterToken}" : sourceTypeName.Trim();
      RebarBarType? source = types.FirstOrDefault(type => type.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
      if (source == null)
        throw new InvalidOperationException($"Template RebarBarType '{sourceName}' was not found for JSON diameter {diameterToken} mm.");
      return (RebarBarType)source.Duplicate(typeName);
    }

    private static RebarHookType ResolveHookType(Document document, RebarBarType barType, string? name, double angleDegrees)
    {
      List<RebarHookType> hooks = new FilteredElementCollector(document).OfClass(typeof(RebarHookType)).Cast<RebarHookType>().OrderBy(hook => hook.Name).ToList();
      RebarHookType? hook = !string.IsNullOrWhiteSpace(name)
        ? hooks.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        : hooks.FirstOrDefault(item => item.Style == RebarStyle.Standard && barType.GetHookPermission(item.Id) && Math.Abs(item.HookAngle * 180.0 / Math.PI - angleDegrees) <= 0.5);
      if (hook == null) throw new InvalidOperationException($"No compatible {angleDegrees:0}-degree hook type was found.");
      if (!barType.GetHookPermission(hook.Id)) throw new InvalidOperationException($"Hook type {hook.Name} is not permitted for {barType.Name}.");
      if (Math.Abs(hook.HookAngle * 180.0 / Math.PI - angleDegrees) > 0.5)
        throw new InvalidOperationException($"Hook type {hook.Name} is not {angleDegrees:0} degrees.");
      return hook;
    }

    private static void ValidateDistribution(DistributionConfig distribution, string owner)
    {
      if (distribution == null) throw new InvalidOperationException($"{owner}: distribution is required.");
      if (distribution.EdgeStartMm < 0 || distribution.EdgeEndMm < 0) throw new InvalidOperationException($"{owner}: edge offsets cannot be negative.");
      if (distribution.SpacingPattern?.Any(item => item.Count <= 0 || item.SpacingMm <= 0) == true)
        throw new InvalidOperationException($"{owner}: spacingPattern items require positive count and spacingMm.");
      if (distribution.Zones?.Any(zone => zone.StartOffsetMm < 0 || zone.EndOffsetMm < zone.StartOffsetMm) == true)
        throw new InvalidOperationException($"{owner}: invalid distribution zone.");
      if (distribution.Zones?.Any(zone => zone.SpacingPattern?.Any(item => item.Count <= 0 || item.SpacingMm <= 0) == true) == true)
        throw new InvalidOperationException($"{owner}: zone spacingPattern items require positive count and spacingMm.");
    }

    private static void ValidateShape(ShapeConfig shape, string owner)
    {
      if (shape == null) throw new InvalidOperationException($"{owner}: shape is required.");
      string[] allowed = { "auto", "straight", "straightNativeHooks", "uToLayer", "hairpinWrapLayer", "polycurve" };
      if (!allowed.Contains(shape.Kind, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{owner}: unsupported shape.kind '{shape.Kind}'.");
      if (shape.LegLengthMm.HasValue && shape.LegLengthMm.Value <= 0)
        throw new InvalidOperationException($"{owner}: shape.legLengthMm must be positive when provided.");
      if (shape.Kind.Equals("polycurve", StringComparison.OrdinalIgnoreCase) && (shape.Segments == null || shape.Segments.Count == 0))
        throw new InvalidOperationException($"{owner}: polycurve requires segments.");
    }

    private static void ValidateLayerShape(ShapeConfig shape, string owner)
    {
      ValidateShape(shape, owner);
      string[] allowed = { "auto", "straight", "straightNativeHooks", "uToLayer", "polycurve" };
      if (!allowed.Contains(shape.Kind, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{owner}: shape.kind '{shape.Kind}' is not valid for a reinforcement layer.");
    }

    private static void ValidateHookShape(ShapeConfig shape, string owner)
    {
      ValidateShape(shape, owner);
      string[] allowed = { "auto", "hairpinWrapLayer", "polycurve" };
      if (!allowed.Contains(shape.Kind, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{owner}: shape.kind '{shape.Kind}' is not valid for a hairpin bar.");
    }

    private static void ValidatePieceShape(ShapeConfig shape, string owner)
    {
      if (shape == null) throw new InvalidOperationException($"{owner}: shape is required.");
      string[] allowed = { "straight", "straightWithNativeHooks", "uBar", "hairpin", "piecewiseLine" };
      if (!allowed.Contains(shape.Kind, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{owner}: unsupported shape.kind '{shape.Kind}'.");
      if (shape.StraightLengthMm.HasValue && shape.StraightLengthMm.Value <= 0)
        throw new InvalidOperationException($"{owner}: straightLengthMm must be positive when provided.");
      if (shape.HookStartMm.HasValue && shape.HookStartMm.Value < 0)
        throw new InvalidOperationException($"{owner}: hookStartMm cannot be negative.");
      if (shape.HookEndMm.HasValue && shape.HookEndMm.Value < 0)
        throw new InvalidOperationException($"{owner}: hookEndMm cannot be negative.");
      if (shape.LegLengthMm.HasValue && shape.LegLengthMm.Value <= 0)
        throw new InvalidOperationException($"{owner}: legLengthMm must be positive when provided.");
      if (shape.Kind.Equals("piecewiseLine", StringComparison.OrdinalIgnoreCase) && (shape.Segments == null || shape.Segments.Count == 0))
        throw new InvalidOperationException($"{owner}: piecewiseLine requires segments.");
      if (shape.Kind.Equals("hairpin", StringComparison.OrdinalIgnoreCase))
      {
        if (!shape.CrownWidthMm.HasValue || shape.CrownWidthMm.Value <= 0)
          throw new InvalidOperationException($"{owner}: hairpin requires positive crownWidthMm.");
        if (!shape.LongLegMm.HasValue || shape.LongLegMm.Value <= 0)
          throw new InvalidOperationException($"{owner}: hairpin requires positive longLegMm.");
        if (!shape.ShortLegMm.HasValue || shape.ShortLegMm.Value <= 0)
          throw new InvalidOperationException($"{owner}: hairpin requires positive shortLegMm.");
      }
    }

    private static void ValidateCurvesInsideBox(IEnumerable<Curve> curves, RectangularSolid box, string owner)
    {
      foreach (XYZ point in curves.SelectMany(curve => curve.Tessellate()))
      {
        double x = point.DotProduct(box.XAxis);
        double y = point.DotProduct(box.YAxis);
        if (x < box.MinX - Tolerance || x > box.MaxX + Tolerance ||
            y < box.MinY - Tolerance || y > box.MaxY + Tolerance ||
            point.Z < box.MinZ - Tolerance || point.Z > box.MaxZ + Tolerance)
          throw new InvalidOperationException($"{owner}: shape extends outside the selected solid.");
      }
    }

    private static bool IsDirection(string direction) => direction.Equals("X", StringComparison.OrdinalIgnoreCase) || direction.Equals("Y", StringComparison.OrdinalIgnoreCase);
    private static double Mm(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

    private static Solid? FindPickedSolid(Element element, XYZ point)
    {
      var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
      var solids = new List<Solid>();
      CollectSolids(element.get_Geometry(options), solids);
      return solids.Select(solid => new
      {
        Solid = solid,
        Distance = solid.Faces.Cast<Face>().Select(face => face.Project(point)).Where(result => result != null).Select(result => result!.Distance).DefaultIfEmpty(double.MaxValue).Min()
      }).OrderBy(candidate => candidate.Distance).FirstOrDefault()?.Solid;
    }

    private static void CollectSolids(GeometryElement? geometry, ICollection<Solid> solids)
    {
      if (geometry == null) return;
      foreach (GeometryObject geometryObject in geometry)
      {
        if (geometryObject is Solid solid && solid.Volume > 1e-9) solids.Add(solid);
        else if (geometryObject is GeometryInstance instance) CollectSolids(instance.GetInstanceGeometry(), solids);
      }
    }

    private sealed class FamilyInstanceFilter : ISelectionFilter
    {
      public bool AllowElement(Element element) => element is FamilyInstance;
      public bool AllowReference(Reference reference, XYZ position) => true;
    }

    private sealed class DoubleToleranceComparer : IEqualityComparer<double>
    {
      public bool Equals(double x, double y) => Math.Abs(x - y) < Tolerance;
      public int GetHashCode(double obj) => Math.Round(obj / Tolerance).GetHashCode();
    }

    private readonly struct BarTerminationSpec
    {
#if D2026 || D2027
      public static BarTerminationSpec None => new(null, null, RebarTerminationOrientation.Right, RebarTerminationOrientation.Right);
      public BarTerminationSpec(RebarHookType? startHook, RebarHookType? endHook, RebarTerminationOrientation startOrientation, RebarTerminationOrientation endOrientation)
      {
        StartHook = startHook; EndHook = endHook; StartOrientation = startOrientation; EndOrientation = endOrientation;
      }
#else
      public static BarTerminationSpec None => new(null, null);
      public BarTerminationSpec(RebarHookType? startHook, RebarHookType? endHook)
      {
        StartHook = startHook; EndHook = endHook;
      }
#endif
      public RebarHookType? StartHook { get; }
      public RebarHookType? EndHook { get; }
#if D2026 || D2027
      public RebarTerminationOrientation StartOrientation { get; }
      public RebarTerminationOrientation EndOrientation { get; }
#endif
    }

    private sealed class LayerLayout
    {
      public LayerLayout(LayerConfig layer, RebarBarType barType, bool alongX, double z, double lineStart, double lineEnd, IList<double> positions, int created)
      {
        Layer = layer; BarType = barType; AlongX = alongX; Z = z; LineStart = lineStart; LineEnd = lineEnd; Positions = positions; Created = created;
      }
      public LayerConfig Layer { get; }
      public RebarBarType BarType { get; }
      public bool AlongX { get; }
      public double Z { get; }
      public double LineStart { get; }
      public double LineEnd { get; }
      public IList<double> Positions { get; }
      public int Created { get; }
    }

    private sealed class RectangularSolid
    {
      private RectangularSolid(XYZ xAxis, XYZ yAxis, double minX, double maxX, double minY, double maxY, double minZ, double maxZ, bool isRectangular)
      { XAxis = xAxis; YAxis = yAxis; MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY; MinZ = minZ; MaxZ = maxZ; IsRectangular = isRectangular; }
      public XYZ XAxis { get; }
      public XYZ YAxis { get; }
      public double MinX { get; }
      public double MaxX { get; }
      public double MinY { get; }
      public double MaxY { get; }
      public double MinZ { get; }
      public double MaxZ { get; }
      public bool IsRectangular { get; }
      public XYZ ToWorld(double x, double y, double z) => XAxis.Multiply(x).Add(YAxis.Multiply(y)).Add(XYZ.BasisZ.Multiply(z));
      public static RectangularSolid Create(Solid solid)
      {
        XYZ xAxis = FindHorizontalEdgeDirection(solid);
        XYZ yAxis = XYZ.BasisZ.CrossProduct(xAxis).Normalize();
        List<XYZ> points = solid.Edges.Cast<Edge>().SelectMany(edge => edge.Tessellate()).ToList();
        double minX = points.Min(point => point.DotProduct(xAxis)); double maxX = points.Max(point => point.DotProduct(xAxis));
        double minY = points.Min(point => point.DotProduct(yAxis)); double maxY = points.Max(point => point.DotProduct(yAxis));
        double minZ = points.Min(point => point.Z); double maxZ = points.Max(point => point.Z);
        double volume = (maxX - minX) * (maxY - minY) * (maxZ - minZ);
        return new RectangularSolid(xAxis, yAxis, minX, maxX, minY, maxY, minZ, maxZ, volume > 1e-9 && solid.Volume / volume > 0.97);
      }
      private static XYZ FindHorizontalEdgeDirection(Solid solid)
      {
        Line? longest = solid.Edges.Cast<Edge>().Select(edge => edge.AsCurve()).OfType<Line>().Where(line => Math.Abs(line.Direction.Z) < 1e-5).OrderByDescending(line => line.Length).FirstOrDefault();
        if (longest == null) throw new InvalidOperationException("The solid has no straight horizontal edge.");
        return new XYZ(longest.Direction.X, longest.Direction.Y, 0).Normalize();
      }
    }
  }

  public sealed class FoundationRebarConfig
  {
    [JsonProperty("$schema")] public string? Schema { get; set; }
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonProperty("units")] public string Units { get; set; } = "mm";
    [JsonProperty("coordinateSystem")] public string CoordinateSystem { get; set; } = "selectedSolidLocalXYZ";
    [JsonProperty("sideCenterCoverMm")] public double SideCenterCoverMm { get; set; } = 150;
    [JsonProperty("layers")] public List<LayerConfig> Layers { get; set; } = new();
    [JsonProperty("hookBars")] public HookBarConfig? HookBars { get; set; }
    [JsonProperty("barMarks")] public List<RebarMarkConfig> BarMarks { get; set; } = new();
    [JsonProperty("barPieces")] public List<RebarPieceConfig> BarPieces { get; set; } = new();
    [JsonProperty("reviewNotes")] public List<string> ReviewNotes { get; set; } = new();
  }

  public sealed class RebarMarkConfig
  {
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("diameterMm")] public double DiameterMm { get; set; }
    [JsonProperty("quantity")] public int Quantity { get; set; }
    [JsonProperty("description")] public string? Description { get; set; }
  }

  public sealed class RebarPieceConfig
  {
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("parentMark")] public string? ParentMark { get; set; }
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
    [JsonProperty("diameterMm")] public double DiameterMm { get; set; }
    [JsonProperty("typeName")] public string? TypeName { get; set; }
    [JsonProperty("sourceTypeName")] public string? SourceTypeName { get; set; }
    [JsonProperty("quantity")] public int Quantity { get; set; }
    [JsonProperty("shape")] public ShapeConfig Shape { get; set; } = new();
    [JsonProperty("placements")] public List<PiecePlacementConfig> Placements { get; set; } = new();
    [JsonProperty("metadata")] public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
  }

  public sealed class PiecePlacementConfig
  {
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("plane")] public string Plane { get; set; } = "horizontal";
    [JsonProperty("direction")] public string Direction { get; set; } = "X";
    [JsonProperty("z")] public PlacementZConfig Z { get; set; } = new();
    [JsonProperty("lineStartOffsetMm")] public double? LineStartOffsetMm { get; set; }
    [JsonProperty("lineEndOffsetMm")] public double? LineEndOffsetMm { get; set; }
    [JsonProperty("distribution")] public DistributionConfig? Distribution { get; set; }
    [JsonProperty("secondaryDistribution")] public DistributionConfig? SecondaryDistribution { get; set; }
  }

  public sealed class PlacementZConfig
  {
    [JsonProperty("reference")] public string Reference { get; set; } = "bottomFace";
    [JsonProperty("offsetMm")] public double OffsetMm { get; set; }
  }

  public sealed class LayerConfig
  {
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("diameterMm")] public double DiameterMm { get; set; }
    [JsonProperty("typeName")] public string? TypeName { get; set; }
    [JsonProperty("direction")] public string Direction { get; set; } = "X";
    [JsonProperty("position")] public PositionConfig Position { get; set; } = new();
    [JsonProperty("sideCenterCoverMm")] public double? SideCenterCoverMm { get; set; }
    [JsonProperty("distribution")] public DistributionConfig Distribution { get; set; } = new();
    [JsonProperty("shape")] public ShapeConfig Shape { get; set; } = new();
  }

  public sealed class PositionConfig
  {
    [JsonProperty("reference")] public string Reference { get; set; } = string.Empty;
    [JsonProperty("layerId")] public string? LayerId { get; set; }
    [JsonProperty("offsetMm")] public double OffsetMm { get; set; }
    [JsonProperty("clearanceMm")] public double ClearanceMm { get; set; }
  }

  public sealed class HookBarConfig
  {
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
    [JsonProperty("diameterMm")] public double DiameterMm { get; set; } = 16;
    [JsonProperty("typeName")] public string? TypeName { get; set; }
    [JsonProperty("wrapLayerId")] public string WrapLayerId { get; set; } = "L2";
    [JsonProperty("longLegEnd")] public LongLegEndConfig LongLegEnd { get; set; } = new();
    [JsonProperty("hookTypeName")] public string? HookTypeName { get; set; }
    [JsonProperty("wrapClearanceMm")] public double WrapClearanceMm { get; set; }
    [JsonProperty("shortLegLengthMm")] public double? ShortLegLengthMm { get; set; }
    [JsonProperty("defaultSpacingMultiplier")] public double DefaultSpacingMultiplier { get; set; } = 4;
    [JsonProperty("defaultSkipL2BarsAtEdges")] public int DefaultSkipL2BarsAtEdges { get; set; } = 2;
    [JsonProperty("defaultStaggerRows")] public bool DefaultStaggerRows { get; set; } = true;
    [JsonProperty("defaultLongitudinalShiftMm")] public double DefaultLongitudinalShiftMm { get; set; } = 100;
    [JsonProperty("rowDistribution")] public DistributionConfig? RowDistribution { get; set; }
    [JsonProperty("longitudinalDistribution")] public DistributionConfig LongitudinalDistribution { get; set; } = new();
    [JsonProperty("shape")] public ShapeConfig Shape { get; set; } = new() { Kind = "hairpinWrapLayer" };
  }

  public sealed class LongLegEndConfig
  {
    [JsonProperty("reference")] public string Reference { get; set; } = "bottomOfLayer";
    [JsonProperty("layerId")] public string LayerId { get; set; } = "L3";
    [JsonProperty("offsetMm")] public double OffsetMm { get; set; }
  }

  public sealed class ShapeConfig
  {
    [JsonProperty("kind")] public string Kind { get; set; } = "auto";
    [JsonProperty("hookTypeName")] public string? HookTypeName { get; set; }
    [JsonProperty("startHookTypeName")] public string? StartHookTypeName { get; set; }
    [JsonProperty("endHookTypeName")] public string? EndHookTypeName { get; set; }
    [JsonProperty("hookAngleDegrees")] public double HookAngleDegrees { get; set; }
    [JsonProperty("hookStartMm")] public double? HookStartMm { get; set; }
    [JsonProperty("hookEndMm")] public double? HookEndMm { get; set; }
    [JsonProperty("startHookOrientation")] public string? StartHookOrientation { get; set; }
    [JsonProperty("endHookOrientation")] public string? EndHookOrientation { get; set; }
    [JsonProperty("hookLegDirection")] public string? HookLegDirection { get; set; }
    [JsonProperty("fallbackToLine")] public bool FallbackToLine { get; set; } = true;
    [JsonProperty("straightLengthMm")] public double? StraightLengthMm { get; set; }
    [JsonProperty("legLengthMm")] public double? LegLengthMm { get; set; }
    [JsonProperty("crownWidthMm")] public double? CrownWidthMm { get; set; }
    [JsonProperty("longLegMm")] public double? LongLegMm { get; set; }
    [JsonProperty("shortLegMm")] public double? ShortLegMm { get; set; }
    [JsonProperty("bendRadiusMm")] public double? BendRadiusMm { get; set; }
    [JsonProperty("targetLayerId")] public string? TargetLayerId { get; set; }
    [JsonProperty("segments")] public List<ShapeSegment>? Segments { get; set; }
  }

  public sealed class ShapeSegment
  {
    [JsonProperty("type")] public string Type { get; set; } = "line";
    [JsonProperty("start")] public ShapePoint? Start { get; set; }
    [JsonProperty("end")] public ShapePoint? End { get; set; }
  }

  public sealed class ShapePoint
  {
    [JsonProperty("uMm")] public double Umm { get; set; }
    [JsonProperty("zMm")] public double Zmm { get; set; }
  }

  public sealed class DistributionConfig
  {
    [JsonProperty("layout")] public string Layout { get; set; } = "fixedSpacingWithRemainderAtEnd";
    [JsonProperty("edgeStartMm")] public double EdgeStartMm { get; set; } = 150;
    [JsonProperty("edgeEndMm")] public double EdgeEndMm { get; set; } = 150;
    [JsonProperty("maximumSpacingMm")] public double MaximumSpacingMm { get; set; } = 250;
    [JsonProperty("offsetsMm")] public List<double>? OffsetsMm { get; set; }
    [JsonProperty("spacingPattern")] public List<SpacingPatternItemConfig>? SpacingPattern { get; set; }
    [JsonProperty("spacingsMm")] public List<double>? SpacingsMm { get; set; }
    [JsonProperty("includeEnd")] public bool IncludeEnd { get; set; } = true;
    [JsonProperty("zones")] public List<DistributionZoneConfig>? Zones { get; set; }
  }

  public sealed class SpacingPatternItemConfig
  {
    [JsonProperty("count")] public int Count { get; set; }
    [JsonProperty("spacingMm")] public double SpacingMm { get; set; }
  }

  public sealed class DistributionZoneConfig
  {
    [JsonProperty("startOffsetMm")] public double StartOffsetMm { get; set; }
    [JsonProperty("endOffsetMm")] public double EndOffsetMm { get; set; }
    [JsonProperty("layout")] public string Layout { get; set; } = "fixedSpacingWithRemainderAtEnd";
    [JsonProperty("maximumSpacingMm")] public double MaximumSpacingMm { get; set; } = 250;
    [JsonProperty("offsetsMm")] public List<double>? OffsetsMm { get; set; }
    [JsonProperty("spacingPattern")] public List<SpacingPatternItemConfig>? SpacingPattern { get; set; }
    [JsonProperty("spacingsMm")] public List<double>? SpacingsMm { get; set; }
    [JsonProperty("includeEnd")] public bool IncludeEnd { get; set; } = true;
  }
}

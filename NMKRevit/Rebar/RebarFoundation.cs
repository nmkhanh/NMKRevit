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

        Dictionary<string, LayerConfig> layers = GetRequiredLayers(config);
        int layerBars = 0;
        int hairpinBars = 0;
        var layouts = new Dictionary<string, LayerLayout>(StringComparer.OrdinalIgnoreCase);
        var usedTypes = new List<string>();
        var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using (var transaction = new Transaction(document, "Rebar foundation from JSON"))
        {
          transaction.Start();
          var layerTypes = new Dictionary<string, RebarBarType>(StringComparer.OrdinalIgnoreCase);
          foreach (string id in new[] { "L1", "L2", "L3", "L4" })
          {
            LayerConfig layer = layers[id];
            layerTypes[id] = ResolveBarType(document, layer.DiameterMm, layer.TypeName, layer.Id);
          }

          Dictionary<string, double> elevations = ResolveLayerElevations(box, layers, layerTypes);
          ValidateLayerOrderAndBounds(box, layerTypes, elevations);
          foreach (string id in new[] { "L1", "L2", "L3", "L4" })
          {
            LayerConfig layer = layers[id];
            RebarBarType barType = layerTypes[id];
            LayerLayout layout = CreateLayer(document, host, box, layer, barType, elevations[id], config.SideCenterCoverMm, elevations["L2"]);
            layouts[id] = layout;
            layerBars += layout.Created;
            layerCounts[id] = layout.Created;
            usedTypes.Add(barType.Name);
          }

          if (config.HookBars?.Enabled == true)
          {
            HookBarConfig hook = config.HookBars;
            RebarBarType hookType = ResolveBarType(document, hook.DiameterMm, hook.TypeName, "Hook");
            hairpinBars = CreateHairpins(document, host, box, hook, hookType, layouts["L2"], layouts["L3"]);
            usedTypes.Add(hookType.Name);
          }
          transaction.Commit();
        }

        RevitTaskDialog.Show(
          "RebarFoundation",
          $"JSON: {Path.GetFileName(jsonPath)}\n" +
          $"Layer bars: {layerBars} " +
          $"(L1: {layerCounts["L1"]}, L2: {layerCounts["L2"]}, L3: {layerCounts["L3"]}, L4: {layerCounts["L4"]})\n" +
          $"Hairpin bars: {hairpinBars}\n" +
          $"Types: {string.Join(", ", usedTypes.Distinct())}");
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
      var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
      FoundationRebarConfig? config = JsonConvert.DeserializeObject<FoundationRebarConfig>(File.ReadAllText(path), settings);
      if (config == null) throw new InvalidOperationException("The JSON file is empty.");
      if (config.SchemaVersion != 1) throw new InvalidOperationException("Only schemaVersion 1 is supported.");
      if (!config.Units.Equals("mm", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Only millimetres (units = mm) are supported.");
      if (!config.CoordinateSystem.Equals("selectedSolidLocalXYZ", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Only coordinateSystem selectedSolidLocalXYZ is supported.");
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
      return config;
    }

    private static Dictionary<string, LayerConfig> GetRequiredLayers(FoundationRebarConfig config)
    {
      var duplicate = config.Layers.GroupBy(layer => layer.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
      if (duplicate != null) throw new InvalidOperationException($"Duplicate layer id: {duplicate.Key}.");
      Dictionary<string, LayerConfig> result = config.Layers.ToDictionary(layer => layer.Id, StringComparer.OrdinalIgnoreCase);
      foreach (string id in new[] { "L1", "L2", "L3", "L4" })
        if (!result.ContainsKey(id)) throw new InvalidOperationException($"Required layer {id} is missing.");
      return result;
    }

    private static Dictionary<string, double> ResolveLayerElevations(
      RectangularSolid box,
      Dictionary<string, LayerConfig> layers,
      Dictionary<string, RebarBarType> layerTypes)
    {
      var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
      foreach (string id in new[] { "L2", "L1", "L3", "L4" })
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

    private static void ValidateLayerOrderAndBounds(
      RectangularSolid box,
      Dictionary<string, RebarBarType> layerTypes,
      Dictionary<string, double> z)
    {
      if (!(z["L1"] > z["L2"] && z["L2"] > z["L3"] && z["L3"] > z["L4"]))
        throw new InvalidOperationException("Layer order must be L1 > L2 > L3 > L4 from top to bottom.");
      foreach (string id in new[] { "L1", "L2", "L3", "L4" })
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
      double l2Z)
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
      if (shapeKind.Equals("straightNativeHooks", StringComparison.OrdinalIgnoreCase))
        hook90 = ResolveHookType(document, barType, layer.Shape.HookTypeName, layer.Shape.HookAngleDegrees <= 0 ? 90 : layer.Shape.HookAngleDegrees);

      int count = 0;
      foreach (double position in positions)
      {
        IList<Curve> curves;
        BarTerminationSpec terminations = BarTerminationSpec.None;
        if (shapeKind.Equals("polycurve", StringComparison.OrdinalIgnoreCase))
          curves = BuildLayerPolycurve(box, alongX, position, layer.Shape);
        else if (shapeKind.Equals("uToLayer", StringComparison.OrdinalIgnoreCase))
          curves = BuildUToLayerCurves(box, alongX, position, lineStart, lineEnd, z, l2Z);
        else if (shapeKind.Equals("straightNativeHooks", StringComparison.OrdinalIgnoreCase))
          curves = BuildDownHookedCurves(box, alongX, position, lineStart, lineEnd, z, barType, hook90!);
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
      if (topZ <= bottomZ) throw new InvalidOperationException("U-to-L2 bar top must be above its layer elevation.");
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
      RebarHookType hook90)
    {
      double tangentLength = barType.GetHookTangentLength(hook90.Id);
      if (tangentLength <= Tolerance) tangentLength = hook90.GetHookExtensionLength(barType);
      if (tangentLength <= 0) throw new InvalidOperationException("The 90-degree hook type does not provide a valid automatic length.");

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
      if (!hook.WrapLayerId.Equals("L2", StringComparison.OrdinalIgnoreCase) ||
          !longLegEnd.Reference.Equals("bottomOfLayer", StringComparison.OrdinalIgnoreCase) ||
          !longLegEnd.LayerId.Equals("L3", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Schema version 1 hairpins must wrap L2 and terminate at bottomOfLayer L3.");
      if (longLegEnd.OffsetMm < 0) throw new InvalidOperationException("hookBars.longLegEnd.offsetMm cannot be negative.");
      RebarHookType hook180 = ResolveHookType(document, hairpinType, hook.HookTypeName, 180);
      double shortLeg = hook.ShortLegLengthMm.HasValue ? Mm(hook.ShortLegLengthMm.Value) : hook180.GetHookExtensionLength(hairpinType);
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
        || hook.LongitudinalDistribution.SpacingsMm?.Count > 0
        || hook.LongitudinalDistribution.Zones?.Count > 0;
      bool autoLongitudinal = !hasExplicitDistribution
        && hook.LongitudinalDistribution.Layout.Equals("autoFromL2Staggered", StringComparison.OrdinalIgnoreCase);
      DistributionConfig? rowDistribution = hook.RowDistribution;
      bool explicitRows = rowDistribution != null
        && (!rowDistribution.Layout.Equals("autoFromL2Staggered", StringComparison.OrdinalIgnoreCase)
          || rowDistribution.OffsetsMm?.Count > 0
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
        throw new InvalidOperationException("Not enough L2 bars remain after skipping the requested bars at both edges.");
      if (multiplier <= 0) throw new InvalidOperationException("hookBars.defaultSpacingMultiplier must be positive.");
      int barStep = Math.Max(1, (int)Math.Round(multiplier));
      var rows = new List<double>();
      for (int index = skip; index < positions.Count - skip; index += barStep) rows.Add(positions[index]);
      return rows;
    }

    private static double ResolveTypicalSpacing(IList<double> positions)
    {
      if (positions.Count < 2) throw new InvalidOperationException("At least two L2 bars are required to derive the hairpin spacing.");
      List<double> gaps = positions.Zip(positions.Skip(1), (left, right) => right - left).Where(gap => gap > Tolerance).OrderBy(gap => gap).ToList();
      if (gaps.Count == 0) throw new InvalidOperationException("Could not derive a valid spacing from L2.");
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
          all.AddRange(BuildRange(zoneStart, zoneEnd, zone.Layout, zone.MaximumSpacingMm, zone.OffsetsMm, zone.SpacingsMm, zone.IncludeEnd));
        }
        return all.Distinct(new DoubleToleranceComparer()).OrderBy(value => value).ToList();
      }
      double start = min + Mm(distribution.EdgeStartMm);
      double end = max - Mm(distribution.EdgeEndMm);
      if (end < start) throw new InvalidOperationException("Distribution edge offsets exceed the available dimension.");
      return BuildRange(start, end, distribution.Layout, distribution.MaximumSpacingMm, distribution.OffsetsMm, distribution.SpacingsMm, distribution.IncludeEnd, min);
    }

    private static IList<double> BuildRange(
      double start,
      double end,
      string layout,
      double spacingMm,
      List<double>? offsetsMm,
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
        if (includeEnd && end - positions[^1] > Tolerance) positions.Add(end);
        return positions;
      }
      double spacingInternal = Mm(spacingMm);
      if (spacingInternal <= 0) throw new InvalidOperationException("maximumSpacingMm must be positive.");
      if (Math.Abs(end - start) < Tolerance) return new List<double> { start };
      if (layout.Equals("fixedSpacingWithRemainderAtEnd", StringComparison.OrdinalIgnoreCase))
      {
        var positions = new List<double>();
        for (double value = start; value <= end + Tolerance; value += spacingInternal) positions.Add(Math.Min(value, end));
        if (includeEnd && end - positions[^1] > Tolerance) positions.Add(end);
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
      string typeName = string.IsNullOrWhiteSpace(requestedName) ? $"F{layerId}_D{diameterMm:0.###}" : requestedName.Trim();
      List<RebarBarType> types = new FilteredElementCollector(document).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().OrderBy(type => type.Name).ToList();
      RebarBarType? named = types.FirstOrDefault(type => type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
      if (named != null) return named;

      string diameterToken = diameterMm.ToString("0.###", CultureInfo.InvariantCulture);
      string sourceName = $"CSS{diameterToken}";
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
      if (distribution.Zones?.Any(zone => zone.StartOffsetMm < 0 || zone.EndOffsetMm < zone.StartOffsetMm) == true)
        throw new InvalidOperationException($"{owner}: invalid distribution zone.");
    }

    private static void ValidateShape(ShapeConfig shape, string owner)
    {
      if (shape == null) throw new InvalidOperationException($"{owner}: shape is required.");
      string[] allowed = { "auto", "straightNativeHooks", "uToLayer", "hairpinWrapLayer", "polycurve" };
      if (!allowed.Contains(shape.Kind, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{owner}: unsupported shape.kind '{shape.Kind}'.");
      if (shape.Kind.Equals("polycurve", StringComparison.OrdinalIgnoreCase) && (shape.Segments == null || shape.Segments.Count == 0))
        throw new InvalidOperationException($"{owner}: polycurve requires segments.");
    }

    private static void ValidateLayerShape(ShapeConfig shape, string owner)
    {
      ValidateShape(shape, owner);
      string[] allowed = { "auto", "straightNativeHooks", "uToLayer", "polycurve" };
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
      public static BarTerminationSpec None => new(null, null, RebarTerminationOrientation.Right, RebarTerminationOrientation.Right);
      public BarTerminationSpec(RebarHookType? startHook, RebarHookType? endHook, RebarTerminationOrientation startOrientation, RebarTerminationOrientation endOrientation)
      {
        StartHook = startHook; EndHook = endHook; StartOrientation = startOrientation; EndOrientation = endOrientation;
      }
      public RebarHookType? StartHook { get; }
      public RebarHookType? EndHook { get; }
      public RebarTerminationOrientation StartOrientation { get; }
      public RebarTerminationOrientation EndOrientation { get; }
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
    [JsonProperty("hookAngleDegrees")] public double HookAngleDegrees { get; set; }
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
    [JsonProperty("spacingsMm")] public List<double>? SpacingsMm { get; set; }
    [JsonProperty("includeEnd")] public bool IncludeEnd { get; set; } = true;
    [JsonProperty("zones")] public List<DistributionZoneConfig>? Zones { get; set; }
  }

  public sealed class DistributionZoneConfig
  {
    [JsonProperty("startOffsetMm")] public double StartOffsetMm { get; set; }
    [JsonProperty("endOffsetMm")] public double EndOffsetMm { get; set; }
    [JsonProperty("layout")] public string Layout { get; set; } = "fixedSpacingWithRemainderAtEnd";
    [JsonProperty("maximumSpacingMm")] public double MaximumSpacingMm { get; set; } = 250;
    [JsonProperty("offsetsMm")] public List<double>? OffsetsMm { get; set; }
    [JsonProperty("spacingsMm")] public List<double>? SpacingsMm { get; set; }
    [JsonProperty("includeEnd")] public bool IncludeEnd { get; set; } = true;
  }
}

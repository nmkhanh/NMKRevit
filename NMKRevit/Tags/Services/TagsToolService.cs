using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.Tags.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using RevitColor = Autodesk.Revit.DB.Color;
using RevitView = Autodesk.Revit.DB.View;

namespace NMKRevit.Tags.Services
{
  public sealed class TagsToolService
  {
    private const double Tolerance = 1d / 304.8d;
    private static readonly RevitColor ErrorColor = new(255, 0, 0);

    public TagsToolResult CheckTags(UIApplication uiapp)
    {
      UIDocument uidoc = uiapp.ActiveUIDocument;
      Document document = uidoc.Document;
      var result = new TagsToolResult();

      if (!TryGetPlanContext(document, document.ActiveView, out PlanViewContext? contextMaybe, out string error))
      {
        result.Items.Add(new TagsToolResultItem(TagsToolLogLevel.Error, string.Empty, error));
        result.Summary = "Check Tags stopped: active view is not a supported plan view.";
        return result;
      }
      PlanViewContext context = contextMaybe!;

      List<IndependentTag> tags = new FilteredElementCollector(document, context.View.Id)
        .OfClass(typeof(IndependentTag))
        .WhereElementIsNotElementType()
        .Cast<IndependentTag>()
        .ToList();
      List<TargetElement> activeTargets = GetActiveTargets(document, context);
      Dictionary<long, TargetElement> activeTargetsById = activeTargets.ToDictionary(target => GetIdValue(target.Element.Id));
      Dictionary<TagTargetKind, List<TargetElement>> activeTargetsByKind = GroupByKind(activeTargets);
      var wrongTagIds = new List<ElementId>();
      int checkedTags = 0;
      int okTags = 0;
      int ignoredTags = 0;

      foreach (IndependentTag tag in tags)
      {
        TagCheckResult check = CheckTag(document, context, tag, activeTargetsById, activeTargetsByKind);
        if (check.Status == TagCheckStatus.Ignored)
        {
          ignoredTags++;
          continue;
        }

        checkedTags++;
        if (check.Status == TagCheckStatus.Ok)
        {
          okTags++;
          continue;
        }

        wrongTagIds.Add(tag.Id);
        result.Items.Add(new TagsToolResultItem(TagsToolLogLevel.Error, tag.Id.ToString(), check.Message));
      }

      if (wrongTagIds.Count > 0)
      {
        ApplyErrorOverrides(document, context.View, wrongTagIds, result);
      }
      uidoc.Selection.SetElementIds(wrongTagIds);

      result.Summary = $"Active View: tags {tags.Count}; checked {checkedTags}; OK {okTags}; wrong {wrongTagIds.Count}; ignored {ignoredTags}.";
      return result;
    }

    public TagsToolResult TagAll(UIApplication uiapp, TagTargetKind requestedKind)
    {
      UIDocument uidoc = uiapp.ActiveUIDocument;
      Document document = uidoc.Document;
      var result = new TagsToolResult();

      if (!TryGetPlanContext(document, document.ActiveView, out PlanViewContext? contextMaybe, out string error))
      {
        result.Items.Add(new TagsToolResultItem(TagsToolLogLevel.Error, string.Empty, error));
        result.Summary = "Tags All stopped: active view is not a supported plan view.";
        return result;
      }
      PlanViewContext context = contextMaybe!;

      List<TargetElement> activeTargets = GetActiveTargets(document, context)
        .Where(target => requestedKind == TagTargetKind.Unknown || target.Kind == requestedKind)
        .ToList();
      Dictionary<long, TargetElement> activeTargetsById = activeTargets.ToDictionary(target => GetIdValue(target.Element.Id));
      Dictionary<TagTargetKind, List<TargetElement>> activeTargetsByKind = GroupByKind(activeTargets);
      HashSet<long> correctlyTagged = GetCorrectlyTaggedTargetIds(document, context, activeTargetsById, activeTargetsByKind);

      int created = 0;
      int skippedExisting = 0;
      int failed = 0;
      var createdIds = new List<ElementId>();

      using var transaction = new Transaction(document, "Tag active view targets");
      transaction.Start();
      foreach (TargetElement target in activeTargets)
      {
        long targetId = GetIdValue(target.Element.Id);
        if (correctlyTagged.Contains(targetId))
        {
          skippedExisting++;
          continue;
        }

        try
        {
          IndependentTag tag = CreateTag(document, context, target);
          created++;
          createdIds.Add(tag.Id);
          result.Items.Add(new TagsToolResultItem(TagsToolLogLevel.Success, tag.Id.ToString(), $"Created {target.Kind} tag for host {target.Element.Id}."));
        }
        catch (Exception ex)
        {
          failed++;
          result.Items.Add(new TagsToolResultItem(TagsToolLogLevel.Error, target.Element.Id.ToString(), $"Could not tag {target.Kind}: {ex.Message}"));
        }
      }
      transaction.Commit();

      uidoc.Selection.SetElementIds(createdIds);
      result.Summary = $"Active View: candidates {activeTargets.Count}; created {created}; skipped existing {skippedExisting}; failed {failed}.";
      return result;
    }

    private static IndependentTag CreateTag(Document document, PlanViewContext context, TargetElement target)
    {
      XYZ point = GetTagPoint(target.Element, context);
      var reference = new Reference(target.Element);
      IndependentTag tag = IndependentTag.Create(
        document,
        context.View.Id,
        reference,
        false,
        TagMode.TM_ADDBY_CATEGORY,
        TagOrientation.Horizontal,
        point);
      try
      {
        tag.HasLeader = false;
      }
      catch
      {
        // Some tag behaviors do not allow changing leader visibility after creation.
      }
      return tag;
    }

    private static HashSet<long> GetCorrectlyTaggedTargetIds(
      Document document,
      PlanViewContext context,
      IReadOnlyDictionary<long, TargetElement> activeTargetsById,
      IReadOnlyDictionary<TagTargetKind, List<TargetElement>> activeTargetsByKind)
    {
      var ids = new HashSet<long>();
      List<IndependentTag> tags = new FilteredElementCollector(document, context.View.Id)
        .OfClass(typeof(IndependentTag))
        .WhereElementIsNotElementType()
        .Cast<IndependentTag>()
        .ToList();

      foreach (IndependentTag tag in tags)
      {
        TagCheckResult check = CheckTag(document, context, tag, activeTargetsById, activeTargetsByKind);
        if (check.Status != TagCheckStatus.Ok || check.HostId == null)
        {
          continue;
        }
        ids.Add(GetIdValue(check.HostId));
      }
      return ids;
    }

    private static TagCheckResult CheckTag(
      Document document,
      PlanViewContext context,
      IndependentTag tag,
      IReadOnlyDictionary<long, TargetElement> activeTargetsById,
      IReadOnlyDictionary<TagTargetKind, List<TargetElement>> activeTargetsByKind)
    {
      List<ElementId> localIds = GetDistinctElementIds(tag.GetTaggedLocalElementIds());
      if (localIds.Count == 0)
      {
        return IsLikelyTargetTag(tag)
          ? TagCheckResult.Wrong("Sai host: tag has no local host.")
          : TagCheckResult.Ignored();
      }

      var supportedHosts = new List<TargetElement>();
      foreach (ElementId id in localIds)
      {
        Element? element = document.GetElement(id);
        TagTargetKind kind = GetTargetKind(element);
        if (element != null && kind != TagTargetKind.Unknown)
        {
          supportedHosts.Add(new TargetElement(element, kind));
        }
      }

      if (supportedHosts.Count == 0)
      {
        return TagCheckResult.Ignored();
      }
      if (supportedHosts.Count != 1)
      {
        return TagCheckResult.Wrong("Sai host: tag references more than one column/floor/wall host.");
      }

      TargetElement host = supportedHosts[0];
      if ((host.Kind == TagTargetKind.Column || host.Kind == TagTargetKind.Wall) && !IsCutByPlane(host.Element, context.CutElevation))
      {
        return TagCheckResult.Wrong($"Sai level host: {host.Kind} {host.Element.Id} is not cut by the active view cut plane.", host.Element.Id);
      }
      if (host.Kind == TagTargetKind.Floor && !activeTargetsById.ContainsKey(GetIdValue(host.Element.Id)))
      {
        return TagCheckResult.Wrong($"Sai host: Floor {host.Element.Id} is not visible in the active view.", host.Element.Id);
      }

      List<XYZ> checkPoints = GetTagCheckPoints(tag);
      foreach (XYZ point in checkPoints)
      {
        bool hostContainsPoint = IsPointOnElementFootprint(host.Element, point);
        if (hostContainsPoint)
        {
          continue;
        }

        TargetElement? other = FindContainingTarget(activeTargetsByKind, host.Kind, point, host.Element.Id);
        if (other != null)
        {
          return TagCheckResult.Wrong($"Sai host: tag point is on {other.Kind} {other.Element.Id}, not host {host.Element.Id}.", host.Element.Id);
        }

        return TagCheckResult.Wrong($"Sai vi tri host: tag point is outside {host.Kind} {host.Element.Id}.", host.Element.Id);
      }

      return TagCheckResult.Ok(host.Element.Id);
    }

    private static void ApplyErrorOverrides(Document document, RevitView view, ICollection<ElementId> wrongTagIds, TagsToolResult result)
    {
      if (!view.AreGraphicsOverridesAllowed())
      {
        result.Items.Add(new TagsToolResultItem(TagsToolLogLevel.Warning, string.Empty, "Active view does not allow graphic overrides; wrong tags were selected only."));
        return;
      }

      using var transaction = new Transaction(document, "Override wrong tags");
      transaction.Start();
      foreach (ElementId tagId in wrongTagIds)
      {
        OverrideGraphicSettings settings = view.GetElementOverrides(tagId);
        settings.SetProjectionLineColor(ErrorColor);
        settings.SetProjectionLineWeight(8);
        view.SetElementOverrides(tagId, settings);
      }
      transaction.Commit();
    }

    private static bool TryGetPlanContext(Document document, RevitView activeView, out PlanViewContext? context, out string error)
    {
      context = null;
      error = string.Empty;

      if (activeView is not ViewPlan viewPlan)
      {
        error = "Active view must be a plan view.";
        return false;
      }

      try
      {
        using PlanViewRange range = viewPlan.GetViewRange();
        if (!TryGetPlaneElevation(document, viewPlan, range, PlanViewPlane.CutPlane, out double cutElevation))
        {
          error = "Could not resolve active view cut plane elevation.";
          return false;
        }
        context = new PlanViewContext(viewPlan, cutElevation);
        return true;
      }
      catch (Exception ex)
      {
        error = $"Could not read active view range: {ex.Message}";
        return false;
      }
    }

    private static bool TryGetPlaneElevation(Document document, ViewPlan viewPlan, PlanViewRange range, PlanViewPlane plane, out double elevation)
    {
      elevation = 0;
      ElementId levelId = range.GetLevelId(plane);
      Level? level = document.GetElement(levelId) as Level ?? viewPlan.GenLevel;
      if (level == null)
      {
        return false;
      }

      elevation = level.Elevation + range.GetOffset(plane);
      return true;
    }

    private static List<TargetElement> GetActiveTargets(Document document, PlanViewContext context)
    {
      var targets = new List<TargetElement>();
      IEnumerable<Element> elements = new FilteredElementCollector(document, context.View.Id)
        .WhereElementIsNotElementType()
        .ToElements();

      foreach (Element element in elements)
      {
        TagTargetKind kind = GetTargetKind(element);
        if (kind == TagTargetKind.Unknown)
        {
          continue;
        }
        if ((kind == TagTargetKind.Column || kind == TagTargetKind.Wall) && !IsCutByPlane(element, context.CutElevation))
        {
          continue;
        }
        targets.Add(new TargetElement(element, kind));
      }
      return targets;
    }

    private static Dictionary<TagTargetKind, List<TargetElement>> GroupByKind(IEnumerable<TargetElement> targets)
    {
      return targets
        .GroupBy(target => target.Kind)
        .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static TagTargetKind GetTargetKind(Element? element)
    {
      if (element?.Category == null)
      {
        return TagTargetKind.Unknown;
      }

      long categoryId = GetIdValue(element.Category.Id);
      if (categoryId == (long)BuiltInCategory.OST_StructuralColumns || categoryId == (long)BuiltInCategory.OST_Columns)
      {
        return TagTargetKind.Column;
      }
      if (categoryId == (long)BuiltInCategory.OST_Floors)
      {
        return TagTargetKind.Floor;
      }
      if (categoryId == (long)BuiltInCategory.OST_Walls)
      {
        return TagTargetKind.Wall;
      }
      return TagTargetKind.Unknown;
    }

    private static bool IsLikelyTargetTag(IndependentTag tag)
    {
      string categoryName = tag.Category?.Name ?? string.Empty;
      return categoryName.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0
        || categoryName.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0
        || categoryName.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCutByPlane(Element element, double cutElevation)
    {
      BoundingBoxXYZ? box = GetBoundingBox(element);
      return box != null && box.Min.Z <= cutElevation + Tolerance && box.Max.Z + Tolerance >= cutElevation;
    }

    private static List<XYZ> GetTagCheckPoints(IndependentTag tag)
    {
      var points = new List<XYZ> { tag.TagHeadPosition };
      try
      {
        if (!tag.HasLeader)
        {
          return points;
        }

        foreach (Reference reference in tag.GetTaggedReferences())
        {
          try
          {
            XYZ leaderEnd = tag.GetLeaderEnd(reference);
            points.Add(leaderEnd);
          }
          catch
          {
            // Some leader modes do not expose a leader end point.
          }
        }
      }
      catch
      {
        // Tags without tag behavior can throw on leader APIs.
      }
      return points;
    }

    private static TargetElement? FindContainingTarget(
      IReadOnlyDictionary<TagTargetKind, List<TargetElement>> activeTargetsByKind,
      TagTargetKind kind,
      XYZ point,
      ElementId excludedId)
    {
      if (!activeTargetsByKind.TryGetValue(kind, out List<TargetElement>? targets))
      {
        return null;
      }

      long excluded = GetIdValue(excludedId);
      return targets.FirstOrDefault(target => GetIdValue(target.Element.Id) != excluded && IsPointOnElementFootprint(target.Element, point));
    }

    private static bool IsPointOnElementFootprint(Element element, XYZ point)
    {
      BoundingBoxXYZ? box = GetBoundingBox(element);
      if (box == null || !IsPointInsideBoxXY(point, box))
      {
        return false;
      }

      try
      {
        XYZ start = new(point.X, point.Y, box.Min.Z - 1);
        XYZ end = new(point.X, point.Y, box.Max.Z + 1);
        if (start.DistanceTo(end) < Tolerance)
        {
          return true;
        }

        Line line = Line.CreateBound(start, end);
        using var options = new SolidCurveIntersectionOptions();
        bool testedSolid = false;
        foreach (Solid solid in GetSolids(element))
        {
          if (solid.Volume <= 1e-9)
          {
            continue;
          }

          testedSolid = true;
          SolidCurveIntersection intersection = solid.IntersectWithCurve(line, options);
          if (intersection.SegmentCount > 0)
          {
            return true;
          }
        }
        return !testedSolid;
      }
      catch
      {
        return true;
      }
    }

    private static IEnumerable<Solid> GetSolids(Element element)
    {
      var options = new Options
      {
        ComputeReferences = false,
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Fine
      };
      GeometryElement? geometry = element.get_Geometry(options);
      return geometry == null ? Enumerable.Empty<Solid>() : GetSolids(geometry);
    }

    private static IEnumerable<Solid> GetSolids(GeometryElement geometry)
    {
      foreach (GeometryObject geometryObject in geometry)
      {
        if (geometryObject is Solid solid)
        {
          yield return solid;
        }
        else if (geometryObject is GeometryInstance instance)
        {
          GeometryElement instanceGeometry = instance.GetInstanceGeometry();
          foreach (Solid instanceSolid in GetSolids(instanceGeometry))
          {
            yield return instanceSolid;
          }
        }
      }
    }

    private static bool IsPointInsideBoxXY(XYZ point, BoundingBoxXYZ box)
    {
      return point.X >= box.Min.X - Tolerance && point.X <= box.Max.X + Tolerance
        && point.Y >= box.Min.Y - Tolerance && point.Y <= box.Max.Y + Tolerance;
    }

    private static XYZ GetTagPoint(Element element, PlanViewContext context)
    {
      BoundingBoxXYZ? box = GetBoundingBox(element);
      if (box == null)
      {
        LocationPoint? locationPoint = element.Location as LocationPoint;
        if (locationPoint != null)
        {
          return new XYZ(locationPoint.Point.X, locationPoint.Point.Y, context.CutElevation);
        }

        LocationCurve? locationCurve = element.Location as LocationCurve;
        if (locationCurve != null)
        {
          XYZ midpoint = locationCurve.Curve.Evaluate(0.5, true);
          return new XYZ(midpoint.X, midpoint.Y, context.CutElevation);
        }

        return XYZ.Zero;
      }

      return new XYZ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, context.CutElevation);
    }

    private static BoundingBoxXYZ? GetBoundingBox(Element element)
    {
      return element.get_BoundingBox(null);
    }

    private static List<ElementId> GetDistinctElementIds(IEnumerable<ElementId> ids)
    {
      return ids
        .GroupBy(GetIdValue)
        .Select(group => group.First())
        .ToList();
    }

    private static long GetIdValue(ElementId id)
    {
#if D2024 || D2025 || D2026 || D2027 || R2024 || R2025 || R2026 || R2027
      return id.Value;
#else
      return id.IntegerValue;
#endif
    }

    private sealed class PlanViewContext
    {
      public PlanViewContext(ViewPlan view, double cutElevation)
      {
        View = view;
        CutElevation = cutElevation;
      }

      public ViewPlan View { get; }
      public double CutElevation { get; }
    }

    private sealed class TargetElement
    {
      public TargetElement(Element element, TagTargetKind kind)
      {
        Element = element;
        Kind = kind;
      }

      public Element Element { get; }
      public TagTargetKind Kind { get; }
    }

    private sealed class TagCheckResult
    {
      private TagCheckResult(TagCheckStatus status, string message, ElementId? hostId)
      {
        Status = status;
        Message = message;
        HostId = hostId;
      }

      public TagCheckStatus Status { get; }
      public string Message { get; }
      public ElementId? HostId { get; }

      public static TagCheckResult Ok(ElementId hostId) => new(TagCheckStatus.Ok, string.Empty, hostId);
      public static TagCheckResult Wrong(string message, ElementId? hostId = null) => new(TagCheckStatus.Wrong, message, hostId);
      public static TagCheckResult Ignored() => new(TagCheckStatus.Ignored, string.Empty, null);
    }

    private enum TagCheckStatus
    {
      Ok,
      Wrong,
      Ignored
    }
  }
}

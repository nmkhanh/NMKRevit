using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.IO;
using System.Text;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;
using View = Autodesk.Revit.DB.View;

namespace NMKRebar.Services
{
  public sealed class RebarHostSelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return elem != null && RebarHostData.GetRebarHostData(elem) != null;
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }

  public sealed class SetTypeCreateRebarRequest
  {
    public string Folder { get; init; } = string.Empty;

    public string VariesLengthParameter { get; init; } = "1_L";

    public bool UseFreeForm { get; init; }

    public bool AllFilteredTypes { get; init; }

    public IReadOnlyList<string> FilteredTypeNames { get; init; } = Array.Empty<string>();

    public string? SelectedTypeName { get; init; }

    public long HostElementId { get; init; }

    public int VariesLineIndex { get; init; } = 1;

    public bool VariesMiddle { get; init; }

    public bool SubtractBending { get; init; } = true;

    public double BendingFactor { get; init; } = 3;

    public bool RevertVaries { get; init; }
  }

  public sealed class CreateRebarByLineResult
  {
    public int Created { get; set; }

    public int Failed { get; set; }

    public int GroupsCreated { get; set; }

    public List<ElementId> CreatedIds { get; } = new();

    public List<string> Warnings { get; } = new();

    public string? LogPath { get; set; }

    public bool IsModelLineCheck { get; set; }

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine(IsModelLineCheck ? $"Model lines created: {Created}" : $"Rebar created: {Created}");
      text.AppendLine($"Failed: {Failed}");
      if (!string.IsNullOrWhiteSpace(LogPath))
      {
        text.AppendLine($"Log: {LogPath}");
      }
      if (GroupsCreated > 0)
      {
        text.AppendLine($"Groups created: {GroupsCreated}");
      }
      if (Warnings.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("Warnings:");
        foreach (string warning in Warnings.Take(IsModelLineCheck ? 80 : 20))
        {
          text.AppendLine("- " + warning);
        }
      }

      return text.ToString();
    }
  }

  public static class CreateRebarByLineService
  {
    public const string ShapeFamilyName = "NMK_Rebar_Shape";
    public const string RebarTypeParameterName = "Rebar Type";
    public const string PathLogDirectory = @"D:\MCP\NMKRevit\NMKRebar\logs";
    public const string PathLogLatestFileName = "create-path-latest.txt";
    private const double PointTolerance = 1.0 / 304.8;
    private const double JoinGapTolerance = 20.0 / 304.8;

    public static bool IsRebarArrayInstance(Element elem)
    {
      return elem is FamilyInstance instance
        && instance.Symbol?.Family?.Name.Equals(RebarTypeCreateService.ArrayFamilyName, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool IsRebarShapeInstance(Element elem)
    {
      return elem is FamilyInstance instance
        && instance.Symbol?.Family?.Name.Equals(ShapeFamilyName, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static CreateRebarByLineResult Create(UIDocument uidoc, bool useFreeForm = false)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("NMKCreateRebarByLine runs in a project document.");
      }

      Reference hostRef = uidoc.Selection.PickObject(ObjectType.Element, new RebarHostSelectionFilter(), "Pick rebar host");
      Element host = doc.GetElement(hostRef)
        ?? throw new InvalidOperationException("Host was not found.");
      if (RebarHostData.GetRebarHostData(host) == null)
      {
        throw new InvalidOperationException("The picked element cannot host rebar.");
      }

      IList<Reference> picked = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new NmkRebarArraySelectionFilter(),
        "Select NMK_Rebar_Array instances");
      List<FamilyInstance> arrays = picked
        .Select(reference => doc.GetElement(reference))
        .OfType<FamilyInstance>()
        .Where(IsRebarArrayInstance)
        .ToList();

      List<FamilyInstance> instances = arrays
        .SelectMany(array => array.GetSubComponentIds())
        .Select(id => doc.GetElement(id))
        .OfType<FamilyInstance>()
        .Where(IsRebarShapeInstance)
        .GroupBy(instance => IdValue(instance.Id))
        .Select(group => group.First())
        .ToList();

      if (instances.Count == 0)
      {
        throw new InvalidOperationException("The selected NMK_Rebar_Array instances have no NMK_Rebar_Shape subcomponents.");
      }

      var result = new CreateRebarByLineResult();
      var log = StartPathLog(doc, "Create");
      try
      {
        using (var tx = new Transaction(doc, "NMK Create Rebar By Line"))
        {
          tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetClearAfterRollback(true));
          tx.Start();
          foreach (FamilyInstance instance in instances)
          {
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
              // RecordCreated(result, CreatePathModelLines(doc, instance, result.Warnings, log));
              RecordCreated(result, CreateOne(doc, host, instance, useFreeForm, log));
              sub.Commit();
            }
            catch (Exception ex)
            {
              sub.RollBack();
              result.Failed++;
              result.Warnings.Add($"{instance.Id}: {DescribeError(ex)}");
              log.AppendLine($"INSTANCE FAIL {instance.Id}: {DescribeError(ex)}");
            }
          }

          if (result.CreatedIds.Count > 0)
          {
            result.GroupsCreated = GroupRebarByTypeService.GroupByType(doc, result.CreatedIds, result.Warnings);
          }

          if (result.Created == 0)
          {
            tx.RollBack();
          }
          else
          {
            tx.Commit();
          }
        }
      }
      finally
      {
        result.LogPath = FinishPathLog(log);
        result.Warnings.Insert(0, $"Log: {result.LogPath}");
      }

      /*
      var createdIds = new List<ElementId>();
      using (var tx = new Transaction(doc, "NMK Create Path DirectShape"))
      {
        tx.Start();
        TryUnhideCategory(doc, BuiltInCategory.OST_GenericModel);
        foreach (FamilyInstance instance in instances)
        {
          try
          {
            createdIds.Add(CreatePathShape(doc, instance));
            result.Created++;
          }
          catch (Exception ex)
          {
            result.Failed++;
            string detail = ex.InnerException == null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            result.Warnings.Add($"{instance.Id}: {detail}");
          }
        }

        tx.Commit();
      }

      if (createdIds.Count > 0)
      {
        try
        {
          uidoc.ShowElements(createdIds);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
        }
      }
      */

      return result;
    }

    public static CreateRebarByLineResult CreatePathModelLinesCheck(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Check Path runs in a project document.");
      }

      List<FamilyInstance> arrays = uidoc.Selection.GetElementIds()
        .Select(doc.GetElement)
        .OfType<FamilyInstance>()
        .Where(IsRebarArrayInstance)
        .GroupBy(instance => IdValue(instance.Id))
        .Select(group => group.First())
        .ToList();
      if (arrays.Count == 0)
      {
        IList<Reference> picked = uidoc.Selection.PickObjects(
          ObjectType.Element,
          new NmkRebarArraySelectionFilter(),
          "Select NMK_Rebar_Array instances");
        arrays = picked
          .Select(reference => doc.GetElement(reference))
          .OfType<FamilyInstance>()
          .Where(IsRebarArrayInstance)
          .GroupBy(instance => IdValue(instance.Id))
          .Select(group => group.First())
          .ToList();
      }

      List<FamilyInstance> instances = CollectShapeInstances(arrays);
      if (instances.Count == 0)
      {
        throw new InvalidOperationException("The selected NMK_Rebar_Array instances have no NMK_Rebar_Shape subcomponents.");
      }

      var result = new CreateRebarByLineResult { IsModelLineCheck = true };
      var log = StartPathLog(doc, "CheckPathModelLines");
      try
      {
        using (var tx = new Transaction(doc, "NMK Path Model Lines"))
        {
          tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetClearAfterRollback(true));
          tx.Start();
          foreach (FamilyInstance instance in instances)
          {
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
              RecordCreated(result, CreatePathModelLines(doc, instance, result.Warnings, log));
              sub.Commit();
            }
            catch (Exception ex)
            {
              sub.RollBack();
              result.Failed++;
              result.Warnings.Add($"{instance.Id}: {DescribeError(ex)}");
              log.AppendLine($"INSTANCE FAIL {instance.Id}: {DescribeError(ex)}");
            }
          }

          if (result.Created == 0)
          {
            tx.RollBack();
          }
          else
          {
            tx.Commit();
          }
        }

        if (result.CreatedIds.Count > 0)
        {
          try
          {
            uidoc.ShowElements(result.CreatedIds);
            uidoc.Selection.SetElementIds(result.CreatedIds);
          }
          catch (Autodesk.Revit.Exceptions.ArgumentException)
          {
          }
          catch (Autodesk.Revit.Exceptions.InvalidOperationException)
          {
          }
        }
      }
      finally
      {
        result.LogPath = FinishPathLog(log);
        result.Warnings.Insert(0, $"Log: {result.LogPath}");
      }

      return result;
    }

    public static CreateRebarByLineResult CreateFromSelection(
      UIDocument uidoc,
      string typeName,
      string folder,
      string variesLengthParameter,
      bool useFreeForm)
    {
      return CreateForSetType(uidoc, new SetTypeCreateRebarRequest
      {
        Folder = folder,
        VariesLengthParameter = variesLengthParameter,
        UseFreeForm = useFreeForm,
        AllFilteredTypes = false,
        SelectedTypeName = typeName,
        FilteredTypeNames = Array.Empty<string>(),
        HostElementId = 0
      });
    }

    public static CreateRebarByLineResult CreateForSetType(UIDocument uidoc, SetTypeCreateRebarRequest request)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("NMKCreateRebarByLine runs in a project document.");
      }

      List<string> typeNames = ResolveTypeNames(request);
      if (typeNames.Count == 0)
      {
        throw new InvalidOperationException("Select a type in the list first.");
      }

      Element host = ResolveRebarHost(uidoc, request.HostElementId);
      Dictionary<string, List<FamilyInstance>> arraysByType = CollectArraysByType(doc, uidoc, typeNames, request.AllFilteredTypes);
      if (arraysByType.Values.Sum(list => list.Count) == 0)
      {
        throw new InvalidOperationException(request.AllFilteredTypes
          ? "No NMK_Rebar_Array instances found in the project for the filtered type(s)."
          : "SELECT NMK_Rebar_Array instances of the selected type first.");
      }

      var result = new CreateRebarByLineResult();
      var log = StartPathLog(doc, "CreateForSetType");
      try
      {
        using (var tx = new Transaction(doc, "NMK Create Rebar By Line"))
        {
          tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetClearAfterRollback(true));
          tx.Start();
          foreach (string typeName in typeNames)
          {
            if (!arraysByType.TryGetValue(typeName, out List<FamilyInstance>? arrays) || arrays.Count == 0)
            {
              continue;
            }

            List<FamilyInstance> instances = CollectShapeInstances(arrays);
            if (instances.Count == 0)
            {
              result.Warnings.Add($"{typeName}: no NMK_Rebar_Shape subcomponents on {arrays.Count} array(s).");
              log.AppendLine($"{typeName}: no NMK_Rebar_Shape subcomponents on {arrays.Count} array(s).");
              continue;
            }

            RebarBarType barType = FindRebarBarType(doc, typeName);
            bool hasVaries = SetRebarVariesService.FindVariesFile(request.Folder, typeName) != null;
            log.AppendLine($"TYPE {typeName} arrays={arrays.Count} shapes={instances.Count} varies={hasVaries} all={request.AllFilteredTypes}");
            double diameterMm = hasVaries ? BarDiameterMm(barType, typeName) : 0;
            if (hasVaries)
            {
              IReadOnlyList<double> variesValues = SetRebarVariesService.LoadSortedValues(request.Folder, typeName);
              if (variesValues.Count == 0)
              {
                result.Warnings.Add($"{typeName}: Varries file has no numeric values.");
                log.AppendLine($"{typeName}: varies values empty");
                continue;
              }

              if (request.RevertVaries)
              {
                variesValues = variesValues.Reverse().ToList();
              }

              log.AppendLine($"VARIES arrays={arrays.Count} values={variesValues.Count} revert={request.RevertVaries} all={request.AllFilteredTypes}");
              foreach (FamilyInstance array in arrays)
              {
                List<FamilyInstance> shapes = CollectShapeInstances(new[] { array });
                if (shapes.Count == 0)
                {
                  result.Warnings.Add($"{array.Id}: no NMK_Rebar_Shape subcomponent.");
                  continue;
                }

                log.AppendLine($"ARRAY {IdValue(array.Id)} shapes={shapes.Count}");
                for (int i = 0; i < shapes.Count; i++)
                {
                  FamilyInstance shape = shapes[i];
                  double rawMm = i < variesValues.Count ? variesValues[i] : variesValues[0];
                  using var sub = new SubTransaction(doc);
                  sub.Start();
                  try
                  {
                    CreateVaries(
                      doc,
                      host,
                      shape,
                      barType,
                      request.VariesLineIndex,
                      request.VariesMiddle,
                      rawMm,
                      request.SubtractBending,
                      request.BendingFactor,
                      diameterMm,
                      request.UseFreeForm,
                      result,
                      log);
                    sub.Commit();
                  }
                  catch (Exception ex)
                  {
                    sub.RollBack();
                    result.Failed++;
                    result.Warnings.Add($"{shape.Id}: {DescribeError(ex)}");
                    log.AppendLine($"SHAPE FAIL {IdValue(shape.Id)}: {DescribeError(ex)}");
                  }
                }
              }

              continue;
            }

            foreach (FamilyInstance instance in instances)
            {
              using var sub = new SubTransaction(doc);
              sub.Start();
              try
              {
                RecordCreated(result, CreateOne(doc, host, instance, barType, request.UseFreeForm, log));

                sub.Commit();
              }
              catch (Exception ex)
              {
                sub.RollBack();
                result.Failed++;
                result.Warnings.Add($"{instance.Id}: {DescribeError(ex)}");
                log.AppendLine($"INSTANCE FAIL {instance.Id}: {DescribeError(ex)}");
              }
            }
          }

          if (result.CreatedIds.Count > 0)
          {
            result.GroupsCreated = GroupRebarByTypeService.GroupByType(doc, result.CreatedIds, result.Warnings);
          }

          if (result.Created == 0)
          {
            tx.RollBack();
          }
          else
          {
            tx.Commit();
          }
        }
      }
      finally
      {
        result.LogPath = FinishPathLog(log);
        result.Warnings.Insert(0, $"Log: {result.LogPath}");
      }

      if (result.Created == 0 && result.Failed == 0 && result.Warnings.Count == 0)
      {
        throw new InvalidOperationException("Nothing was created.");
      }

      return result;
    }

    public static Element PickRebarHost(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      Reference hostRef = uidoc.Selection.PickObject(ObjectType.Element, new RebarHostSelectionFilter(), "Pick rebar host");
      Element host = doc.GetElement(hostRef)
        ?? throw new InvalidOperationException("Host was not found.");
      if (RebarHostData.GetRebarHostData(host) == null)
      {
        throw new InvalidOperationException("The picked element cannot host rebar.");
      }

      return host;
    }

    public static string FormatHostDisplayName(Element? host)
    {
      if (host == null)
      {
        return "(no host)";
      }

      string name = host.Name?.Trim() ?? string.Empty;
      if (name.Length > 0)
      {
        return name;
      }

      string category = host.Category?.Name ?? host.GetType().Name;
      return $"{category} ({IdValue(host.Id)})";
    }

    public static long ParseHostElementId(string? raw)
    {
      if (string.IsNullOrWhiteSpace(raw))
      {
        return 0;
      }

      return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long id)
        ? id
        : 0;
    }

    private static List<string> ResolveTypeNames(SetTypeCreateRebarRequest request)
    {
      if (request.AllFilteredTypes)
      {
        return request.FilteredTypeNames
          .Where(name => !string.IsNullOrWhiteSpace(name))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToList();
      }

      if (string.IsNullOrWhiteSpace(request.SelectedTypeName))
      {
        return new List<string>();
      }

      return new List<string> { request.SelectedTypeName! };
    }

    private static Dictionary<string, List<FamilyInstance>> CollectArraysByType(
      Document doc,
      UIDocument uidoc,
      IReadOnlyList<string> typeNames,
      bool allInDocument)
    {
      var typeSet = new HashSet<string>(typeNames, StringComparer.OrdinalIgnoreCase);
      var result = typeNames.ToDictionary(name => name, _ => new List<FamilyInstance>(), StringComparer.OrdinalIgnoreCase);

      IEnumerable<FamilyInstance> candidates;
      if (allInDocument)
      {
        candidates = new FilteredElementCollector(doc)
          .OfClass(typeof(FamilyInstance))
          .Cast<FamilyInstance>()
          .Where(IsRebarArrayInstance);
      }
      else
      {
        candidates = uidoc.Selection.GetElementIds()
          .Select(doc.GetElement)
          .OfType<FamilyInstance>()
          .Where(IsRebarArrayInstance);
      }

      foreach (FamilyInstance instance in candidates)
      {
        string? symbolName = instance.Symbol?.Name;
        if (string.IsNullOrWhiteSpace(symbolName) || !typeSet.Contains(symbolName!))
        {
          continue;
        }

        string key = typeNames.First(name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
        List<FamilyInstance> list = result[key];
        if (!list.Any(item => IdValue(item.Id) == IdValue(instance.Id)))
        {
          list.Add(instance);
        }
      }

      return result;
    }

    private static RebarBarType FindRebarBarType(Document doc, string typeName)
    {
      return new FilteredElementCollector(doc)
        .OfClass(typeof(RebarBarType))
        .Cast<RebarBarType>()
        .FirstOrDefault(type => type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"RebarBarType '{typeName}' was not found.");
    }

    private static Element ResolveRebarHost(UIDocument uidoc, long hostElementId)
    {
      Document doc = uidoc.Document;
      if (hostElementId != 0)
      {
        ElementId id = ToElementId(hostElementId);
        if (doc.GetElement(id) is Element existing && RebarHostData.GetRebarHostData(existing) != null)
        {
          return existing;
        }
      }

      return PickRebarHost(uidoc);
    }

    public static ElementId ToElementId(long value)
    {
#if NETFRAMEWORK
      return new ElementId((int)value);
#else
      return new ElementId(value);
#endif
    }

    public static List<FamilyInstance> CollectShapeInstances(IEnumerable<FamilyInstance> arrays)
    {
      var instances = new List<FamilyInstance>();
      var seen = new HashSet<long>();
      foreach (FamilyInstance array in arrays)
      {
        Document doc = array.Document;
        foreach (ElementId id in array.GetSubComponentIds())
        {
          if (doc.GetElement(id) is FamilyInstance nested
              && IsRebarShapeInstance(nested)
              && seen.Add(IdValue(nested.Id)))
          {
            instances.Add(nested);
          }
        }
      }

      return instances;
    }

    private static void CreateVaries(
      Document doc,
      Element host,
      FamilyInstance instance,
      RebarBarType barType,
      int lineIndex,
      bool middle,
      double variesMm,
      bool subtractBending,
      double bendingFactor,
      double diameterMm,
      bool useFreeForm,
      CreateRebarByLineResult result,
      StringBuilder? log)
    {
      IList<Curve> extracted = ExtractCurves(instance);
      IList<Curve> basePath = WeldEndpoints(SortIntoClockwiseCurveLoop(extracted, instance, out _));
      int segmentIndex = IndexOfNthLine(basePath, lineIndex);
      if (segmentIndex < 0)
      {
        throw new InvalidOperationException($"Line index {lineIndex} was not found in the path ({basePath.Count(curve => curve is Line)} line(s)).");
      }

      double actualMm = variesMm;
      if (subtractBending)
      {
        actualMm = variesMm - bendingFactor * diameterMm;
      }

      log?.AppendLine(
        $"VARIES instance={IdValue(instance.Id)} lineIndex={lineIndex} pathIndex={segmentIndex} middle={middle}"
        + $" raw={variesMm} actual={actualMm} subtract={subtractBending} factor={bendingFactor} d={diameterMm}");
      if (actualMm <= 1e-6)
      {
        throw new InvalidOperationException(
          $"Varries length {actualMm} mm is not > 0 (raw={variesMm}, factor={bendingFactor}, d={diameterMm}).");
      }

      List<Curve> path = basePath.Select(curve => curve.Clone()).ToList();
      ApplyVariesLength(path, segmentIndex, actualMm, middle);
      XYZ planeNormal = PlaneNormalForRebar(path);
      RecordCreated(result, CreateRebar(doc, host, barType, planeNormal, path, useFreeForm));
    }

    private static double BarDiameterMm(RebarBarType barType, string typeName)
    {
      double feet = barType.BarModelDiameter;
      if (feet > 1e-9)
      {
        return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
      }

      if (SetTypeEditorService.TryParseBarDiameterMm(typeName, out double mm) && mm > 0)
      {
        return mm;
      }

      throw new InvalidOperationException($"Bar diameter was not found for '{typeName}'.");
    }

    private static int IndexOfNthLine(IList<Curve> path, int lineIndex)
    {
      int n = 0;
      for (int i = 0; i < path.Count; i++)
      {
        if (path[i] is not Line)
        {
          continue;
        }

        n++;
        if (n == lineIndex)
        {
          return i;
        }
      }

      return -1;
    }

    private static void ApplyVariesLength(IList<Curve> path, int segmentIndex, double lengthMm, bool middle)
    {
      if (path[segmentIndex] is not Line line)
      {
        throw new InvalidOperationException("Varies segment must be a line.");
      }

      double length = UnitUtils.ConvertToInternalUnits(lengthMm, UnitTypeId.Millimeters);
      if (length <= 1e-6)
      {
        throw new InvalidOperationException("Varies length must be > 0.");
      }

      XYZ old0 = line.GetEndPoint(0);
      XYZ old1 = line.GetEndPoint(1);
      XYZ dir = (old1 - old0).Normalize();
      if (dir.GetLength() < PointTolerance)
      {
        dir = XYZ.BasisX;
      }

      XYZ new0;
      XYZ new1;
      if (middle)
      {
        XYZ mid = (old0 + old1) * 0.5;
        XYZ half = dir.Multiply(length * 0.5);
        new0 = mid - half;
        new1 = mid + half;
        MoveCurves(path, 0, segmentIndex, new0 - old0);
        MoveCurves(path, segmentIndex + 1, path.Count, new1 - old1);
      }
      else if (segmentIndex > 0)
      {
        new0 = old0;
        new1 = old0 + dir.Multiply(length);
        MoveCurves(path, segmentIndex + 1, path.Count, new1 - old1);
      }
      else
      {
        new1 = old1;
        new0 = old1 - dir.Multiply(length);
      }

      path[segmentIndex] = Line.CreateBound(new0, new1);
    }

    private static void MoveCurves(IList<Curve> path, int fromInclusive, int toExclusive, XYZ delta)
    {
      if (delta.GetLength() < 1e-12 || fromInclusive >= toExclusive)
      {
        return;
      }

      Transform move = Transform.CreateTranslation(delta);
      for (int i = fromInclusive; i < toExclusive; i++)
      {
        path[i] = path[i].CreateTransformed(move);
      }
    }

    private static RevitRebar CreateOne(Document doc, Element host, FamilyInstance instance, bool useFreeForm, StringBuilder? log = null)
    {
      string typeName = ReadRebarTypeName(doc, instance);
      if (string.IsNullOrWhiteSpace(typeName))
      {
        throw new InvalidOperationException($"Parameter '{RebarTypeParameterName}' is empty.");
      }

      RebarBarType barType = new FilteredElementCollector(doc)
        .OfClass(typeof(RebarBarType))
        .Cast<RebarBarType>()
        .FirstOrDefault(type => type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"RebarBarType '{typeName}' was not found.");
      return CreateOne(doc, host, instance, barType, useFreeForm, log);
    }

    private static RevitRebar CreateOne(Document doc, Element host, FamilyInstance instance, RebarBarType barType, bool useFreeForm, StringBuilder? log = null)
    {
      IList<Curve> extracted = ExtractCurves(instance);
      IList<Curve> path = WeldEndpoints(SortIntoClockwiseCurveLoop(extracted, instance, out XYZ normal));
      if (log != null)
      {
        log.AppendLine();
        log.AppendLine($"--- instance {IdValue(instance.Id)} symbol={instance.Symbol?.Name} ---");
        log.AppendLine($"EXTRACTED {extracted.Count} ORDERED {path.Count}");
        log.AppendLine($"SEQUENCE {string.Join(" ", path.Select(curve => curve is Arc ? "Arc" : "Line"))}");
        for (int i = 0; i < path.Count; i++)
        {
          double sequentialGapMm = i == 0 ? 0 : EndpointGapMm(path[i - 1].GetEndPoint(1), path[i].GetEndPoint(0));
          double sharedMm = i == 0 ? 0 : MinSharedEndMm(path[i - 1], path[i]);
          log.AppendLine($"  {(i + 1).ToString("00")} {DescribeCurve(path[i])} P1toP0={sequentialGapMm:0.#####} closestEnds={sharedMm:0.#####}");
          if (i > 0 && sharedMm > 1.0)
          {
            log.AppendLine($"    PREV {DescribeCurve(path[i - 1])}");
            log.AppendLine($"    NEXT {DescribeCurve(path[i])}");
            log.AppendLine(
              $"    ENDS prevP0-nextP0={EndpointGapMm(path[i - 1].GetEndPoint(0), path[i].GetEndPoint(0)):0.00}"
              + $" prevP0-nextP1={EndpointGapMm(path[i - 1].GetEndPoint(0), path[i].GetEndPoint(1)):0.00}"
              + $" prevP1-nextP0={EndpointGapMm(path[i - 1].GetEndPoint(1), path[i].GetEndPoint(0)):0.00}"
              + $" prevP1-nextP1={EndpointGapMm(path[i - 1].GetEndPoint(1), path[i].GetEndPoint(1)):0.00}");
          }
        }
      }

      return CreateRebar(doc, host, barType, normal, path, useFreeForm);
    }

    /*
    private static ElementId CreatePathShape(Document doc, FamilyInstance instance)
    {
      IList<Curve> curves = ExtractCurves(instance);
      if (curves.Count == 0)
      {
        throw new InvalidOperationException("No curves were found in the instance.");
      }

      curves = PrepareRebarPath(curves);
      var geometry = curves
        .Where(curve => curve != null && curve.Length >= PointTolerance)
        .Select(curve => (GeometryObject)curve.Clone())
        .ToList();
      if (geometry.Count == 0)
      {
        throw new InvalidOperationException("No lines remain to display.");
      }

      DirectShape shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
      shape.ApplicationId = "NMKRebar";
      shape.ApplicationDataId = IdValue(instance.Id).ToString();
      shape.SetShape(geometry);
      shape.SetName("NMK Rebar Path");
      return shape.Id;
    }

    private static void TryUnhideCategory(Document doc, BuiltInCategory builtInCategory)
    {
      View? view = doc.ActiveView;
      if (view == null || !view.CanCategoryBeHidden(new ElementId(builtInCategory)))
      {
        return;
      }

      try
      {
        view.SetCategoryHidden(new ElementId(builtInCategory), false);
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException)
      {
      }
    }
    */

    private static string DescribeError(Exception ex)
    {
      if (ex is Autodesk.Revit.Exceptions.InternalException)
      {
        return "Revit internal error while creating rebar (check host, bar type, and that arcs are real bends).";
      }

      return ex.InnerException == null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
    }

    private static IList<Curve> SortIntoClockwiseCurveLoop(IList<Curve> source, FamilyInstance instance, out XYZ normal)
    {
      List<Curve> unique = source
        .Where(curve =>
          curve != null
          && (curve is Line || curve is Arc)
          && curve.IsBound
          && curve.Length > 1e-6)
        .ToList();
      if (unique.Count == 0)
      {
        throw new InvalidOperationException("No lines or arcs were found in the instance.");
      }

      XYZ origin = InstanceLocation(instance);
      Curve? firstLine = LineNearest(unique, origin)
        ?? unique.FirstOrDefault(curve => curve is Line);
      if (firstLine == null)
      {
        throw new InvalidOperationException("Path must start with a line.");
      }

      List<Curve> path = WalkBySharedPoints(unique, OrientStartLine(firstLine, unique, PointTolerance), PointTolerance)
        .Select(curve => curve.Clone())
        .ToList();
      if (path.Count == 0)
      {
        throw new InvalidOperationException("Walk produced no curves.");
      }

      path = IncludeEveryExtracted(unique, path);
      path = OrientConsecutiveBySharedEnds(path);
      normal = PlaneNormalForRebar(path);
      return path;
    }

    private static XYZ InstanceLocation(FamilyInstance instance)
    {
      return instance.Location is LocationPoint location && location.Point != null
        ? location.Point
        : instance.GetTotalTransform().Origin;
    }

    private static Curve? LineNearest(IList<Curve> curves, XYZ origin)
    {
      Curve? best = null;
      double bestDistance = double.MaxValue;
      foreach (Curve curve in curves)
      {
        if (curve is not Line)
        {
          continue;
        }

        XYZ p0 = curve.GetEndPoint(0);
        XYZ p1 = curve.GetEndPoint(1);
        bool shared0 = EndTouchesOther(p0, curve, curves, PointTolerance);
        bool shared1 = EndTouchesOther(p1, curve, curves, PointTolerance);
        if (shared0 == shared1)
        {
          continue;
        }

        XYZ joint = shared0 ? p0 : p1;
        double distance = joint.DistanceTo(origin);
        if (distance < bestDistance)
        {
          bestDistance = distance;
          best = curve;
        }
      }

      return best;
    }

    private static List<Curve> WalkKeepAll(IList<Curve> all, Curve firstLine, double coincident)
    {
      List<Curve> path = WalkBySharedPoints(all, firstLine, coincident).Select(curve => curve.Clone()).ToList();
      foreach (Curve curve in all)
      {
        if (path.All(existing => !SameCurve(existing, curve)))
        {
          path.Add(curve.Clone());
        }
      }

      return path;
    }

    private static CurveLoop AppendEachWithReverse(IList<Curve> ordered)
    {
      CurveLoop? best = null;
      foreach (bool reverseFirst in new[] { false, true })
      {
        CurveLoop loop = TryFillLoop(ordered, reverseFirst);
        int count = loop.Count();
        if (count == ordered.Count)
        {
          return loop;
        }

        if (best == null || count > best.Count())
        {
          best = loop;
        }
      }

      int used = best?.Count() ?? 0;
      throw new InvalidOperationException(
        $"Could not append remaining curve(s) to CurveLoop ({ordered.Count - used} left).");
    }

    private static CurveLoop TryFillLoop(IList<Curve> ordered, bool reverseFirst)
    {
      var remaining = ordered.Select(curve => curve.Clone()).ToList();
      var loop = new CurveLoop();
      Curve first = reverseFirst ? remaining[0].CreateReversed() : remaining[0].Clone();
      remaining.RemoveAt(0);
      if (!TryAppend(loop, first))
      {
        return loop;
      }

      bool flippedSinceAdd = false;
      while (remaining.Count > 0)
      {
        if (TryAppendNearest(loop, remaining))
        {
          flippedSinceAdd = false;
          continue;
        }

        if (!flippedSinceAdd)
        {
          try
          {
            loop.Flip();
            flippedSinceAdd = true;
            continue;
          }
          catch (Autodesk.Revit.Exceptions.ApplicationException)
          {
          }
        }

        break;
      }

      return loop;
    }

    private static bool TryAppendNearest(CurveLoop loop, List<Curve> remaining)
    {
      Curve? last = null;
      foreach (Curve curve in loop)
      {
        last = curve;
      }

      if (last == null)
      {
        return false;
      }

      XYZ end = last.GetEndPoint(1);
      bool wantArc = last is Line;
      var order = Enumerable.Range(0, remaining.Count)
        .Where(i => wantArc == remaining[i] is Arc)
        .OrderBy(i => Math.Min(end.DistanceTo(remaining[i].GetEndPoint(0)), end.DistanceTo(remaining[i].GetEndPoint(1))))
        .ToList();
      foreach (int i in order)
      {
        Curve candidate = remaining[i];
        if (TryAppend(loop, candidate.Clone()) || TryAppend(loop, candidate.CreateReversed()))
        {
          remaining.RemoveAt(i);
          return true;
        }
      }

      return false;
    }

    private static bool TryAppend(CurveLoop loop, Curve curve)
    {
      try
      {
        loop.Append(curve);
        return true;
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException)
      {
        return false;
      }
    }

    private static List<ElementId> CreatePathModelLines(Document doc, FamilyInstance instance, List<string> warnings, StringBuilder log)
    {
      log.AppendLine();
      log.AppendLine($"--- instance {IdValue(instance.Id)} symbol={instance.Symbol?.Name} ---");
      IList<Curve> extracted = ExtractCurves(instance);
      log.AppendLine($"EXTRACTED {extracted.Count}");
      for (int i = 0; i < extracted.Count; i++)
      {
        log.AppendLine($"  extract[{i}] {DescribeCurve(extracted[i])}");
      }

      IList<Curve> path;
      XYZ normal;
      try
      {
        path = SortIntoClockwiseCurveLoop(extracted, instance, out normal);
      }
      catch (Exception ex)
      {
        log.AppendLine($"SORT FAIL {DescribeError(ex)}");
        throw;
      }

      log.AppendLine($"ORDERED {path.Count} extracted={extracted.Count} normal={Fmt(normal)} (unitless)");
      log.AppendLine($"SEQUENCE {string.Join(" ", path.Select(curve => curve is Arc ? "Arc" : "Line"))}");
      XYZ origin = path[0].GetEndPoint(0);
      Plane plane = Plane.CreateByNormalAndOrigin(normal, origin);
      SketchPlane sketch = SketchPlane.Create(doc, plane);
      var ids = new List<ElementId>();
      int group = 1;
      int indexInGroup = 0;
      for (int i = 0; i < path.Count; i++)
      {
        double sequentialGapMm = i == 0 ? 0 : EndpointGapMm(path[i - 1].GetEndPoint(1), path[i].GetEndPoint(0));
        double sharedMm = i == 0 ? 0 : MinSharedEndMm(path[i - 1], path[i]);
        bool continuous = i == 0 || sharedMm <= 1.0;
        if (i > 0 && !continuous)
        {
          group++;
          indexInGroup = 0;
          LogDiscontinuity(log, warnings, instance, group, path[i - 1], path[i], sequentialGapMm, sharedMm);
        }

        string styleName = $"{group:00}-{indexInGroup + 1:00}";
        TryAddPathModelCurve(
          doc,
          instance,
          sketch,
          plane,
          path[i],
          styleName,
          group - 1,
          continuous ? 0 : sequentialGapMm,
          warnings,
          log,
          ids);
        indexInGroup++;
      }

      double closeMm = EndpointGapMm(path[path.Count - 1].GetEndPoint(1), path[0].GetEndPoint(0));
      log.AppendLine($"CLOSE last->first gapMm={closeMm:0.#####} created={ids.Count} ordered={path.Count} extracted={extracted.Count}");
      if (ids.Count == 0)
      {
        throw new InvalidOperationException("Revit rejected every curve as a model line. See style warnings.");
      }

      return ids;
    }

    private static List<Curve> IncludeEveryExtracted(IList<Curve> extracted, IList<Curve> ordered)
    {
      var path = ordered.Select(curve => curve.Clone()).ToList();
      var used = new bool[extracted.Count];
      foreach (Curve curve in path)
      {
        for (int i = 0; i < extracted.Count; i++)
        {
          if (!used[i] && SameCurve(curve, extracted[i]))
          {
            used[i] = true;
            break;
          }
        }
      }

      for (int i = 0; i < extracted.Count; i++)
      {
        if (used[i])
        {
          continue;
        }

        Curve leftover = extracted[i].Clone();
        int insertAfter = -1;
        for (int p = 0; p < path.Count; p++)
        {
          if (MinSharedEndMm(path[p], leftover) <= 1.0)
          {
            insertAfter = p;
          }
        }

        if (insertAfter >= 0)
        {
          leftover = OrientToward(leftover, path[insertAfter].GetEndPoint(1));
          path.Insert(insertAfter + 1, leftover);
        }
        else
        {
          path.Add(leftover);
        }
      }

      return path;
    }

    private static List<Curve> OrientConsecutiveBySharedEnds(IList<Curve> path)
    {
      var oriented = path.Select(curve => curve.Clone()).ToList();
      for (int i = 1; i < oriented.Count; i++)
      {
        oriented[i] = OrientToward(oriented[i], oriented[i - 1].GetEndPoint(1));
      }

      return oriented;
    }

    private static double MinSharedEndMm(Curve left, Curve right)
    {
      XYZ[] a = { left.GetEndPoint(0), left.GetEndPoint(1) };
      XYZ[] b = { right.GetEndPoint(0), right.GetEndPoint(1) };
      double min = double.MaxValue;
      foreach (XYZ p in a)
      {
        foreach (XYZ q in b)
        {
          min = Math.Min(min, EndpointGapMm(p, q));
        }
      }

      return min;
    }

    private static void LogDiscontinuity(
      StringBuilder log,
      List<string> warnings,
      FamilyInstance instance,
      int newGroup,
      Curve previous,
      Curve next,
      double sequentialGapMm,
      double sharedMm)
    {
      string prevText = DescribeCurve(previous);
      string nextText = DescribeCurve(next);
      string message =
        $"{instance.Id} new group {newGroup:00}: sequential P1→P0 {sequentialGapMm:0.00} mm, closest ends {sharedMm:0.00} mm.";
      warnings.Add(message);
      log.AppendLine($"    GAP {message}");
      log.AppendLine($"    PREV {prevText}");
      log.AppendLine($"    NEXT {nextText}");
      log.AppendLine(
        $"    ENDS prevP0-nextP0={EndpointGapMm(previous.GetEndPoint(0), next.GetEndPoint(0)):0.00}"
        + $" prevP0-nextP1={EndpointGapMm(previous.GetEndPoint(0), next.GetEndPoint(1)):0.00}"
        + $" prevP1-nextP0={EndpointGapMm(previous.GetEndPoint(1), next.GetEndPoint(0)):0.00}"
        + $" prevP1-nextP1={EndpointGapMm(previous.GetEndPoint(1), next.GetEndPoint(1)):0.00}");
    }

    private static void TryAddPathModelCurve(
      Document doc,
      FamilyInstance instance,
      SketchPlane sketch,
      Plane plane,
      Curve curve,
      string styleName,
      int order,
      double gapMm,
      List<string> warnings,
      StringBuilder log,
      List<ElementId> ids)
    {
      log.AppendLine($"  style {styleName} {DescribeCurve(curve)} gapFromPrevMm={gapMm:0.#####}");
      GraphicsStyle? style;
      try
      {
        style = GetOrCreateOrderLineStyle(doc, styleName, order);
      }
      catch (Exception ex)
      {
        string message = $"{instance.Id} style {styleName}: failed to create line style ({DescribeError(ex)}).";
        warnings.Add(message);
        log.AppendLine($"    STYLE FAIL {DescribeError(ex)}");
        return;
      }

      List<ElementId> created = new();
      try
      {
        ModelCurve model = doc.Create.NewModelCurve(curve.Clone(), sketch);
        ApplyLineStyle(model, style, instance, styleName, warnings, log);
        created.Add(model.Id);
      }
      catch (Exception ex)
      {
        log.AppendLine($"    MODEL FAIL {DescribeError(ex)}; tessellate fallback");
        foreach (ElementId id in CreateModelCurvesOnPlane(doc, curve, sketch, plane))
        {
          if (doc.GetElement(id) is ModelCurve tessellated)
          {
            ApplyLineStyle(tessellated, style, instance, styleName, warnings, log);
          }

          created.Add(id);
        }

        if (created.Count == 0)
        {
          warnings.Add($"{instance.Id} style {styleName}: {DescribeError(ex)} ({(curve is Arc ? "Arc" : "Line")} L={Mm(curve.Length):0.0} mm).");
        }
      }

      ids.AddRange(created);
      if (created.Count > 0)
      {
        log.AppendLine($"    MODEL OK ids={string.Join(",", created.Select(IdValue))}");
      }
    }

    private static void ApplyLineStyle(
      ModelCurve model,
      GraphicsStyle style,
      FamilyInstance instance,
      string styleName,
      List<string> warnings,
      StringBuilder log)
    {
      try
      {
        model.LineStyle = style;
      }
      catch (Exception ex)
      {
        string message = $"{instance.Id} style {styleName}: created curve but could not set LineStyle ({DescribeError(ex)}).";
        warnings.Add(message);
        log.AppendLine($"    LINESTYLE FAIL {DescribeError(ex)}");
      }
    }

    private static StringBuilder StartPathLog(Document doc, string action)
    {
      var log = new StringBuilder();
      log.AppendLine($"=== NMK path log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
      log.AppendLine($"action={action}");
      log.AppendLine($"doc={doc.Title}");
      log.AppendLine($"path={doc.PathName}");
      log.AppendLine("units=mm (XYZ * 304.8)");
      return log;
    }

    private static string FinishPathLog(StringBuilder log)
    {
      Directory.CreateDirectory(PathLogDirectory);
      string latest = Path.Combine(PathLogDirectory, PathLogLatestFileName);
      string stamped = Path.Combine(PathLogDirectory, $"create-path-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
      string text = log.ToString();
      File.WriteAllText(latest, text);
      File.WriteAllText(stamped, text);
      return latest;
    }

    private static string DescribeCurve(Curve curve)
    {
      XYZ p0 = curve.GetEndPoint(0);
      XYZ p1 = curve.GetEndPoint(1);
      string text = $"{(curve is Arc ? "Arc" : "Line")} L={RoundMm(curve.Length):0.#####} P0={Fmt(p0)} P1={Fmt(p1)}";
      if (curve is Arc arc)
      {
        text += $" C={Fmt(arc.Center)} R={RoundMm(arc.Radius):0.#####} N={Fmt(arc.Normal)}";
      }

      return text;
    }

    private static string Fmt(XYZ p)
    {
      return $"({RoundMm(p.X):0.#####},{RoundMm(p.Y):0.#####},{RoundMm(p.Z):0.#####})";
    }

    private static double EndpointGapMm(XYZ a, XYZ b)
    {
      double dx = RoundMm(a.X) - RoundMm(b.X);
      double dy = RoundMm(a.Y) - RoundMm(b.Y);
      double dz = RoundMm(a.Z) - RoundMm(b.Z);
      return Math.Round(Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)), 5);
    }

    private static double RoundMm(double feet)
    {
      return Math.Round(feet * 304.8, 5);
    }

    private static double Mm(double feet)
    {
      return feet * 304.8;
    }

    private static GraphicsStyle GetOrCreateOrderLineStyle(Document doc, string name, int order)
    {
      Categories categories = doc.Settings.Categories;
      Category lines = categories.get_Item(BuiltInCategory.OST_Lines)
        ?? throw new InvalidOperationException("OST_Lines category was not found.");
      Category? sub = null;
      foreach (Category category in lines.SubCategories)
      {
        if (category.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
          sub = category;
          break;
        }
      }

      if (sub == null)
      {
        sub = categories.NewSubcategory(lines, name);
        try
        {
          sub.LineColor = OrderColor(order);
          sub.SetLineWeight(6, GraphicsStyleType.Projection);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
      }

      return sub.GetGraphicsStyle(GraphicsStyleType.Projection)
        ?? throw new InvalidOperationException($"Line style '{name}' has no projection graphics style.");
    }

    private static Autodesk.Revit.DB.Color OrderColor(int order)
    {
      byte[][] colors =
      {
        new byte[] { 255, 0, 0 },
        new byte[] { 255, 128, 0 },
        new byte[] { 255, 220, 0 },
        new byte[] { 0, 180, 0 },
        new byte[] { 0, 160, 255 },
        new byte[] { 0, 80, 255 },
        new byte[] { 160, 0, 255 },
        new byte[] { 255, 0, 160 }
      };
      byte[] rgb = colors[order % colors.Length];
      return new Autodesk.Revit.DB.Color(rgb[0], rgb[1], rgb[2]);
    }

    private static List<ElementId> CreateModelCurvesOnPlane(Document doc, Curve curve, SketchPlane sketch, Plane plane)
    {
      var ids = new List<ElementId>();
      try
      {
        ids.Add(doc.Create.NewModelCurve(curve.Clone(), sketch).Id);
        return ids;
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException)
      {
      }

      IList<XYZ> points = curve.Tessellate();
      for (int i = 0; i < points.Count - 1; i++)
      {
        XYZ a = ProjectPoint(points[i], plane);
        XYZ b = ProjectPoint(points[i + 1], plane);
        if (a.DistanceTo(b) <= 1e-6)
        {
          continue;
        }

        try
        {
          ids.Add(doc.Create.NewModelCurve(Line.CreateBound(a, b), sketch).Id);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
      }

      return ids;
    }

    private static XYZ ProjectPoint(XYZ point, Plane plane)
    {
      return point - plane.Normal.Multiply((point - plane.Origin).DotProduct(plane.Normal));
    }

    private static CurveLoop BuildCurveLoop(IList<Curve> path)
    {
      var loop = new CurveLoop();
      foreach (Curve curve in path)
      {
        loop.Append(curve);
      }

      return loop;
    }

    private static List<Curve> ReversePath(IList<Curve> path)
    {
      var reversed = new List<Curve>(path.Count);
      for (int i = path.Count - 1; i >= 0; i--)
      {
        reversed.Add(path[i].CreateReversed());
      }

      return reversed;
    }

    private static double SignedArea(IList<Curve> path, XYZ normal)
    {
      XYZ origin = path[0].GetEndPoint(0);
      XYZ n = normal.Normalize();
      XYZ xdir = n.CrossProduct(Math.Abs(n.DotProduct(XYZ.BasisX)) < 0.9 ? XYZ.BasisX : XYZ.BasisY).Normalize();
      XYZ ydir = n.CrossProduct(xdir).Normalize();
      var pts = new List<XYZ>();
      foreach (Curve curve in path)
      {
        IList<XYZ> tess = curve.Tessellate();
        int start = pts.Count == 0 ? 0 : 1;
        for (int i = start; i < tess.Count; i++)
        {
          pts.Add(tess[i]);
        }
      }

      double area = 0;
      for (int i = 0; i < pts.Count; i++)
      {
        XYZ a = pts[i] - origin;
        XYZ b = pts[(i + 1) % pts.Count] - origin;
        double ax = a.DotProduct(xdir);
        double ay = a.DotProduct(ydir);
        double bx = b.DotProduct(xdir);
        double by = b.DotProduct(ydir);
        area += ax * by - bx * ay;
      }

      return area * 0.5;
    }

    private static int SharedEndCount(Curve curve, IList<Curve> all, double coincident)
    {
      int count = 0;
      if (EndTouchesOther(curve.GetEndPoint(0), curve, all, coincident))
      {
        count++;
      }

      if (EndTouchesOther(curve.GetEndPoint(1), curve, all, coincident))
      {
        count++;
      }

      return count;
    }

    private static bool EndTouchesOther(XYZ point, Curve self, IList<Curve> all, double coincident)
    {
      foreach (Curve curve in all)
      {
        if (ReferenceEquals(curve, self) || SameCurve(curve, self))
        {
          continue;
        }

        if (HasEnd(curve, point, coincident))
        {
          return true;
        }
      }

      return false;
    }

    private static bool HasEnd(Curve curve, XYZ point, double coincident)
    {
      return curve.GetEndPoint(0).IsAlmostEqualTo(point, coincident)
        || curve.GetEndPoint(1).IsAlmostEqualTo(point, coincident);
    }

    private static Curve OrientStartLine(Curve line, IList<Curve> all, double coincident)
    {
      bool startShared = EndTouchesOther(line.GetEndPoint(0), line, all, coincident);
      bool endShared = EndTouchesOther(line.GetEndPoint(1), line, all, coincident);
      if (startShared && !endShared)
      {
        return line.CreateReversed();
      }

      return line.Clone();
    }

    private static IList<Curve> WalkBySharedPoints(IList<Curve> all, Curve firstLine, double coincident)
    {
      var unused = all.Where(curve => !ReferenceEquals(curve, firstLine) && !SameCurve(curve, firstLine)).ToList();
      var path = new List<Curve> { firstLine };
      XYZ? arcNormal = null;
      bool wantArc = true;
      while (unused.Count > 0)
      {
        XYZ end = path[path.Count - 1].GetEndPoint(1);
        XYZ incoming = TangentAtEnd(path[path.Count - 1], 1);
        int index = IndexOfBestJoin(unused, end, incoming, wantArc, coincident, arcNormal);
        if (index < 0)
        {
          index = IndexOfBestJoin(unused, end, incoming, !wantArc, coincident, arcNormal);
        }

        if (index < 0)
        {
          index = IndexOfNearestSharedEnd(unused, end, coincident);
        }

        if (index < 0)
        {
          break;
        }

        Curve next = OrientToward(unused[index], end);
        path.Add(next);
        unused.RemoveAt(index);
        if (next is Arc arc && arc.Normal.GetLength() > 1e-9)
        {
          arcNormal ??= arc.Normal.Normalize();
        }

        wantArc = next is Line;
      }

      return path;
    }

    private static Curve OrientToward(Curve curve, XYZ end)
    {
      if (curve.GetEndPoint(1).DistanceTo(end) < curve.GetEndPoint(0).DistanceTo(end))
      {
        return curve.CreateReversed();
      }

      return curve.Clone();
    }

    private static int IndexOfNearestSharedEnd(IList<Curve> unused, XYZ point, double coincident)
    {
      int bestIndex = -1;
      double best = double.MaxValue;
      for (int i = 0; i < unused.Count; i++)
      {
        Curve curve = unused[i];
        if (!HasEnd(curve, point, coincident))
        {
          continue;
        }

        double distance = Math.Min(curve.GetEndPoint(0).DistanceTo(point), curve.GetEndPoint(1).DistanceTo(point));
        if (distance < best)
        {
          best = distance;
          bestIndex = i;
        }
      }

      return bestIndex;
    }

    private static int IndexOfBestJoin(
      IList<Curve> unused,
      XYZ point,
      XYZ incoming,
      bool wantArc,
      double coincident,
      XYZ? arcNormal)
    {
      int bestIndex = -1;
      double bestScore = double.MaxValue;
      for (int i = 0; i < unused.Count; i++)
      {
        Curve curve = unused[i];
        if (wantArc != curve is Arc)
        {
          continue;
        }

        if (!HasEnd(curve, point, coincident))
        {
          continue;
        }

        Curve oriented = OrientToward(curve, point);
        XYZ far = oriented.GetEndPoint(1);
        bool farTouchesNext = false;
        for (int j = 0; j < unused.Count; j++)
        {
          if (j == i)
          {
            continue;
          }

          if (HasEnd(unused[j], far, coincident))
          {
            farTouchesNext = true;
            break;
          }
        }

        double score = farTouchesNext ? 0 : 1000;

        XYZ outgoing = TangentAtEnd(oriented, 0);
        score += 1.0 - Math.Max(-1.0, Math.Min(1.0, incoming.DotProduct(outgoing)));
        if (oriented is Arc arc && arcNormal != null && arc.Normal.GetLength() > 1e-9)
        {
          if (arc.Normal.Normalize().DotProduct(arcNormal) < 0)
          {
            score += 10;
          }
        }

        if (score < bestScore)
        {
          bestScore = score;
          bestIndex = i;
        }
      }

      return bestIndex;
    }

    private static IList<Curve> WeldEndpoints(IList<Curve> path)
    {
      if (path.Count == 0)
      {
        return path;
      }

      var welded = path.Select(curve => curve.Clone()).ToList();
      for (int i = 1; i < welded.Count; i++)
      {
        XYZ prevEnd = welded[i - 1].GetEndPoint(1);
        if (welded[i].GetEndPoint(1).DistanceTo(prevEnd) < welded[i].GetEndPoint(0).DistanceTo(prevEnd))
        {
          welded[i] = welded[i].CreateReversed();
        }

        XYZ nextStart = welded[i].GetEndPoint(0);
        if (nextStart.DistanceTo(prevEnd) <= 1e-12)
        {
          continue;
        }

        if (nextStart.DistanceTo(prevEnd) > JoinGapTolerance)
        {
          continue;
        }

        if (welded[i] is Arc)
        {
          welded[i - 1] = ForceLineEnd(welded[i - 1], nextStart) ?? welded[i - 1];
        }
        else
        {
          welded[i] = ForceLineStart(welded[i], prevEnd) ?? welded[i];
        }
      }

      return welded;
    }

    private static IList<Curve> SnapClosedLoop(IList<Curve> path)
    {
      if (path.Count < 2)
      {
        return path;
      }

      XYZ start = path[0].GetEndPoint(0);
      XYZ end = path[path.Count - 1].GetEndPoint(1);
      double gap = start.DistanceTo(end);
      if (gap <= 1e-12 || gap > PointTolerance)
      {
        return path;
      }

      var closed = path.ToList();
      if (closed[closed.Count - 1] is Line)
      {
        closed[closed.Count - 1] = ForceLineEnd(closed[closed.Count - 1], start) ?? closed[closed.Count - 1];
      }
      else if (closed[0] is Line)
      {
        closed[0] = ForceLineStart(closed[0], end) ?? closed[0];
      }

      return closed;
    }

    private static Curve? ForceLineStart(Curve curve, XYZ newStart)
    {
      if (curve is not Line)
      {
        return null;
      }

      XYZ end = curve.GetEndPoint(1);
      if (newStart.DistanceTo(end) <= 1e-9)
      {
        return null;
      }

      return Line.CreateBound(newStart, end);
    }

    private static Curve? ForceLineEnd(Curve curve, XYZ newEnd)
    {
      if (curve is not Line)
      {
        return null;
      }

      XYZ start = curve.GetEndPoint(0);
      if (start.DistanceTo(newEnd) <= 1e-9)
      {
        return null;
      }

      return Line.CreateBound(start, newEnd);
    }

    private static string ReadRebarTypeName(Document doc, FamilyInstance instance)
    {
      Parameter? parameter = instance.LookupParameter(RebarTypeParameterName);
      if (parameter == null)
      {
        return string.Empty;
      }

      if (parameter.StorageType == StorageType.ElementId)
      {
        Element? element = doc.GetElement(parameter.AsElementId());
        return element?.Name?.Trim() ?? string.Empty;
      }

      string text = parameter.AsString();
      if (string.IsNullOrWhiteSpace(text))
      {
        text = parameter.AsValueString();
      }

      return text?.Trim() ?? string.Empty;
    }

    private static IList<Curve> ExtractCurves(FamilyInstance instance)
    {
      var curves = new List<Curve>();
      var options = new Options
      {
        ComputeReferences = false,
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Fine
      };
      GeometryElement? geometry = instance.get_Geometry(options);
      if (geometry != null)
      {
        CollectCurves(geometry, Transform.Identity, curves);
      }

      return curves;
    }

    private static void CollectCurves(GeometryElement geometry, Transform transform, List<Curve> curves)
    {
      foreach (GeometryObject obj in geometry)
      {
        switch (obj)
        {
          case GeometryInstance gi:
            GeometryElement? symbolGeometry = gi.GetSymbolGeometry();
            if (symbolGeometry != null)
            {
              CollectCurves(symbolGeometry, transform.Multiply(gi.Transform), curves);
            }

            break;
          case Line line:
            curves.Add(line.CreateTransformed(transform));
            break;
          case Arc arc:
            curves.Add(arc.CreateTransformed(transform));
            break;
        }
      }
    }

    private static IList<Curve> PrepareRebarPath(IList<Curve> source)
    {
      List<Curve> unique = DeduplicateCurves(source.Where(c => c != null && c.IsBound && c.Length >= PointTolerance));
      if (unique.Count == 0)
      {
        throw new InvalidOperationException("No usable curves were found in the instance.");
      }

      IList<Curve> path = ChainCurves(unique);
      path = ResolveArcsByLinePairs(path);
      return path;
    }

    private static List<Curve> DeduplicateCurves(IEnumerable<Curve> source)
    {
      var unique = new List<Curve>();
      foreach (Curve curve in source)
      {
        if (unique.Any(existing => SameCurve(existing, curve)))
        {
          continue;
        }

        unique.Add(curve);
      }

      return unique;
    }

    private static bool SameCurve(Curve a, Curve b)
    {
      XYZ a0 = a.GetEndPoint(0);
      XYZ a1 = a.GetEndPoint(1);
      XYZ b0 = b.GetEndPoint(0);
      XYZ b1 = b.GetEndPoint(1);
      bool sameEnds = (a0.IsAlmostEqualTo(b0, PointTolerance) && a1.IsAlmostEqualTo(b1, PointTolerance))
        || (a0.IsAlmostEqualTo(b1, PointTolerance) && a1.IsAlmostEqualTo(b0, PointTolerance));
      return sameEnds && Math.Abs(a.Length - b.Length) < PointTolerance;
    }

    private static IList<Curve> ChainCurves(IList<Curve> source)
    {
      if (source.Count == 0)
      {
        return source;
      }

      return ChainFrom(source, source.OrderByDescending(curve => curve.Length).First());
    }

    private static IList<Curve> ChainFrom(IList<Curve> source, Curve seed)
    {
      var remaining = source.ToList();
      int seedIndex = remaining.FindIndex(curve => ReferenceEquals(curve, seed) || SameCurve(curve, seed));
      if (seedIndex < 0)
      {
        return new List<Curve> { seed.Clone() };
      }

      var path = new List<Curve> { remaining[seedIndex] };
      remaining.RemoveAt(seedIndex);
      while (remaining.Count > 0)
      {
        XYZ start = path[0].GetEndPoint(0);
        XYZ end = path[path.Count - 1].GetEndPoint(1);
        int bestIndex = -1;
        double bestDistance = JoinGapTolerance;
        Curve? bestCurve = null;
        bool prepend = false;
        for (int i = 0; i < remaining.Count; i++)
        {
          Curve candidate = remaining[i];
          XYZ c0 = candidate.GetEndPoint(0);
          XYZ c1 = candidate.GetEndPoint(1);
          TryCloser(end.DistanceTo(c0), i, candidate, prepend: false, ref bestDistance, ref bestIndex, ref bestCurve, ref prepend);
          TryCloser(end.DistanceTo(c1), i, candidate.CreateReversed(), prepend: false, ref bestDistance, ref bestIndex, ref bestCurve, ref prepend);
          TryCloser(start.DistanceTo(c1), i, candidate, prepend: true, ref bestDistance, ref bestIndex, ref bestCurve, ref prepend);
          TryCloser(start.DistanceTo(c0), i, candidate.CreateReversed(), prepend: true, ref bestDistance, ref bestIndex, ref bestCurve, ref prepend);
        }

        if (bestIndex < 0 || bestCurve == null)
        {
          break;
        }

        if (prepend)
        {
          path.Insert(0, bestCurve);
        }
        else
        {
          path.Add(bestCurve);
        }

        remaining.RemoveAt(bestIndex);
      }

      return path;
    }

    private static void TryCloser(
      double distance,
      int index,
      Curve oriented,
      bool prepend,
      ref double bestDistance,
      ref int bestIndex,
      ref Curve? bestCurve,
      ref bool bestPrepend)
    {
      if (distance > JoinGapTolerance || (bestIndex >= 0 && distance >= bestDistance))
      {
        return;
      }

      bestDistance = distance;
      bestIndex = index;
      bestCurve = oriented;
      bestPrepend = prepend;
    }

    private const double MaxCornerExtend = 2.0;
    private const double ParallelAngleDegrees = 2.0;

    private static IList<Curve> ResolveArcsByLinePairs(IList<Curve> path)
    {
      var items = path.ToList();
      var result = new List<Curve>();
      Line? current = null;
      int i = 0;

      while (i < items.Count)
      {
        if (items[i] is Line line)
        {
          ConsumeNextLine(result, ref current, (Line)line.Clone(), null);
          i++;
          continue;
        }

        if (items[i] is Arc)
        {
          int arcStart = i;
          while (i < items.Count && items[i] is Arc)
          {
            i++;
          }

          List<Arc> arcs = items
            .Skip(arcStart)
            .Take(i - arcStart)
            .Cast<Arc>()
            .Select(arc => (Arc)arc.Clone())
            .ToList();

          if (i < items.Count && items[i] is Line nextLine)
          {
            ConsumeNextLine(result, ref current, (Line)nextLine.Clone(), arcs);
            i++;
            continue;
          }

          if (current != null)
          {
            result.Add(current);
            current = null;
          }

          result.AddRange(arcs);
          continue;
        }

        if (current != null)
        {
          result.Add(current);
          current = null;
        }

        result.Add(items[i].Clone());
        i++;
      }

      if (current != null)
      {
        result.Add(current);
      }

      ResolveClosedWrap(items, result);
      return result.Count > 0 ? result : path;
    }

    private static void ConsumeNextLine(List<Curve> result, ref Line? current, Line next, List<Arc>? arcsBetween)
    {
      if (current == null)
      {
        if (arcsBetween != null && arcsBetween.Count > 0)
        {
          result.AddRange(arcsBetween);
        }

        current = next;
        return;
      }

      bool keepArc = arcsBetween != null && arcsBetween.Count > 0 && AreParallel(current, next);
      if (keepArc)
      {
        result.Add(current);
        result.AddRange(arcsBetween!);
        current = next;
        return;
      }

      if (!AreParallel(current, next) && TryJoinAtCorner(current, next, out Line first, out Line second))
      {
        result.Add(first);
        current = second;
        return;
      }

      result.Add(current);
      if (arcsBetween != null && arcsBetween.Count > 0)
      {
        result.AddRange(arcsBetween);
      }

      current = next;
    }

    private static void ResolveClosedWrap(List<Curve> original, List<Curve> result)
    {
      if (result.Count < 2 || result[0] is not Line firstLine)
      {
        return;
      }

      bool closed = original[0].GetEndPoint(0).DistanceTo(original[original.Count - 1].GetEndPoint(1)) <= JoinGapTolerance;
      if (!closed)
      {
        return;
      }

      if (result[result.Count - 1] is Arc && result[result.Count - 2] is Line lastLine)
      {
        if (AreParallel(lastLine, firstLine))
        {
          return;
        }

        if (TryJoinAtCorner(lastLine, firstLine, out Line a, out Line b))
        {
          result[result.Count - 2] = a;
          result[0] = b;
          result.RemoveAt(result.Count - 1);
        }

        return;
      }

      if (result[result.Count - 1] is Line last && !AreParallel(last, firstLine)
        && TryJoinAtCorner(last, firstLine, out Line joinedLast, out Line joinedFirst))
      {
        result[result.Count - 1] = joinedLast;
        result[0] = joinedFirst;
      }
    }

    private static bool AreParallel(Line a, Line b)
    {
      double cosine = Math.Cos(ParallelAngleDegrees * Math.PI / 180.0);
      return Math.Abs(a.Direction.DotProduct(b.Direction)) >= cosine;
    }

    private static bool TryJoinAtCorner(Line lineA, Line lineB, out Line first, out Line second)
    {
      first = lineA;
      second = lineB;
      XYZ a0 = lineA.GetEndPoint(0);
      XYZ a1 = lineA.GetEndPoint(1);
      XYZ b0 = lineB.GetEndPoint(0);
      XYZ b1 = lineB.GetEndPoint(1);
      if (!TryIntersectUnbounded(a0, a1, b0, b1, out XYZ corner)
        || corner.DistanceTo(a0) < PointTolerance
        || corner.DistanceTo(b1) < PointTolerance
        || corner.DistanceTo(a1) > MaxCornerExtend
        || corner.DistanceTo(b0) > MaxCornerExtend)
      {
        return false;
      }

      first = Line.CreateBound(a0, corner);
      second = Line.CreateBound(corner, b1);
      return first.Length >= PointTolerance && second.Length >= PointTolerance;
    }

    private static bool TryIntersectUnbounded(XYZ a0, XYZ a1, XYZ b0, XYZ b1, out XYZ corner)
    {
      corner = XYZ.Zero;
      XYZ da = a1 - a0;
      XYZ db = b1 - b0;
      if (da.GetLength() < PointTolerance || db.GetLength() < PointTolerance)
      {
        return false;
      }

      using Line ua = Line.CreateUnbound(a0, da.Normalize());
      using Line ub = Line.CreateUnbound(b0, db.Normalize());
#if NETFRAMEWORK
      SetComparisonResult result = ua.Intersect(ub, out IntersectionResultArray? hits);
      if (result != SetComparisonResult.Overlap || hits == null || hits.Size == 0)
      {
        return false;
      }

      corner = hits.get_Item(0).XYZPoint;
      return true;
#else
      using CurveIntersectResult hit = ua.Intersect(ub, CurveIntersectResultOption.Detailed);
      if (hit.Result != SetComparisonResult.Overlap)
      {
        return false;
      }

      IList<CurveOverlapPoint> overlaps = hit.GetOverlaps();
      if (overlaps == null || overlaps.Count == 0)
      {
        return false;
      }

      corner = overlaps[0].Point;
      return true;
#endif
    }

    private static XYZ PlaneNormalForRebar(IList<Curve> curves)
    {
      Arc? arc = curves.OfType<Arc>().FirstOrDefault();
      if (arc != null && arc.Normal.GetLength() > 1e-9)
      {
        return arc.Normal.Normalize();
      }

      for (int i = 0; i < curves.Count - 1; i++)
      {
        XYZ ta = TangentAtEnd(curves[i], 1);
        XYZ tb = TangentAtEnd(curves[i + 1], 0);
        XYZ cross = ta.CrossProduct(tb);
        if (cross.GetLength() > 1e-6)
        {
          return cross.Normalize();
        }
      }

      return ComputePlaneNormal(curves);
    }

    private static XYZ TangentAtEnd(Curve curve, int end)
    {
      if (curve is Line line)
      {
        return line.Direction;
      }

      return curve.ComputeDerivatives(end == 0 ? 0.0 : 1.0, true).BasisX.Normalize();
    }

    private static XYZ ComputePlaneNormal(IList<Curve> curves)
    {
      var points = new List<XYZ>();
      foreach (Curve curve in curves)
      {
        points.Add(curve.GetEndPoint(0));
        points.Add(curve.GetEndPoint(1));
        try
        {
          points.Add(curve.Evaluate(0.5, true));
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
        }
      }

      XYZ origin = points[0];
      XYZ? second = points.FirstOrDefault(p => !p.IsAlmostEqualTo(origin, PointTolerance));
      if (second == null)
      {
        return XYZ.BasisZ;
      }

      XYZ dir = (second - origin).Normalize();
      foreach (XYZ point in points)
      {
        XYZ cross = dir.CrossProduct(point - origin);
        if (cross.GetLength() > PointTolerance)
        {
          return cross.Normalize();
        }
      }

      XYZ axis = Math.Abs(dir.DotProduct(XYZ.BasisZ)) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
      return dir.CrossProduct(axis).Normalize();
    }

    private static void RecordCreated(CreateRebarByLineResult result, IList<ElementId> ids)
    {
      foreach (ElementId id in ids)
      {
        result.Created++;
        result.CreatedIds.Add(id);
      }
    }

    private static void RecordCreated(CreateRebarByLineResult result, RevitRebar rebar)
    {
      result.Created++;
      result.CreatedIds.Add(rebar.Id);
    }

    private static RevitRebar CreateRebar(
      Document document,
      Element host,
      RebarBarType barType,
      XYZ planeNormal,
      IList<Curve> curves,
      bool useFreeForm)
    {
      List<Curve> copies = curves.Select(curve => curve.Clone()).ToList();
      bool closed = copies[0].GetEndPoint(0).IsAlmostEqualTo(copies[copies.Count - 1].GetEndPoint(1), PointTolerance);
      RebarStyle[] styles = closed
        ? new[] { RebarStyle.StirrupTie, RebarStyle.Standard }
        : new[] { RebarStyle.Standard, RebarStyle.StirrupTie };

      Exception? last = null;
      if (useFreeForm)
      {
        if (TryCreateFreeForm(document, host, barType, copies, styles, out RevitRebar? freeForm, out last) && freeForm != null)
        {
          return freeForm;
        }
      }

      if (TryCreateFromCurves(document, host, barType, planeNormal, copies, styles, out RevitRebar? fromCurves, out Exception? fromCurvesError) && fromCurves != null)
      {
        return fromCurves;
      }

      last = fromCurvesError ?? last;
      if (!useFreeForm)
      {
        if (TryCreateFreeForm(document, host, barType, copies, styles, out RevitRebar? freeForm, out Exception? freeFormError) && freeForm != null)
        {
          return freeForm;
        }

        last = freeFormError ?? last;
      }

      throw last ?? new InvalidOperationException("Revit did not create the rebar.");
    }

    private static bool TryCreateFromCurves(
      Document document,
      Element host,
      RebarBarType barType,
      XYZ planeNormal,
      IList<Curve> copies,
      RebarStyle[] styles,
      out RevitRebar? rebar,
      out Exception? error)
    {
      rebar = null;
      error = null;
      XYZ[] normals = { planeNormal, planeNormal.Negate() };
      (bool UseExisting, bool CreateNew)[] flags = { (false, true), (true, true) };
      foreach (RebarStyle style in styles)
      {
        foreach (XYZ normal in normals)
        {
          foreach ((bool useExisting, bool createNew) in flags)
          {
            using var sub = new SubTransaction(document);
            sub.Start();
            try
            {
              rebar = CreateFromCurves(document, host, barType, normal, copies, style, useExisting, createNew);
              TryPinRebar(rebar);
              sub.Commit();
              TryShowUnobscured(document, rebar);
              return true;
            }
            catch (Exception ex)
            {
              sub.RollBack();
              error = ex;
            }
          }
        }
      }

      return false;
    }

    private static bool TryCreateFreeForm(
      Document document,
      Element host,
      RebarBarType barType,
      IList<Curve> copies,
      RebarStyle[] styles,
      out RevitRebar? rebar,
      out Exception? error)
    {
      rebar = null;
      error = null;
      foreach (RebarStyle style in styles)
      {
        using var sub = new SubTransaction(document);
        sub.Start();
        try
        {
          rebar = CreateFreeFormBar(document, host, barType, copies, style);
          TryPinRebar(rebar);
          sub.Commit();
          TryShowUnobscured(document, rebar);
          return true;
        }
        catch (Exception ex)
        {
          sub.RollBack();
          error = ex;
        }
      }

      return false;
    }

    private static RevitRebar CreateFromCurves(
      Document document,
      Element host,
      RebarBarType barType,
      XYZ planeNormal,
      IList<Curve> curves,
      RebarStyle style,
      bool useExistingShapeIfPossible,
      bool createNewShape)
    {
#if NETFRAMEWORK
      return RevitRebar.CreateFromCurves(
        document,
        style,
        barType,
        null,
        null,
        host,
        planeNormal,
        curves,
        RebarHookOrientation.Right,
        RebarHookOrientation.Right,
        useExistingShapeIfPossible,
        createNewShape)
        ?? throw new InvalidOperationException("Revit did not create the rebar.");
#else
      using var terminations = new BarTerminationsData(document);
      terminations.HookTypeIdAtStart = ElementId.InvalidElementId;
      terminations.HookTypeIdAtEnd = ElementId.InvalidElementId;
      terminations.CrankTypeIdAtStart = ElementId.InvalidElementId;
      terminations.CrankTypeIdAtEnd = ElementId.InvalidElementId;
      terminations.EndTreatmentTypeIdAtStart = ElementId.InvalidElementId;
      terminations.EndTreatmentTypeIdAtEnd = ElementId.InvalidElementId;
      return RevitRebar.CreateFromCurves(
        document,
        style,
        barType,
        host,
        planeNormal,
        curves,
        terminations,
        useExistingShapeIfPossible,
        createNewShape)
        ?? throw new InvalidOperationException("Revit did not create the rebar.");
#endif
    }

    private static RevitRebar CreateFreeFormBar(
      Document document,
      Element host,
      RebarBarType barType,
      IList<Curve> curves,
      RebarStyle style)
    {
      var bars = new List<IList<Curve>> { curves.ToList() };
#if NETFRAMEWORK
      RevitRebar rebar = RevitRebar.CreateFreeForm(document, barType, host, bars, out RebarFreeFormValidationResult error)
        ?? throw new InvalidOperationException($"FreeForm returned null ({error}).");
      if (error != RebarFreeFormValidationResult.Success)
      {
        throw new InvalidOperationException($"FreeForm {error}.");
      }

      return rebar;
#else
      using RebarFreeFormCreationResult created = RevitRebar.CreateFreeForm(document, barType, host, bars, style);
      if (created.Rebar == null || created.Error != RebarFreeFormValidationResult.Success)
      {
        throw new InvalidOperationException($"FreeForm {created.Error}.");
      }

      return created.Rebar;
#endif
    }

    private static void TryPinRebar(RevitRebar rebar)
    {
      try
      {
        rebar.Pinned = true;
      }
      catch (Autodesk.Revit.Exceptions.InvalidOperationException)
      {
      }
    }

    private static void TryShowUnobscured(Document document, RevitRebar rebar)
    {
      try
      {
        View? view = document.ActiveView;
        if (view != null)
        {
          rebar.SetUnobscuredInView(view, true);
        }
      }
      catch (Autodesk.Revit.Exceptions.ArgumentException)
      {
      }
      catch (Autodesk.Revit.Exceptions.InvalidOperationException)
      {
      }
    }

    public static long IdValue(ElementId id)
    {
#if NETFRAMEWORK
      return id.IntegerValue;
#else
      return id.Value;
#endif
    }
  }
}

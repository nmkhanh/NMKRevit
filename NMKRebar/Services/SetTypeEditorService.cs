using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace NMKRebar.Services
{
  public sealed class DetailLineSelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return elem is DetailLine;
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }

  public sealed class ShapeParameterValue
  {
    public ShapeParameterValue(string name, string value, bool isYesNo)
    {
      Name = name;
      Value = value;
      IsYesNo = isYesNo;
    }

    public string Name { get; }

    public string Value { get; set; }

    public bool IsYesNo { get; }
  }

  public sealed class SetTypeEditorResult
  {
    public int ValuesSet { get; set; }

    public int InstancesUpdated { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      if (InstancesUpdated > 0)
      {
        text.AppendLine($"Instances updated: {InstancesUpdated}");
      }

      text.AppendLine($"Values set: {ValuesSet}");
      if (Warnings.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("Warnings:");
        foreach (string warning in Warnings.Take(20))
        {
          text.AppendLine("- " + warning);
        }
      }

      return text.ToString().TrimEnd();
    }
  }

  public static class SetTypeEditorService
  {
    public const int ZCount = 100;

    /// <summary>Instance Z &gt; 0 hides nested bar; unused slots are set to this (mm).</summary>
    public const double ZUnusedHideValueMm = 100;

    public const int DimensionStart = 0;
    public const int DimensionEnd = 10;

    private static readonly Regex BarDiameterInName = new(
      @"_D(\d+(?:\.\d+)?)\s*$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingDiameterOnly = new(
      @"D\d+(?:\.\d+)?\s*$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static readonly string[] DimensionParameterNames =
    {
      "Curve",
      "0_V", "0_Angle", "0_Bending", "0_L",
      "1_V", "1_Angle", "1_Bending", "1_L",
      "2_V", "2_Angle", "2_Bending", "2_L",
      "3_V", "3_Angle", "3_Bending", "3_L",
      "4_V", "4_Angle", "4_Bending", "4_L",
      "5_V", "5_Angle", "5_Bending", "5_L",
      "6_V", "6_Angle", "6_Bending", "6_L",
      "7_V", "7_Angle", "7_Bending", "7_L",
      "8_V", "8_Angle", "8_Bending", "8_L",
      "9_V", "9_Angle", "9_Bending", "9_L",
      "10_V", "10_Angle", "10_Bending", "10_L"
    };

    public static List<ShapeParameterValue> LoadShapeParameters(Document doc, string folder, string typeName)
    {
      VerticalCsvTable? csv = TryLoadShapeCsv(folder);
      FamilySymbol? symbol = FindArraySymbol(doc, typeName);
      int typeIndex = csv == null ? -1 : VerticalCsvService.IndexOfType(csv, typeName);
      var rows = new List<ShapeParameterValue>();
      foreach (string name in DimensionParameterNames.Concat(new[] { "d", "Angle" }))
      {
        Parameter? parameter = symbol?.LookupParameter(name);
        string value = parameter != null
          ? CsvValueConverter.GetDisplayValue(parameter)
          : string.Empty;

        if (string.IsNullOrWhiteSpace(value) && csv != null && typeIndex >= 0)
        {
          int p = VerticalCsvService.IndexOfParameter(csv, name);
          if (p >= 0)
          {
            IReadOnlyList<string> row = csv.ValueRows[p];
            value = typeIndex < row.Count ? row[typeIndex] : string.Empty;
          }
        }

        bool isYesNo = CsvValueConverter.IsYesNoName(name)
          || (parameter != null && CsvValueConverter.IsYesNo(parameter));
        rows.Add(new ShapeParameterValue(name, value, isYesNo));
      }

      return rows;
    }

    public static bool TryParseBarDiameterMm(string typeName, out double mm)
    {
      mm = 0;
      if (string.IsNullOrWhiteSpace(typeName))
      {
        return false;
      }

      Match match = BarDiameterInName.Match(typeName);
      return match.Success
        && VerticalCsvService.TryParseNumber(match.Groups[1].Value, out mm)
        && mm > 0;
    }

    public static bool TryResolveBarDiameterMm(
      string typeName,
      IReadOnlyDictionary<string, ShapeParameterValue>? values,
      out double mm)
    {
      if (values != null
          && values.TryGetValue("d", out ShapeParameterValue diameter)
          && VerticalCsvService.TryParseNumber(diameter.Value, out mm)
          && mm > 0)
      {
        return true;
      }

      return TryParseBarDiameterMm(typeName, out mm);
    }

    public static string FormatStraightBendingMm(double diameterMm)
    {
      double bending = 3 * diameterMm;
      if (Math.Abs(bending - Math.Round(bending)) < 0.0001)
      {
        return Math.Round(bending).ToString(CultureInfo.InvariantCulture);
      }

      return bending.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> CollectTypeNames(Document doc, string folder)
    {
      var names = new List<string>();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      void Add(string? name)
      {
        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name!))
        {
          return;
        }

        names.Add(name!);
      }

      string shapePath = Path.Combine(folder ?? string.Empty, RebarTypeCreateService.TypeShapeFileName);
      if (File.Exists(shapePath))
      {
        foreach (string name in VerticalCsvService.LoadTypeShape(shapePath).TypeNames)
        {
          Add(name);
        }
      }
      else if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
      {
        try
        {
          foreach (RebarTxtRow row in RebarTxtParser.Load(RebarTxtParser.FindTxt(folder!)))
          {
            Add(row.RebarTypeName);
          }
        }
        catch
        {
        }
      }

      foreach (FamilySymbol symbol in TryCollectArraySymbols(doc))
      {
        Add(symbol.Name);
      }

      names.Sort(StringComparer.OrdinalIgnoreCase);
      return names;
    }

    public static SetTypeEditorResult SaveShape(
      Document doc,
      string folder,
      string typeName,
      IReadOnlyList<ShapeParameterValue> rows)
    {
      FamilySymbol symbol = FindArraySymbol(doc, typeName)
        ?? throw new InvalidOperationException($"NMK_Rebar_Array type '{typeName}' was not found.");

      var result = new SetTypeEditorResult();
      using (var tx = new Transaction(doc, "NMK Set Shape Type"))
      {
        tx.Start();
        if (!symbol.IsActive)
        {
          symbol.Activate();
        }

        foreach (ShapeParameterValue row in rows)
        {
          Parameter? parameter = symbol.LookupParameter(row.Name);
          if (parameter == null)
          {
            if (!string.IsNullOrWhiteSpace(row.Value))
            {
              result.Warnings.Add($"'{typeName}' has no parameter '{row.Name}'.");
            }

            continue;
          }

          if (CsvValueConverter.TrySetParameter(parameter, row.Value, result.Warnings))
          {
            result.ValuesSet++;
          }
        }

        tx.Commit();
      }

      SaveShapeCsv(folder, typeName, rows);
      return result;
    }

    public static string CopyShapesByNameReplace(
      Document doc,
      string folder,
      IReadOnlyList<string> typeNames,
      string fromText,
      string toText)
    {
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Same Shape 2 runs in a project document.");
      }

      string from = fromText ?? string.Empty;
      string to = toText ?? string.Empty;
      if (string.IsNullOrEmpty(to))
      {
        throw new InvalidOperationException("Enter To text.");
      }

      List<string> names = typeNames
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (names.Count == 0)
      {
        throw new InvalidOperationException("No types in the filtered list.");
      }

      int copied = 0;
      int skipped = 0;
      var warnings = new List<string>();
      foreach (string targetName in names)
      {
        if (targetName.IndexOf(to, StringComparison.Ordinal) < 0)
        {
          skipped++;
          continue;
        }

        string sourceName = targetName.Replace(to, from);
        if (string.IsNullOrWhiteSpace(sourceName) || sourceName.Equals(targetName, StringComparison.Ordinal))
        {
          skipped++;
          continue;
        }

        FamilySymbol? sourceSymbol = FindArraySymbolIgnoreTrailingDiameter(doc, sourceName, targetName);
        FamilySymbol? targetSymbol = FindArraySymbol(doc, targetName);
        if (sourceSymbol == null || targetSymbol == null)
        {
          skipped++;
          continue;
        }

        try
        {
          List<ShapeParameterValue> rows = LoadShapeParameters(doc, folder, sourceSymbol.Name)
            .Where(row => !string.Equals(row.Name, "d", StringComparison.OrdinalIgnoreCase))
            .ToList();
          SetTypeEditorResult saved = SaveShape(doc, folder, targetName, rows);
          copied++;
          warnings.AddRange(saved.Warnings.Select(warning => $"{sourceSymbol.Name} → {targetName}: {warning}"));
        }
        catch (Exception ex)
        {
          skipped++;
          warnings.Add($"{sourceName} → {targetName}: {ex.Message}");
        }
      }

      string text = $"Copied shape {copied}/{names.Count} type(s).";
      if (skipped > 0)
      {
        text += $" Skipped {skipped}.";
      }

      if (warnings.Count > 0)
      {
        text += " " + string.Join(" ", warnings.Take(3));
      }

      return text;
    }

    public sealed class SelectInstanceZResult
    {
      public ElementId InstanceId { get; set; } = ElementId.InvalidElementId;

      public string TypeName { get; set; } = string.Empty;

      public IReadOnlyList<string> ZTexts { get; set; } = Array.Empty<string>();

      public IReadOnlyList<string> XTexts { get; set; } = Array.Empty<string>();

      public IReadOnlyList<string> YTexts { get; set; } = Array.Empty<string>();

      public List<string> Warnings { get; } = new();
    }

    public static SelectInstanceZResult SelectInstanceAndLoadZ(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Select runs in a project document.");
      }

      Reference picked = uidoc.Selection.PickObject(
        ObjectType.Element,
        new NmkRebarArraySelectionFilter(),
        "Select an NMK_Rebar_Array instance");
      ElementId id = picked.ElementId;
      if (doc.GetElement(id) is not FamilyInstance instance)
      {
        throw new InvalidOperationException("Selection is not a family instance.");
      }

      uidoc.Selection.SetElementIds(new[] { id });
      var result = new SelectInstanceZResult
      {
        InstanceId = id,
        TypeName = instance.Symbol?.Name ?? string.Empty
      };
      result.ZTexts = ZInputParser.LoadZTextRowsFromInstance(instance, ZCount, result.Warnings);
      result.XTexts = LoadAxisTexts(instance, "X", ZCount);
      result.YTexts = LoadAxisTexts(instance, "Y", ZCount);
      return result;
    }

    public static int SelectArrays(UIDocument uidoc, string typeName)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Select runs in a project document.");
      }

      if (string.IsNullOrWhiteSpace(typeName))
      {
        throw new InvalidOperationException("Select a type in the list first.");
      }

      IList<Reference> picked = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new NmkRebarArrayTypeSelectionFilter(typeName),
        $"Select NMK_Rebar_Array instances of type {typeName}");
      var ids = picked
        .Select(item => item.ElementId)
        .Distinct()
        .Where(id => doc.GetElement(id) is FamilyInstance)
        .ToList();
      uidoc.Selection.SetElementIds(ids);
      return ids.Count;
    }

    public static string ChangeArrayTypesFromTo(
      UIDocument uidoc,
      IReadOnlyList<string> filteredTypeNames,
      string fromText,
      string toText)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Change type runs in a project document.");
      }

      string from = (fromText ?? string.Empty);
      if (string.IsNullOrEmpty(from))
      {
        throw new InvalidOperationException("Enter From text.");
      }

      string to = toText ?? string.Empty;
      List<string> names = filteredTypeNames
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (names.Count == 0)
      {
        throw new InvalidOperationException("No types in the filtered list.");
      }

      IList<Reference> picked = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new NmkRebarArrayFilteredTypesSelectionFilter(names),
        "Select NMK_Rebar_Array instances (filtered types)");
      List<FamilyInstance> instances = picked
        .Select(item => doc.GetElement(item.ElementId))
        .OfType<FamilyInstance>()
        .GroupBy(instance => CreateRebarByLineService.IdValue(instance.Id))
        .Select(group => group.First())
        .ToList();
      if (instances.Count == 0)
      {
        throw new InvalidOperationException("No array instance was selected.");
      }

      int changed = 0;
      var warnings = new List<string>();
      var changedIds = new List<ElementId>();
      using (var tx = new Transaction(doc, "NMK Change Array Type"))
      {
        tx.Start();
        foreach (FamilyInstance instance in instances)
        {
          string current = instance.Symbol?.Name ?? string.Empty;
          if (current.IndexOf(from, StringComparison.Ordinal) < 0)
          {
            warnings.Add($"{current}: From '{from}' not in type name.");
            continue;
          }

          string next = current.Replace(from, to);
          if (string.IsNullOrWhiteSpace(next) || next.Equals(current, StringComparison.Ordinal))
          {
            warnings.Add($"{current}: name unchanged.");
            continue;
          }

          FamilySymbol? symbol = FindArraySymbolIgnoreTrailingDiameter(doc, next, current);
          if (symbol == null)
          {
            warnings.Add($"{current}: type '{next}' not found (ignore Dxx).");
            continue;
          }

          try
          {
            if (!symbol.IsActive)
            {
              symbol.Activate();
            }

            instance.ChangeTypeId(symbol.Id);
            changed++;
            changedIds.Add(instance.Id);
          }
          catch (Exception ex)
          {
            warnings.Add($"{current} → {next}: {ex.Message}");
          }
        }

        if (changed == 0)
        {
          tx.RollBack();
        }
        else
        {
          tx.Commit();
        }
      }

      if (changedIds.Count > 0)
      {
        uidoc.Selection.SetElementIds(changedIds);
      }

      string text = $"Changed {changed}/{instances.Count} array instance(s).";
      if (warnings.Count > 0)
      {
        text += " " + string.Join(" ", warnings.Take(3));
      }

      return text;
    }

    public static int SelectType(
      UIDocument uidoc,
      IReadOnlyList<string> typeNames,
      bool includeRebar,
      bool includeArray)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Select Type runs in a project document.");
      }

      List<string> names = typeNames
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (names.Count == 0)
      {
        throw new InvalidOperationException("No types in the filtered list.");
      }

      if (!includeRebar && !includeArray)
      {
        throw new InvalidOperationException("Check Rebar and/or Array first.");
      }

      var typeSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
      var ids = new List<ElementId>();
      var seen = new HashSet<long>();
      void Add(ElementId id)
      {
        long value = CreateRebarByLineService.IdValue(id);
        if (seen.Add(value))
        {
          ids.Add(id);
        }
      }

      if (includeArray)
      {
        foreach (FamilyInstance instance in new FilteredElementCollector(doc)
          .OfClass(typeof(FamilyInstance))
          .Cast<FamilyInstance>()
          .Where(CreateRebarByLineService.IsRebarArrayInstance))
        {
          if (instance.Symbol?.Name != null && typeSet.Contains(instance.Symbol.Name))
          {
            Add(instance.Id);
          }
        }
      }

      if (includeRebar)
      {
        foreach (RevitRebar rebar in new FilteredElementCollector(doc)
          .OfClass(typeof(RevitRebar))
          .Cast<RevitRebar>())
        {
          if (doc.GetElement(rebar.GetTypeId()) is RebarBarType barType
              && typeSet.Contains(barType.Name))
          {
            Add(rebar.Id);
          }
        }
      }

      uidoc.Selection.SetElementIds(ids);
      return ids.Count;
    }

    public static SetTypeEditorResult SetZOnSelection(
      UIDocument uidoc,
      IReadOnlyList<string> zTexts,
      IReadOnlyList<string> xTexts,
      IReadOnlyList<string> yTexts)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Set Z runs in a project document.");
      }

      var result = new SetTypeEditorResult();
      Dictionary<int, double> values = ZInputParser.ParseCumulative(zTexts, ZCount, result.Warnings);
      bool setZ = values.Count > 0;
      bool setXy = HasAny(xTexts) || HasAny(yTexts);
      if (!setZ && !setXy)
      {
        throw new InvalidOperationException("Enter Z values first (100 or 5x100), or X / Y (200 or 3x200).");
      }

      List<FamilyInstance> instances = uidoc.Selection.GetElementIds()
        .Select(doc.GetElement)
        .OfType<FamilyInstance>()
        .Where(instance =>
          instance.Symbol?.Family?.Name.Equals(RebarTypeCreateService.ArrayFamilyName, StringComparison.OrdinalIgnoreCase) == true)
        .ToList();
      if (instances.Count == 0)
      {
        throw new InvalidOperationException("No selected NMK_Rebar_Array instances. Use SELECT first.");
      }

      using (var tx = new Transaction(doc, "NMK Set Data Type"))
      {
        tx.Start();
        string hideValueText = ZUnusedHideValueMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (FamilyInstance instance in instances)
        {
          int setOnInstance = 0;
          if (setZ)
          {
            for (int n = 1; n <= ZCount; n++)
            {
              Parameter? parameter = FindZ(instance, n);
              if (parameter == null)
              {
                if (values.ContainsKey(n))
                {
                  result.Warnings.Add($"'{instance.Id}' has no parameter Z_{n} / Z{n}.");
                }

                continue;
              }

              string raw = values.TryGetValue(n, out double zValue)
                ? zValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : hideValueText;

              if (CsvValueConverter.TrySetParameter(parameter, raw, result.Warnings))
              {
                setOnInstance++;
                result.ValuesSet++;
              }
            }
          }

          bool hasX = HasAny(xTexts);
          bool hasY = HasAny(yTexts);
          setOnInstance += SetAxisColumn(instance, "X", xTexts, result, zeroIfEmpty: hasY && !hasX);
          setOnInstance += SetAxisColumn(instance, "Y", yTexts, result, zeroIfEmpty: hasX && !hasY);
          if (setOnInstance > 0)
          {
            result.InstancesUpdated++;
          }
        }

        tx.Commit();
      }

      return result;
    }

    public static Parameter? FindZ(Element element, int n)
    {
      return element.LookupParameter($"Z_{n}") ?? element.LookupParameter($"Z{n}");
    }

    public static Parameter? FindAxis(Element element, string axis, int n)
    {
      return element.LookupParameter($"{axis}{n}")
        ?? element.LookupParameter($"{axis}_{n}");
    }

    public static AddXyDataResult AddXyzFromPicks(UIDocument uidoc, bool toX, bool sole, bool start)
    {
      XYZ a = PickPointOnView(uidoc, "Pick sort-direction start");
      XYZ b = PickPointOnView(uidoc, "Pick sort-direction end");
      XYZ vector = b - a;
      if (vector.GetLength() < 1e-9)
      {
        throw new InvalidOperationException("The two picks are the same point.");
      }

      XYZ axis = vector.Normalize();
      IList<Reference> picked = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new DetailLineSelectionFilter(),
        "Select detail lines");
      var lines = new List<(double T, double OffsetMm)>();
      foreach (Reference reference in picked)
      {
        if (uidoc.Document.GetElement(reference) is not CurveElement element)
        {
          continue;
        }

        Curve? curve = element.GeometryCurve;
        if (curve == null || !curve.IsBound)
        {
          continue;
        }

        XYZ startPt = curve.GetEndPoint(0);
        XYZ endPt = curve.GetEndPoint(1);
        double t = 0.5 * ((startPt - a).DotProduct(axis) + (endPt - a).DotProduct(axis));
        double offsetMm = ToMm(Math.Min(DistanceToAxis(startPt, a, axis), DistanceToAxis(endPt, a, axis)));
        lines.Add((t, offsetMm));
      }

      if (lines.Count == 0)
      {
        throw new InvalidOperationException("No detail line was selected.");
      }

      lines.Sort((left, right) => left.T.CompareTo(right.T));
      var gaps = new List<double>();
      var offsets = new List<double>();
      for (int i = 0; i < lines.Count; i++)
      {
        offsets.Add(lines[i].OffsetMm);
        if (i > 0)
        {
          gaps.Add(ToMm(Math.Abs(lines[i].T - lines[i - 1].T)));
        }
      }

      var result = new AddXyDataResult { ToX = toX };
      foreach (double value in ApplySoleGaps(gaps, sole, start))
      {
        result.ZValues.Add(FormatMm(value));
      }

      foreach (double value in ApplySoleOffsets(offsets, sole, start))
      {
        result.AxisValues.Add(FormatMm(value));
      }

      result.Filled = result.ZValues.Count + result.AxisValues.Count;
      return result;
    }

    private static List<double> ApplySoleGaps(IReadOnlyList<double> gaps, bool sole, bool start)
    {
      if (!sole)
      {
        var all = new List<double> { 0 };
        all.AddRange(gaps);
        return all;
      }

      if (start)
      {
        var result = new List<double> { 0 };
        result.AddRange(SumStridePairs(gaps, 1));
        return result;
      }

      return SumStridePairs(gaps, 0);
    }

    private static List<double> ApplySoleOffsets(IReadOnlyList<double> offsets, bool sole, bool start)
    {
      if (!sole)
      {
        return offsets.ToList();
      }

      return TakeStride(offsets, start ? 0 : 1);
    }

    private static List<double> TakeStride(IReadOnlyList<double> values, int startIndex)
    {
      var result = new List<double>();
      for (int index = startIndex; index < values.Count; index += 2)
      {
        result.Add(values[index]);
      }

      return result;
    }

    private static List<double> SumStridePairs(IReadOnlyList<double> values, int startIndex)
    {
      var result = new List<double>();
      for (int index = startIndex; index + 1 < values.Count; index += 2)
      {
        result.Add(values[index] + values[index + 1]);
      }

      return result;
    }

    private static double DistanceToAxis(XYZ point, XYZ origin, XYZ axis)
    {
      XYZ delta = point - origin;
      XYZ projected = origin + axis.Multiply(delta.DotProduct(axis));
      return point.DistanceTo(projected);
    }

    private static double ToMm(double feet)
    {
      return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    }

    private static string FormatMm(double mm)
    {
      return Math.Round(mm, 3).ToString(CultureInfo.InvariantCulture);
    }

    public static XYZ PickPointOnView(UIDocument uidoc, string prompt)
    {
      Reference reference = uidoc.Selection.PickObject(ObjectType.PointOnElement, prompt);
      return reference.GlobalPoint;
    }

    public sealed class AddXyDataResult
    {
      public bool ToX { get; set; }

      public int Filled { get; set; }

      public List<string> ZValues { get; } = new();

      public List<string> AxisValues { get; } = new();
    }

    private static IReadOnlyList<string> LoadAxisTexts(Element instance, string axis, int count)
    {
      var values = new List<double>();
      for (int n = 1; n <= count; n++)
      {
        Parameter? parameter = FindAxis(instance, axis, n);
        if (parameter == null || !parameter.HasValue)
        {
          break;
        }

        string display = CsvValueConverter.GetDisplayValue(parameter);
        if (string.IsNullOrWhiteSpace(display) || !VerticalCsvService.TryParseNumber(display, out double mm))
        {
          break;
        }

        values.Add(mm);
      }

      while (values.Count > 0 && Math.Abs(values[values.Count - 1]) < 0.0005)
      {
        values.RemoveAt(values.Count - 1);
      }

      return ZInputParser.EncodeSpacingsForDisplay(values, count);
    }

    private static int SetAxisColumn(
      Element instance,
      string axis,
      IReadOnlyList<string> texts,
      SetTypeEditorResult result,
      bool zeroIfEmpty = false)
    {
      if (!HasAny(texts) && !zeroIfEmpty)
      {
        return 0;
      }

      Dictionary<int, double> values = HasAny(texts)
        ? ZInputParser.ParseExpanded(texts, ZCount, result.Warnings)
        : new Dictionary<int, double>();
      int set = 0;
      for (int n = 1; n <= ZCount; n++)
      {
        Parameter? parameter = FindAxis(instance, axis, n);
        if (parameter == null)
        {
          if (values.ContainsKey(n))
          {
            result.Warnings.Add($"'{instance.Id}' has no parameter {axis}{n}.");
          }

          continue;
        }

        string raw = values.TryGetValue(n, out double axisValue)
          ? axisValue.ToString(CultureInfo.InvariantCulture)
          : "0";
        if (CsvValueConverter.TrySetParameter(parameter, raw, result.Warnings))
        {
          set++;
          result.ValuesSet++;
        }
      }

      return set;
    }

    private static bool HasAny(IReadOnlyList<string> texts)
    {
      return texts != null && texts.Any(text => !string.IsNullOrWhiteSpace(text));
    }

    private static FamilySymbol? FindArraySymbol(Document doc, string typeName)
    {
      return TryCollectArraySymbols(doc).FirstOrDefault(symbol =>
        symbol.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
    }

    private static FamilySymbol? FindArraySymbolIgnoreTrailingDiameter(
      Document doc,
      string typeName,
      string? excludeTypeName)
    {
      FamilySymbol? exact = FindArraySymbol(doc, typeName);
      if (exact != null)
      {
        return exact;
      }

      string baseName = StripTrailingDiameter(typeName);
      if (string.IsNullOrWhiteSpace(baseName))
      {
        return null;
      }

      return TryCollectArraySymbols(doc).FirstOrDefault(symbol =>
        !symbol.Name.Equals(excludeTypeName, StringComparison.OrdinalIgnoreCase)
        && StripTrailingDiameter(symbol.Name).Equals(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripTrailingDiameter(string typeName)
    {
      if (string.IsNullOrWhiteSpace(typeName))
      {
        return string.Empty;
      }

      return TrailingDiameterOnly.Replace(typeName.Trim(), string.Empty);
    }

    private static List<FamilySymbol> TryCollectArraySymbols(Document doc)
    {
      try
      {
        return ProjectTypeCsvApplier.CollectArraySymbols(doc);
      }
      catch
      {
        return new List<FamilySymbol>();
      }
    }

    private static VerticalCsvTable? TryLoadShapeCsv(string folder)
    {
      string path = Path.Combine(folder ?? string.Empty, RebarTypeCreateService.TypeShapeFileName);
      if (!File.Exists(path))
      {
        return null;
      }

      return VerticalCsvService.LoadTypeShape(path);
    }

    private static void SaveShapeCsv(string folder, string typeName, IReadOnlyList<ShapeParameterValue> rows)
    {
      if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
      {
        return;
      }

      RebarTypeCreateService.EnsureCsvTemplates(folder);
      string path = Path.Combine(folder, RebarTypeCreateService.TypeShapeFileName);
      if (!File.Exists(path))
      {
        return;
      }

      VerticalCsvTable table = VerticalCsvService.LoadTypeShape(path);
      table = VerticalCsvService.EnsureTypeColumns(table, new[] { typeName });
      foreach (ShapeParameterValue row in rows)
      {
        table = EnsureParameterRow(table, row.Name);
        table = VerticalCsvService.SetCell(table, row.Name, typeName, row.Value ?? string.Empty);
      }

      VerticalCsvService.SaveTypeShape(path, table);
    }

    private static VerticalCsvTable EnsureParameterRow(VerticalCsvTable table, string parameterName)
    {
      if (VerticalCsvService.IndexOfParameter(table, parameterName) >= 0)
      {
        return table;
      }

      var names = table.ParameterNames.ToList();
      var rows = table.ValueRows.Select(row => row.ToList()).ToList();
      names.Add(parameterName);
      rows.Add(Enumerable.Repeat(string.Empty, table.TypeNames.Count).ToList());
      return new VerticalCsvTable(
        names,
        table.TypeNames,
        rows.Select(row => (IReadOnlyList<string>)row).ToList());
    }
  }
}

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

    public Dictionary<string, List<int>> VarriesGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public const int DimensionStart = -1;
    public const int DimensionEnd = 10;
    public const string VarText = "var";

    private static readonly Regex BarDiameterInName = new(
      @"_D(\d+(?:\.\d+)?)\s*$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingDiameterOnly = new(
      @"D\d+(?:\.\d+)?\s*$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<string> DimensionParameterNames
    {
      get
      {
        var names = new List<string> { "Curve", "Angle", "Angle_Hook", "d" };
        for (int n = DimensionStart; n <= DimensionEnd; n++)
        {
          names.Add($"{n}_V");
          names.Add($"{n}_Angle");
          names.Add($"{n}_Bending");
          names.Add($"{n}_L");
        }

        return names;
      }
    }

    public static bool IsVarValue(string? text)
    {
      return string.Equals((text ?? string.Empty).Trim(), VarText, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInstanceShapeName(string name)
    {
      return string.Equals(name, "Angle", StringComparison.OrdinalIgnoreCase);
    }

    public static List<ShapeParameterValue> LoadShapeParameters(Document doc, string folder, string typeName)
    {
      VerticalCsvTable? csv = TryLoadShapeCsv(folder);
      FamilySymbol? symbol = FindArraySymbol(doc, typeName);
      int typeIndex = csv == null ? -1 : VerticalCsvService.IndexOfType(csv, typeName);
      var rows = new List<ShapeParameterValue>();
      foreach (string name in DimensionParameterNames)
      {
        if (IsInstanceShapeName(name))
        {
          rows.Add(new ShapeParameterValue(name, string.Empty, false));
          continue;
        }

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

      ApplyMappedLGroupToShapeRows(symbol, rows);
      return rows;
    }

    public static (IReadOnlyList<string> L1, IReadOnlyList<string> L2, IReadOnlyList<string> L3) LoadMappedLFromType(Document doc, string typeName)
    {
      FamilySymbol? symbol = FindArraySymbol(doc, typeName);
      Element? target = (Element?)symbol
        ?? CollectArrayInstances(doc, typeName).FirstOrDefault();
      if (target == null)
      {
        return (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
      }

      return (
        LoadMappedLTexts(target, "1L"),
        LoadMappedLTexts(target, "2L"),
        LoadMappedLTexts(target, "3L"));
    }

    public static string CombineMappedLGroup(IReadOnlyList<string> texts)
    {
      IReadOnlyList<string> compact = CompactMappedLValues(texts);
      if (compact.Count == 0)
      {
        return string.Empty;
      }

      if (compact.All(value => string.Equals(value, compact[0], StringComparison.OrdinalIgnoreCase)))
      {
        return compact[0];
      }

      return VarText;
    }

    public static IReadOnlyList<string> TrimMappedLForGet(IReadOnlyList<string> texts)
    {
      List<string> compact = CompactMappedLValues(texts).ToList();
      if (compact.Count <= 1)
      {
        return Array.Empty<string>();
      }

      int start = compact.Count - 1;
      while (start > 0
        && string.Equals(compact[start], compact[start - 1], StringComparison.OrdinalIgnoreCase))
      {
        start--;
      }

      int run = compact.Count - start;
      if (run < 2)
      {
        return compact;
      }

      if (start == 0)
      {
        return Array.Empty<string>();
      }

      return compact.Take(start).ToList();
    }

    private static List<string> CompactMappedLValues(IReadOnlyList<string> texts)
    {
      var values = new List<string>();
      foreach (string text in texts ?? Array.Empty<string>())
      {
        string raw = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
          continue;
        }

        if (VerticalCsvService.TryParseNumber(raw, out double mm))
        {
          if (Math.Abs(mm) < 0.0005)
          {
            continue;
          }

          values.Add(FormatMm(mm));
          continue;
        }

        values.Add(raw);
      }

      return values;
    }

    private static void ApplyMappedLGroupToShapeRows(FamilySymbol? symbol, List<ShapeParameterValue> rows)
    {
      if (symbol == null)
      {
        return;
      }

      ReplaceShapeRow(rows, "1_L", CombineMappedLGroup(LoadMappedLTexts(symbol, "1L")));
      ReplaceShapeRow(rows, "2_L", CombineMappedLGroup(LoadMappedLTexts(symbol, "2L")));
      ReplaceShapeRow(rows, "3_L", CombineMappedLGroup(LoadMappedLTexts(symbol, "3L")));
    }

    private static void ReplaceShapeRow(List<ShapeParameterValue> rows, string name, string value)
    {
      int index = rows.FindIndex(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));
      var next = new ShapeParameterValue(name, value, false);
      if (index >= 0)
      {
        rows[index] = next;
        return;
      }

      rows.Add(next);
    }

    public static string CombineDisplayValues(IReadOnlyList<string> values)
    {
      var distinct = values
        .Select(value => (value ?? string.Empty).Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (distinct.Count == 0)
      {
        return string.Empty;
      }

      if (distinct.Count == 1)
      {
        return distinct[0];
      }

      return VarText;
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
      return FormatBarMultipleMm(diameterMm, 3);
    }

    public static string FormatBarMultipleMm(double diameterMm, double factor)
    {
      double value = factor * diameterMm;
      if (Math.Abs(value - Math.Round(value)) < 0.0001)
      {
        return Math.Round(value).ToString(CultureInfo.InvariantCulture);
      }

      return value.ToString("0.###", CultureInfo.InvariantCulture);
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

      names.Sort(PlaceCouplerService.CompareNumericNames);
      return names;
    }

    public static SetTypeEditorResult SaveShape(
      UIDocument uidoc,
      string folder,
      string typeName,
      IReadOnlyList<ShapeParameterValue> rows,
      IReadOnlyList<string>? l1Texts = null,
      IReadOnlyList<string>? l2Texts = null,
      IReadOnlyList<string>? l3Texts = null)
    {
      SetTypeEditorResult result = SaveShape(uidoc.Document, folder, typeName, rows, l1Texts, l2Texts, l3Texts);
      List<FamilyInstance> instances = CollectArrayInstances(uidoc.Document, typeName);
      if (instances.Count > 0)
      {
        uidoc.Selection.SetElementIds(instances.Select(instance => instance.Id).ToList());
      }

      return result;
    }

    public static SetTypeEditorResult SaveShape(
      Document doc,
      string folder,
      string typeName,
      IReadOnlyList<ShapeParameterValue> rows,
      IReadOnlyList<string>? l1Texts = null,
      IReadOnlyList<string>? l2Texts = null,
      IReadOnlyList<string>? l3Texts = null)
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

        IList<Element> targets = new List<Element> { symbol };
        foreach (Element target in targets)
        {
          int setOnTarget = 0;
          foreach (ShapeParameterValue row in rows)
          {
            if (IsInstanceShapeName(row.Name)
                || IsVarValue(row.Value)
                || string.IsNullOrWhiteSpace(row.Value))
            {
              continue;
            }

            Parameter? parameter = target.LookupParameter(row.Name);
            if (parameter == null)
            {
              if (!string.IsNullOrWhiteSpace(row.Value) && ReferenceEquals(target, targets[0]))
              {
                result.Warnings.Add($"'{typeName}' has no parameter '{row.Name}'.");
              }

              continue;
            }

            if (CsvValueConverter.TrySetParameter(parameter, row.Value, result.Warnings))
            {
              setOnTarget++;
              result.ValuesSet++;
            }
          }

          if (setOnTarget > 0 && target is FamilySymbol)
          {
            result.InstancesUpdated++;
          }
        }

        SetMappedLColumn(symbol, "1L", l1Texts, result);
        SetMappedLColumn(symbol, "2L", l2Texts, result);
        SetMappedLColumn(symbol, "3L", l3Texts, result);

        tx.Commit();
      }

      SaveShapeCsv(
        folder,
        typeName,
        rows.Where(row => !IsVarValue(row.Value) && !IsInstanceShapeName(row.Name)).ToList());
      return result;
    }

    public static SetTypeEditorResult CopyShapeExceptDiameter(
      Document doc,
      string folder,
      string sourceTypeName,
      string targetTypeName)
    {
      List<ShapeParameterValue> rows = LoadShapeParameters(doc, folder, sourceTypeName)
        .Where(row => !string.Equals(row.Name, "d", StringComparison.OrdinalIgnoreCase)
          && !IsInstanceShapeName(row.Name))
        .ToList();
      (IReadOnlyList<string> l1, IReadOnlyList<string> l2, IReadOnlyList<string> l3) =
        LoadMappedLFromType(doc, sourceTypeName);
      return SaveShape(doc, folder, targetTypeName, rows, l1, l2, l3);
    }

    public static string DisallowJoinAllBeams(Document doc)
    {
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Disallow Join runs in a project document.");
      }

      List<FamilyInstance> framing = new FilteredElementCollector(doc)
        .OfClass(typeof(FamilyInstance))
        .OfCategory(BuiltInCategory.OST_StructuralFraming)
        .WhereElementIsNotElementType()
        .Cast<FamilyInstance>()
        .ToList();

      int ends = 0;
      using (var tx = new Transaction(doc, "NMK Disallow Beam Join"))
      {
        tx.Start();
        foreach (FamilyInstance beam in framing)
        {
          for (int end = 0; end <= 1; end++)
          {
            try
            {
              if (StructuralFramingUtils.IsJoinAllowedAtEnd(beam, end))
              {
                StructuralFramingUtils.DisallowJoinAtEnd(beam, end);
                ends++;
              }
            }
            catch
            {
            }
          }
        }

        tx.Commit();
      }

      return $"Disallow join: {ends} end(s) on {framing.Count} beam(s).";
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
          SetTypeEditorResult saved = CopyShapeExceptDiameter(doc, folder, sourceSymbol.Name, targetName);
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

      public IReadOnlyList<string> L1Texts { get; set; } = Array.Empty<string>();

      public IReadOnlyList<string> L2Texts { get; set; } = Array.Empty<string>();

      public IReadOnlyList<string> L3Texts { get; set; } = Array.Empty<string>();

      public string AngleText { get; set; } = string.Empty;

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
      result.L1Texts = LoadMappedLTexts(instance, "1L");
      result.L2Texts = LoadMappedLTexts(instance, "2L");
      result.L3Texts = LoadMappedLTexts(instance, "3L");
      Parameter? angle = instance.LookupParameter("Angle");
      result.AngleText = angle == null ? string.Empty : CsvValueConverter.GetDisplayValue(angle);
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

    public static string ChangeSelectedArraysToType(UIDocument uidoc, string typeName)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Change type runs in a project document.");
      }

      if (string.IsNullOrWhiteSpace(typeName))
      {
        return string.Empty;
      }

      List<FamilyInstance> instances = uidoc.Selection.GetElementIds()
        .Select(doc.GetElement)
        .OfType<FamilyInstance>()
        .Where(CreateRebarByLineService.IsRebarArrayInstance)
        .GroupBy(instance => CreateRebarByLineService.IdValue(instance.Id))
        .Select(group => group.First())
        .ToList();
      if (instances.Count == 0)
      {
        return string.Empty;
      }

      FamilySymbol symbol = FindArraySymbol(doc, typeName)
        ?? throw new InvalidOperationException($"NMK_Rebar_Array type '{typeName}' was not found.");

      int changed = 0;
      var warnings = new List<string>();
      using (var tx = new Transaction(doc, "NMK Change Array Type"))
      {
        tx.Start();
        if (!symbol.IsActive)
        {
          symbol.Activate();
        }

        foreach (FamilyInstance instance in instances)
        {
          if (instance.Symbol != null && instance.Symbol.Id == symbol.Id)
          {
            continue;
          }

          try
          {
            instance.ChangeTypeId(symbol.Id);
            changed++;
          }
          catch (Exception ex)
          {
            warnings.Add($"{instance.Id}: {ex.Message}");
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

      if (changed == 0 && warnings.Count == 0)
      {
        return $"Selected instance(s) already type {typeName}.";
      }

      string text = $"Changed {changed}/{instances.Count} instance(s) to {typeName}.";
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
      IReadOnlyList<string> yTexts,
      string? angleText = null)
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
      bool setAngle = !string.IsNullOrWhiteSpace(angleText) && !IsVarValue(angleText);
      if (!setZ && !setXy && !setAngle)
      {
        throw new InvalidOperationException("Enter Z values first (100 or 5x100), or X / Y (200 or 3x200), or Angle.");
      }

      List<FamilyInstance> instances = uidoc.Selection.GetElementIds()
        .Select(doc.GetElement)
        .OfType<FamilyInstance>()
        .Where(CreateRebarByLineService.IsRebarArrayInstance)
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
                ? ZInputParser.FormatRounded(zValue)
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
          if (setAngle)
          {
            Parameter? angle = instance.LookupParameter("Angle");
            if (angle == null)
            {
              result.Warnings.Add($"'{instance.Id}' has no parameter Angle.");
            }
            else if (CsvValueConverter.TrySetParameter(angle, angleText!, result.Warnings))
            {
              setOnInstance++;
              result.ValuesSet++;
            }
          }

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
      var ts = new List<double>(lines.Count);
      var offsets = new List<double>(lines.Count);
      foreach ((double t, double offsetMm) in lines)
      {
        ts.Add(t);
        offsets.Add(offsetMm);
      }

      var result = new AddXyDataResult { ToX = toX };
      foreach (double value in ApplySoleZs(ts, sole, start))
      {
        result.ZValues.Add(ZInputParser.FormatRounded(value));
      }

      foreach (double value in ApplySoleOffsets(offsets, sole, start))
      {
        result.AxisValues.Add(ZInputParser.FormatRounded(value));
      }

      result.Filled = result.ZValues.Count + result.AxisValues.Count;
      return result;
    }

    public static RevertXyzResult RevertXyz(string axis, IReadOnlyList<string> axisTexts)
    {
      string key = (axis ?? "X").Trim().ToUpperInvariant();
      if (key is not ("X" or "Y" or "Z" or "1L" or "2L" or "3L"))
      {
        throw new InvalidOperationException("Choose X, Y, Z, 1L, 2L, or 3L.");
      }

      if (key is "1L" or "2L" or "3L")
      {
        List<string> reversed = ReverseNonZeroTexts(axisTexts);
        int filled = reversed.Count(text => !string.IsNullOrWhiteSpace(text));
        return new RevertXyzResult
        {
          Axis = key,
          DisplayTexts = reversed,
          Message = $"Reverted {key}: {filled} non-zero value(s) reversed. SET to write."
        };
      }

      List<double> values = ZInputParser.ExpandInOrder(axisTexts, ZCount);
      if (values.Count == 0)
      {
        throw new InvalidOperationException($"No {key} values to reverse.");
      }

      values.Reverse();
      return new RevertXyzResult
      {
        Axis = key,
        Values = values,
        Message = $"Reverted {key}: {values.Count} value(s) 0..n → n..0. SET to write."
      };
    }

    public static List<string> ReverseNonZeroTexts(IReadOnlyList<string> texts)
    {
      var result = (texts ?? Array.Empty<string>()).Select(text => text ?? string.Empty).ToList();
      while (result.Count < ZCount)
      {
        result.Add(string.Empty);
      }

      var indexes = new List<int>();
      var values = new List<string>();
      for (int i = 0; i < result.Count; i++)
      {
        string text = result[i].Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
          continue;
        }

        if (VerticalCsvService.TryParseNumber(text, out double number) && Math.Abs(number) < 0.0005)
        {
          continue;
        }

        indexes.Add(i);
        values.Add(result[i]);
      }

      if (values.Count == 0)
      {
        throw new InvalidOperationException("No non-zero 1L/2L/3L values to reverse.");
      }

      values.Reverse();
      for (int i = 0; i < indexes.Count; i++)
      {
        result[indexes[i]] = values[i];
      }

      return result;
    }

    public sealed class RevertXyzResult
    {
      public string Axis { get; set; } = "X";

      public List<double> Values { get; set; } = new();

      public List<string> DisplayTexts { get; set; } = new();

      public string Message { get; set; } = string.Empty;
    }

    public static List<string> MeasureDetailLineLengthsMm(UIDocument uidoc, string prompt)
    {
      IList<Reference> picked = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new DetailLineSelectionFilter(),
        prompt);
      var lengths = new List<string>();
      Document doc = uidoc.Document;
      foreach (Reference reference in picked)
      {
        if (doc.GetElement(reference) is not DetailLine line || line.GeometryCurve == null)
        {
          continue;
        }

        lengths.Add(FormatMm(ToMm(line.GeometryCurve.Length)));
      }

      if (lengths.Count == 0)
      {
        throw new InvalidOperationException("No detail line was selected.");
      }

      return lengths;
    }

    private static List<double> ApplySoleZs(IReadOnlyList<double> ts, bool sole, bool start)
    {
      if (!sole)
      {
        var all = new List<double> { 0 };
        for (int i = 1; i < ts.Count; i++)
        {
          all.Add(ToMm(Math.Abs(ts[i] - ts[i - 1])));
        }

        return all;
      }

      if (start)
      {
        var even = new List<double> { 0 };
        for (int i = 0; i + 2 < ts.Count; i += 2)
        {
          even.Add(ToMm(Math.Abs(ts[i + 2] - ts[i])));
        }

        return even;
      }

      var odd = new List<double>();
      if (ts.Count >= 2)
      {
        odd.Add(ToMm(Math.Abs(ts[1] - ts[0])));
      }

      for (int i = 1; i + 2 < ts.Count; i += 2)
      {
        odd.Add(ToMm(Math.Abs(ts[i + 2] - ts[i])));
      }

      return odd;
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

        if (!ZInputParser.TryReadLengthMm(parameter, out double mm))
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
          ? ZInputParser.FormatRounded(axisValue)
          : "0";
        if (CsvValueConverter.TrySetParameter(parameter, raw, result.Warnings))
        {
          set++;
          result.ValuesSet++;
        }
      }

      return set;
    }

    private static int SetMappedLColumn(
      Element instance,
      string prefix,
      IReadOnlyList<string>? texts,
      SetTypeEditorResult result)
    {
      if (!HasAny(texts))
      {
        return 0;
      }

      int set = 0;
      for (int n = 1; n <= ZCount; n++)
      {
        string raw = n - 1 < texts!.Count ? texts[n - 1] ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || !VerticalCsvService.TryParseNumber(raw, out double mm))
        {
          continue;
        }

        Parameter? parameter = FindMappedL(instance, prefix, n);
        if (parameter == null)
        {
          result.Warnings.Add($"'{instance.Id}' has no parameter {prefix}_{n}.");
          continue;
        }

        if (CsvValueConverter.TrySetParameter(
          parameter,
          mm.ToString(CultureInfo.InvariantCulture),
          result.Warnings))
        {
          set++;
          result.ValuesSet++;
        }
      }

      return set;
    }

    public static Parameter? FindMappedL(Element element, string prefix, int n)
    {
      return element.LookupParameter($"{prefix}_{n}") ?? element.LookupParameter($"{prefix}{n}");
    }

    public static IReadOnlyList<string> LoadMappedLTexts(Element instance, string prefix)
    {
      var list = new List<string>(ZCount);
      for (int n = 1; n <= ZCount; n++)
      {
        Parameter? parameter = FindMappedL(instance, prefix, n);
        if (parameter == null || !parameter.HasValue)
        {
          list.Add(string.Empty);
          continue;
        }

        string display = CsvValueConverter.GetDisplayValue(parameter) ?? string.Empty;
        if (VerticalCsvService.TryParseNumber(display, out double mm) && Math.Abs(mm) < 0.0005)
        {
          list.Add(string.Empty);
          continue;
        }

        list.Add(display.Trim());
      }

      return list;
    }

    private static bool HasAny(IReadOnlyList<string>? texts)
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

    public static SetTypeEditorResult SetXyzZerosOnSelection(UIDocument uidoc)
    {
      var zeros = Enumerable.Repeat("0", ZCount).ToList();
      return SetZOnSelection(uidoc, zeros, zeros, zeros);
    }

    public static List<FamilyInstance> CollectArrayInstances(Document doc, string typeName)
    {
      return new FilteredElementCollector(doc)
        .OfClass(typeof(FamilyInstance))
        .Cast<FamilyInstance>()
        .Where(CreateRebarByLineService.IsRebarArrayInstance)
        .Where(instance => instance.Symbol?.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true)
        .ToList();
    }

    public static List<string> CollectViewFamilyTypeNames(Document doc)
    {
      return new FilteredElementCollector(doc)
        .OfClass(typeof(ViewFamilyType))
        .Cast<ViewFamilyType>()
        .Select(type => type.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
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

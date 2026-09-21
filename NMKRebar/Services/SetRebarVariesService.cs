using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NMKRebar.Services
{
  public sealed class NmkRebarVariesSelectionFilter : ISelectionFilter
  {
    private readonly HashSet<string> _baseTypeNames;

    public NmkRebarVariesSelectionFilter(IEnumerable<string> baseTypeNames)
    {
      _baseTypeNames = new HashSet<string>(baseTypeNames, StringComparer.OrdinalIgnoreCase);
    }

    public bool AllowElement(Element elem)
    {
      return CreateRebarByLineService.IsRebarArrayInstance(elem)
        && elem is FamilyInstance instance
        && instance.Symbol != null
        && _baseTypeNames.Contains(instance.Symbol.Name);
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }

  public sealed class SetRebarVariesResult
  {
    public int TypesCreated { get; set; }

    public int TypesUpdated { get; set; }

    public int InstancesCopied { get; set; }

    public List<FamilyInstance> CopiedInstances { get; } = new();

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Child types created: {TypesCreated}");
      text.AppendLine($"Types updated: {TypesUpdated}");
      text.AppendLine($"Instances copied: {InstancesCopied}");
      if (Warnings.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("Warnings:");
        foreach (string warning in Warnings.Take(20))
        {
          text.AppendLine("- " + warning);
        }
      }

      return text.ToString();
    }
  }

  public static class SetRebarVariesService
  {
    public const string FileName = "Varries.csv";
    public const string VariesFilePrefix = "TypeShape_Varies_";
    private const int SlotCount = 50;
    private static readonly Regex TrailingDiameterSuffix = new(
      @"_?D\d+(?:\.\d+)?\s*$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TypeNameEquals(string csvType, string selectedType)
    {
      if (string.IsNullOrWhiteSpace(csvType) || string.IsNullOrWhiteSpace(selectedType))
      {
        return false;
      }

      if (csvType.Equals(selectedType, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }

      string stripped = StripTrailingDiameterSuffix(selectedType);
      return stripped.Length > 0
        && csvType.Equals(stripped, StringComparison.OrdinalIgnoreCase);
    }

    public static string StripTrailingDiameterSuffix(string typeName)
    {
      if (string.IsNullOrWhiteSpace(typeName))
      {
        return string.Empty;
      }

      return TrailingDiameterSuffix.Replace(typeName.Trim(), string.Empty, 1).TrimEnd('_', ' ');
    }

    public static string? FindVariesFile(string folder, string typeName)
    {
      if (LoadSortedValues(folder, typeName).Count == 0 && IndexOfType(folder, typeName) < 0)
      {
        return null;
      }

      return FindVarriesPath(folder);
    }

    public static string? FindVarriesPath(string folder)
    {
      if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
      {
        return null;
      }

      foreach (string name in new[] { FileName, "Varies.csv" })
      {
        string path = Path.Combine(folder, name);
        if (File.Exists(path))
        {
          return path;
        }
      }

      return null;
    }

    public static IReadOnlyList<double> LoadSortedValues(string folder, string typeName)
    {
      int typeIndex = IndexOfType(folder, typeName);
      string? path = FindVarriesPath(folder);
      if (typeIndex < 0 || path == null)
      {
        return Array.Empty<double>();
      }

      string[] lines = VerticalCsvService.ReadAllLinesShared(path)
        .Select(line => line.TrimEnd())
        .Where(line => line.Length > 0)
        .ToArray();
      if (lines.Length < 2)
      {
        return Array.Empty<double>();
      }

      IReadOnlyList<string> header = VerticalCsvService.ParseCsvLine(lines[0]);
      int startCol = HeaderStartColumn(header);
      var values = new List<double>();
      for (int i = 1; i < lines.Length; i++)
      {
        IReadOnlyList<string> cells = VerticalCsvService.ParseCsvLine(lines[i]);
        int cellIndex = startCol + typeIndex;
        string raw = cellIndex < cells.Count ? cells[cellIndex].Trim() : string.Empty;
        if (VerticalCsvService.TryParseNumber(raw, out double value))
        {
          values.Add(value);
        }
      }

      values.Sort();
      return values;
    }

    public sealed class VarriesMappedLGroup
    {
      public VarriesMappedLGroup(string typeName, int group, IReadOnlyList<string> values)
      {
        TypeName = typeName;
        Group = group;
        Values = values;
      }

      public string TypeName { get; }

      public int Group { get; }

      public IReadOnlyList<string> Values { get; }
    }

    public static IReadOnlyList<VarriesMappedLGroup> LoadMappedLGroups(string folder)
    {
      string? path = FindVarriesPath(folder);
      if (path == null)
      {
        throw new InvalidOperationException($"Varries.csv was not found in:\n{folder}");
      }

      string[] lines = VerticalCsvService.ReadAllLinesShared(path)
        .Select(line => line.TrimEnd())
        .ToArray();
      int headerIndex = IndexOfNonEmptyLine(lines, 0);
      int groupIndex = IndexOfNonEmptyLine(lines, headerIndex + 1);
      if (headerIndex < 0 || groupIndex < 0)
      {
        throw new InvalidOperationException($"{Path.GetFileName(path)} needs a TYPE row and a 1/2/3 row.");
      }

      IReadOnlyList<string> header = VerticalCsvService.ParseCsvLine(lines[headerIndex]);
      IReadOnlyList<string> groups = VerticalCsvService.ParseCsvLine(lines[groupIndex]);
      int startCol = HeaderStartColumn(header);
      var buckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
      var order = new List<string>();
      for (int col = startCol; col < header.Count; col++)
      {
        string typeName = col < header.Count ? header[col].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(typeName) || !TryParseMappedLGroup(col < groups.Count ? groups[col] : string.Empty, out int group))
        {
          continue;
        }

        string key = typeName + "\n" + group.ToString(CultureInfo.InvariantCulture);
        if (!buckets.TryGetValue(key, out List<string>? values))
        {
          values = new List<string>();
          buckets[key] = values;
          order.Add(key);
        }

        for (int i = groupIndex + 1; i < lines.Length; i++)
        {
          IReadOnlyList<string> cells = VerticalCsvService.ParseCsvLine(lines[i]);
          if (IsEmptyCsvRow(cells, startCol))
          {
            continue;
          }

          string raw = col < cells.Count ? cells[col] : string.Empty;
          string number = NormalizeCsvNumber(raw);
          if (string.IsNullOrWhiteSpace(number))
          {
            continue;
          }

          values.Add(number);
        }
      }

      var list = new List<VarriesMappedLGroup>();
      foreach (string key in order)
      {
        int split = key.LastIndexOf('\n');
        list.Add(new VarriesMappedLGroup(
          key.Substring(0, split),
          int.Parse(key.Substring(split + 1), CultureInfo.InvariantCulture),
          buckets[key]));
      }

      if (list.Count == 0)
      {
        throw new InvalidOperationException($"{Path.GetFileName(path)} has no TYPE + 1/2/3 columns.");
      }

      return list;
    }

    private static int IndexOfNonEmptyLine(IReadOnlyList<string> lines, int start)
    {
      for (int i = start; i < lines.Count; i++)
      {
        if (!string.IsNullOrWhiteSpace(lines[i]))
        {
          return i;
        }
      }

      return -1;
    }

    private static bool IsEmptyCsvRow(IReadOnlyList<string> cells, int startCol)
    {
      for (int i = startCol; i < cells.Count; i++)
      {
        if (!string.IsNullOrWhiteSpace(cells[i]))
        {
          return false;
        }
      }

      return true;
    }

    private static bool TryParseMappedLGroup(string raw, out int group)
    {
      group = 0;
      string text = (raw ?? string.Empty).Trim();
      if (text.Length == 0)
      {
        return false;
      }

      if (text.StartsWith("1L", StringComparison.OrdinalIgnoreCase) || text == "1")
      {
        group = 1;
        return true;
      }

      if (text.StartsWith("2L", StringComparison.OrdinalIgnoreCase) || text == "2")
      {
        group = 2;
        return true;
      }

      if (text.StartsWith("3L", StringComparison.OrdinalIgnoreCase) || text == "3")
      {
        group = 3;
        return true;
      }

      return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out group)
        && group is 1 or 2 or 3;
    }

    private static string NormalizeCsvNumber(string? raw)
    {
      if (string.IsNullOrWhiteSpace(raw))
      {
        return string.Empty;
      }

      var chars = new StringBuilder(raw.Length);
      foreach (char c in raw)
      {
        if (!char.IsWhiteSpace(c))
        {
          chars.Append(c);
        }
      }

      string compact = chars.ToString();
      if (compact.Length == 0)
      {
        return string.Empty;
      }

      if (VerticalCsvService.TryParseNumber(compact, out double mm) && Math.Abs(mm) >= 0.0005)
      {
        if (Math.Abs(mm - Math.Round(mm)) < 0.0001)
        {
          return Math.Round(mm).ToString(CultureInfo.InvariantCulture);
        }

        return mm.ToString("0.###", CultureInfo.InvariantCulture);
      }

      return string.Empty;
    }

    public static IReadOnlyDictionary<string, string> FindVariesFiles(string folder)
    {
      var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      string? path = FindVarriesPath(folder);
      if (path == null)
      {
        return map;
      }

      foreach (string typeName in LoadTypeNames(path))
      {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
          map[typeName] = path;
        }
      }

      return map;
    }

    private static int IndexOfType(string folder, string typeName)
    {
      string? path = FindVarriesPath(folder);
      if (path == null || string.IsNullOrWhiteSpace(typeName))
      {
        return -1;
      }

      IReadOnlyList<string> names = LoadTypeNames(path);
      for (int i = 0; i < names.Count; i++)
      {
        if (names[i].Equals(typeName, StringComparison.OrdinalIgnoreCase))
        {
          return i;
        }
      }

      for (int i = 0; i < names.Count; i++)
      {
        if (TypeNameEquals(names[i], typeName))
        {
          return i;
        }
      }

      return -1;
    }

    private static IReadOnlyList<string> LoadTypeNames(string path)
    {
      string[] lines = VerticalCsvService.ReadAllLinesShared(path)
        .Select(line => line.TrimEnd())
        .Where(line => line.Length > 0)
        .ToArray();
      if (lines.Length == 0)
      {
        return Array.Empty<string>();
      }

      IReadOnlyList<string> header = VerticalCsvService.ParseCsvLine(lines[0]);
      int startCol = HeaderStartColumn(header);
      return header.Skip(startCol).Select(name => name.Trim()).Where(name => name.Length > 0).ToList();
    }

    private static int HeaderStartColumn(IReadOnlyList<string> header)
    {
      if (header.Count == 0)
      {
        return 0;
      }

      string first = header[0].Trim();
      return first.Length == 0
        || first.Equals(VerticalCsvService.RebarTypeRowName, StringComparison.OrdinalIgnoreCase)
        ? 1
        : 0;
    }

    public static SetRebarVariesResult Create(UIDocument uidoc, string folder)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Set Rebar Varies runs in a project document.");
      }

      IReadOnlyDictionary<string, string> variesFiles = FindVariesFiles(folder);
      if (variesFiles.Count == 0)
      {
        throw new InvalidOperationException($"Varries.csv was not found or has no type columns in:\n{folder}");
      }

      Reference picked = uidoc.Selection.PickObject(
        ObjectType.Element,
        new NmkRebarVariesSelectionFilter(variesFiles.Keys),
        "Pick an NMK_Rebar_Array whose type has a TypeShape_Varies file");
      FamilyInstance instance = doc.GetElement(picked) as FamilyInstance
        ?? throw new InvalidOperationException("The picked element is not a family instance.");
      FamilySymbol sourceSymbol = instance.Symbol
        ?? throw new InvalidOperationException("The picked instance has no type.");
      string parentName = sourceSymbol.Name;
      if (!variesFiles.TryGetValue(parentName, out string? variesPath) || variesPath == null)
      {
        throw new InvalidOperationException($"No varies file for type '{parentName}'.");
      }

      XYZ start = uidoc.Selection.PickPoint("Pick start point (original type, then child _1, _2, …)");
      XYZ end = uidoc.Selection.PickPoint("Pick end point (last child type)");
      return ApplyToInstance(uidoc, instance, folder, variesPath, start, end);
    }

    public static SetRebarVariesResult ApplyToInstance(
      UIDocument uidoc,
      FamilyInstance instance,
      string folder,
      string variesPath,
      XYZ start,
      XYZ end)
    {
      Document doc = uidoc.Document;
      FamilySymbol sourceSymbol = instance.Symbol
        ?? throw new InvalidOperationException("The instance has no type.");
      string parentName = sourceSymbol.Name;
      XYZ direction = end - start;
      if (direction.GetLength() < 1.0 / 304.8)
      {
        throw new InvalidOperationException("The two points are too close to define an order.");
      }

      direction = direction.Normalize();

      VerticalCsvTable varies = VerticalCsvService.LoadTypeShape(variesPath);
      List<string> childNames = varies.TypeNames
        .Where(name => !name.Equals(parentName, StringComparison.OrdinalIgnoreCase))
        .OrderBy(name => ChildOrder(parentName, name))
        .ToList();
      if (childNames.Count == 0)
      {
        throw new InvalidOperationException($"{Path.GetFileName(variesPath)} has no child type columns.");
      }

      List<BarSlot> slots = CollectSlots(instance, start, direction);
      List<string> typeOrder = new List<string> { parentName };
      typeOrder.AddRange(childNames);
      List<BarSlot> assigned = AssignSlots(slots, typeOrder.Count, instance, start, end, direction);

      RebarTypeCreateService.EnsureCsvTemplates(folder);
      string shapePath = Path.Combine(folder, RebarTypeCreateService.TypeShapeFileName);
      string dataPath = Path.Combine(folder, RebarTypeCreateService.TypeDataFileName);
      VerticalCsvTable shape = VerticalCsvService.LoadTypeShape(shapePath);
      VerticalCsvTable data = VerticalCsvService.Load(dataPath);

      shape = VerticalCsvService.EnsureTypeColumns(shape, childNames);
      data = VerticalCsvService.EnsureTypeColumns(data, typeOrder);
      foreach (string childName in childNames)
      {
        shape = VerticalCsvService.CopyTypeColumn(shape, varies, childName);
        shape = VerticalCsvService.SetCell(shape, VerticalCsvService.RebarTypeRowName, childName, parentName);
      }

      for (int i = 0; i < typeOrder.Count; i++)
      {
        data = WriteSingleBar(data, typeOrder[i], i < assigned.Count ? assigned[i] : assigned.Last());
      }

      VerticalCsvService.SaveTypeShape(shapePath, shape);
      VerticalCsvService.Save(dataPath, data);

      var result = new SetRebarVariesResult();
      var childSymbols = new List<FamilySymbol>();
      using (var tx = new Transaction(doc, "NMK Set Rebar Varies"))
      {
        tx.Start();
        if (!sourceSymbol.IsActive)
        {
          sourceSymbol.Activate();
        }

        List<FamilySymbol> existing = ProjectTypeCsvApplier.CollectArraySymbols(doc);
        foreach (string childName in childNames)
        {
          FamilySymbol? symbol = existing.FirstOrDefault(item =>
            item.Name.Equals(childName, StringComparison.OrdinalIgnoreCase));
          if (symbol == null)
          {
            if (sourceSymbol.Duplicate(childName) is not FamilySymbol created)
            {
              result.Warnings.Add($"Could not duplicate type '{childName}'.");
              continue;
            }

            symbol = created;
            existing.Add(symbol);
            result.TypesCreated++;
          }

          if (!symbol.IsActive)
          {
            symbol.Activate();
          }

          childSymbols.Add(symbol);
        }

        List<FamilySymbol> applySymbols = new List<FamilySymbol> { sourceSymbol };
        applySymbols.AddRange(childSymbols);
        doc.Regenerate();
        ProjectTypeCsvApplyResult applied = ProjectTypeCsvApplier.ApplyInOpenTransaction(
          doc,
          applySymbols,
          shape,
          VerticalCsvService.WithCumulativeZ(data));
        result.TypesUpdated = applied.TypesUpdated;
        result.Warnings.AddRange(applied.Warnings);

        foreach (FamilySymbol child in childSymbols)
        {
          ICollection<ElementId> copied = ElementTransformUtils.CopyElement(doc, instance.Id, XYZ.Zero);
          foreach (ElementId id in copied)
          {
            if (doc.GetElement(id) is FamilyInstance copy)
            {
              copy.ChangeTypeId(child.Id);
              result.CopiedInstances.Add(copy);
              result.InstancesCopied++;
            }
          }
        }

        tx.Commit();
      }

      if (slots.Count != typeOrder.Count)
      {
        result.Warnings.Add($"Nested bars: {slots.Count}, types (parent + children): {typeOrder.Count}. Extra bars were dropped or extra types reused the last/interpolated point.");
      }

      return result;
    }

    private static int ChildOrder(string parentName, string childName)
    {
      if (childName.StartsWith(parentName + "_", StringComparison.OrdinalIgnoreCase)
          && int.TryParse(childName.Substring(parentName.Length + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
      {
        return index;
      }

      return int.MaxValue;
    }

    private static List<BarSlot> CollectSlots(FamilyInstance instance, XYZ start, XYZ direction)
    {
      var slots = new List<BarSlot>();
      Document doc = instance.Document;
      foreach (ElementId id in instance.GetSubComponentIds())
      {
        if (doc.GetElement(id) is not FamilyInstance nested || !CreateRebarByLineService.IsRebarShapeInstance(nested))
        {
          continue;
        }

        if (IsHidden(nested))
        {
          continue;
        }

        XYZ origin = nested.GetTotalTransform().Origin;
        slots.Add(new BarSlot(
          ReadMm(nested, "X"),
          ReadMm(nested, "Y"),
          ReadMm(nested, "Z"),
          (origin - start).DotProduct(direction)));
      }

      return slots
        .OrderBy(slot => slot.Order)
        .ToList();
    }

    private static List<BarSlot> AssignSlots(
      List<BarSlot> existing,
      int typeCount,
      FamilyInstance instance,
      XYZ start,
      XYZ end,
      XYZ direction)
    {
      if (existing.Count >= typeCount)
      {
        return existing.Take(typeCount).ToList();
      }

      var assigned = new List<BarSlot>(existing);
      Transform inverse = instance.GetTotalTransform().Inverse;
      int total = typeCount;
      for (int i = assigned.Count; i < total; i++)
      {
        double t = total == 1 ? 0 : (double)i / (total - 1);
        XYZ world = start + (end - start) * t;
        XYZ local = inverse.OfPoint(world);
        assigned.Add(new BarSlot(
          ToMm(local.X),
          ToMm(local.Y),
          ToMm(local.Z),
          (world - start).DotProduct(direction)));
      }

      return assigned;
    }

    private static VerticalCsvTable WriteSingleBar(VerticalCsvTable data, string typeName, BarSlot slot)
    {
      VerticalCsvTable result = data;
      for (int n = 1; n <= SlotCount; n++)
      {
        string suffix = n.ToString(CultureInfo.InvariantCulture);
        result = VerticalCsvService.SetCell(result, "X_" + suffix, typeName, n == 1 ? Format(slot.X) : string.Empty);
        result = VerticalCsvService.SetCell(result, "Y_" + suffix, typeName, n == 1 ? Format(slot.Y) : string.Empty);
        result = VerticalCsvService.SetCell(result, "Z_" + suffix, typeName, n == 1 ? Format(slot.Z) : string.Empty);
      }

      return result;
    }

    private static bool IsHidden(FamilyInstance nested)
    {
      Parameter? visible = nested.LookupParameter("Visible");
      if (visible == null || visible.StorageType != StorageType.Integer)
      {
        return false;
      }

      return visible.AsInteger() == 0;
    }

    private static double ReadMm(Element element, string name)
    {
      Parameter? parameter = element.LookupParameter(name);
      if (parameter == null || parameter.StorageType != StorageType.Double)
      {
        return 0;
      }

      return ToMm(parameter.AsDouble());
    }

    private static double ToMm(double feet)
    {
      return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    }

    private static string Format(double value)
    {
      return Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
    }

    private readonly struct BarSlot
    {
      public BarSlot(double x, double y, double z, double order)
      {
        X = x;
        Y = y;
        Z = z;
        Order = order;
      }

      public double X { get; }

      public double Y { get; }

      public double Z { get; }

      public double Order { get; }
    }
  }
}

using Autodesk.Revit.DB;
using System.IO;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class GenericModelTypeResult
  {
    public string FamilyName { get; set; } = string.Empty;
    public int TypesCreated { get; set; }
    public int TypesUpdated { get; set; }
    public int ValuesSet { get; set; }
    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var sb = new StringBuilder();
      sb.AppendLine($"Family: {FamilyName}");
      sb.AppendLine($"Types tạo mới: {TypesCreated}");
      sb.AppendLine($"Types cập nhật: {TypesUpdated}");
      sb.AppendLine($"Giá trị Parameters đã gán: {ValuesSet}");

      if (Warnings.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine($"Cảnh báo ({Warnings.Count}):");
        foreach (string w in Warnings.Take(25))
        {
          sb.AppendLine($"  ⚠ {w}");
        }
        if (Warnings.Count > 25)
        {
          sb.AppendLine($"  ... và {Warnings.Count - 25} cảnh báo khác.");
        }
      }

      return sb.ToString();
    }
  }

  public static class GenericModelTypeCsvService
  {
    /// <summary>
    /// Gets all Family names belonging to the Generic Model category in the document.
    /// Combines multiple retrieval strategies to ensure all Generic Model families are included.
    /// </summary>
    public static List<string> GetGenericModelFamilyNames(Document doc)
    {
      if (doc == null)
      {
        return new List<string>();
      }

      var familyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      // Strategy 1: Find all FamilySymbols belonging to OST_GenericModel
      try
      {
        var symbols = new FilteredElementCollector(doc)
          .OfCategory(BuiltInCategory.OST_GenericModel)
          .OfClass(typeof(FamilySymbol))
          .Cast<FamilySymbol>();

        foreach (var symbol in symbols)
        {
          if (symbol.Family != null && !string.IsNullOrWhiteSpace(symbol.Family.Name))
          {
            familyNames.Add(symbol.Family.Name);
          }
        }
      }
      catch
      {
      }

      // Strategy 2: Find all Family elements directly
      try
      {
        var families = new FilteredElementCollector(doc)
          .OfClass(typeof(Family))
          .Cast<Family>();

        foreach (var f in families)
        {
          if (string.IsNullOrWhiteSpace(f.Name))
          {
            continue;
          }

          if (f.FamilyCategory != null
              && CreateRebarByLineService.IdValue(f.FamilyCategory.Id) == (long)BuiltInCategory.OST_GenericModel)
          {
            familyNames.Add(f.Name);
            continue;
          }

          var symbolIds = f.GetFamilySymbolIds();
          if (symbolIds != null && symbolIds.Count > 0)
          {
            if (doc.GetElement(symbolIds.First()) is FamilySymbol firstSymbol
                && firstSymbol.Category != null
                && CreateRebarByLineService.IdValue(firstSymbol.Category.Id) == (long)BuiltInCategory.OST_GenericModel)
            {
              familyNames.Add(f.Name);
            }
          }
        }
      }
      catch
      {
      }

      return familyNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Finds a Family belonging to Generic Model category by name using multiple strategies.
    /// </summary>
    public static Family? FindGenericModelFamily(Document doc, string familyName)
    {
      if (doc == null || string.IsNullOrWhiteSpace(familyName))
      {
        return null;
      }

      string target = familyName.Trim();

      // Strategy 1: Search via FamilySymbol in OST_GenericModel
      try
      {
        var symbol = new FilteredElementCollector(doc)
          .OfCategory(BuiltInCategory.OST_GenericModel)
          .OfClass(typeof(FamilySymbol))
          .Cast<FamilySymbol>()
          .FirstOrDefault(s => s.Family != null && s.Family.Name.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (symbol?.Family != null)
        {
          return symbol.Family;
        }
      }
      catch
      {
      }

      // Strategy 2: Search via Family elements
      try
      {
        var families = new FilteredElementCollector(doc)
          .OfClass(typeof(Family))
          .Cast<Family>()
          .Where(f => f.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
          .ToList();

        foreach (var f in families)
        {
          if (f.FamilyCategory != null
              && CreateRebarByLineService.IdValue(f.FamilyCategory.Id) == (long)BuiltInCategory.OST_GenericModel)
          {
            return f;
          }

          var symbolIds = f.GetFamilySymbolIds();
          if (symbolIds != null && symbolIds.Count > 0)
          {
            if (doc.GetElement(symbolIds.First()) is FamilySymbol firstSymbol
                && firstSymbol.Category != null
                && CreateRebarByLineService.IdValue(firstSymbol.Category.Id) == (long)BuiltInCategory.OST_GenericModel)
            {
              return f;
            }
          }

          return f;
        }
      }
      catch
      {
      }

      return null;
    }

    /// <summary>
    /// Concatenates column 1 and column 2 to form the Type name using the specified separator.
    /// </summary>
    public static string BuildTypeName(string col1, string col2, string separator = "_")
    {
      col1 = col1?.Trim() ?? string.Empty;
      col2 = col2?.Trim() ?? string.Empty;

      if (string.IsNullOrEmpty(col1))
      {
        return col2;
      }

      if (string.IsNullOrEmpty(col2))
      {
        return col1;
      }

      if (string.IsNullOrEmpty(separator))
      {
        return col1 + col2;
      }

      if (col1.EndsWith(separator, StringComparison.Ordinal) || col2.StartsWith(separator, StringComparison.Ordinal))
      {
        string left = col1.EndsWith(separator, StringComparison.Ordinal)
          ? col1.Substring(0, col1.Length - separator.Length)
          : col1;
        string right = col2.StartsWith(separator, StringComparison.Ordinal)
          ? col2.Substring(separator.Length)
          : col2;
        return $"{left}{separator}{right}";
      }

      return $"{col1}{separator}{col2}";
    }

    /// <summary>
    /// Reads CSV file:
    /// - Column 1 and Column 2 concatenated form the Type Name.
    /// - Row 0 from Column 3 onwards contains Parameter Names.
    /// - Values are in mm for length parameters.
    /// Creates or updates types and parameters for the specified Generic Model family.
    /// </summary>
    public static GenericModelTypeResult Apply(Document doc, string familyName, string csvPath, string separator = "_")
    {
      if (!File.Exists(csvPath))
      {
        throw new FileNotFoundException($"Không tìm thấy file CSV: {csvPath}");
      }

      if (string.IsNullOrWhiteSpace(familyName))
      {
        throw new InvalidOperationException("Chưa chọn Family Generic Model.");
      }

      Family? family = FindGenericModelFamily(doc, familyName);

      if (family == null)
      {
        throw new InvalidOperationException($"Không tìm thấy Family '{familyName}' thuộc Category Generic Model trong dự án.");
      }

      var symbolIds = family.GetFamilySymbolIds();
      if (symbolIds == null || symbolIds.Count == 0)
      {
        throw new InvalidOperationException($"Family '{familyName}' không có Type (FamilySymbol) nào để nhân bản hoặc chỉnh sửa.");
      }

      var symbols = symbolIds
        .Select(id => doc.GetElement(id))
        .OfType<FamilySymbol>()
        .ToList();

      if (symbols.Count == 0)
      {
        throw new InvalidOperationException($"Không thể lấy FamilySymbol từ Family '{familyName}'.");
      }

      FamilySymbol template = symbols[0];

      string[] lines = VerticalCsvService.ReadAllLinesShared(csvPath, Encoding.UTF8)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToArray();

      if (lines.Length < 2)
      {
        throw new InvalidOperationException("File CSV cần ít nhất 1 dòng tiêu đề Parameter và các dòng dữ liệu Type.");
      }

      // Row 0: Parameter names (from col index 2 onwards)
      IReadOnlyList<string> header = VerticalCsvService.ParseCsvLine(lines[0]);
      var colToParamName = new Dictionary<int, string>();
      for (int c = 2; c < header.Count; c++)
      {
        string paramName = header[c].Trim();
        if (!string.IsNullOrWhiteSpace(paramName))
        {
          colToParamName[c] = paramName;
        }
      }

      var result = new GenericModelTypeResult
      {
        FamilyName = family.Name
      };

      using (var tx = new Transaction(doc, $"NMK Generic Model Types - {family.Name}"))
      {
        tx.Start();

        for (int r = 1; r < lines.Length; r++)
        {
          IReadOnlyList<string> cells = VerticalCsvService.ParseCsvLine(lines[r]);
          if (cells.Count == 0)
          {
            continue;
          }

          string col1 = cells.Count > 0 ? cells[0].Trim() : string.Empty;
          string col2 = cells.Count > 1 ? cells[1].Trim() : string.Empty;
          string typeName = BuildTypeName(col1, col2, separator);

          if (string.IsNullOrWhiteSpace(typeName))
          {
            continue;
          }

          FamilySymbol? symbol = symbols.FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
          if (symbol == null)
          {
            try
            {
              if (template.Duplicate(typeName) is FamilySymbol created)
              {
                symbol = created;
                symbols.Add(created);
                result.TypesCreated++;
              }
              else
              {
                result.Warnings.Add($"Không thể duplicate Type '{typeName}' từ template '{template.Name}'.");
                continue;
              }
            }
            catch (Exception ex)
            {
              result.Warnings.Add($"Lỗi khi tạo Type '{typeName}': {ex.Message}");
              continue;
            }
          }
          else
          {
            result.TypesUpdated++;
          }

          if (!symbol.IsActive)
          {
            symbol.Activate();
          }

          foreach (var kvp in colToParamName)
          {
            int colIndex = kvp.Key;
            string paramName = kvp.Value;

            string rawValue = colIndex < cells.Count ? cells[colIndex].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
              continue;
            }

            Parameter? parameter = symbol.LookupParameter(paramName);
            if (parameter == null)
            {
              parameter = symbol.Parameters.Cast<Parameter>().FirstOrDefault(p =>
                p.Definition.Name.Trim().Equals(paramName, StringComparison.OrdinalIgnoreCase));
            }

            if (parameter == null)
            {
              result.Warnings.Add($"Type '{typeName}': Family không có parameter '{paramName}'.");
              continue;
            }

            if (parameter.IsReadOnly)
            {
              result.Warnings.Add($"Type '{typeName}': Parameter '{paramName}' là Read-Only (hoặc có công thức Formula).");
              continue;
            }

            if (SetParameterValue(parameter, rawValue, result.Warnings, typeName))
            {
              result.ValuesSet++;
            }
          }
        }

        tx.Commit();
      }

      return result;
    }

    private static bool SetParameterValue(Parameter parameter, string raw, List<string> warnings, string typeName)
    {
      try
      {
        switch (parameter.StorageType)
        {
          case StorageType.Integer:
            if (VerticalCsvService.TryParseYesNo(raw, out int yesNo))
            {
              return parameter.Set(yesNo);
            }

            if (VerticalCsvService.TryParseNumber(raw, out double integerNumber))
            {
              return parameter.Set(Convert.ToInt32(Math.Round(integerNumber)));
            }

            warnings.Add($"Type '{typeName}': Không thể chuyển '{raw}' thành số nguyên cho '{parameter.Definition.Name}'.");
            return false;

          case StorageType.Double:
            if (!VerticalCsvService.TryParseNumber(raw, out double number))
            {
              warnings.Add($"Type '{typeName}': Không thể chuyển '{raw}' thành số cho '{parameter.Definition.Name}'.");
              return false;
            }

            double internalValue = ToInternalValue(parameter, number);
            return parameter.Set(internalValue);

          case StorageType.String:
            return parameter.Set(raw);

          default:
            warnings.Add($"Type '{typeName}': Không hỗ trợ StorageType '{parameter.StorageType}' cho '{parameter.Definition.Name}'.");
            return false;
        }
      }
      catch (Exception ex)
      {
        warnings.Add($"Type '{typeName}', Parameter '{parameter.Definition.Name}': {ex.Message}");
        return false;
      }
    }

    private static double ToInternalValue(Parameter parameter, double displayMm)
    {
      try
      {
        ForgeTypeId spec = parameter.Definition.GetDataType();
        if (spec == SpecTypeId.Length)
        {
          return UnitUtils.ConvertToInternalUnits(displayMm, UnitTypeId.Millimeters);
        }

        if (spec == SpecTypeId.Angle)
        {
          return UnitUtils.ConvertToInternalUnits(displayMm, UnitTypeId.Degrees);
        }

        if (spec == SpecTypeId.Number)
        {
          return displayMm;
        }
      }
      catch
      {
      }

      // Default: In Generic Model, double values from CSV with unit mm are dimensions (Length)
      return UnitUtils.ConvertToInternalUnits(displayMm, UnitTypeId.Millimeters);
    }
  }
}

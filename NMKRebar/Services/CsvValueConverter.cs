using Autodesk.Revit.DB;
using System.Globalization;
using System.Text;

namespace NMKRebar.Services
{
  public static class CsvValueConverter
  {
    public static bool TrySetParameter(Parameter parameter, string raw, List<string> warnings)
    {
      if (parameter == null || parameter.IsReadOnly || string.IsNullOrWhiteSpace(raw))
      {
        return false;
      }

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
              return parameter.Set(Convert.ToInt32(integerNumber));
            }

            warnings.Add($"Could not parse integer '{raw}' for '{parameter.Definition.Name}'.");
            return false;

          case StorageType.Double:
            if (!VerticalCsvService.TryParseNumber(raw, out double number))
            {
              warnings.Add($"Could not parse number '{raw}' for '{parameter.Definition.Name}'.");
              return false;
            }

            return parameter.Set(ToInternalValue(parameter, number));

          case StorageType.String:
            return parameter.Set(raw);

          default:
            warnings.Add($"Unsupported storage type for '{parameter.Definition.Name}'.");
            return false;
        }
      }
      catch (Exception ex)
      {
        warnings.Add($"{parameter.Definition.Name}: {ex.Message}");
        return false;
      }
    }

    public static bool IsYesNo(Parameter parameter)
    {
      ForgeTypeId spec = parameter.Definition.GetDataType();
      if (spec == SpecTypeId.Boolean.YesNo)
      {
        return true;
      }

      string name = parameter.Definition.Name;
      return FamilyTypeCsvApplier.IsYesNoParameterName(name);
    }

    public static bool IsYesNoName(string name)
    {
      return FamilyTypeCsvApplier.IsYesNoParameterName(name);
    }

    public static string GetDisplayValue(Parameter parameter)
    {
      if (parameter == null || !parameter.HasValue)
      {
        return string.Empty;
      }

      switch (parameter.StorageType)
      {
        case StorageType.Integer:
          int integer = parameter.AsInteger();
          if (IsYesNo(parameter))
          {
            return integer == 1 ? "Yes" : "No";
          }

          return integer.ToString(CultureInfo.InvariantCulture);

        case StorageType.Double:
          double display = FromInternalValue(parameter, parameter.AsDouble());
          return display.ToString("0.###", CultureInfo.InvariantCulture);

        case StorageType.String:
          return parameter.AsString() ?? string.Empty;

        default:
          return string.Empty;
      }
    }

    public static double FromInternalValue(Parameter parameter, double internalValue)
    {
      ForgeTypeId spec = parameter.Definition.GetDataType();
      if (spec == SpecTypeId.Length)
      {
        return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters);
      }

      if (spec == SpecTypeId.Angle)
      {
        return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Degrees);
      }

      return internalValue;
    }

    public static bool TrySetFamilyParameter(FamilyManager fm, FamilyParameter parameter, string raw, List<string> warnings)
    {
      if (parameter.IsDeterminedByFormula || string.IsNullOrWhiteSpace(raw))
      {
        return false;
      }

      try
      {
        switch (parameter.StorageType)
        {
          case StorageType.Integer:
            if (VerticalCsvService.TryParseYesNo(raw, out int yesNo))
            {
              fm.Set(parameter, yesNo);
              return true;
            }

            if (VerticalCsvService.TryParseNumber(raw, out double integerNumber))
            {
              fm.Set(parameter, Convert.ToInt32(integerNumber));
              return true;
            }

            warnings.Add($"Could not parse integer '{raw}' for '{parameter.Definition.Name}'.");
            return false;

          case StorageType.Double:
            if (!VerticalCsvService.TryParseNumber(raw, out double number))
            {
              warnings.Add($"Could not parse number '{raw}' for '{parameter.Definition.Name}'.");
              return false;
            }

            fm.Set(parameter, ToInternalValue(parameter.Definition.GetDataType(), number));
            return true;

          case StorageType.String:
            fm.Set(parameter, raw);
            return true;

          default:
            warnings.Add($"Unsupported storage type for '{parameter.Definition.Name}'.");
            return false;
        }
      }
      catch (Exception ex)
      {
        warnings.Add($"{parameter.Definition.Name}: {ex.Message}");
        return false;
      }
    }

    public static double ToInternalValue(Parameter parameter, double displayValue)
    {
      return ToInternalValue(parameter.Definition.GetDataType(), displayValue);
    }

    public static double ToInternalValue(ForgeTypeId spec, double displayValue)
    {
      if (spec == SpecTypeId.Length)
      {
        return UnitUtils.ConvertToInternalUnits(displayValue, UnitTypeId.Millimeters);
      }

      if (spec == SpecTypeId.Angle)
      {
        return UnitUtils.ConvertToInternalUnits(displayValue, UnitTypeId.Degrees);
      }

      return displayValue;
    }
  }

  public sealed class ProjectTypeCsvApplyResult
  {
    public int TypesUpdated { get; set; }

    public int ValuesSet { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Types updated: {TypesUpdated}");
      text.AppendLine($"Values set: {ValuesSet}");
      if (Warnings.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("Warnings:");
        foreach (string warning in Warnings.Take(20))
        {
          text.AppendLine("- " + warning);
        }

        if (Warnings.Count > 20)
        {
          text.AppendLine($"... and {Warnings.Count - 20} more");
        }
      }

      return text.ToString();
    }
  }

  public static class ProjectTypeCsvApplier
  {
    public static List<FamilySymbol> CollectArraySymbols(Document doc)
    {
      Family? family = RebarTypeCreateService.FindArrayFamily(doc);

      if (family == null)
      {
        throw new InvalidOperationException($"Family '{RebarTypeCreateService.ArrayFamilyName}' (Structural Framing) is not loaded in this project.");
      }

      return family.GetFamilySymbolIds()
        .Select(id => doc.GetElement(id))
        .OfType<FamilySymbol>()
        .ToList();
    }

    public static ProjectTypeCsvApplyResult ApplyFolderCsv(Document doc, VerticalCsvTable table, string transactionName)
    {
      var missing = new List<string>();
      List<FamilySymbol> all = CollectArraySymbols(doc);
      var matched = new List<FamilySymbol>();
      foreach (string typeName in table.TypeNames)
      {
        FamilySymbol? symbol = all.FirstOrDefault(item =>
          item.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (symbol == null)
        {
          missing.Add($"Type '{typeName}' from CSV was not found on '{RebarTypeCreateService.ArrayFamilyName}'.");
          continue;
        }

        matched.Add(symbol);
      }

      if (matched.Count == 0)
      {
        throw new InvalidOperationException("No NMK_Rebar_Array types in the project match the CSV type columns.");
      }

      ProjectTypeCsvApplyResult applied = Apply(doc, matched, transactionName, table);
      applied.Warnings.InsertRange(0, missing);
      return applied;
    }

    public static ProjectTypeCsvApplyResult Apply(Document doc, IReadOnlyCollection<FamilySymbol> symbols, params VerticalCsvTable[] tables)
    {
      return Apply(doc, symbols, "NMK Set Data Type", tables);
    }

    public static ProjectTypeCsvApplyResult Apply(
      Document doc,
      IReadOnlyCollection<FamilySymbol> symbols,
      string transactionName,
      params VerticalCsvTable[] tables)
    {
      return ApplyCore(doc, symbols, transactionName, startTransaction: true, tables);
    }

    public static ProjectTypeCsvApplyResult ApplyInOpenTransaction(
      Document doc,
      IReadOnlyCollection<FamilySymbol> symbols,
      params VerticalCsvTable[] tables)
    {
      return ApplyCore(doc, symbols, "NMK Set Data Type", startTransaction: false, tables);
    }

    private static ProjectTypeCsvApplyResult ApplyCore(
      Document doc,
      IReadOnlyCollection<FamilySymbol> symbols,
      string transactionName,
      bool startTransaction,
      VerticalCsvTable[] tables)
    {
      var result = new ProjectTypeCsvApplyResult();
      var unique = symbols
        .GroupBy(symbol => symbol.Id)
        .Select(group => group.First())
        .ToList();

      Transaction? tx = null;
      if (startTransaction)
      {
        tx = new Transaction(doc, transactionName);
        tx.Start();
      }

      try
      {

        foreach (FamilySymbol symbol in unique)
        {
          if (!symbol.IsActive)
          {
            symbol.Activate();
          }

          int setOnType = 0;
          bool foundInCsv = false;
          foreach (VerticalCsvTable table in tables)
          {
            int typeIndex = IndexOfType(table, symbol.Name);
            if (typeIndex < 0)
            {
              continue;
            }

            foundInCsv = true;

            for (int p = 0; p < table.ParameterNames.Count; p++)
            {
              string parameterName = table.ParameterNames[p];
              string raw = typeIndex < table.ValueRows[p].Count ? table.ValueRows[p][typeIndex] : string.Empty;
              Parameter? parameter = symbol.LookupParameter(parameterName);
              if (parameter == null)
              {
                if (!string.IsNullOrWhiteSpace(raw))
                {
                  result.Warnings.Add($"'{symbol.Name}' has no parameter '{parameterName}'.");
                }

                continue;
              }

              if (CsvValueConverter.TrySetParameter(parameter, raw, result.Warnings))
              {
                setOnType++;
                result.ValuesSet++;
              }
            }
          }

          if (!foundInCsv)
          {
            result.Warnings.Add($"Type '{symbol.Name}' was not found in CSV.");
          }

          if (setOnType > 0)
          {
            result.TypesUpdated++;
          }
        }

        tx?.Commit();
      }
      catch
      {
        if (tx != null && tx.HasStarted())
        {
          tx.RollBack();
        }

        throw;
      }
      finally
      {
        tx?.Dispose();
      }

      return result;
    }

    private static int IndexOfType(VerticalCsvTable table, string typeName)
    {
      for (int i = 0; i < table.TypeNames.Count; i++)
      {
        if (table.TypeNames[i].Equals(typeName, StringComparison.OrdinalIgnoreCase))
        {
          return i;
        }
      }

      return -1;
    }
  }
}

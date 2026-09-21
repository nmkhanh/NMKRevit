using Autodesk.Revit.DB;

namespace NMKRebar.Services
{
  public sealed class FamilyTypeCsvApplyResult
  {
    public int TypesCreated { get; set; }

    public int TypesUpdated { get; set; }

    public int ParametersAdded { get; set; }

    public int ValuesSet { get; set; }

    public List<string> Warnings { get; } = new();
  }

  public static class FamilyTypeCsvApplier
  {
    public static FamilyTypeCsvApplyResult Apply(Document doc, TypeShapeTable table)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Active document is not a family document.");
      }

      var result = new FamilyTypeCsvApplyResult();
      FamilyManager fm = doc.FamilyManager;

      using (var tx = new Transaction(doc, "NMK Add Parameters By CSV"))
      {
        tx.Start();
        EnsureCurrentType(fm);
        EnsureSegments6To10(fm, result);
        ApplySegment6To10Defaults(fm, result);

        foreach (string parameterName in table.ParameterNames)
        {
          if (FindParameter(fm, parameterName) == null)
          {
            bool other = !FamilyParameterGroups.KeepInDimensions(parameterName);
            FamilyParameterGroups.AddFamilyParameter(
              fm,
              parameterName,
              InferSpec(parameterName),
              isInstance: false,
              other ? FamilyParameterGroups.ParameterBucket.Other : FamilyParameterGroups.ParameterBucket.Dimensions);
            result.ParametersAdded++;
          }
        }

        foreach (TypeShapeRow row in table.Rows)
        {
          FamilyType? existing = FindType(fm, row.TypeName);
          if (existing == null)
          {
            fm.NewType(row.TypeName);
            result.TypesCreated++;
          }
          else
          {
            fm.CurrentType = existing;
            result.TypesUpdated++;
          }

          foreach (string parameterName in table.ParameterNames)
          {
            FamilyParameter? parameter = FindParameter(fm, parameterName);
            if (parameter == null)
            {
              result.Warnings.Add($"Parameter '{parameterName}' was not found for type '{row.TypeName}'.");
              continue;
            }

            row.Values.TryGetValue(parameterName, out string? raw);
            if (CsvValueConverter.TrySetFamilyParameter(fm, parameter, raw ?? string.Empty, result.Warnings))
            {
              result.ValuesSet++;
            }
          }
        }

        tx.Commit();
      }

      return result;
    }

    private static void EnsureSegments6To10(FamilyManager fm, FamilyTypeCsvApplyResult result)
    {
      for (int n = SetTypeEditorService.DimensionStart; n <= SetTypeEditorService.DimensionEnd; n++)
      {
        EnsureParameter(fm, $"{n}_Bending", SpecTypeId.Length, true, result);
        EnsureParameter(fm, $"{n}_L", SpecTypeId.Length, true, result);
        EnsureParameter(fm, $"{n}_Angle", SpecTypeId.Angle, true, result);
        EnsureParameter(fm, $"{n}_V", SpecTypeId.Boolean.YesNo, true, result);
        EnsureParameter(fm, $"{n}_V_", SpecTypeId.Boolean.YesNo, true, result);
        EnsureParameter(fm, $"{n}_V_Curve", SpecTypeId.Boolean.YesNo, true, result);
        EnsureParameter(fm, $"{n}_L_Curve", SpecTypeId.Length, true, result);
        EnsureParameter(fm, $"Curve_{n}", SpecTypeId.Boolean.YesNo, true, result);
      }

      EnsureParameter(fm, "Angle_Hook", SpecTypeId.Angle, true, result);
    }

    private static void EnsureParameter(
      FamilyManager fm,
      string name,
      ForgeTypeId spec,
      bool otherGroup,
      FamilyTypeCsvApplyResult result)
    {
      FamilyParameter? existing = FindParameter(fm, name);
      if (existing != null)
      {
        if (!existing.IsInstance)
        {
          try
          {
            fm.MakeInstance(existing);
          }
          catch (Exception ex)
          {
            result.Warnings.Add($"Make instance {name}: {ex.Message}");
          }
        }

        return;
      }

      FamilyParameterGroups.AddFamilyParameter(fm, name, spec, otherGroup, true);
      result.ParametersAdded++;
    }

    private static void EnsureCurrentType(FamilyManager fm)
    {
      if (fm.CurrentType != null)
      {
        return;
      }

      FamilyType? first = fm.Types.Cast<FamilyType>().FirstOrDefault();
      if (first != null)
      {
        fm.CurrentType = first;
      }
    }

    private static void ApplySegment6To10Defaults(FamilyManager fm, FamilyTypeCsvApplyResult result)
    {
      if (fm.CurrentType == null)
      {
        return;
      }

      FamilyType keep = fm.CurrentType;
      foreach (FamilyType type in fm.Types.Cast<FamilyType>())
      {
        fm.CurrentType = type;
        for (int n = 6; n <= 10; n++)
        {
          SetDefault(fm, $"{n}_Bending", "0", result);
          SetDefault(fm, $"{n}_L", "500", result);
          SetDefault(fm, $"{n}_Angle", "90", result);
          SetDefault(fm, $"{n}_V", "No", result);
          SetDefault(fm, $"{n}_V_", "No", result);
          SetDefault(fm, $"{n}_V_Curve", "No", result);
          SetDefault(fm, $"{n}_L_Curve", "500", result);
          SetDefault(fm, $"Curve_{n}", "No", result);
        }
      }

      fm.CurrentType = keep;
    }

    private static void SetDefault(
      FamilyManager fm,
      string name,
      string raw,
      FamilyTypeCsvApplyResult result)
    {
      FamilyParameter? parameter = FindParameter(fm, name);
      if (parameter == null)
      {
        return;
      }

      if (CsvValueConverter.TrySetFamilyParameter(fm, parameter, raw, result.Warnings))
      {
        result.ValuesSet++;
      }
    }

    private static ForgeTypeId InferSpec(string parameterName)
    {
      if (IsYesNoParameterName(parameterName))
      {
        return SpecTypeId.Boolean.YesNo;
      }

      if (parameterName.Contains("Angle", StringComparison.OrdinalIgnoreCase))
      {
        return SpecTypeId.Angle;
      }

      if (parameterName.Equals("Bending_Factor", StringComparison.OrdinalIgnoreCase))
      {
        return SpecTypeId.Number;
      }

      if (parameterName.Equals(VerticalCsvService.RebarTypeRowName, StringComparison.OrdinalIgnoreCase))
      {
        return SpecTypeId.String.Text;
      }

      return SpecTypeId.Length;
    }

    public static bool IsYesNoParameterName(string name)
    {
      return name.Equals("Curve", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Curve_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Visible_", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_V", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_V_", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_V_Curve", StringComparison.OrdinalIgnoreCase);
    }

    private static FamilyParameter? FindParameter(FamilyManager fm, string name)
    {
      foreach (FamilyParameter parameter in fm.Parameters)
      {
        if (string.Equals(parameter.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
        {
          return parameter;
        }
      }

      return null;
    }

    private static FamilyType? FindType(FamilyManager fm, string name)
    {
      foreach (FamilyType type in fm.Types)
      {
        if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
        {
          return type;
        }
      }

      return null;
    }
  }
}

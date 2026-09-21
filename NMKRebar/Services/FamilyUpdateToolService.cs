using Autodesk.Revit.DB;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class FamilyUpdateToolResult
  {
    public int ParametersAdded { get; set; }

    public int MadeType { get; set; }

    public int MadeInstance { get; set; }

    public int AssociationsSet { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Parameters added: {ParametersAdded}");
      text.AppendLine($"Made type (Dimensions, values kept): {MadeType}");
      text.AppendLine($"Made instance (1L_n / 3L_n): {MadeInstance}");
      text.AppendLine($"1L/3L associations: {AssociationsSet}");
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

  public static class FamilyUpdateToolService
  {
    public static FamilyUpdateToolResult Apply(Document doc)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Update Tool Family runs in a family document.");
      }

      var result = new FamilyUpdateToolResult();
      FamilyManager fm = doc.FamilyManager;
      using (var tx = new Transaction(doc, "NMK Update Tool Family"))
      {
        tx.Start();
        EnsureCurrentType(fm);
        ConvertDimensionParameters(fm, result);
        EnsureAngleHookType(fm, result);
        EnsureMappedLParameters(fm, result);
        MapNestedLParameters(doc, fm, result);
        tx.Commit();
      }

      return result;
    }

    private static void ConvertDimensionParameters(FamilyManager fm, FamilyUpdateToolResult result)
    {
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        if (FamilyParameterGroups.IsBuiltIn(parameter) || !FamilyParameterGroups.IsDimensions(parameter))
        {
          continue;
        }

        if (IsMappedLSlot(parameter.Definition.Name))
        {
          MakeInstanceKeepValues(fm, parameter, result);
        }
        else
        {
          MakeTypeKeepValues(fm, parameter, result);
        }
      }
    }

    private static void EnsureAngleHookType(FamilyManager fm, FamilyUpdateToolResult result)
    {
      FamilyParameter? existing = Find(fm, "Angle_Hook");
      if (existing != null)
      {
        if (FamilyParameterGroups.IsDimensions(existing))
        {
          MakeTypeKeepValues(fm, existing, result);
        }

        return;
      }

      try
      {
        FamilyParameterGroups.AddFamilyParameter(
          fm,
          "Angle_Hook",
          SpecTypeId.Angle,
          isInstance: false,
          FamilyParameterGroups.ParameterBucket.Dimensions);
        result.ParametersAdded++;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"Add Angle_Hook: {ex.Message}");
      }
    }

    private static void EnsureMappedLParameters(FamilyManager fm, FamilyUpdateToolResult result)
    {
      for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
      {
        EnsureInstanceLength(fm, $"1L_{n}", result);
        EnsureInstanceLength(fm, $"3L_{n}", result);
      }
    }

    private static void EnsureInstanceLength(FamilyManager fm, string name, FamilyUpdateToolResult result)
    {
      FamilyParameter? existing = Find(fm, name);
      if (existing != null)
      {
        MakeInstanceKeepValues(fm, existing, result);
        return;
      }

      try
      {
        FamilyParameterGroups.AddFamilyParameter(
          fm,
          name,
          SpecTypeId.Length,
          isInstance: true,
          FamilyParameterGroups.ParameterBucket.Dimensions);
        result.ParametersAdded++;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"Add {name}: {ex.Message}");
      }
    }

    private static void MapNestedLParameters(Document doc, FamilyManager fm, FamilyUpdateToolResult result)
    {
      List<FamilyInstance> instances = CollectNestedInstances(doc);
      if (instances.Count == 0)
      {
        result.Warnings.Add("No nested family instance found to map 1L_n / 3L_n.");
        return;
      }

      if (instances.Count == 1)
      {
        FamilyInstance instance = instances[0];
        for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
        {
          result.AssociationsSet += Associate(fm, instance, $"1L_{n}", $"1L_{n}", result);
          result.AssociationsSet += Associate(fm, instance, $"3L_{n}", $"3L_{n}", result);
        }

        return;
      }

      int count = Math.Min(instances.Count, SetTypeEditorService.ZCount);
      if (instances.Count != SetTypeEditorService.ZCount)
      {
        result.Warnings.Add($"Nested instances: {instances.Count}. Mapping 1_L / 3_L on first {count}.");
      }

      for (int i = 0; i < count; i++)
      {
        int n = i + 1;
        FamilyInstance instance = instances[i];
        result.AssociationsSet += Associate(fm, instance, "1_L", $"1L_{n}", result);
        result.AssociationsSet += Associate(fm, instance, "3_L", $"3L_{n}", result);
      }
    }

    private static int Associate(
      FamilyManager fm,
      FamilyInstance instance,
      string elementParamName,
      string familyParamName,
      FamilyUpdateToolResult result)
    {
      Parameter? elementParam = instance.LookupParameter(elementParamName);
      if (elementParam == null)
      {
        return 0;
      }

      FamilyParameter? familyParam = Find(fm, familyParamName);
      if (familyParam == null)
      {
        result.Warnings.Add($"Missing family parameter '{familyParamName}'.");
        return 0;
      }

      if (!familyParam.IsDeterminedByFormula)
      {
        CsvValueConverter.TrySetFamilyParameter(
          fm,
          familyParam,
          CsvValueConverter.GetDisplayValue(elementParam),
          result.Warnings);
      }

      try
      {
        fm.AssociateElementParameterToFamilyParameter(elementParam, familyParam);
        return 1;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"{instance.Id}: {elementParamName} -> {familyParamName}: {ex.Message}");
        return 0;
      }
    }

    private static List<FamilyInstance> CollectNestedInstances(Document doc)
    {
      var ids = new HashSet<long>();
      var instances = new List<FamilyInstance>();
      foreach (FamilyInstance instance in new FilteredElementCollector(doc)
        .OfClass(typeof(FamilyInstance))
        .Cast<FamilyInstance>())
      {
        long id = CreateRebarByLineService.IdValue(instance.Id);
        if (!ids.Add(id))
        {
          continue;
        }

        instances.Add(instance);
      }

      return instances.OrderBy(item => CreateRebarByLineService.IdValue(item.Id)).ToList();
    }

    private static bool IsMappedLSlot(string name)
    {
      for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
      {
        if (name.Equals($"1L_{n}", StringComparison.OrdinalIgnoreCase)
            || name.Equals($"3L_{n}", StringComparison.OrdinalIgnoreCase))
        {
          return true;
        }
      }

      return false;
    }

    private static void MakeTypeKeepValues(
      FamilyManager fm,
      FamilyParameter parameter,
      FamilyUpdateToolResult result)
    {
      if (!parameter.IsInstance)
      {
        return;
      }

      ConvertKindKeepValues(fm, parameter, toInstance: false, result);
    }

    private static void MakeInstanceKeepValues(
      FamilyManager fm,
      FamilyParameter parameter,
      FamilyUpdateToolResult result)
    {
      if (parameter.IsInstance)
      {
        return;
      }

      ConvertKindKeepValues(fm, parameter, toInstance: true, result);
    }

    private static void ConvertKindKeepValues(
      FamilyManager fm,
      FamilyParameter parameter,
      bool toInstance,
      FamilyUpdateToolResult result)
    {
      string name = parameter.Definition.Name;
      FamilyType? keep = fm.CurrentType;
      var typeValues = new List<(FamilyType Type, string Display)>();
      if (!parameter.IsDeterminedByFormula)
      {
        foreach (FamilyType type in fm.Types.Cast<FamilyType>())
        {
          typeValues.Add((type, type.AsValueString(parameter) ?? string.Empty));
        }
      }

      try
      {
        if (toInstance)
        {
          fm.MakeInstance(parameter);
          result.MadeInstance++;
        }
        else
        {
          fm.MakeType(parameter);
          result.MadeType++;
        }
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"{(toInstance ? "Make instance" : "Make type")} {name}: {ex.Message}");
        return;
      }

      FamilyParameter? converted = Find(fm, name);
      if (converted == null || converted.IsDeterminedByFormula)
      {
        if (keep != null)
        {
          fm.CurrentType = keep;
        }

        return;
      }

      foreach ((FamilyType type, string display) in typeValues)
      {
        fm.CurrentType = type;
        CsvValueConverter.TrySetFamilyParameter(fm, converted, display, result.Warnings);
      }

      if (keep != null)
      {
        fm.CurrentType = keep;
      }
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

    private static FamilyParameter? Find(FamilyManager fm, string name)
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
  }
}

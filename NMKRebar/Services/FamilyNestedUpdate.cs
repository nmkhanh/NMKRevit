using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace NMKRebar.Services
{
  internal static class FamilyNestedUpdate
  {
    public static FamilyInstance? TryResolveInstance(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      List<FamilyInstance> selected = uidoc.Selection.GetElementIds()
        .Select(doc.GetElement)
        .OfType<FamilyInstance>()
        .ToList();
      if (selected.Count == 1)
      {
        return selected[0];
      }

      List<FamilyInstance> nested = CollectNestedInstances(doc);
      if (nested.Count == 0)
      {
        return null;
      }

      if (selected.Count > 1)
      {
        return nested[0];
      }

      return nested[0];
    }

    public static FamilyInstance ResolveInstance(UIDocument uidoc, string action)
    {
      return TryResolveInstance(uidoc)
        ?? throw new InvalidOperationException($"No nested instance was found for {action}.");
    }

    public static List<FamilyInstance> CollectNestedInstances(Document doc)
    {
      return new FilteredElementCollector(doc)
        .OfClass(typeof(FamilyInstance))
        .Cast<FamilyInstance>()
        .OrderBy(item => CreateRebarByLineService.IdValue(item.Id))
        .ToList();
    }

    public static int ConvertAllDimensionsToInstance(FamilyManager fm, List<string> warnings)
    {
      int convertedCount = 0;
      EnsureCurrentType(fm);
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        if (FamilyParameterGroups.IsBuiltIn(parameter)
            || !FamilyParameterGroups.IsDimensions(parameter)
            || parameter.IsInstance)
        {
          continue;
        }

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
          fm.MakeInstance(parameter);
          convertedCount++;
        }
        catch (Exception ex)
        {
          warnings.Add($"Make instance {name}: {ex.Message}");
          continue;
        }

        FamilyParameter? converted = Find(fm, name);
        if (converted == null || converted.IsDeterminedByFormula)
        {
          if (keep != null)
          {
            fm.CurrentType = keep;
          }

          continue;
        }

        foreach ((FamilyType type, string display) in typeValues)
        {
          fm.CurrentType = type;
          CsvValueConverter.TrySetFamilyParameter(fm, converted, display, warnings);
        }

        if (keep != null)
        {
          fm.CurrentType = keep;
        }
      }

      return convertedCount;
    }

    public static int ConvertMappedLSlotsToType(FamilyManager fm, List<string> warnings)
    {
      int convertedCount = 0;
      EnsureCurrentType(fm);
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        if (FamilyParameterGroups.IsBuiltIn(parameter)
            || !IsMappedLSlot(parameter.Definition.Name)
            || !parameter.IsInstance)
        {
          continue;
        }

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
          fm.MakeType(parameter);
          convertedCount++;
        }
        catch (Exception ex)
        {
          warnings.Add($"Make type {name}: {ex.Message}");
          continue;
        }

        FamilyParameter? converted = Find(fm, name);
        if (converted == null || converted.IsDeterminedByFormula)
        {
          if (keep != null)
          {
            fm.CurrentType = keep;
          }

          continue;
        }

        foreach ((FamilyType type, string display) in typeValues)
        {
          fm.CurrentType = type;
          CsvValueConverter.TrySetFamilyParameter(fm, converted, display, warnings);
        }

        if (keep != null)
        {
          fm.CurrentType = keep;
        }
      }

      return convertedCount;
    }

    public static int EnsureMappedLTypeParameters(FamilyManager fm, List<string> warnings)
    {
      int added = 0;
      for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
      {
        added += EnsureTypeLength(fm, $"1L_{n}", warnings);
        added += EnsureTypeLength(fm, $"2L_{n}", warnings);
        added += EnsureTypeLength(fm, $"3L_{n}", warnings);
      }

      return added;
    }

    private static int EnsureTypeLength(FamilyManager fm, string name, List<string> warnings)
    {
      if (Find(fm, name) != null)
      {
        return 0;
      }

      try
      {
        FamilyParameterGroups.AddFamilyParameter(
          fm,
          name,
          SpecTypeId.Length,
          isInstance: false,
          FamilyParameterGroups.ParameterBucket.Dimensions);
        return 1;
      }
      catch (Exception ex)
      {
        warnings.Add($"Add {name}: {ex.Message}");
        return 0;
      }
    }

    public static List<Parameter> CollectParameters(FamilyInstance instance)
    {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var list = new List<Parameter>();
      foreach (Parameter parameter in instance.GetOrderedParameters().Concat(instance.Parameters.Cast<Parameter>()))
      {
        if (parameter?.Definition == null)
        {
          continue;
        }

        string name = parameter.Definition.Name;
        if (string.IsNullOrWhiteSpace(name)
            || parameter.StorageType == StorageType.None
            || parameter.StorageType == StorageType.ElementId
            || !seen.Add(name))
        {
          continue;
        }

        list.Add(parameter);
      }

      return list;
    }

    public static bool IsSegmentL(string name)
    {
      for (int n = SetTypeEditorService.DimensionStart; n <= SetTypeEditorService.DimensionEnd; n++)
      {
        if (name.Equals($"{n}_L", StringComparison.OrdinalIgnoreCase))
        {
          return true;
        }
      }

      return false;
    }

    public static bool IsMappedLSlot(string name)
    {
      for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
      {
        if (name.Equals($"1L_{n}", StringComparison.OrdinalIgnoreCase)
            || name.Equals($"2L_{n}", StringComparison.OrdinalIgnoreCase)
            || name.Equals($"3L_{n}", StringComparison.OrdinalIgnoreCase))
        {
          return true;
        }
      }

      return false;
    }

    public static void EnsureCurrentType(FamilyManager fm)
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

    public static FamilyParameter? Find(FamilyManager fm, string name)
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

    public static bool IsBuiltIn(Parameter parameter)
    {
      return parameter.Definition is InternalDefinition internalDefinition
        && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID;
    }

    public static string InstanceLabel(FamilyInstance instance)
    {
      string family = instance.Symbol?.FamilyName ?? instance.Name;
      string type = instance.Symbol?.Name ?? string.Empty;
      return string.IsNullOrWhiteSpace(type) ? family : $"{family} : {type}";
    }

    public sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
    {
      public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
      {
        overwriteParameterValues = false;
        return true;
      }

      public bool OnSharedFamilyFound(
        Family sharedFamily,
        bool familyInUse,
        out FamilySource source,
        out bool overwriteParameterValues)
      {
        source = FamilySource.Family;
        overwriteParameterValues = false;
        return true;
      }
    }
  }
}

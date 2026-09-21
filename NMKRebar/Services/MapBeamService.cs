using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class MapBeamResult
  {
    public string InstanceName { get; set; } = string.Empty;

    public int ParametersAdded { get; set; }

    public int MadeInstance { get; set; }

    public int AssociationsSet { get; set; }

    public int AlreadyMapped { get; set; }

    public int Skipped { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Instance: {InstanceName}");
      text.AppendLine($"Parameters added: {ParametersAdded}");
      text.AppendLine($"Made instance: {MadeInstance}");
      text.AppendLine($"Associations set: {AssociationsSet}");
      text.AppendLine($"Already mapped: {AlreadyMapped}");
      text.AppendLine($"Not mappable: {Skipped}");
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

  public static class MapBeamService
  {
    public static MapBeamResult Apply(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Map Beam runs in a family document.");
      }

      FamilyInstance instance = ResolveInstance(uidoc);
      var result = new MapBeamResult
      {
        InstanceName = InstanceLabel(instance)
      };
      FamilyManager fm = doc.FamilyManager;
      using (var tx = new Transaction(doc, "NMK Map Beam"))
      {
        tx.Start();
        EnsureCurrentType(fm);
        foreach (Parameter elementParam in CollectParameters(instance))
        {
          MapOne(fm, instance, elementParam, result);
        }

        tx.Commit();
      }

      return result;
    }

    private static FamilyInstance ResolveInstance(UIDocument uidoc)
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

      if (selected.Count > 1)
      {
        throw new InvalidOperationException("Select one nested instance to map.");
      }

      List<FamilyInstance> nested = new FilteredElementCollector(doc)
        .OfClass(typeof(FamilyInstance))
        .Cast<FamilyInstance>()
        .OrderBy(item => CreateRebarByLineService.IdValue(item.Id))
        .ToList();
      if (nested.Count == 1)
      {
        return nested[0];
      }

      if (nested.Count == 0)
      {
        throw new InvalidOperationException("No nested instance was found. Place or select a beam instance first.");
      }

      throw new InvalidOperationException($"Found {nested.Count} nested instances. Select the current one, then Map Beam.");
    }

    private static List<Parameter> CollectParameters(FamilyInstance instance)
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

    private static void MapOne(
      FamilyManager fm,
      FamilyInstance instance,
      Parameter elementParam,
      MapBeamResult result)
    {
      string name = elementParam.Definition.Name;
      bool builtIn = IsBuiltIn(elementParam);
      try
      {
        FamilyParameter? associated = fm.GetAssociatedFamilyParameter(elementParam);
        if (associated != null
            && string.Equals(associated.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
        {
          result.AlreadyMapped++;
          return;
        }
      }
      catch
      {
      }

      FamilyParameter? familyParam = Find(fm, name) ?? CreateFamilyParameter(fm, elementParam, result);
      if (familyParam == null)
      {
        result.Skipped++;
        return;
      }

      if (IsInstanceParameter(elementParam) && !familyParam.IsInstance)
      {
        try
        {
          fm.MakeInstance(familyParam);
          familyParam = Find(fm, name) ?? familyParam;
          result.MadeInstance++;
        }
        catch (Exception ex)
        {
          result.Warnings.Add($"Make instance {name}: {ex.Message}");
        }
      }

      if (!familyParam.IsDeterminedByFormula)
      {
        string display = CsvValueConverter.GetDisplayValue(elementParam);
        CsvValueConverter.TrySetFamilyParameter(fm, familyParam, display, result.Warnings);
      }

      try
      {
        fm.AssociateElementParameterToFamilyParameter(elementParam, familyParam);
        result.AssociationsSet++;
      }
      catch (Exception ex)
      {
        result.Skipped++;
        if (!builtIn)
        {
          result.Warnings.Add($"{instance.Id}: {name}: {ex.Message}");
        }
      }
    }

    private static FamilyParameter? CreateFamilyParameter(
      FamilyManager fm,
      Parameter elementParam,
      MapBeamResult result)
    {
      string name = elementParam.Definition.Name;
      bool builtIn = IsBuiltIn(elementParam);
      try
      {
        FamilyParameterGroups.AddFamilyParameter(
          fm,
          name,
          elementParam.Definition,
          IsInstanceParameter(elementParam));
        result.ParametersAdded++;
        return Find(fm, name);
      }
      catch (Exception ex)
      {
        try
        {
          FamilyParameterGroups.AddFamilyParameter(
            fm,
            name,
            FamilyParameterGroups.GetSpec(elementParam.Definition),
            IsInstanceParameter(elementParam),
            FamilyParameterGroups.ParameterBucket.Other);
          result.ParametersAdded++;
          result.Warnings.Add($"Add {name}: used Other group ({ex.Message})");
          return Find(fm, name);
        }
        catch (Exception fallback)
        {
          if (!builtIn)
          {
            result.Warnings.Add($"Add {name}: {fallback.Message}");
          }

          return Find(fm, name);
        }
      }
    }

    private static bool IsInstanceParameter(Parameter parameter)
    {
      return parameter.Element is FamilyInstance;
    }

    private static bool IsBuiltIn(Parameter parameter)
    {
      return parameter.Definition is InternalDefinition internalDefinition
        && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID;
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

    private static string InstanceLabel(FamilyInstance instance)
    {
      string family = instance.Symbol?.FamilyName ?? instance.Name;
      string type = instance.Symbol?.Name ?? string.Empty;
      return string.IsNullOrWhiteSpace(type) ? family : $"{family} : {type}";
    }
  }
}

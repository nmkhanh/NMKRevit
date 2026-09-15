using Autodesk.Revit.DB;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class AddArraySegmentParaResult
  {
    public int ParametersAdded { get; set; }

    public int MadeType { get; set; }

    public int ValuesSet { get; set; }

    public int AssociationsSet { get; set; }

    public int InstanceCount { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Type parameters added: {ParametersAdded}");
      text.AppendLine($"Made type: {MadeType}");
      text.AppendLine($"Defaults set: {ValuesSet}");
      text.AppendLine($"Nested instances: {InstanceCount}");
      text.AppendLine($"Associations: {AssociationsSet}");
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

  public static class AddArraySegmentParaService
  {
    private static readonly string[] Suffixes = { "Angle", "Bending", "L", "V" };

    public static AddArraySegmentParaResult Apply(Document doc)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Add Array Para runs in a family document.");
      }

      var result = new AddArraySegmentParaResult();
      FamilyManager fm = doc.FamilyManager;
      using (var tx = new Transaction(doc, "NMK Add Array Para 6-10"))
      {
        tx.Start();
        EnsureCurrentType(fm);
        EnsureTypeParameters(fm, result);
        ApplyDefaults(fm, result);

        List<FamilyInstance> instances = CollectNestedInstances(doc);
        result.InstanceCount = instances.Count;
        foreach (FamilyInstance instance in instances)
        {
          for (int n = 6; n <= 10; n++)
          {
            foreach (string suffix in Suffixes)
            {
              string name = $"{n}_{suffix}";
              result.AssociationsSet += AssociateSameName(fm, instance, name, result);
            }
          }
        }

        tx.Commit();
      }

      return result;
    }

    private static void EnsureTypeParameters(FamilyManager fm, AddArraySegmentParaResult result)
    {
      for (int n = 6; n <= 10; n++)
      {
        EnsureTypeParameter(fm, $"{n}_Angle", SpecTypeId.Angle, result);
        EnsureTypeParameter(fm, $"{n}_Bending", SpecTypeId.Length, result);
        EnsureTypeParameter(fm, $"{n}_L", SpecTypeId.Length, result);
        EnsureTypeParameter(fm, $"{n}_V", SpecTypeId.Boolean.YesNo, result);
      }
    }

    private static void EnsureTypeParameter(
      FamilyManager fm,
      string name,
      ForgeTypeId spec,
      AddArraySegmentParaResult result)
    {
      FamilyParameter? existing = FindFamilyParameter(fm, name);
      if (existing != null)
      {
        if (existing.IsInstance)
        {
          try
          {
            fm.MakeType(existing);
            result.MadeType++;
          }
          catch (Exception ex)
          {
            result.Warnings.Add($"Make type {name}: {ex.Message}");
          }
        }

        return;
      }

      try
      {
        FamilyParameterGroups.AddFamilyParameter(fm, name, spec, otherGroup: false, isInstance: false);
        result.ParametersAdded++;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"Add {name}: {ex.Message}");
      }
    }

    private static void ApplyDefaults(FamilyManager fm, AddArraySegmentParaResult result)
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
        }
      }

      fm.CurrentType = keep;
    }

    private static void SetDefault(
      FamilyManager fm,
      string name,
      string raw,
      AddArraySegmentParaResult result)
    {
      FamilyParameter? parameter = FindFamilyParameter(fm, name);
      if (parameter == null)
      {
        return;
      }

      if (CsvValueConverter.TrySetFamilyParameter(fm, parameter, raw, result.Warnings))
      {
        result.ValuesSet++;
      }
    }

    private static int AssociateSameName(
      FamilyManager fm,
      FamilyInstance instance,
      string name,
      AddArraySegmentParaResult result)
    {
      Parameter? elementParam = instance.LookupParameter(name);
      if (elementParam == null)
      {
        return 0;
      }

      FamilyParameter? familyParam = FindFamilyParameter(fm, name);
      if (familyParam == null)
      {
        result.Warnings.Add($"Missing family parameter '{name}'.");
        return 0;
      }

      try
      {
        fm.AssociateElementParameterToFamilyParameter(elementParam, familyParam);
        return 1;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"{instance.Id}: {name}: {ex.Message}");
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

    private static FamilyParameter? FindFamilyParameter(FamilyManager fm, string name)
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

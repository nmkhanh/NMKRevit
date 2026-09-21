using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class FamilyUpdateMainResult
  {
    public string InstanceName { get; set; } = string.Empty;

    public int ParametersAdded { get; set; }

    public int MadeType { get; set; }

    public int AssociationsSet { get; set; }

    public int AlreadyMapped { get; set; }

    public int Skipped { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Instance: {InstanceName}");
      text.AppendLine($"Parameters added: {ParametersAdded}");
      text.AppendLine($"1L/2L/3L_n made type: {MadeType}");
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

  public static class FamilyUpdateMainService
  {
    public static FamilyUpdateMainResult Apply(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Update Main runs in a family document.");
      }

      List<FamilyInstance> instances = ResolveInstances(uidoc);
      var result = new FamilyUpdateMainResult
      {
        InstanceName = string.Join(", ", instances.Select(FamilyNestedUpdate.InstanceLabel).Distinct())
      };
      FamilyManager fm = doc.FamilyManager;
      using (var tx = new Transaction(doc, "NMK Update Main"))
      {
        tx.Start();
        FamilyNestedUpdate.EnsureCurrentType(fm);
        result.MadeType = FamilyNestedUpdate.ConvertMappedLSlotsToType(fm, result.Warnings);
        result.ParametersAdded += FamilyNestedUpdate.EnsureMappedLTypeParameters(fm, result.Warnings);
        foreach (FamilyInstance instance in instances)
        {
          foreach (Parameter elementParam in FamilyNestedUpdate.CollectParameters(instance))
          {
            if (!FamilyParameterGroups.IsDimensions(elementParam.Definition))
            {
              continue;
            }

            MapOne(fm, instance, elementParam, result);
          }

          MapTwoLSlots(fm, instance, result);
        }

        tx.Commit();
      }

      return result;
    }

    private static List<FamilyInstance> ResolveInstances(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      List<FamilyInstance> selected = uidoc.Selection.GetElementIds()
        .Select(doc.GetElement)
        .OfType<FamilyInstance>()
        .ToList();
      if (selected.Count > 0)
      {
        return selected;
      }

      List<FamilyInstance> nested = FamilyNestedUpdate.CollectNestedInstances(doc);
      if (nested.Count == 0)
      {
        throw new InvalidOperationException("No nested instance was found for Update Main.");
      }

      return nested;
    }

    private static void MapTwoLSlots(
      FamilyManager fm,
      FamilyInstance instance,
      FamilyUpdateMainResult result)
    {
      for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
      {
        string name = $"2L_{n}";
        Parameter? elementParam = instance.LookupParameter(name);
        if (elementParam == null)
        {
          continue;
        }

        MapOne(fm, instance, elementParam, result);
      }
    }

    private static void MapOne(
      FamilyManager fm,
      FamilyInstance instance,
      Parameter elementParam,
      FamilyUpdateMainResult result)
    {
      string name = elementParam.Definition.Name;
      bool builtIn = FamilyNestedUpdate.IsBuiltIn(elementParam);
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

      FamilyParameter? familyParam = FamilyNestedUpdate.Find(fm, name)
        ?? CreateFamilyParameter(fm, elementParam, result);
      if (familyParam == null)
      {
        result.Skipped++;
        return;
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
      FamilyUpdateMainResult result)
    {
      string name = elementParam.Definition.Name;
      try
      {
        bool asType = FamilyNestedUpdate.IsMappedLSlot(name);
        FamilyParameterGroups.AddFamilyParameter(fm, name, elementParam.Definition, isInstance: !asType);
        result.ParametersAdded++;
        return FamilyNestedUpdate.Find(fm, name);
      }
      catch (Exception ex)
      {
        try
        {
          FamilyParameterGroups.AddFamilyParameter(
            fm,
            name,
            FamilyParameterGroups.GetSpec(elementParam.Definition),
            isInstance: !FamilyNestedUpdate.IsMappedLSlot(name),
            FamilyParameterGroups.ParameterBucket.Dimensions);
          result.ParametersAdded++;
          result.Warnings.Add($"Add {name}: used Dimensions group ({ex.Message})");
          return FamilyNestedUpdate.Find(fm, name);
        }
        catch (Exception fallback)
        {
          if (!FamilyNestedUpdate.IsBuiltIn(elementParam))
          {
            result.Warnings.Add($"Add {name}: {fallback.Message}");
          }

          return FamilyNestedUpdate.Find(fm, name);
        }
      }
    }
  }
}

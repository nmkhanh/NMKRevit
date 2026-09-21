using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class FamilyUpdateChildResult
  {
    public string Target { get; set; } = string.Empty;

    public int ChildMadeInstance { get; set; }

    public int ParametersAdded { get; set; }

    public int AssociationsSet { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Family: {Target}");
      text.AppendLine($"Dimensions made instance: {ChildMadeInstance}");
      text.AppendLine($"2L_n added: {ParametersAdded}");
      text.AppendLine($"2_L associations: {AssociationsSet}");
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

  public static class FamilyUpdateChildService
  {
    public static FamilyUpdateChildResult Apply(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Update Child runs in a family document.");
      }

      var result = new FamilyUpdateChildResult
      {
        Target = doc.Title
      };
      FamilyManager fm = doc.FamilyManager;
      using (var tx = new Transaction(doc, "NMK Update Child"))
      {
        tx.Start();
        FamilyNestedUpdate.EnsureCurrentType(fm);
        result.ChildMadeInstance = FamilyNestedUpdate.ConvertAllDimensionsToInstance(fm, result.Warnings);
        EnsureTwoLParameters(fm, result);
        MapTwoLFromInstances(doc, fm, result);
        tx.Commit();
      }

      return result;
    }

    private static void EnsureTwoLParameters(FamilyManager fm, FamilyUpdateChildResult result)
    {
      for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
      {
        string name = $"2L_{n}";
        FamilyParameter? existing = FamilyNestedUpdate.Find(fm, name);
        if (existing != null)
        {
          if (!existing.IsInstance)
          {
            try
            {
              fm.MakeInstance(existing);
              result.ChildMadeInstance++;
            }
            catch (Exception ex)
            {
              result.Warnings.Add($"Make instance {name}: {ex.Message}");
            }
          }

          continue;
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
    }

    private static void MapTwoLFromInstances(Document doc, FamilyManager fm, FamilyUpdateChildResult result)
    {
      List<FamilyInstance> instances = FamilyNestedUpdate.CollectNestedInstances(doc);
      if (instances.Count == 0)
      {
        result.Warnings.Add("No nested family instance found to map 2_L.");
        return;
      }

      int count = Math.Min(instances.Count, SetTypeEditorService.ZCount);
      if (instances.Count != SetTypeEditorService.ZCount)
      {
        result.Warnings.Add($"Nested instances: {instances.Count}. Mapping 2_L on first {count}.");
      }

      for (int i = 0; i < count; i++)
      {
        int n = i + 1;
        result.AssociationsSet += Associate(fm, instances[i], "2_L", $"2L_{n}", result);
      }
    }

    private static int Associate(
      FamilyManager fm,
      FamilyInstance instance,
      string elementParamName,
      string familyParamName,
      FamilyUpdateChildResult result)
    {
      Parameter? elementParam = instance.LookupParameter(elementParamName);
      if (elementParam == null)
      {
        return 0;
      }

      FamilyParameter? familyParam = FamilyNestedUpdate.Find(fm, familyParamName);
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
  }
}

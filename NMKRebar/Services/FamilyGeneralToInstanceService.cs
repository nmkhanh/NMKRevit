using Autodesk.Revit.DB;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class FamilyGeneralToInstanceResult
  {
    public int ParametersDeleted { get; set; }

    public int MadeInstance { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"General parameters deleted: {ParametersDeleted}");
      text.AppendLine($"Remaining made instance: {MadeInstance}");
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

  public static class FamilyGeneralToInstanceService
  {
    public static FamilyGeneralToInstanceResult Apply(Document doc)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Remove General / Make Instance runs in a family document.");
      }

      var result = new FamilyGeneralToInstanceResult();
      FamilyManager fm = doc.FamilyManager;
      using (var tx = new Transaction(doc, "NMK Remove General Make Instance"))
      {
        tx.Start();
        result.ParametersDeleted = DeleteGeneralParameters(fm, result);
        result.MadeInstance = MakeRemainingInstance(fm, result);
        tx.Commit();
      }

      return result;
    }

    private static int DeleteGeneralParameters(FamilyManager fm, FamilyGeneralToInstanceResult result)
    {
      int deleted = 0;
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        if (FamilyParameterGroups.IsBuiltIn(parameter) || !FamilyParameterGroups.IsGeneral(parameter))
        {
          continue;
        }

        string name = parameter.Definition.Name;
        try
        {
          if (parameter.IsDeterminedByFormula)
          {
            fm.SetFormula(parameter, null);
          }

          fm.RemoveParameter(parameter);
          deleted++;
        }
        catch (Exception ex)
        {
          result.Warnings.Add($"Delete {name}: {ex.Message}");
        }
      }

      return deleted;
    }

    private static int MakeRemainingInstance(FamilyManager fm, FamilyGeneralToInstanceResult result)
    {
      int converted = 0;
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        if (FamilyParameterGroups.IsBuiltIn(parameter) || parameter.IsInstance)
        {
          continue;
        }

        string name = parameter.Definition.Name;
        try
        {
          fm.MakeInstance(parameter);
          converted++;
        }
        catch (Exception ex)
        {
          result.Warnings.Add($"Make instance {name}: {ex.Message}");
        }
      }

      return converted;
    }
  }
}

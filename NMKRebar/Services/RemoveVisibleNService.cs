using Autodesk.Revit.DB;
using System.Text;
using System.Text.RegularExpressions;

namespace NMKRebar.Services
{
  public sealed class RemoveVisibleNResult
  {
    public int ParametersDeleted { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Visible_n parameters deleted: {ParametersDeleted}");
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

  public static class RemoveVisibleNService
  {
    private static readonly Regex VisibleN = new(
      @"^Visible_?\d+$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RemoveVisibleNResult Apply(Document doc)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Remove Visible_n runs in a family document.");
      }

      var result = new RemoveVisibleNResult();
      FamilyManager fm = doc.FamilyManager;
      using (var tx = new Transaction(doc, "NMK Remove Visible_n"))
      {
        tx.Start();
        foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
        {
          if (FamilyParameterGroups.IsBuiltIn(parameter))
          {
            continue;
          }

          string name = parameter.Definition.Name;
          if (!VisibleN.IsMatch(name))
          {
            continue;
          }

          try
          {
            if (parameter.IsDeterminedByFormula)
            {
              fm.SetFormula(parameter, null);
            }

            fm.RemoveParameter(parameter);
            result.ParametersDeleted++;
          }
          catch (Exception ex)
          {
            result.Warnings.Add($"Delete {name}: {ex.Message}");
          }
        }

        tx.Commit();
      }

      return result;
    }
  }
}

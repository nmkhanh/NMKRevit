using Autodesk.Revit.DB;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class MapVToVisibleResult
  {
    public int AssociationsSet { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"V associated to Visible_n: {AssociationsSet}");
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

  public static class MapVToVisibleService
  {
    public static MapVToVisibleResult Apply(Document doc)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Map V to Visible_n runs in a family document.");
      }

      var result = new MapVToVisibleResult();
      FamilyManager fm = doc.FamilyManager;
      List<Element> elements = CollectElementsWithV(doc);
      if (elements.Count == 0)
      {
        throw new InvalidOperationException("No nested element with parameter 'V' was found.");
      }

      using (var tx = new Transaction(doc, "NMK Map V to Visible_n"))
      {
        tx.Start();
        for (int i = 0; i < elements.Count; i++)
        {
          int n = i + 1;
          Element element = elements[i];
          Parameter? v = element.LookupParameter("V");
          if (v == null)
          {
            continue;
          }

          FamilyParameter? visible = Find(fm, $"Visible_{n}") ?? Find(fm, $"Visible{n}");
          if (visible == null)
          {
            result.Warnings.Add($"Missing family parameter Visible_{n} for element {element.Id}.");
            continue;
          }

          try
          {
            fm.AssociateElementParameterToFamilyParameter(v, visible);
            result.AssociationsSet++;
          }
          catch (Exception ex)
          {
            result.Warnings.Add($"{element.Id}: V -> Visible_{n}: {ex.Message}");
          }
        }

        tx.Commit();
      }

      return result;
    }

    private static List<Element> CollectElementsWithV(Document doc)
    {
      var ids = new HashSet<long>();
      var elements = new List<Element>();
      foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
      {
        if (element.LookupParameter("V") == null)
        {
          continue;
        }

        long id = CreateRebarByLineService.IdValue(element.Id);
        if (!ids.Add(id))
        {
          continue;
        }

        elements.Add(element);
      }

      return elements.OrderBy(item => CreateRebarByLineService.IdValue(item.Id)).ToList();
    }

    private static FamilyParameter? Find(FamilyManager fm, string name)
    {
      foreach (FamilyParameter parameter in fm.Parameters)
      {
        if (parameter.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
          return parameter;
        }
      }

      return null;
    }
  }
}

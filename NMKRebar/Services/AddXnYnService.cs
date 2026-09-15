using Autodesk.Revit.DB;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class AddXnYnResult
  {
    public int ParametersAdded { get; set; }

    public int MadeInstance { get; set; }

    public int AssociationsSet { get; set; }

    public int ElementCount { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Xn/Yn added: {ParametersAdded}");
      text.AppendLine($"Made instance: {MadeInstance}");
      text.AppendLine($"Elements with X/Y: {ElementCount}");
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

  public static class AddXnYnService
  {
    public const int Count = 100;

    public static AddXnYnResult Apply(Document doc)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Add Xn Yn runs in a family document.");
      }

      var result = new AddXnYnResult();
      FamilyManager fm = doc.FamilyManager;
      List<Element> elements = CollectElementsWithXy(doc);
      result.ElementCount = elements.Count;
      using (var tx = new Transaction(doc, "NMK Add Xn Yn"))
      {
        tx.Start();
        for (int n = 1; n <= Count; n++)
        {
          EnsureInstanceLength(fm, $"X{n}", result);
          EnsureInstanceLength(fm, $"Y{n}", result);
        }

        int mapCount = Math.Min(elements.Count, Count);
        if (elements.Count > Count)
        {
          result.Warnings.Add($"Found {elements.Count} elements with X/Y; mapped first {Count}.");
        }

        for (int i = 0; i < mapCount; i++)
        {
          int n = i + 1;
          Element element = elements[i];
          result.AssociationsSet += Associate(fm, element, "X", $"X{n}", result);
          result.AssociationsSet += Associate(fm, element, "Y", $"Y{n}", result);
        }

        tx.Commit();
      }

      return result;
    }

    private static List<Element> CollectElementsWithXy(Document doc)
    {
      var ids = new HashSet<long>();
      var elements = new List<Element>();
      foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
      {
        if (element.LookupParameter("X") == null && element.LookupParameter("Y") == null)
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

    private static void EnsureInstanceLength(FamilyManager fm, string name, AddXnYnResult result)
    {
      FamilyParameter? existing = Find(fm, name);
      if (existing != null)
      {
        if (!FamilyParameterGroups.IsGeneral(existing))
        {
          result.Warnings.Add($"{name} exists but is not in General.");
        }

        if (!existing.IsInstance)
        {
          try
          {
            fm.MakeInstance(existing);
            result.MadeInstance++;
          }
          catch (Exception ex)
          {
            result.Warnings.Add($"Make instance {name}: {ex.Message}");
          }
        }

        return;
      }

      try
      {
        FamilyParameterGroups.AddFamilyParameter(
          fm,
          name,
          SpecTypeId.Length,
          isInstance: true,
          FamilyParameterGroups.ParameterBucket.General);
        result.ParametersAdded++;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"Add {name}: {ex.Message}");
      }
    }

    private static int Associate(FamilyManager fm, Element element, string elementName, string familyName, AddXnYnResult result)
    {
      Parameter? elementParam = element.LookupParameter(elementName);
      if (elementParam == null)
      {
        return 0;
      }

      FamilyParameter? familyParam = Find(fm, familyName);
      if (familyParam == null)
      {
        result.Warnings.Add($"Missing family parameter '{familyName}'.");
        return 0;
      }

      try
      {
        fm.AssociateElementParameterToFamilyParameter(elementParam, familyParam);
        return 1;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"{element.Id}: {elementName} -> {familyName}: {ex.Message}");
        return 0;
      }
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

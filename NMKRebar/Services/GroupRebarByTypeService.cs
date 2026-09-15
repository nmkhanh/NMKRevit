using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace NMKRebar.Services
{
  public sealed class RebarElementSelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return elem is RevitRebar;
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }

  public static class GroupRebarByTypeService
  {
    public static string GroupPicked(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Group Rebar By Type runs in a project document.");
      }

      IList<Reference> picks = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new RebarElementSelectionFilter(),
        "Select rebars to group by type");
      List<ElementId> ids = picks
        .Select(pick => pick.ElementId)
        .Distinct()
        .ToList();
      if (ids.Count == 0)
      {
        throw new InvalidOperationException("No rebar was selected.");
      }

      var warnings = new List<string>();
      int groups = GroupByType(doc, ids, warnings);
      string text = $"Grouped {ids.Count} rebar(s) into {groups} group(s).";
      if (warnings.Count > 0)
      {
        text += " " + string.Join(" ", warnings.Take(3));
      }

      return text;
    }

    public static int GroupByType(Document doc, IEnumerable<ElementId> rebarIds, List<string> warnings)
    {
      var byBase = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
      foreach (ElementId id in rebarIds.GroupBy(CreateRebarByLineService.IdValue).Select(group => group.First()))
      {
        if (doc.GetElement(id) is not RevitRebar rebar)
        {
          continue;
        }

        string typeName = (doc.GetElement(rebar.GetTypeId()) as RebarBarType)?.Name ?? "Unknown";
        string baseName = GetBaseName(typeName);
        if (!byBase.TryGetValue(baseName, out List<ElementId>? ids))
        {
          ids = new List<ElementId>();
          byBase[baseName] = ids;
        }

        ids.Add(id);
      }

      int groupsCreated = 0;
      bool ownTransaction = !doc.IsModifiable;
      Transaction? tx = null;
      if (ownTransaction)
      {
        tx = new Transaction(doc, "NMK Group Rebar By Type");
        tx.Start();
      }

      try
      {
        foreach (KeyValuePair<string, List<ElementId>> pair in byBase)
        {
          if (pair.Value.Count == 0)
          {
            continue;
          }

          try
          {
            foreach (ElementId id in pair.Value)
            {
              if (doc.GetElement(id) is Element element && element.Pinned)
              {
                element.Pinned = false;
              }
            }

            Group group = doc.Create.NewGroup(pair.Value);
            for (int suffix = 0; suffix <= 100; suffix++)
            {
              try
              {
                group.GroupType.Name = suffix == 0 ? pair.Key : $"{pair.Key}_{suffix}";
                break;
              }
              catch
              {
              }
            }

            group.Pinned = true;
            groupsCreated++;
          }
          catch (Exception ex)
          {
            warnings.Add($"Group '{pair.Key}': {ex.Message}");
          }
        }

        tx?.Commit();
      }
      finally
      {
        tx?.Dispose();
      }

      return groupsCreated;
    }

    public static string GetBaseName(string typeName)
    {
      if (string.IsNullOrWhiteSpace(typeName))
      {
        return "Unknown";
      }

      int index = typeName.IndexOf('-');
      return index > 0 ? typeName.Substring(0, index) : typeName;
    }
  }
}

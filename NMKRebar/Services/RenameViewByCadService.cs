using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.IO;
using View = Autodesk.Revit.DB.View;

namespace NMKRebar.Services
{
  public static class RenameViewByCadService
  {
    public static string RenameSelectedViews(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Rename View by CAD runs in a project document.");
      }

      List<View> views = uidoc.Selection.GetElementIds()
        .Select(doc.GetElement)
        .OfType<View>()
        .Where(view => !view.IsTemplate)
        .ToList();
      if (views.Count == 0 && doc.ActiveView != null && !doc.ActiveView.IsTemplate)
      {
        views.Add(doc.ActiveView);
      }

      if (views.Count == 0)
      {
        throw new InvalidOperationException("Select a view (or activate a view).");
      }

      int renamed = 0;
      var warnings = new List<string>();
      using (var tx = new Transaction(doc, "NMK Rename View by CAD"))
      {
        tx.Start();
        foreach (View view in views)
        {
          try
          {
            ImportInstance? cad = new FilteredElementCollector(doc, view.Id)
              .OfClass(typeof(ImportInstance))
              .Cast<ImportInstance>()
              .FirstOrDefault();
            if (cad == null)
            {
              warnings.Add($"{view.Name}: no CAD import.");
              continue;
            }

            if (doc.GetElement(cad.GetTypeId()) is not CADLinkType cadType)
            {
              warnings.Add($"{view.Name}: no CAD link type.");
              continue;
            }

            string name = Path.GetFileNameWithoutExtension(cadType.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
              warnings.Add($"{view.Name}: empty CAD name.");
              continue;
            }

            view.Name = name;
            renamed++;
          }
          catch (Exception ex)
          {
            warnings.Add($"{view.Name}: {ex.Message}");
          }
        }

        if (renamed == 0)
        {
          tx.RollBack();
        }
        else
        {
          tx.Commit();
        }
      }

      string text = $"Renamed {renamed} view(s).";
      if (warnings.Count > 0)
      {
        text += " " + string.Join(" ", warnings.Take(3));
      }

      return text;
    }
  }
}

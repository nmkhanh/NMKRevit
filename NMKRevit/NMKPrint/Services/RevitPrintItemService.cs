using Autodesk.Revit.DB;
using NMKRevit.NMKPrint.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using View = Autodesk.Revit.DB.View;

namespace NMKRevit.NMKPrint.Services
{
  public class RevitPrintItemService
  {
    private readonly PaperSizeService _paperSizeService;

    public RevitPrintItemService(PaperSizeService paperSizeService)
    {
      _paperSizeService = paperSizeService;
    }

    public List<PrintItem> GetSheets(Document doc)
    {
      var titleBlocks = new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_TitleBlocks)
        .WhereElementIsNotElementType()
        .Cast<FamilyInstance>()
        .ToList();

      BrowserOrganization? browserOrganization = null;
      try
      {
        browserOrganization = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
      }
      catch
      {
        browserOrganization = null;
      }

      return new FilteredElementCollector(doc)
        .OfClass(typeof(ViewSheet))
        .Cast<ViewSheet>()
        .Where(sheet => !sheet.IsTemplate)
        .OrderBy(sheet => sheet.SheetNumber, NaturalStringComparer.Instance)
        .Select(sheet =>
        {
          FamilyInstance? titleBlock = titleBlocks.FirstOrDefault(x => x.OwnerViewId == sheet.Id);
          string revision = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsValueString() ?? "-";
          string revisionDate = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION_DATE)?.AsValueString() ?? "-";

          return new PrintItem
          {
            Id = sheet.Id,
            View = sheet,
            IsSheet = true,
            Number = sheet.SheetNumber,
            Name = sheet.Name,
            DisplayName = $"{sheet.SheetNumber} - {sheet.Name}",
            Revision = revision,
            RevisionDate = revisionDate,
            Paper = _paperSizeService.FromTitleBlock(titleBlock),
            BrowserPath = GetBrowserPath(browserOrganization, sheet.Id)
          };
        })
        .ToList();
    }

    public List<PrintItem> GetViews(Document doc)
    {
      BrowserOrganization? browserOrganization = null;
      try
      {
        browserOrganization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
      }
      catch
      {
        browserOrganization = null;
      }

      return new FilteredElementCollector(doc)
        .OfClass(typeof(View))
        .Cast<View>()
        .Where(view => !view.IsTemplate && view is not ViewSheet && view is not ViewSchedule)
        .OrderBy(view => view.Name, NaturalStringComparer.Instance)
        .Select(view => new PrintItem
        {
          Id = view.Id,
          View = view,
          IsSheet = false,
          Number = GetElementIdValue(view.Id),
          Name = view.Name,
          DisplayName = view.Name,
          Paper = new PaperSpec("-", 0, 0),
          BrowserPath = GetBrowserPath(browserOrganization, view.Id)
        })
        .ToList();
    }

    private static IReadOnlyList<string> GetBrowserPath(BrowserOrganization? browserOrganization, ElementId elementId)
    {
      if (browserOrganization == null)
      {
        return Array.Empty<string>();
      }

      try
      {
        return browserOrganization.GetFolderItems(elementId)
          .Where(x => !string.IsNullOrWhiteSpace(x.Name))
          .Select(x => x.Name)
          .ToList();
      }
      catch
      {
        return Array.Empty<string>();
      }
    }

    public List<SelectionSource> GetSelectionSources(Document doc)
    {
      var result = new List<SelectionSource>
      {
        new SelectionSource { Name = "Select by user", Kind = SelectionSourceKind.User }
      };

      result.AddRange(new FilteredElementCollector(doc)
        .OfClass(typeof(ViewSchedule))
        .Cast<ViewSchedule>()
        .Where(IsSheetListOrViewList)
        .OrderBy(x => x.Name)
        .Select(x => new SelectionSource
        {
          Name = $"Schedule : {x.Name}",
          Kind = SelectionSourceKind.Schedule,
          Id = x.Id
        }));

      result.AddRange(new FilteredElementCollector(doc)
        .OfClass(typeof(ViewSheetSet))
        .Cast<ViewSheetSet>()
        .OrderBy(x => x.Name)
        .Select(x => new SelectionSource
        {
          Name = $"ViewSheetSet : {x.Name}",
          Kind = SelectionSourceKind.ViewSheetSet,
          Id = x.Id
        }));

      return result;
    }

    public HashSet<ElementId> ResolveSelectionSource(Document doc, SelectionSource source, IEnumerable<PrintItem> allItems)
    {
      var ids = new HashSet<ElementId>();
      if (source.Kind == SelectionSourceKind.User)
      {
        return ids;
      }

      if (source.Kind == SelectionSourceKind.ViewSheetSet && doc.GetElement(source.Id) is ViewSheetSet set)
      {
        foreach (View view in set.Views)
        {
          ids.Add(view.Id);
        }
        return ids;
      }

      if (source.Kind == SelectionSourceKind.Schedule && doc.GetElement(source.Id) is ViewSchedule schedule)
      {
        var scheduledIds = new FilteredElementCollector(doc, schedule.Id)
          .OfClass(typeof(View))
          .Cast<View>()
          .Where(view => !view.IsTemplate)
          .Select(view => view.Id)
          .ToHashSet();

        foreach (ElementId id in scheduledIds)
        {
          ids.Add(id);
        }
      }

      return ids;
    }

    private static bool IsSheetListOrViewList(ViewSchedule schedule)
    {
      if (schedule.IsTemplate)
      {
        return false;
      }

      try
      {
#if D2024 || D2025 || D2026 || D2027
        long categoryId = schedule.Definition.CategoryId.Value;
        return categoryId == (long)BuiltInCategory.OST_Sheets || categoryId == (long)BuiltInCategory.OST_Views;
#else
        int categoryId = schedule.Definition.CategoryId.IntegerValue;
        return categoryId == (int)BuiltInCategory.OST_Sheets || categoryId == (int)BuiltInCategory.OST_Views;
#endif
      }
      catch
      {
        return false;
      }
    }

    private static string GetElementIdValue(ElementId id)
    {
#if D2024 || D2025 || D2026 || D2027
      return id.Value.ToString();
#else
      return id.IntegerValue.ToString();
#endif
    }
  }
}

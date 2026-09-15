using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace NMKRebar.Services
{
  public static class ViewCategoryVisibilityService
  {
    public static string ToggleSiteVersusRebar(View view, bool showSiteHideRebar)
    {
      RequireGraphicView(view);
      using (var tx = new Transaction(view.Document, "NMK Site / Rebar visibility"))
      {
        tx.Start();
        SetHidden(view, BuiltInCategory.OST_Site, hide: !showSiteHideRebar);
        SetHidden(view, BuiltInCategory.OST_Rebar, hide: showSiteHideRebar);
        SetHidden(view, BuiltInCategory.OST_Coupler, hide: showSiteHideRebar);
        tx.Commit();
      }

      return showSiteHideRebar
        ? "Active view: Site on, Rebar and Coupler off."
        : "Active view: Rebar and Coupler on, Site off.";
    }

    public static string ShowAllCategories(View view)
    {
      RequireGraphicView(view);
      int shown = 0;
      using (var tx = new Transaction(view.Document, "NMK Show All categories"))
      {
        tx.Start();
        foreach (Category category in view.Document.Settings.Categories)
        {
          shown += UnhideCategoryTree(view, category);
        }

        tx.Commit();
      }

      return $"Active view: showed {shown} hidden categor(y/ies).";
    }

    private static void RequireGraphicView(View view)
    {
      if (view == null || view.IsTemplate)
      {
        throw new InvalidOperationException("Activate a graphic view first.");
      }
    }

    private static void SetHidden(View view, BuiltInCategory builtIn, bool hide)
    {
      Category? category;
      try
      {
        category = view.Document.Settings.Categories.get_Item(builtIn);
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException)
      {
        return;
      }
      catch (ArgumentException)
      {
        return;
      }

      if (category == null)
      {
        return;
      }

      try
      {
        if (!view.CanCategoryBeHidden(category.Id))
        {
          return;
        }

        view.SetCategoryHidden(category.Id, hide);
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException)
      {
      }
    }

    private static int UnhideCategoryTree(View view, Category category)
    {
      int shown = 0;
      try
      {
        if (view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id))
        {
          view.SetCategoryHidden(category.Id, false);
          shown++;
        }
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException)
      {
      }

      foreach (Category sub in category.SubCategories)
      {
        shown += UnhideCategoryTree(view, sub);
      }

      return shown;
    }
  }
}

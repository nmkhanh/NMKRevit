using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRebar.ViewModels;
using NMKRebar.Views;
using Revit.Async;
using System.Windows;
using System.Windows.Interop;

namespace NMKRebar.Commands
{
  internal static class ToolWindowHost
  {
    private static readonly List<Window> OpenWindows = new();

    public static IReadOnlyList<Window> Open => OpenWindows;

    public static Window? Current => OpenWindows.Count == 0 ? null : OpenWindows[OpenWindows.Count - 1];

    public static void Attach(UIApplication uiapp, Window window)
    {
      OpenWindows.Add(window);
      window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
      window.ShowInTaskbar = true;
      new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
      window.Closed += (_, _) => OpenWindows.Remove(window);
    }

    public static bool ActivateIfOpen(Window? window)
    {
      if (window == null)
      {
        return false;
      }

      if (window.WindowState == WindowState.Minimized)
      {
        window.WindowState = WindowState.Normal;
      }

      window.Activate();
      return true;
    }
  }

  [Transaction(TransactionMode.Manual)]
  public sealed class NMKFamilySupportCommand : IExternalCommand
  {
    private static FamilySupportWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "No active document.");
          return Result.Cancelled;
        }

        if (!uidoc.Document.IsFamilyDocument)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "Family Support runs in a family document only.");
          return Result.Cancelled;
        }

        if (ToolWindowHost.ActivateIfOpen(_window))
        {
          return Result.Succeeded;
        }

        _window = new FamilySupportWindow
        {
          DataContext = new FamilySupportViewModel(uiapp)
        };
        ToolWindowHost.Attach(uiapp, _window);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.ToString();
        return Result.Failed;
      }
    }
  }

  [Transaction(TransactionMode.Manual)]
  public sealed class NMKProjectCommand : IExternalCommand
  {
    private static RebarDataWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "No active document.");
          return Result.Cancelled;
        }

        if (uidoc.Document.IsFamilyDocument)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "Project tools run in a project document only.");
          return Result.Cancelled;
        }

        if (ToolWindowHost.ActivateIfOpen(_window))
        {
          return Result.Succeeded;
        }

        _window = new RebarDataWindow
        {
          DataContext = new RebarDataViewModel(uiapp)
        };
        ToolWindowHost.Attach(uiapp, _window);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.ToString();
        return Result.Failed;
      }
    }
  }

  [Transaction(TransactionMode.Manual)]
  public sealed class NMKCreateRebarTypeCommand : IExternalCommand
  {
    private static CreateRebarTypeWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "No active document.");
          return Result.Cancelled;
        }

        if (uidoc.Document.IsFamilyDocument)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "Create Rebar Type runs in a project document only.");
          return Result.Cancelled;
        }

        if (ToolWindowHost.ActivateIfOpen(_window))
        {
          return Result.Succeeded;
        }

        _window = new CreateRebarTypeWindow
        {
          DataContext = new CreateRebarTypeViewModel(uiapp)
        };
        ToolWindowHost.Attach(uiapp, _window);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.ToString();
        return Result.Failed;
      }
    }
  }

  [Transaction(TransactionMode.Manual)]
  public sealed class NMKCheckPathCommand : IExternalCommand
  {
    private static CheckPathWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "No active document.");
          return Result.Cancelled;
        }

        if (uidoc.Document.IsFamilyDocument)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "Check Path runs in a project document only.");
          return Result.Cancelled;
        }

        if (ToolWindowHost.ActivateIfOpen(_window))
        {
          return Result.Succeeded;
        }

        _window = new CheckPathWindow
        {
          DataContext = new CheckPathViewModel(uiapp)
        };
        ToolWindowHost.Attach(uiapp, _window);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.ToString();
        return Result.Failed;
      }
    }
  }

  [Transaction(TransactionMode.Manual)]
  public sealed class NMKSetTypeCommand : IExternalCommand
  {
    private static SetTypeWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "No active document.");
          return Result.Cancelled;
        }

        if (uidoc.Document.IsFamilyDocument)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "Set Type runs in a project document only.");
          return Result.Cancelled;
        }

        if (ToolWindowHost.ActivateIfOpen(_window))
        {
          return Result.Succeeded;
        }

        _window = new SetTypeWindow
        {
          DataContext = new SetTypeViewModel(uiapp)
        };
        ToolWindowHost.Attach(uiapp, _window);
        _window.Closed += (_, _) =>
        {
          if (_window?.DataContext is SetTypeViewModel viewModel)
          {
            viewModel.PersistSettings();
          }

          _window = null;
        };
        _window.Show();
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.ToString();
        return Result.Failed;
      }
    }
  }
}

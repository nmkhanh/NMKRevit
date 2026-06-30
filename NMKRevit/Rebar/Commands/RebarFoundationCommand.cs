using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.Rebar.ViewModels;
using NMKRevit.Rebar.Views;
using Revit.Async;
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace NMKRevit.Rebar.Commands
{
  [Transaction(TransactionMode.Manual)]
  public sealed class RebarFoundationCommand : IExternalCommand
  {
    private static RebarPlacementWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);

        if (_window != null)
        {
          if (_window.WindowState == WindowState.Minimized)
          {
            _window.WindowState = WindowState.Normal;
          }
          _window.Activate();
          return Result.Succeeded;
        }

        string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        string defaultJson = Path.Combine(folder, "RebarFoundation.json");
        _window = new RebarPlacementWindow
        {
          DataContext = new RebarPlacementViewModel(defaultJson),
          WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var source = HwndSource.FromHwnd(uiapp.MainWindowHandle);
        if (source?.RootVisual is Window revitWindow)
        {
          _window.Owner = revitWindow;
        }
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
}

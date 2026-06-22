using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.FloorTool.ViewModels;
using NMKRevit.FloorTool.Views;
using Revit.Async;
using System;
using System.Windows;
using System.Windows.Interop;

namespace NMKRevit.FloorTool.Commands
{
  [Transaction(TransactionMode.Manual)]
  public sealed class FloorToolCommand : IExternalCommand
  {
    private static FloorToolWindow? _window;

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

        _window = new FloorToolWindow
        {
          DataContext = new FloorToolViewModel(),
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

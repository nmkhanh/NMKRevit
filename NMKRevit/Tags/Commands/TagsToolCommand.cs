using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.Tags.ViewModels;
using NMKRevit.Tags.Views;
using Revit.Async;
using System;
using System.Windows;
using System.Windows.Interop;

namespace NMKRevit.Tags.Commands
{
  [Transaction(TransactionMode.Manual)]
  public sealed class TagsToolCommand : IExternalCommand
  {
    private static TagsToolWindow? _window;

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

        _window = new TagsToolWindow
        {
          DataContext = new TagsToolViewModel(),
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

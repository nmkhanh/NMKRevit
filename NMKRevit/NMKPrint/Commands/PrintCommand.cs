using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.NMKPrint.ViewModels;
using NMKRevit.NMKPrint.Views;
using Revit.Async;
using System;
using System.Windows;
using System.Windows.Interop;

namespace NMKRevit.NMKPrint.Commands
{
  [Transaction(TransactionMode.Manual)]
  public class PrintCommand : IExternalCommand
  {
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);

        var window = new PrintWindow
        {
          DataContext = new PrintViewModel(uiapp),
          WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var hwndSource = HwndSource.FromHwnd(uiapp.MainWindowHandle);
        if (hwndSource?.RootVisual is Window revitWindow)
        {
          window.Owner = revitWindow;
        }

        window.Show();
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

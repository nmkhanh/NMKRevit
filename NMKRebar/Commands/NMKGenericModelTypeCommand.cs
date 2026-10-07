using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRebar.ViewModels;
using NMKRebar.Views;
using Revit.Async;

namespace NMKRebar.Commands
{
  [Transaction(TransactionMode.Manual)]
  public sealed class NMKGenericModelTypeCommand : IExternalCommand
  {
    private static GenericModelTypeWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        RevitTask.Initialize(uiapp);
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "Không tìm thấy document đang hoạt động trong Revit.");
          return Result.Cancelled;
        }

        if (uidoc.Document.IsFamilyDocument)
        {
          Autodesk.Revit.UI.TaskDialog.Show("NMKRebar", "Generic Model Types tool chỉ chạy trong Project document.");
          return Result.Cancelled;
        }

        if (ToolWindowHost.ActivateIfOpen(_window))
        {
          return Result.Succeeded;
        }

        _window = new GenericModelTypeWindow
        {
          DataContext = new GenericModelTypeViewModel(uiapp)
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
}

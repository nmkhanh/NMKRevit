using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRebar.Services;
using System.IO;
using System.Reflection;
using RevitTaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace NMKRebar.Commands
{
  [Transaction(TransactionMode.Manual)]
  public sealed class NMKCreateModelLinesFromCsvCommand : IExternalCommand
  {
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
        {
          RevitTaskDialog.Show("NMKRebar", "Không tìm thấy document đang hoạt động trong Revit.");
          return Result.Cancelled;
        }

        Document doc = uidoc.Document;

        // 1. Prompt user to select CSV file
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
          Title = "Chọn file CSV để tạo Model Lines (đơn vị: mét)",
          Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
          CheckFileExists = true
        };

        string? initialDir = GetInitialDirectory(doc);
        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
        {
          openFileDialog.InitialDirectory = initialDir;
        }

        bool? dialogResult = openFileDialog.ShowDialog();
        if (dialogResult != true || string.IsNullOrWhiteSpace(openFileDialog.FileName))
        {
          return Result.Cancelled;
        }

        string csvPath = openFileDialog.FileName;

        // 2. Process and create Model Lines (tọa độ trong file CSV mặc định là mét)
        ModelLinesFromCsvResult result = ModelLinesFromCsvService.CreateModelLines(doc, csvPath, CsvCoordinateUnit.Meters);

        // 3. Report outcome
        var summaryDialog = new RevitTaskDialog("Kết quả tạo Model Lines")
        {
          MainInstruction = $"Đã tạo thành công {result.CreatedLinesCount} Model Line (đơn vị: mét)!",
          MainContent = result.GetSummaryMessage()
        };
        summaryDialog.Show();

        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.ToString();
        RevitTaskDialog.Show("Lỗi NMKRebar", $"Đã xảy ra lỗi khi tạo Model Lines:\n{ex.Message}");
        return Result.Failed;
      }
    }

    private static string? GetInitialDirectory(Document doc)
    {
      if (!string.IsNullOrWhiteSpace(doc.PathName) && File.Exists(doc.PathName))
      {
        return Path.GetDirectoryName(doc.PathName);
      }

      string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
      string resourcesDir = Path.Combine(assemblyFolder, "Resources");
      if (Directory.Exists(resourcesDir))
      {
        return resourcesDir;
      }

      return Directory.Exists(assemblyFolder) ? assemblyFolder : null;
    }
  }
}

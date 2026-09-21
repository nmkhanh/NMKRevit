using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRebar.Services;
using Revit.Async;
using System.IO;
using System.Reflection;
using System.Text;

namespace NMKRebar.ViewModels
{
  public partial class FamilySupportViewModel : ObservableObject
  {
    public FamilySupportViewModel(UIApplication uiapp)
    {
    }

    [ObservableProperty]
    private string _status = "Open a family document, then run a command.";

    [RelayCommand]
    private async Task AddParaByCsv()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
          string csvPath = Path.Combine(folder, "TypeShape.csv");
          if (!File.Exists(csvPath))
          {
            throw new InvalidOperationException($"TypeShape.csv was not found:\n{csvPath}");
          }

          TypeShapeTable table = VerticalCsvService.LoadTypeShape(csvPath).ToTypeShapeTable();
          FamilyTypeCsvApplyResult result = FamilyTypeCsvApplier.Apply(doc, table);
          var text = new StringBuilder();
          text.AppendLine($"CSV: {csvPath}");
          text.AppendLine($"Types created: {result.TypesCreated}");
          text.AppendLine($"Types updated: {result.TypesUpdated}");
          text.AppendLine($"Parameters added: {result.ParametersAdded}");
          text.AppendLine($"Values set: {result.ValuesSet}");
          AppendWarnings(text, result.Warnings);
          return text.ToString();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task CopyAndAddPara()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          return CopyAndAddParaService.Apply(doc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task AddArrayPara()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          return AddArraySegmentParaService.Apply(doc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task RemoveGeneralMakeInstance()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          return FamilyGeneralToInstanceService.Apply(doc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task AddXnYn()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          return AddXnYnService.Apply(doc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task UpdateToolFamily()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          return FamilyUpdateToolService.Apply(doc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task UpdateChild()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument
            ?? throw new InvalidOperationException("No active document.");
          if (!uidoc.Document.IsFamilyDocument)
          {
            throw new InvalidOperationException("Family Support runs in a family document only.");
          }

          return FamilyUpdateChildService.Apply(uidoc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task UpdateMain()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument
            ?? throw new InvalidOperationException("No active document.");
          if (!uidoc.Document.IsFamilyDocument)
          {
            throw new InvalidOperationException("Family Support runs in a family document only.");
          }

          return FamilyUpdateMainService.Apply(uidoc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task MapBeam()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument
            ?? throw new InvalidOperationException("No active document.");
          if (!uidoc.Document.IsFamilyDocument)
          {
            throw new InvalidOperationException("Family Support runs in a family document only.");
          }

          return MapBeamService.Apply(uidoc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task MapVToVisible()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          return MapVToVisibleService.Apply(doc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task RemoveVisibleN()
    {
      try
      {
        string message = await RevitTask.RunAsync(uiapp =>
        {
          Document doc = RequireFamilyDocument(uiapp);
          return RemoveVisibleNService.Apply(doc).ToMessage();
        });

        Status = message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    private static Document RequireFamilyDocument(UIApplication uiapp)
    {
      Document doc = uiapp.ActiveUIDocument?.Document
        ?? throw new InvalidOperationException("No active document.");
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Family Support runs in a family document only.");
      }

      return doc;
    }

    private static void AppendWarnings(StringBuilder text, IReadOnlyList<string> warnings)
    {
      if (warnings.Count == 0)
      {
        return;
      }

      text.AppendLine();
      text.AppendLine("Warnings:");
      foreach (string warning in warnings)
      {
        text.AppendLine("- " + warning);
      }
    }
  }
}

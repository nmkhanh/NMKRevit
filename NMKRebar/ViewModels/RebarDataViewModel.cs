using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRebar.Services;
using System.IO;
using Forms = System.Windows.Forms;

namespace NMKRebar.ViewModels
{
  public partial class RebarDataViewModel : ObservableObject
  {
    private readonly UIApplication _uiapp;

    private bool _ready;

    public RebarDataViewModel(UIApplication uiapp)
    {
      _uiapp = uiapp;
      DataFolder = NMKRebar.Properties.Settings.Default.DataFolder ?? string.Empty;
      _ready = true;
    }

    [ObservableProperty]
    private string _dataFolder = string.Empty;

    [ObservableProperty]
    private string _status = "Select the project folder that contains TypeShape_Varies files.";

    [RelayCommand]
    private void BrowseFolder()
    {
      try
      {
        using var dialog = new Forms.FolderBrowserDialog
        {
          Description = "Folder containing TypeShape_Varies CSV files",
          SelectedPath = Directory.Exists(DataFolder) ? DataFolder : string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
          return;
        }

        DataFolder = dialog.SelectedPath;
        Status = "Folder saved. Use Set Rebar Varies.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task SetRebarVaries()
    {
      try
      {
        SaveSelections();
        string folder = DataFolder;
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return SetRebarVariesService.Create(uidoc, folder).ToMessage();
        });
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Selection cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    partial void OnDataFolderChanged(string value) => SaveSelections();

    private void SaveSelections()
    {
      if (!_ready)
      {
        return;
      }

      var settings = NMKRebar.Properties.Settings.Default;
      settings.DataFolder = DataFolder ?? string.Empty;
      settings.Save();
    }
  }
}

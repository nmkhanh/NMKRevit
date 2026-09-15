using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRebar.Services;
using System.IO;
using Forms = System.Windows.Forms;

namespace NMKRebar.ViewModels
{
  public partial class CreateRebarTypeViewModel : ObservableObject
  {
    private readonly UIApplication _uiapp;
    private bool _ready;

    public CreateRebarTypeViewModel(UIApplication uiapp)
    {
      _uiapp = uiapp;
      DataFolder = NMKRebar.Properties.Settings.Default.DataFolder ?? string.Empty;
      _ready = true;
    }

    [ObservableProperty]
    private string _dataFolder = string.Empty;

    [ObservableProperty]
    private string _status = "Select the folder that contains Rebar.txt.";

    [RelayCommand]
    private void BrowseFolder()
    {
      try
      {
        using var dialog = new Forms.FolderBrowserDialog
        {
          Description = "Folder containing Rebar.txt",
          SelectedPath = Directory.Exists(DataFolder) ? DataFolder : string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
          return;
        }

        DataFolder = dialog.SelectedPath;
        Status = "Folder saved. Create Rebar Type uses Rebar.txt only.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task CreateRebarType()
    {
      try
      {
        SaveFolder();
        string folder = DataFolder;
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = uiapp.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No active document.");
          if (doc.IsFamilyDocument)
          {
            throw new InvalidOperationException("Create Rebar Type runs in a project document.");
          }

          return RebarTypeCreateService.Create(doc, folder).ToMessage();
        });
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    partial void OnDataFolderChanged(string value) => SaveFolder();

    private void SaveFolder()
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

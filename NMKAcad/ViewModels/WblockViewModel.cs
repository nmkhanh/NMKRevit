using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKAcad.Services;
using Forms = System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace NMKAcad.ViewModels
{
  public partial class WblockViewModel : ObservableObject, IDisposable
  {
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public WblockViewModel(Dispatcher dispatcher)
    {
      _dispatcher = dispatcher;
      SaveFolder = Properties.Settings.Default.SaveFolder ?? string.Empty;
      Prefix = Properties.Settings.Default.Prefix ?? string.Empty;
      Main = Properties.Settings.Default.Main ?? string.Empty;
      Suffix = Properties.Settings.Default.Suffix ?? string.Empty;
      RefreshDrawing();
      AcadApp.DocumentManager.DocumentActivated += OnDocumentActivated;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WblockCommand))]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WblockCommand))]
    [NotifyPropertyChangedFor(nameof(CombinedName))]
    private string _prefix = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WblockCommand))]
    [NotifyPropertyChangedFor(nameof(CombinedName))]
    private string _main = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WblockCommand))]
    [NotifyPropertyChangedFor(nameof(CombinedName))]
    private string _suffix = string.Empty;

    [ObservableProperty]
    private string _currentDrawing = string.Empty;

    [ObservableProperty]
    private string _status = "Pick a folder, set Prefix / Main / Suffix, then Wblock. Suffix increases after each save. Switching drawings keeps this panel.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WblockCommand))]
    private bool _isBusy;

    public string CombinedName => WblockService.SanitizeFileName($"{Prefix}{Main}{Suffix}");

    [RelayCommand]
    private void BrowseFolder()
    {
      try
      {
        using var dialog = new Forms.FolderBrowserDialog
        {
          Description = "Folder to save Wblock DWG files",
          SelectedPath = Directory.Exists(SaveFolder) ? SaveFolder : string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
          return;
        }

        SaveFolder = dialog.SelectedPath;
        SaveSettings();
        Status = "Folder saved.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand(CanExecute = nameof(CanWblock))]
    private async Task WblockAsync()
    {
      IsBusy = true;
      WblockPalette.HideForPick();
      try
      {
        SaveSettings();
        string folder = SaveFolder;
        string name = CombinedName;

        string path = string.Empty;
        await AcadCommandRunner.RunAsync(() =>
        {
          path = WblockService.ExportSelection(folder, name);
        });

        Suffix = SuffixIncrementer.Next(Suffix);
        SaveSettings();
        Status = $"Saved: {path}";
      }
      catch (OperationCanceledException)
      {
        Status = "Selection cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
      finally
      {
        WblockPalette.RestoreAfterPick();
        RefreshDrawing();
        IsBusy = false;
      }
    }

    public void Dispose()
    {
      if (_disposed)
      {
        return;
      }

      AcadApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
      SaveSettings();
      _disposed = true;
    }

    private bool CanWblock()
    {
      return !IsBusy
        && !string.IsNullOrWhiteSpace(SaveFolder)
        && Directory.Exists(SaveFolder)
        && !string.IsNullOrWhiteSpace(CombinedName);
    }

    private void OnDocumentActivated(object sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
    {
      _dispatcher.BeginInvoke(new Action(RefreshDrawing));
    }

    private void RefreshDrawing()
    {
      CurrentDrawing = WblockService.CurrentDrawingName();
    }

    private void SaveSettings()
    {
      Properties.Settings.Default.SaveFolder = SaveFolder ?? string.Empty;
      Properties.Settings.Default.Prefix = Prefix ?? string.Empty;
      Properties.Settings.Default.Main = Main ?? string.Empty;
      Properties.Settings.Default.Suffix = Suffix ?? string.Empty;
      Properties.Settings.Default.Save();
    }
  }
}

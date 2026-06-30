using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRevit.Rebar.Models;
using NMKRevit.Rebar.Services;
using Revit.Async;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace NMKRevit.Rebar.ViewModels
{
  public partial class RebarPlacementViewModel : ObservableObject
  {
    private readonly RebarPlacementService _placementService = new();

    public RebarPlacementViewModel(string defaultJsonPath)
    {
      JsonPath = File.Exists(defaultJsonPath) ? defaultJsonPath : string.Empty;
    }

    public ObservableCollection<RebarToolLogEntry> Logs { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseJsonCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlaceRebarCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _jsonPath = string.Empty;

    [ObservableProperty]
    private string _typeNamePrefix = string.Empty;

    [ObservableProperty]
    private string _leftOffsetMm = "150";

    [ObservableProperty]
    private string _rightOffsetMm = "150";

    [ObservableProperty]
    private string _faceOffsetMm = "150";

    [ObservableProperty]
    private bool _alternateRotate180;

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void BrowseJson()
    {
      var dialog = new Microsoft.Win32.OpenFileDialog
      {
        Title = "Chon AI Rebar JSON",
        Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        CheckFileExists = true
      };

      if (!string.IsNullOrWhiteSpace(JsonPath))
      {
        string? folder = Path.GetDirectoryName(JsonPath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
          dialog.InitialDirectory = folder;
        }
      }

      if (dialog.ShowDialog() == true)
      {
        JsonPath = dialog.FileName;
      }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PlaceRebar()
    {
      if (IsBusy)
      {
        return;
      }

      const string tool = "AI Rebar";
      try
      {
        ValidateInputs();
      }
      catch (Exception ex)
      {
        Logs.Add(new RebarToolLogEntry(RebarToolLogLevel.Warning, tool, string.Empty, ex.Message));
        return;
      }

      IsBusy = true;
      Logs.Add(new RebarToolLogEntry(RebarToolLogLevel.Info, tool, string.Empty, "Started."));
      try
      {
        RebarPlacementResult? result = null;
        RebarPlacementOptions options = ToOptions();
        await RevitTask.RunAsync(uiapp =>
        {
          result = _placementService.Place(uiapp, options);
        });

        if (result != null)
        {
          foreach (var item in result.CountsByBarId)
          {
            Logs.Add(new RebarToolLogEntry(RebarToolLogLevel.Success, tool, item.Key, $"Created {item.Value}."));
          }
          Logs.Add(new RebarToolLogEntry(RebarToolLogLevel.Success, tool, string.Empty, $"Created {result.CreatedCount} bars."));
        }
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Logs.Add(new RebarToolLogEntry(RebarToolLogLevel.Warning, tool, string.Empty, "Cancelled."));
      }
      catch (Exception ex)
      {
        Logs.Add(new RebarToolLogEntry(RebarToolLogLevel.Error, tool, string.Empty, ex.Message));
      }
      finally
      {
        IsBusy = false;
      }
    }

    private void ValidateInputs()
    {
      if (string.IsNullOrWhiteSpace(JsonPath) || !File.Exists(JsonPath))
      {
        throw new InvalidOperationException("Chon file JSON hop le.");
      }
      RebarPlacementViewModelParser.ReadNumber(LeftOffsetMm);
      RebarPlacementViewModelParser.ReadNumber(RightOffsetMm);
      RebarPlacementViewModelParser.ReadNumber(FaceOffsetMm);
    }

    public RebarPlacementOptions ToOptions()
    {
      return new RebarPlacementOptions
      {
        Accepted = true,
        JsonPath = JsonPath,
        TypeNamePrefix = TypeNamePrefix,
        LeftOffsetMm = RebarPlacementViewModelParser.ReadNumber(LeftOffsetMm),
        RightOffsetMm = RebarPlacementViewModelParser.ReadNumber(RightOffsetMm),
        FaceOffsetMm = RebarPlacementViewModelParser.ReadNumber(FaceOffsetMm),
        AlternateRotate180 = AlternateRotate180
      };
    }
  }
}

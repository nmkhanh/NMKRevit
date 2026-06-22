using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRevit.FloorTool.Models;
using NMKRevit.FloorTool.Services;
using Revit.Async;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace NMKRevit.FloorTool.ViewModels
{
  public partial class FloorToolViewModel : ObservableObject
  {
    private readonly FloorSelectionService _selectionService = new();
    private readonly FloorSplitService _splitService = new();
    private readonly FloorJoinService _joinService = new();

    public ObservableCollection<FloorToolLogEntry> Logs { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectMultiIslandFloorsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SplitMultiIslandFloorsCommand))]
    [NotifyCanExecuteChangedFor(nameof(JoinFloorsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSelectedScope = true;

    [ObservableProperty]
    private bool _isActiveViewScope;

    [ObservableProperty]
    private bool _isAllModelScope;

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SelectMultiIslandFloors()
    {
      await Run("Select Multi-Island", uiapp => _selectionService.SelectMultiIslandFloors(uiapp));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SplitMultiIslandFloors()
    {
      await Run("Split Multi-Island", uiapp => _splitService.SplitMultiIslandFloors(uiapp));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task JoinFloors()
    {
      JoinFloorScope scope = IsAllModelScope
        ? JoinFloorScope.AllModel
        : IsActiveViewScope ? JoinFloorScope.ActiveView : JoinFloorScope.Selected;
      await Run("Join Floors", uiapp => _joinService.JoinFloors(uiapp, scope));
    }

    private async Task Run(string tool, Func<Autodesk.Revit.UI.UIApplication, FloorToolResult> action)
    {
      if (IsBusy)
      {
        return;
      }
      IsBusy = true;
      Logs.Add(new FloorToolLogEntry(FloorToolLogLevel.Info, tool, string.Empty, "Started."));
      try
      {
        FloorToolResult? result = null;
        await RevitTask.RunAsync(uiapp =>
        {
          result = action(uiapp);
        });
        if (result != null)
        {
          foreach (FloorToolResultItem item in result.Items)
          {
            Logs.Add(new FloorToolLogEntry(item.Level, tool, item.ElementId, item.Message));
          }
          Logs.Add(new FloorToolLogEntry(FloorToolLogLevel.Success, tool, string.Empty, result.Summary));
        }
      }
      catch (Exception ex)
      {
        Logs.Add(new FloorToolLogEntry(FloorToolLogLevel.Error, tool, string.Empty, ex.Message));
      }
      finally
      {
        IsBusy = false;
      }
    }
  }
}

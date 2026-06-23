using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRevit.Tags.Models;
using NMKRevit.Tags.Services;
using Revit.Async;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace NMKRevit.Tags.ViewModels
{
  public partial class TagsToolViewModel : ObservableObject
  {
    private readonly TagsToolService _tagsToolService = new();

    public ObservableCollection<TagsToolLogEntry> Logs { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckTagsCommand))]
    [NotifyCanExecuteChangedFor(nameof(TagColumnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(TagFloorsCommand))]
    [NotifyCanExecuteChangedFor(nameof(TagWallsCommand))]
    [NotifyCanExecuteChangedFor(nameof(TagAllCommand))]
    private bool _isBusy;

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckTags()
    {
      await Run("Check Tags", uiapp => _tagsToolService.CheckTags(uiapp));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TagColumns()
    {
      await Run("Tag Columns", uiapp => _tagsToolService.TagAll(uiapp, TagTargetKind.Column));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TagFloors()
    {
      await Run("Tag Floors", uiapp => _tagsToolService.TagAll(uiapp, TagTargetKind.Floor));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TagWalls()
    {
      await Run("Tag Walls", uiapp => _tagsToolService.TagAll(uiapp, TagTargetKind.Wall));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TagAll()
    {
      await Run("Tag All", uiapp => _tagsToolService.TagAll(uiapp, TagTargetKind.Unknown));
    }

    private async Task Run(string tool, Func<Autodesk.Revit.UI.UIApplication, TagsToolResult> action)
    {
      if (IsBusy)
      {
        return;
      }
      IsBusy = true;
      Logs.Add(new TagsToolLogEntry(TagsToolLogLevel.Info, tool, string.Empty, "Started."));
      try
      {
        TagsToolResult? result = null;
        await RevitTask.RunAsync(uiapp =>
        {
          result = action(uiapp);
        });
        if (result != null)
        {
          foreach (TagsToolResultItem item in result.Items)
          {
            Logs.Add(new TagsToolLogEntry(item.Level, tool, item.ElementId, item.Message));
          }
          Logs.Add(new TagsToolLogEntry(TagsToolLogLevel.Success, tool, string.Empty, result.Summary));
        }
      }
      catch (Exception ex)
      {
        Logs.Add(new TagsToolLogEntry(TagsToolLogLevel.Error, tool, string.Empty, ex.Message));
      }
      finally
      {
        IsBusy = false;
      }
    }
  }
}

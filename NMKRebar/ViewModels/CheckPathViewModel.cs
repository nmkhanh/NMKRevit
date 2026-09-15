using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRebar.Services;

namespace NMKRebar.ViewModels
{
  public partial class CheckPathViewModel : ObservableObject
  {
    private readonly UIApplication _uiapp;

    public CheckPathViewModel(UIApplication uiapp)
    {
      _uiapp = uiapp;
    }

    [ObservableProperty]
    private string _status = "Select NMK_Rebar_Array in the model, or click Create Model Lines to pick.";

    [RelayCommand]
    private async Task CreateModelLines()
    {
      try
      {
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return CreateRebarByLineService.CreatePathModelLinesCheck(uidoc).ToMessage();
        });
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Pick cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }
  }
}

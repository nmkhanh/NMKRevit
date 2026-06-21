using CommunityToolkit.Mvvm.ComponentModel;

namespace NMKRevit.NMKPrint.ViewModels
{
  public partial class FilterViewModel : ObservableObject
  {
    [ObservableProperty]
    private string _keyword = string.Empty;
  }
}

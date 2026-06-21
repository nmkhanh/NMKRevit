using System.Windows;
using NMKRevit.NMKPrint.ViewModels;

namespace NMKRevit.NMKPrint.Views
{
  public partial class PrintWindow : Window
  {
    public PrintWindow()
    {
      InitializeComponent();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
      if (DataContext is PrintViewModel viewModel)
      {
        viewModel.SaveState();
      }

      base.OnClosing(e);
    }
  }
}

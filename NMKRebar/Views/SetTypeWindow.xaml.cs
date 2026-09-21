using System.Windows;
using System.Windows.Input;
using NMKRebar.ViewModels;

namespace NMKRebar.Views
{
  public partial class SetTypeWindow : Window
  {
    public SetTypeWindow()
    {
      InitializeComponent();
    }

    private void OnTypeListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
      if (DataContext is SetTypeViewModel viewModel)
      {
        viewModel.ChangeSelectedInstancesToTypeCommand.Execute(null);
      }
    }
  }
}

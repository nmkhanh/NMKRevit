using CommunityToolkit.Mvvm.ComponentModel;
using Autodesk.Revit.DB;
using NMKRevit.NMKPrint.Models;
using NMKRevit.NMKPrint.Services;
using System.Collections.ObjectModel;
using System.Drawing.Printing;
using System.Linq;

namespace NMKRevit.NMKPrint.ViewModels
{
  public partial class SettingsViewModel : ObservableObject
  {
    public SettingsViewModel(PrintSettings settings, Pdf24Service pdf24Service)
    {
      Settings = settings;
      foreach (string printer in PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(x => x))
      {
        Printers.Add(printer);
      }

      string pdf24 = pdf24Service.ResolvePrinterName();
      if (string.IsNullOrWhiteSpace(Settings.PrinterName) || !Printers.Contains(Settings.PrinterName))
      {
        Settings.PrinterName = Printers.FirstOrDefault(x => x == pdf24) ?? pdf24;
      }
    }

    public PrintSettings Settings { get; }
    public ObservableCollection<string> Printers { get; } = new();
    public ObservableCollection<string> DwgExportSetups { get; } = new();

    public void LoadDwgExportSetups(Document doc)
    {
      DwgExportSetups.Clear();
      DwgExportSetups.Add(string.Empty);
      foreach (string name in DWGExportOptions.GetPredefinedSetupNames(doc).OrderBy(x => x))
      {
        DwgExportSetups.Add(name);
      }
    }
  }
}

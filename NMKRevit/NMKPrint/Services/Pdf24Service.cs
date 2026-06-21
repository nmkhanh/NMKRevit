using Microsoft.Win32;
using System;
using System.Drawing.Printing;
using System.IO;
using System.Linq;

namespace NMKRevit.NMKPrint.Services
{
  public class Pdf24Service
  {
    private const string KeyPath = @"Software\PDF24\Services\PDF";

    public string ResolvePrinterName()
    {
      string[] printers = PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
      return printers.FirstOrDefault(p => p.Equals("PDF24", StringComparison.OrdinalIgnoreCase))
        ?? printers.FirstOrDefault(p => p.IndexOf("PDF24", StringComparison.OrdinalIgnoreCase) >= 0)
        ?? "PDF24";
    }

    public bool IsInstalled(string printerName)
    {
      return PrinterSettings.InstalledPrinters.Cast<string>()
        .Any(p => p.Equals(printerName, StringComparison.OrdinalIgnoreCase));
    }

    public void ConfigureAutoSave(string outputFolder)
    {
      Directory.CreateDirectory(outputFolder);

      using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
      key.SetValue("AutoSaveDir", outputFolder, RegistryValueKind.String);
      key.SetValue("AutoSaveFilename", "$fileName", RegistryValueKind.String);
      key.SetValue("ShowSaveDialog", 0, RegistryValueKind.DWord);
      key.SetValue("AutoSaveEnabled", 1, RegistryValueKind.DWord);
      key.SetValue("LoadInCreatorIfOpen", 0, RegistryValueKind.DWord);
      key.SetValue("AutoSaveProfile", "default/high", RegistryValueKind.String);
      key.SetValue("AutoSaveOpenDir", 0, RegistryValueKind.DWord);
      key.SetValue("AutoSaveOverwriteFile", 1, RegistryValueKind.DWord);
      key.SetValue("AutoSaveShowProgress", 0, RegistryValueKind.DWord);
      key.SetValue("AutoSaveUseFileChooser", 0, RegistryValueKind.DWord);
      key.SetValue("AutoSaveUseFileCmd", 0, RegistryValueKind.DWord);
      key.SetValue("Handler", "autoSave", RegistryValueKind.String);
    }
  }
}

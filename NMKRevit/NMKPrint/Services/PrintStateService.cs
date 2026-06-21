using NMKRevit.NMKPrint.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace NMKRevit.NMKPrint.Services
{
  public class PrintStateService
  {
    private readonly string _statePath;

    public PrintStateService()
    {
      string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NMKRevit", "NMKPrint");
      _statePath = Path.Combine(folder, "state.json");
    }

    public PrintState Load()
    {
      try
      {
        if (!File.Exists(_statePath))
        {
          return new PrintState();
        }

        string json = File.ReadAllText(_statePath);
        return JsonConvert.DeserializeObject<PrintState>(json) ?? new PrintState();
      }
      catch
      {
        return new PrintState();
      }
    }

    public void Save(PrintState state)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(_statePath) ?? string.Empty);
      File.WriteAllText(_statePath, JsonConvert.SerializeObject(state, Formatting.Indented));
    }
  }

  public class PrintState
  {
    public string OutputFolder { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public bool ExportPdf { get; set; } = true;
    public bool ExportDwg { get; set; }
    public bool CombinePdf { get; set; }
    public bool CreateSeparateFiles { get; set; } = true;
    public bool UseNamingConvention { get; set; } = true;
    public string NamingDate { get; set; } = string.Empty;
    public string NamingProjectCode { get; set; } = string.Empty;
    public string NamingNode { get; set; } = string.Empty;
    public string FileCombineName { get; set; } = string.Empty;
    public bool SplitByFormat { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public string DwgExportSetup { get; set; } = string.Empty;
    public bool ExportViewsAsExternalReferences { get; set; }
    public string SelectedSourceName { get; set; } = "Select by user";
    public string ViewSheetSetName { get; set; } = string.Empty;
    public List<string> SelectedItemKeys { get; set; } = new();
    public List<NamingTemplateItem> PdfTemplate { get; set; } = new();
    public List<NamingTemplateItem> DwgTemplate { get; set; } = new();
    public List<string> ImportPresetPaths { get; set; } = new();
    public string SelectedImportPresetPath { get; set; } = string.Empty;
  }
}

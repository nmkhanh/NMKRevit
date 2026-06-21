using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRevit.NMKPrint.Models;
using NMKRevit.NMKPrint.Services;
using Revit.Async;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace NMKRevit.NMKPrint.ViewModels
{
  public partial class PrintViewModel : ObservableObject
  {
    private readonly UIApplication _uiapp;
    private readonly PaperSizeService _paperSizeService = new();
    private readonly Pdf24Service _pdf24Service = new();
    private readonly NamingTemplateService _namingTemplateService = new();
    private readonly PrintService _printService;
    private readonly RevitPrintItemService _printItemService;
    private readonly PrintStateService _stateService = new();
    private readonly PrintState _state;

    public PrintViewModel(UIApplication uiapp)
    {
      _uiapp = uiapp;
      _state = _stateService.Load();
      Settings = CreateSettings(_state);
      Settings.PropertyChanged += async (_, _) => await RefreshPreviewRowsFromDocument();

      _printService = new PrintService(_paperSizeService, _pdf24Service);
      _printItemService = new RevitPrintItemService(_paperSizeService);

      Select = new SelectViewModel();
      Select.SelectionChanged += async (_, _) =>
      {
        await RefreshPreviewRowsFromDocument();
        await UpdateCustomNamePreview();
      };
      Select.SelectionSourceChanged += async (_, source) => await ApplySelectionSource(source);
      Logs = new PrintLogViewModel();
      CustomName = new CustomNameViewModel(_namingTemplateService);
      CustomName.DraftChanged += async (_, _) => await UpdateCustomNamePreview();
      CustomName.TemplateChanged += async (_, _) =>
      {
        await UpdateCustomNamePreview();
        await RefreshPreviewRowsFromDocument();
      };
      SettingsView = new SettingsViewModel(Settings, _pdf24Service);
      Filter = new FilterViewModel();

      CustomName.AddImportPresetPaths(_state.ImportPresetPaths, _state.SelectedImportPresetPath);
    }

    public PrintSettings Settings { get; }
    public SelectViewModel Select { get; }
    public SettingsViewModel SettingsView { get; }
    public FilterViewModel Filter { get; }
    public CustomNameViewModel CustomName { get; }
    public PrintLogViewModel Logs { get; }
    public ObservableCollection<PrintPreviewRow> PreviewRows { get; } = new();

    [ObservableProperty]
    private int _activePage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _printProgressValue;

    [ObservableProperty]
    private bool _isPrintStatus;

    [ObservableProperty]
    private bool _isPrintProgressIndeterminate;

    [ObservableProperty]
    private string _printStatus = string.Empty;

    partial void OnIsBusyChanged(bool value)
    {
      RefreshPreviewRows();
    }

    public string SelectStatus => $"{Select.SelectedItems.Count} / {Select.Items.Count} selected";

    public void SaveState()
    {
      _stateService.Save(new PrintState
      {
        OutputFolder = Settings.OutputFolder,
        PrinterName = Settings.PrinterName,
        ExportPdf = Settings.ExportPdf,
        ExportDwg = Settings.ExportDwg,
        CombinePdf = Settings.CombinePdf,
        CreateSeparateFiles = Settings.CreateSeparateFiles,
        UseNamingConvention = Settings.UseNamingConvention,
        NamingDate = Settings.NamingDate,
        NamingProjectCode = Settings.NamingProjectCode,
        NamingNode = Settings.NamingNode,
        FileCombineName = Settings.FileCombineName,
        SplitByFormat = Settings.SplitByFormat,
        TimeoutSeconds = Settings.TimeoutSeconds,
        DwgExportSetup = Settings.DwgExportSetup,
        ExportViewsAsExternalReferences = Settings.ExportViewsAsExternalReferences,
        SelectedSourceName = Select.SelectedSource?.Name ?? "Select by user",
        ViewSheetSetName = Select.ViewSheetSetName,
        SelectedItemKeys = Select.GetSelectedKeys(),
        PdfTemplate = CustomName.GetPdfTemplate().ToList(),
        DwgTemplate = CustomName.GetDwgTemplate().ToList(),
        ImportPresetPaths = CustomName.GetImportPresetPaths().ToList(),
        SelectedImportPresetPath = CustomName.SelectedImportPresetPath
      });
    }

    [RelayCommand]
    private async Task Load()
    {
      await RevitTask.RunAsync(uiapp =>
      {
        Document doc = uiapp.ActiveUIDocument.Document;
        if (string.IsNullOrWhiteSpace(Settings.NamingProjectCode))
        {
          Settings.NamingProjectCode = doc.ProjectInformation.BuildingName ?? string.Empty;
        }

        var sheets = _printItemService.GetSheets(doc);
        var views = _printItemService.GetViews(doc);
        Select.SetData(sheets, views);
        Select.SetSelectionSources(_printItemService.GetSelectionSources(doc), _state.SelectedSourceName);
        if (Select.SelectedSource != null && Select.SelectedSource.Kind != SelectionSourceKind.User && Select.SelectedSource.Name == _state.SelectedSourceName)
        {
          Select.ApplySelectionIds(_printItemService.ResolveSelectionSource(doc, Select.SelectedSource, Select.AllItems));
        }
        else
        {
          Select.ApplySelectionKeys(_state.SelectedItemKeys);
        }

        Select.ViewSheetSetName = _state.ViewSheetSetName;
        CustomName.LoadProjectParameters(_namingTemplateService.ScanProjectParameters(doc, sheets, views), _state.PdfTemplate, _state.DwgTemplate);
        SettingsView.LoadDwgExportSetups(doc);
      });

      await RefreshPreviewRowsFromDocument();
    }

    [RelayCommand]
    private void ShowSelect()
    {
      ActivePage = 0;
    }

    [RelayCommand]
    private void ShowSettings()
    {
      ActivePage = 1;
    }

    [RelayCommand]
    private void ShowFilter()
    {
      ActivePage = 2;
    }

    [RelayCommand]
    private void OpenCustomName()
    {
      if (!Settings.CanUseCustomName)
      {
        Logs.Add(LogLevel.Warning, "Custom File Name is only available when Create separate files is selected.", null);
        return;
      }

      CustomName.OpenIfAllowed(Settings.CanUseCustomName);
    }

    [RelayCommand]
    private void SelectFolder()
    {
      using var dialog = new Forms.FolderBrowserDialog
      {
        SelectedPath = Directory.Exists(Settings.OutputFolder) ? Settings.OutputFolder : string.Empty,
        Description = "Select print output folder"
      };

      if (dialog.ShowDialog() == Forms.DialogResult.OK)
      {
        Settings.OutputFolder = dialog.SelectedPath;
      }
    }

    [RelayCommand]
    private void OpenFolder()
    {
      Directory.CreateDirectory(Settings.OutputFolder);
      Process.Start(new ProcessStartInfo
      {
        FileName = Settings.OutputFolder,
        UseShellExecute = true
      });
    }

    private async Task ApplySelectionSource(SelectionSource? source)
    {
      if (source == null || source.Kind == SelectionSourceKind.User)
      {
        return;
      }

      try
      {
        await RevitTask.RunAsync(uiapp =>
        {
          Document doc = uiapp.ActiveUIDocument.Document;
          var ids = _printItemService.ResolveSelectionSource(doc, source, Select.AllItems);
          Select.ApplySelectionIds(ids);
        });
      }
      catch (Exception ex)
      {
        Logs.Add(LogLevel.Error, $"Cannot apply {source.Name}: {ex.Message}", null);
      }
    }

    [RelayCommand]
    private async Task SaveViewSheetSet()
    {
      Select.RefreshSelectionCommand.Execute(null);
      if (Select.SelectedItems.Count == 0)
      {
        Logs.Add(LogLevel.Warning, "Select sheets/views before saving a ViewSheetSet.", null);
        return;
      }

      string setName = Select.ViewSheetSetName?.Trim() ?? string.Empty;
      if (string.IsNullOrWhiteSpace(setName))
      {
        Logs.Add(LogLevel.Warning, "Enter a ViewSheetSet name.", null);
        return;
      }

      await RevitTask.RunAsync(uiapp =>
      {
        Document doc = uiapp.ActiveUIDocument.Document;
        bool exists = new FilteredElementCollector(doc)
          .OfClass(typeof(ViewSheetSet))
          .Cast<ViewSheetSet>()
          .Any(x => x.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
          Logs.Add(LogLevel.Error, $"ViewSheetSet already exists: {setName}", null);
          return;
        }

        PrintManager printManager = doc.PrintManager;
        ViewSheetSetting setting = printManager.ViewSheetSetting;
        ViewSet viewSet = new ViewSet();
        foreach (PrintItem item in Select.SelectedItems)
        {
          viewSet.Insert(item.View);
        }

        using Transaction transaction = new Transaction(doc, "NMK Save ViewSheetSet");
        transaction.Start();
        setting.CurrentViewSheetSet.Views = viewSet;
        bool saved = setting.SaveAs(setName);
        transaction.Commit();

        if (saved)
        {
          Select.SetSelectionSources(_printItemService.GetSelectionSources(doc), $"ViewSheetSet : {setName}");
          Logs.Add(LogLevel.Done, $"ViewSheetSet created: {setName}", null);
        }
        else
        {
          Logs.Add(LogLevel.Error, $"Cannot create ViewSheetSet: {setName}", null);
        }
      });
    }

    private async Task UpdateCustomNamePreview()
    {
      PrintItem? item = Select.SelectedItems.FirstOrDefault();
      if (item == null)
      {
        CustomName.Preview = "Preview...";
        return;
      }

      await RevitTask.RunAsync(uiapp =>
      {
        Document doc = uiapp.ActiveUIDocument.Document;
        CustomName.Preview = _namingTemplateService.BuildName(doc, item, CustomName.Items);
      });
    }

    [RelayCommand]
    private async Task Run()
    {
      if (IsBusy)
      {
        return;
      }

      Select.RefreshSelectionCommand.Execute(null);
      await RefreshPreviewRowsFromDocument();
      List<PrintItem> selected = Select.SelectedItems.ToList();
      if (selected.Count == 0)
      {
        Logs.Add(LogLevel.Warning, "No sheets/views selected.", null);
        return;
      }

      if (!Settings.ExportPdf && !Settings.ExportDwg)
      {
        Logs.Add(LogLevel.Warning, "Select PDF, DWG, or both before printing.", null);
        return;
      }

      IsBusy = true;
      Logs.Clear();
      try
      {
        List<PrintJob> jobs = new();
        await RevitTask.RunAsync(uiapp =>
        {
          Document doc = uiapp.ActiveUIDocument.Document;
          jobs = BuildJobs(doc, selected);
        });

        int completed = 0;
        var completedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BeginPrintProgress(jobs.Count);
        await _printService.RunAsync(
          _uiapp,
          jobs,
          Settings,
          (level, message, path) => Logs.Add(level, message, path),
          async (job, level, message, path) =>
          {
            await Logs.UpdateJobAsync(job, level, message, path);
            if (level == LogLevel.Done || level == LogLevel.Error)
            {
              string key = $"{job.Format}:{job.Item.Id}";
              if (completedKeys.Add(key))
              {
                completed++;
                UpdatePrintProgress(completed, jobs.Count);
              }
            }
          },
          async (level, message, path) =>
          {
            await Logs.UpdateStageAsync("PDF:Combine", "PDF", "Combine", level, message, path);
            if (level == LogLevel.Printing)
            {
              BeginIndeterminatePrintProgress(message);
            }
            else
            {
              EndIndeterminatePrintProgress(completed, jobs.Count);
            }
          });
      }
      finally
      {
        IsBusy = false;
        IsPrintStatus = false;
        IsPrintProgressIndeterminate = false;
      }
    }

    private List<PrintJob> BuildJobs(Document doc, IEnumerable<PrintItem> selected)
    {
      var jobs = new List<PrintJob>();
      foreach (PrintItem item in selected)
      {
        string pdfFileName = GetOutputFileName(doc, item, PrintFormat.PDF);
        string dwgFileName = GetOutputFileName(doc, item, PrintFormat.DWG);
        if (Settings.ExportPdf)
        {
          jobs.Add(new PrintJob
          {
            Item = item,
            Format = PrintFormat.PDF,
            FileName = Settings.CombinePdf ? GetCombineFileName(selected) : pdfFileName,
            OutputFolder = GetOutputFolder(PrintFormat.PDF)
          });
        }

        if (Settings.ExportDwg)
        {
          jobs.Add(new PrintJob
          {
            Item = item,
            Format = PrintFormat.DWG,
            FileName = dwgFileName,
            OutputFolder = GetOutputFolder(PrintFormat.DWG)
          });
        }
      }

      return jobs;
    }

    private string GetOutputFolder(PrintFormat format)
    {
      if (!Settings.SplitByFormat)
      {
        return Settings.OutputFolder;
      }

      return Path.Combine(Settings.OutputFolder, format.ToString());
    }

    private string GetOutputFileName(Document doc, PrintItem item, PrintFormat format)
    {
      IReadOnlyList<NamingTemplateItem> template = format == PrintFormat.PDF
        ? CustomName.AppliedPdfItems
        : CustomName.AppliedDwgItems;

      return HasCustomName(template)
        ? _namingTemplateService.BuildName(doc, item, template)
        : FileNameSanitizer.Sanitize(GetDefaultFileName(item));
    }

    private string GetPreviewName(Document doc, PrintItem item, PrintFormat format)
    {
      if (Settings.CombinePdf && format == PrintFormat.PDF)
      {
        return GetItemName(item);
      }

      IReadOnlyList<NamingTemplateItem> template = format == PrintFormat.PDF
        ? CustomName.AppliedPdfItems
        : CustomName.AppliedDwgItems;

      return HasCustomName(template)
        ? _namingTemplateService.BuildName(doc, item, template)
        : GetItemName(item);
    }

    private string GetCombineFileName(IEnumerable<PrintItem> selected)
    {
      if (!string.IsNullOrWhiteSpace(Settings.EffectiveFileCombineName))
      {
        return Settings.EffectiveFileCombineName;
      }

      PrintItem? first = selected.FirstOrDefault();
      return first == null ? "NMKPrint" : FileNameSanitizer.Sanitize(GetDefaultFileName(first));
    }

    private static bool HasCustomName(IEnumerable<NamingTemplateItem> template)
    {
      return template.Any(x => x.IsChecked);
    }

    private static string GetItemName(PrintItem item)
    {
      return item.Name;
    }

    private static string GetDefaultFileName(PrintItem item)
    {
      return item.IsSheet && !string.IsNullOrWhiteSpace(item.Number)
        ? $"{item.Number}-{item.Name}"
        : item.Name;
    }

    private void RefreshPreviewRows()
    {
      PreviewRows.Clear();
      int pdfIndex = 1;
      foreach (PrintItem item in Select.SelectedItems.OrderBy(x => x.Number + x.Name, NaturalStringComparer.Instance))
      {
        if (Settings.ExportPdf)
        {
          PreviewRows.Add(new PrintPreviewRow
          {
            Index = pdfIndex++,
            Number = item.Number,
            Name = item.IsSheet ? $"[{item.Number}] - {item.Name}" : item.Name,
            Revision = item.Revision,
            RevisionDate = item.RevisionDate,
            Size = item.Size,
            Format = "PDF",
            Orientation = item.Orientation
          });
        }
      }

      foreach (PrintItem item in Select.SelectedItems.OrderBy(x => x.Number + x.Name, NaturalStringComparer.Instance))
      {
        if (Settings.ExportDwg)
        {
          PreviewRows.Add(new PrintPreviewRow
          {
            Index = 0,
            Number = item.Number,
            Name = item.Name,
            Revision = item.Revision,
            RevisionDate = item.RevisionDate,
            Size = item.Size,
            Format = "DWG",
            Orientation = item.Orientation
          });
        }
      }

      OnPropertyChanged(nameof(SelectStatus));
    }

    private async Task RefreshPreviewRowsFromDocument()
    {
      if (Select.SelectedItems.Count == 0)
      {
        RefreshPreviewRows();
        return;
      }

      try
      {
        await RevitTask.RunAsync(uiapp =>
        {
          Document doc = uiapp.ActiveUIDocument.Document;
          List<PrintPreviewRow> rows = BuildPreviewRows(doc);
          PreviewRows.Clear();
          foreach (PrintPreviewRow row in rows)
          {
            PreviewRows.Add(row);
          }
          OnPropertyChanged(nameof(SelectStatus));
        });
      }
      catch
      {
        RefreshPreviewRows();
      }
    }

    private List<PrintPreviewRow> BuildPreviewRows(Document doc)
    {
      var rows = new List<PrintPreviewRow>();
      int pdfIndex = 1;
      foreach (PrintItem item in Select.SelectedItems.OrderBy(x => x.Number + x.Name, NaturalStringComparer.Instance))
      {
        if (Settings.ExportPdf)
        {
          string pdfName = Settings.CombinePdf
            ? GetItemName(item)
            : GetPreviewName(doc, item, PrintFormat.PDF);
          rows.Add(new PrintPreviewRow
          {
            Index = pdfIndex++,
            Number = item.Number,
            Name = pdfName,
            Revision = item.Revision,
            RevisionDate = item.RevisionDate,
            Size = item.Size,
            Format = "PDF",
            Orientation = item.Orientation
          });
        }
      }

      foreach (PrintItem item in Select.SelectedItems.OrderBy(x => x.Number + x.Name, NaturalStringComparer.Instance))
      {
        if (Settings.ExportDwg)
        {
          rows.Add(new PrintPreviewRow
          {
            Index = 0,
            Number = item.Number,
            Name = GetPreviewName(doc, item, PrintFormat.DWG),
            Revision = item.Revision,
            RevisionDate = item.RevisionDate,
            Size = item.Size,
            Format = "DWG",
            Orientation = item.Orientation
          });
        }
      }

      return rows;
    }

    private static PrintSettings CreateSettings(PrintState state)
    {
      string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NMKPrint");
      return new PrintSettings
      {
        OutputFolder = string.IsNullOrWhiteSpace(state.OutputFolder) ? defaultFolder : state.OutputFolder,
        PrinterName = string.IsNullOrWhiteSpace(state.PrinterName) ? "PDF24" : state.PrinterName,
        ExportPdf = state.ExportPdf,
        ExportDwg = state.ExportDwg,
        CreateSeparateFiles = state.CreateSeparateFiles,
        CombinePdf = state.CombinePdf,
        UseNamingConvention = string.IsNullOrWhiteSpace(state.FileCombineName) ? true : state.UseNamingConvention,
        NamingDate = string.IsNullOrWhiteSpace(state.NamingDate) ? DateTime.Now.ToString("yyMMdd") : state.NamingDate,
        NamingProjectCode = state.NamingProjectCode,
        NamingNode = string.IsNullOrWhiteSpace(state.NamingNode) ? "GA PLANS" : state.NamingNode,
        FileCombineName = state.FileCombineName,
        SplitByFormat = state.SplitByFormat,
        TimeoutSeconds = state.TimeoutSeconds <= 0 ? 120 : state.TimeoutSeconds,
        DwgExportSetup = state.DwgExportSetup,
        ExportViewsAsExternalReferences = state.ExportViewsAsExternalReferences
      };
    }

    private void BeginPrintProgress(int total)
    {
      OnUiThread(() =>
      {
        PrintProgressValue = 0;
        PrintStatus = $"0 / {total} (0 %)";
        IsPrintProgressIndeterminate = false;
        IsPrintStatus = true;
      });
    }

    private void UpdatePrintProgress(int current, int total)
    {
      double progress = total == 0 ? 0 : Math.Round(current * 100.0 / total, 0);
      OnUiThread(() =>
      {
        IsPrintProgressIndeterminate = false;
        PrintProgressValue = progress;
        PrintStatus = $"{current} / {total} ({progress:0} %)";
      });
    }

    private void BeginIndeterminatePrintProgress(string message)
    {
      OnUiThread(() =>
      {
        IsPrintProgressIndeterminate = true;
        IsPrintStatus = true;
        PrintStatus = message;
      });
    }

    private void EndIndeterminatePrintProgress(int current, int total)
    {
      OnUiThread(() =>
      {
        IsPrintProgressIndeterminate = false;
        UpdatePrintProgress(current, total);
      });
    }

    private static void OnUiThread(Action action)
    {
      if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
      {
        dispatcher.Invoke(action);
        return;
      }

      action();
    }
  }
}

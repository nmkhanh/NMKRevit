using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRevit.NMKPrint.Models;
using NMKRevit.NMKPrint.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace NMKRevit.NMKPrint.ViewModels
{
  public partial class CustomNameViewModel : ObservableObject
  {
    private readonly NamingTemplateService _namingTemplateService;
    private readonly List<NamingTemplateItem> _availableParameters = new();
    private List<NamingTemplateItem> _appliedPdfItems = new();
    private List<NamingTemplateItem> _appliedDwgItems = new();
    private bool _normalizing;
    private bool _suppressPresetApply;

    public CustomNameViewModel(NamingTemplateService namingTemplateService)
    {
      _namingTemplateService = namingTemplateService;
    }

    public event EventHandler? TemplateChanged;
    public event EventHandler? DraftChanged;

    public sealed class NamingPreset
    {
      public string Name { get; init; } = string.Empty;
      public string Path { get; init; } = string.Empty;

      public override string ToString()
      {
        return Name;
      }
    }

    public ObservableCollection<NamingTemplateItem> PdfItems { get; } = new();
    public ObservableCollection<NamingTemplateItem> DwgItems { get; } = new();
    public ObservableCollection<NamingTemplateItem> AvailableItems { get; } = new();
    public ObservableCollection<NamingTemplateItem> CheckedItems { get; } = new();
    public ObservableCollection<NamingPreset> ImportPresets { get; } = new();

    public ObservableCollection<NamingTemplateItem> Items => ActiveFormat == PrintFormat.PDF ? PdfItems : DwgItems;
    public string ParameterStatus => $"{CheckedItems.Count}/{Items.Count} selected from current project";
    public bool HasAppliedRules => _appliedPdfItems.Any(x => x.IsChecked) || _appliedDwgItems.Any(x => x.IsChecked);
    public IReadOnlyList<NamingTemplateItem> AppliedPdfItems => _appliedPdfItems;
    public IReadOnlyList<NamingTemplateItem> AppliedDwgItems => _appliedDwgItems;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private PrintFormat _activeFormat = PrintFormat.PDF;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private NamingTemplateItem? _selectedItem;

    [ObservableProperty]
    private NamingPreset? _selectedImportPreset;

    partial void OnActiveFormatChanged(PrintFormat value)
    {
      OnPropertyChanged(nameof(Items));
      RebuildParameterViews();
      DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value)
    {
      RebuildParameterViews();
    }

    partial void OnSelectedImportPresetChanged(NamingPreset? value)
    {
      if (_suppressPresetApply || _availableParameters.Count == 0 || value == null || !File.Exists(value.Path))
      {
        return;
      }

      ApplyTemplateToActiveCollection(_namingTemplateService.Load(value.Path));
      OnPropertyChanged(nameof(Items));
      RebuildParameterViews();
      DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LoadProjectParameters(
      IEnumerable<NamingTemplateItem> availableParameters,
      IEnumerable<NamingTemplateItem>? savedPdfTemplate = null,
      IEnumerable<NamingTemplateItem>? savedDwgTemplate = null)
    {
      _availableParameters.Clear();
      _availableParameters.AddRange(availableParameters);

      ReplaceCollection(PdfItems, _availableParameters.Select(Clone));
      ReplaceCollection(DwgItems, _availableParameters.Select(Clone));

      if (savedPdfTemplate?.Any() == true)
      {
        ReplaceCollection(PdfItems, _namingTemplateService.MergeTemplate(_availableParameters, savedPdfTemplate));
        NormalizeCheckedIndexes(PdfItems);
      }

      if (savedDwgTemplate?.Any() == true)
      {
        ReplaceCollection(DwgItems, _namingTemplateService.MergeTemplate(_availableParameters, savedDwgTemplate));
        NormalizeCheckedIndexes(DwgItems);
      }

      CommitAppliedFromDraft(raiseChanged: false);
      OnPropertyChanged(nameof(Items));
      RebuildParameterViews();
    }

    public IReadOnlyList<NamingTemplateItem> GetPdfTemplate()
    {
      return _appliedPdfItems.Select(PersistableClone).ToList();
    }

    public IReadOnlyList<NamingTemplateItem> GetDwgTemplate()
    {
      return _appliedDwgItems.Select(PersistableClone).ToList();
    }

    public IReadOnlyList<string> GetImportPresetPaths()
    {
      return ImportPresets.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string SelectedImportPresetPath => SelectedImportPreset?.Path ?? string.Empty;

    public void AddImportPresetPaths(IEnumerable<string> paths, string selectedPath)
    {
      _suppressPresetApply = true;
      foreach (string path in paths)
      {
        AddPreset(path, select: false);
      }

      NamingPreset? selected = ImportPresets.FirstOrDefault(x => string.Equals(x.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
      if (selected != null)
      {
        SelectedImportPreset = selected;
      }
      _suppressPresetApply = false;
    }

    [RelayCommand]
    private void Open()
    {
      IsOpen = true;
    }

    public void OpenIfAllowed(bool canOpen)
    {
      if (canOpen)
      {
        ResetDraftFromApplied();
        IsOpen = true;
      }
    }

    [RelayCommand]
    private void Close()
    {
      ResetDraftFromApplied();
      IsOpen = false;
    }

    [RelayCommand]
    private void Apply()
    {
      CommitAppliedFromDraft(raiseChanged: true);
      IsOpen = false;
    }

    [RelayCommand]
    private void Reload()
    {
      string templatePath = SelectedImportPreset?.Path ?? string.Empty;
      if (File.Exists(templatePath))
      {
        ApplyTemplateToActiveCollection(_namingTemplateService.Load(templatePath));
        DraftChanged?.Invoke(this, EventArgs.Empty);
      }
    }

    [RelayCommand]
    private void ShowPdf()
    {
      ActiveFormat = PrintFormat.PDF;
    }

    [RelayCommand]
    private void ShowDwg()
    {
      ActiveFormat = PrintFormat.DWG;
    }

    [RelayCommand]
    private void BrowseImport()
    {
      var dialog = new Microsoft.Win32.OpenFileDialog
      {
        Filter = "JSON (*.json)|*.json|All files (*.*)|*.*"
      };
      if (dialog.ShowDialog() != true)
      {
        return;
      }

      AddPreset(dialog.FileName, select: true);
      ApplyTemplateToActiveCollection(_namingTemplateService.Load(dialog.FileName));
      OnPropertyChanged(nameof(Items));
      RebuildParameterViews();
      DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemoveImport()
    {
      if (SelectedImportPreset == null)
      {
        return;
      }

      NamingPreset preset = SelectedImportPreset;
      _suppressPresetApply = true;
      ImportPresets.Remove(preset);
      SelectedImportPreset = null;
      _suppressPresetApply = false;

    }

    [RelayCommand]
    private void Export()
    {
      var dialog = new Microsoft.Win32.SaveFileDialog
      {
        Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
        FileName = ActiveFormat == PrintFormat.PDF ? "PDFNamingTemplate.json" : "DWGNamingTemplate.json"
      };
      if (dialog.ShowDialog() != true)
      {
        return;
      }

      File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(Items, Formatting.Indented));
      AddPreset(dialog.FileName, select: true);
    }

    [RelayCommand]
    private void ClearSelection()
    {
      foreach (NamingTemplateItem item in Items)
      {
        item.IsChecked = false;
        item.Index = 0;
        item.Prefix = null;
        item.Suffix = null;
        item.Separator = "-";
      }

      RebuildParameterViews();
      DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTemplateToActiveCollections(IEnumerable<NamingTemplateItem> template, bool raiseChanged)
    {
      ReplaceCollection(PdfItems, _namingTemplateService.MergeTemplate(_availableParameters, template));
      ReplaceCollection(DwgItems, _namingTemplateService.MergeTemplate(_availableParameters, template));
      NormalizeCheckedIndexes(PdfItems);
      NormalizeCheckedIndexes(DwgItems);
      OnPropertyChanged(nameof(Items));
      RebuildParameterViews();
      if (raiseChanged)
      {
        DraftChanged?.Invoke(this, EventArgs.Empty);
      }
    }

    private void ApplyTemplateToActiveCollection(IEnumerable<NamingTemplateItem> template)
    {
      ReplaceCollection(Items, _namingTemplateService.MergeTemplate(_availableParameters, template));
      NormalizeCheckedIndexes(Items);
      RebuildParameterViews();
    }

    private void ReplaceCollection(ObservableCollection<NamingTemplateItem> target, IEnumerable<NamingTemplateItem> source)
    {
      foreach (NamingTemplateItem existing in target)
      {
        existing.PropertyChanged -= OnTemplateItemChanged;
      }

      target.Clear();
      foreach (NamingTemplateItem item in source)
      {
        item.PropertyChanged += OnTemplateItemChanged;
        target.Add(item);
      }
    }

    private void OnTemplateItemChanged(object? sender, PropertyChangedEventArgs e)
    {
      if (_normalizing)
      {
        return;
      }

      if (sender is NamingTemplateItem item && e.PropertyName == nameof(NamingTemplateItem.IsChecked))
      {
        _normalizing = true;
        if (item.IsChecked && item.Index <= 0)
        {
          item.Index = Items.Where(x => x.IsChecked).Select(x => x.Index).DefaultIfEmpty(0).Max() + 1;
        }
        else if (!item.IsChecked)
        {
          item.Index = 0;
        }

        NormalizeCheckedIndexes(Items);
        _normalizing = false;
        RebuildParameterViews();
      }
      else if (e.PropertyName == nameof(NamingTemplateItem.Index))
      {
        _normalizing = true;
        if (sender is NamingTemplateItem movedItem)
        {
          MoveCheckedItemToIndex(movedItem);
        }
        else
        {
          NormalizeCheckedIndexes(Items);
        }
        _normalizing = false;
        RebuildParameterViews();
      }

      DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NormalizeCheckedIndexes(IEnumerable<NamingTemplateItem> items)
    {
      int index = 1;
      foreach (NamingTemplateItem item in items
        .Where(x => x.IsChecked)
        .OrderBy(x => x.Index <= 0 ? int.MaxValue : x.Index)
        .ThenBy(x => x.Name))
      {
        item.Index = index++;
      }
    }

    private void MoveCheckedItemToIndex(NamingTemplateItem movedItem)
    {
      if (!movedItem.IsChecked)
      {
        movedItem.Index = 0;
        return;
      }

      List<NamingTemplateItem> ordered = CheckedItems
        .Where(x => x.IsChecked)
        .ToList();

      if (!ordered.Contains(movedItem))
      {
        ordered = Items
          .Where(x => x.IsChecked)
          .OrderBy(x => x.Index <= 0 ? int.MaxValue : x.Index)
          .ThenBy(x => x.Name)
          .ToList();
      }

      int count = ordered.Count;
      if (count == 0)
      {
        return;
      }

      int targetIndex = Math.Max(1, Math.Min(movedItem.Index <= 0 ? 1 : movedItem.Index, count));
      ordered.Remove(movedItem);
      ordered.Insert(targetIndex - 1, movedItem);

      for (int index = 0; index < ordered.Count; index++)
      {
        ordered[index].Index = index + 1;
      }
    }

    private void RebuildParameterViews()
    {
      string keyword = SearchText?.Trim() ?? string.Empty;
      AvailableItems.Clear();
      CheckedItems.Clear();

      foreach (NamingTemplateItem item in Items
        .Where(x => !x.IsChecked)
        .Where(x => string.IsNullOrWhiteSpace(keyword) || (x.Name?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
        .OrderBy(x => x.Name))
      {
        AvailableItems.Add(item);
      }

      foreach (NamingTemplateItem item in Items
        .Where(x => x.IsChecked)
        .OrderBy(x => x.Index <= 0 ? int.MaxValue : x.Index)
        .ThenBy(x => x.Name))
      {
        CheckedItems.Add(item);
      }

      OnPropertyChanged(nameof(ParameterStatus));
    }

    private void CommitAppliedFromDraft(bool raiseChanged)
    {
      _appliedPdfItems = PdfItems.Select(Clone).ToList();
      _appliedDwgItems = DwgItems.Select(Clone).ToList();
      OnPropertyChanged(nameof(HasAppliedRules));
      if (raiseChanged)
      {
        TemplateChanged?.Invoke(this, EventArgs.Empty);
      }
    }

    private void ResetDraftFromApplied()
    {
      if (_appliedPdfItems.Count > 0)
      {
        ReplaceCollection(PdfItems, _appliedPdfItems.Select(Clone));
      }

      if (_appliedDwgItems.Count > 0)
      {
        ReplaceCollection(DwgItems, _appliedDwgItems.Select(Clone));
      }

      OnPropertyChanged(nameof(Items));
      RebuildParameterViews();
      DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshImportPresets()
    {
      ImportPresets.Clear();
    }

    private void AddPreset(string path, bool select)
    {
      if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
      {
        return;
      }

      NamingPreset? existing = ImportPresets.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
      if (existing != null)
      {
        if (select)
        {
          SelectedImportPreset = existing;
        }
        return;
      }

      var preset = new NamingPreset
      {
        Name = Path.GetFileNameWithoutExtension(path),
        Path = path
      };
      ImportPresets.Add(preset);
      if (select)
      {
        SelectedImportPreset = preset;
      }
    }

    private static NamingTemplateItem Clone(NamingTemplateItem item)
    {
      return new NamingTemplateItem
      {
        FontWeight = item.FontWeight,
        Id = item.Id,
        Index = item.Index,
        Index_Old = item.Index_Old,
        IsChecked = item.IsChecked,
        ParameterInfo = item.ParameterInfo,
        Type = item.Type,
        Name = item.Name,
        Prefix = item.Prefix,
        Suffix = item.Suffix,
        Separator = item.Separator
      };
    }

    private static NamingTemplateItem PersistableClone(NamingTemplateItem item)
    {
      return new NamingTemplateItem
      {
        FontWeight = item.FontWeight,
        Id = item.Id,
        Index = item.Index,
        Index_Old = item.Index_Old,
        IsChecked = item.IsChecked,
        ParameterInfo = null,
        Type = item.Type,
        Name = item.Name,
        Prefix = item.Prefix,
        Suffix = item.Suffix,
        Separator = item.Separator
      };
    }
  }
}

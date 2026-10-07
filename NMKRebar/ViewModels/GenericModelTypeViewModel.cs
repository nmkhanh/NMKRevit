using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRebar.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace NMKRebar.ViewModels
{
  public partial class GenericModelTypeViewModel : ObservableObject
  {
    private readonly UIApplication _uiapp;
    private bool _ready;
    private bool _syncingFamily;

    public GenericModelTypeViewModel(UIApplication uiapp)
    {
      _uiapp = uiapp;
      CsvPath = Properties.Settings.Default.GenericModelCsvPath ?? string.Empty;
      SelectedFamilyText = Properties.Settings.Default.LastGenericModelFamily ?? string.Empty;
      SelectedFamily = SelectedFamilyText;
      Separator = Properties.Settings.Default.GenericModelTypeSeparator ?? "_";
      _ready = true;

      _ = LoadFamiliesAsync();
    }

    [ObservableProperty]
    private string _csvPath = string.Empty;

    [ObservableProperty]
    private string? _selectedFamily;

    [ObservableProperty]
    private string _selectedFamilyText = string.Empty;

    [ObservableProperty]
    private string _separator = "_";

    [ObservableProperty]
    private string _status = "Chọn file CSV và chọn Family Generic Model, sau đó bấm RUN.";

    [ObservableProperty]
    private bool _isRunning;

    public ObservableCollection<string> AllFamilies { get; } = new();

    partial void OnCsvPathChanged(string value) => SaveSettings();

    partial void OnSeparatorChanged(string value) => SaveSettings();

    partial void OnSelectedFamilyChanged(string? value)
    {
      if (_syncingFamily)
      {
        return;
      }

      _syncingFamily = true;
      SelectedFamilyText = value ?? string.Empty;
      _syncingFamily = false;
      SaveSettings();
    }

    partial void OnSelectedFamilyTextChanged(string value)
    {
      if (_syncingFamily || !_ready)
      {
        return;
      }

      string? nearest = FindNearestFamilyName(value);
      _syncingFamily = true;
      SelectedFamily = nearest;
      _syncingFamily = false;
      SaveSettings();
    }

    private string? FindNearestFamilyName(string? typed)
    {
      if (AllFamilies.Count == 0)
      {
        return null;
      }

      string text = (typed ?? string.Empty).Trim();
      if (text.Length == 0)
      {
        return AllFamilies.FirstOrDefault();
      }

      string? exact = AllFamilies.FirstOrDefault(name =>
        name.Equals(text, StringComparison.OrdinalIgnoreCase));
      if (exact != null)
      {
        return exact;
      }

      List<string> starts = AllFamilies
        .Where(name => name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        .OrderBy(name => name.Length)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (starts.Count > 0)
      {
        return starts[0];
      }

      List<string> contains = AllFamilies
        .Where(name => name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
        .OrderBy(name => name.Length)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (contains.Count > 0)
      {
        return contains[0];
      }

      return null;
    }

    private string ResolveFamilyName()
    {
      if (!string.IsNullOrWhiteSpace(SelectedFamily)
          && AllFamilies.Any(name => name.Equals(SelectedFamily, StringComparison.OrdinalIgnoreCase)))
      {
        return AllFamilies.First(name =>
          name.Equals(SelectedFamily, StringComparison.OrdinalIgnoreCase));
      }

      string text = (SelectedFamilyText ?? string.Empty).Trim();
      string? nearest = FindNearestFamilyName(text);
      if (!string.IsNullOrWhiteSpace(nearest))
      {
        return nearest!;
      }

      return text;
    }

    [RelayCommand]
    private void BrowseCsv()
    {
      try
      {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
          Title = "Chọn file CSV Generic Model Types",
          Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
          CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(CsvPath) && File.Exists(CsvPath))
        {
          dialog.InitialDirectory = Path.GetDirectoryName(CsvPath);
        }

        if (dialog.ShowDialog() == true)
        {
          CsvPath = dialog.FileName;
          Status = $"Đã chọn file: {Path.GetFileName(CsvPath)}";
        }
      }
      catch (Exception ex)
      {
        Status = $"Lỗi khi chọn file: {ex.Message}";
      }
    }

    [RelayCommand]
    public async Task RefreshFamilies()
    {
      await LoadFamiliesAsync();
    }

    public async Task LoadFamiliesAsync()
    {
      try
      {
        List<string> families = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = uiapp.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No active document.");
          if (doc.IsFamilyDocument)
          {
            throw new InvalidOperationException("Tool chỉ chạy trong Project document.");
          }

          return GenericModelTypeCsvService.GetGenericModelFamilyNames(doc);
        });

        _syncingFamily = true;
        AllFamilies.Clear();
        foreach (string match in families)
        {
          AllFamilies.Add(match);
        }

        string current = SelectedFamily ?? SelectedFamilyText;
        string? matched = null;
        if (!string.IsNullOrWhiteSpace(current))
        {
          matched = AllFamilies.FirstOrDefault(f => f.Equals(current, StringComparison.OrdinalIgnoreCase));
        }

        if (matched != null)
        {
          SelectedFamily = matched;
          SelectedFamilyText = matched;
        }
        else if (AllFamilies.Count > 0)
        {
          SelectedFamily = AllFamilies[0];
          SelectedFamilyText = AllFamilies[0];
        }
        _syncingFamily = false;

        Status = $"Đã tải {families.Count} Family Generic Model trong dự án. Chọn file CSV và bấm RUN.";
      }
      catch (Exception ex)
      {
        Status = $"Lỗi tải danh sách Family: {ex.Message}";
      }
    }

    [RelayCommand]
    private async Task Run()
    {
      if (string.IsNullOrWhiteSpace(CsvPath) || !File.Exists(CsvPath))
      {
        Status = "Vui lòng chọn file CSV hợp lệ.";
        return;
      }

      string family = ResolveFamilyName();
      if (string.IsNullOrWhiteSpace(family))
      {
        Status = "Vui lòng chọn hoặc nhập tên Family Generic Model.";
        return;
      }

      IsRunning = true;
      Status = "Đang xử lý...";
      SaveSettings();

      try
      {
        string path = CsvPath;
        string separator = Separator ?? string.Empty;

        GenericModelTypeResult result = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = uiapp.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No active document.");
          if (doc.IsFamilyDocument)
          {
            throw new InvalidOperationException("Tool chỉ chạy trong Project document.");
          }

          return GenericModelTypeCsvService.Apply(doc, family, path, separator);
        });

        Status = result.ToMessage();
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Exception ex)
      {
        Status = $"Lỗi: {ex.Message}";
      }
      finally
      {
        IsRunning = false;
      }
    }

    private void SaveSettings()
    {
      if (!_ready)
      {
        return;
      }

      var settings = Properties.Settings.Default;
      settings.GenericModelCsvPath = CsvPath ?? string.Empty;
      settings.LastGenericModelFamily = SelectedFamily ?? SelectedFamilyText ?? string.Empty;
      settings.GenericModelTypeSeparator = Separator ?? "_";
      settings.Save();
    }
  }
}

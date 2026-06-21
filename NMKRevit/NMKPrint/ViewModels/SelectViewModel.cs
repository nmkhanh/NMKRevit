using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRevit.NMKPrint.Models;
using NMKRevit.NMKPrint.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace NMKRevit.NMKPrint.ViewModels
{
  public partial class SelectViewModel : ObservableObject
  {
    private readonly List<PrintItem> _allSheets = new();
    private readonly List<PrintItem> _allViews = new();
    private bool _isRebuildingTree;
    private bool _isSettingSources;

    public event EventHandler? SelectionChanged;
    public event EventHandler<SelectionSource?>? SelectionSourceChanged;

    public ObservableCollection<PrintItem> Items { get; } = new();
    public ObservableCollection<PrintTreeNode> TreeItems { get; } = new();
    public ObservableCollection<PrintItem> SelectedItems { get; } = new();
    public ObservableCollection<SelectionSource> SelectionSources { get; } = new();
    public IEnumerable<PrintItem> AllItems => _allSheets.Concat(_allViews);

    [ObservableProperty]
    private bool _showSheets = true;

    public bool ShowViews
    {
      get => !ShowSheets;
      set => ShowSheets = !value;
    }

    [ObservableProperty]
    private SelectionSource? _selectedSource;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _viewSheetSetName = string.Empty;

    partial void OnShowSheetsChanged(bool value)
    {
      OnPropertyChanged(nameof(ShowViews));
      RefreshItems();
    }

    partial void OnSelectedSourceChanged(SelectionSource? value)
    {
      if (_isSettingSources)
      {
        return;
      }

      SelectionSourceChanged?.Invoke(this, value);
    }

    partial void OnSearchTextChanged(string value)
    {
      ApplySearchFilter();
    }

    public void SetData(IEnumerable<PrintItem> sheets, IEnumerable<PrintItem> views)
    {
      foreach (PrintItem item in AllItems)
      {
        item.PropertyChanged -= OnItemPropertyChanged;
      }

      _allSheets.Clear();
      _allSheets.AddRange(sheets);
      _allViews.Clear();
      _allViews.AddRange(views);

      foreach (PrintItem item in AllItems)
      {
        item.PropertyChanged += OnItemPropertyChanged;
      }

      RefreshItems();
    }

    public void SetSelectionSources(IEnumerable<SelectionSource> sources, string? selectedSourceName = null)
    {
      _isSettingSources = true;
      SelectionSources.Clear();
      foreach (SelectionSource source in sources)
      {
        SelectionSources.Add(source);
      }

      SelectedSource = SelectionSources.FirstOrDefault(x => x.Name == selectedSourceName) ?? SelectionSources.FirstOrDefault();
      _isSettingSources = false;
    }

    public void ApplySelectionIds(HashSet<ElementId> ids)
    {
      foreach (PrintItem item in AllItems)
      {
        item.IsSelected = ids.Contains(item.Id);
      }

      RefreshItems();
      RefreshSelection();
    }

    public void ApplySelectionKeys(IEnumerable<string> keys)
    {
      var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
      foreach (PrintItem item in AllItems)
      {
        item.IsSelected = keySet.Contains(GetItemKey(item));
      }

      RefreshItems();
      RefreshSelection();
    }

    public List<string> GetSelectedKeys()
    {
      return AllItems.Where(x => x.IsSelected).Select(GetItemKey).ToList();
    }

    [RelayCommand]
    private void RefreshSelection()
    {
      SelectedItems.Clear();
      foreach (PrintItem item in AllItems.Where(x => x.IsSelected))
      {
        SelectedItems.Add(item);
      }

      SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshItems()
    {
      string keyword = SearchText?.Trim() ?? string.Empty;
      IEnumerable<PrintItem> source = ShowSheets ? _allSheets : _allViews;
      if (!string.IsNullOrWhiteSpace(keyword))
      {
        source = source.Where(x => x.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
      }

      Items.Clear();
      foreach (PrintItem item in source)
      {
        Items.Add(item);
      }

      RebuildTree(source);
      ApplySearchFilter();
      RefreshSelection();
    }

    private void ApplySearchFilter()
    {
      string keyword = SearchText?.Trim() ?? string.Empty;
      foreach (PrintTreeNode node in TreeItems)
      {
        node.ApplyFilter(keyword);
      }

      IEnumerable<PrintItem> source = ShowSheets ? _allSheets : _allViews;
      if (!string.IsNullOrWhiteSpace(keyword))
      {
        source = source.Where(x =>
          x.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
          x.Number.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
      }

      Items.Clear();
      foreach (PrintItem item in source)
      {
        Items.Add(item);
      }
    }

    private void RebuildTree(IEnumerable<PrintItem> source)
    {
      _isRebuildingTree = true;
      TreeItems.Clear();

      var rootLookup = new Dictionary<string, PrintTreeNode>(StringComparer.OrdinalIgnoreCase);
      foreach (PrintItem item in source.OrderBy(x => x.Number + x.Name, NaturalStringComparer.Instance))
      {
        IReadOnlyList<string> path = item.BrowserPath.Count > 0 ? item.BrowserPath : new[] { GetFallbackGroupName(item) };
        Dictionary<string, PrintTreeNode> currentLookup = rootLookup;
        PrintTreeNode? parent = null;

        foreach (string rawPart in path)
        {
          string part = string.IsNullOrWhiteSpace(rawPart) ? "Other" : rawPart.Trim();
          if (!currentLookup.TryGetValue(part, out PrintTreeNode? folder))
          {
            folder = new PrintTreeNode { Name = part };
            currentLookup[part] = folder;
            if (parent == null)
            {
              TreeItems.Add(folder);
            }
            else
            {
              parent.AddChild(folder);
            }
          }

          parent = folder;
          currentLookup = folder.Items
            .Where(x => !x.IsItem)
            .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase);
        }

        var leaf = new PrintTreeNode
        {
          Name = item.DisplayName,
          Item = item,
          IsChecked = item.IsSelected
        };

        if (parent == null)
        {
          TreeItems.Add(leaf);
        }
        else
        {
          parent.AddChild(leaf);
        }
      }

      foreach (PrintTreeNode root in TreeItems)
      {
        root.UpdateFromChildren();
      }

      _isRebuildingTree = false;
    }

    private static string GetFallbackGroupName(PrintItem item)
    {
      if (!item.IsSheet)
      {
        return item.View.ViewType.ToString();
      }

      string number = item.Number?.Trim() ?? string.Empty;
      return string.IsNullOrWhiteSpace(number) ? "Other" : number.Substring(0, 1).ToUpperInvariant();
    }

    private static string GetItemKey(PrintItem item)
    {
      return item.IsSheet ? $"S:{item.Number}" : $"V:{GetElementIdValue(item.Id)}";
    }

    private static string GetElementIdValue(ElementId id)
    {
#if D2024 || D2025 || D2026
      return id.Value.ToString();
#else
      return id.IntegerValue.ToString();
#endif
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
      if (_isRebuildingTree)
      {
        return;
      }

      if (e.PropertyName == nameof(PrintItem.IsSelected))
      {
        RefreshSelection();
      }
    }
  }
}

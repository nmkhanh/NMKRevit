using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;

namespace NMKRevit.NMKPrint.Models
{
  public partial class PrintTreeNode : ObservableObject
  {
    private bool _suppressUpdate;

    public ObservableCollection<PrintTreeNode> Items { get; } = new();
    public PrintTreeNode? Parent { get; private set; }
    public PrintItem? Item { get; init; }
    public bool IsItem => Item != null;
    public bool CanUseThreeState => !IsItem;
    public int TotalCount => IsItem ? 1 : Items.Sum(x => x.TotalCount);
    public int CheckedCount => IsItem ? (IsChecked == true ? 1 : 0) : Items.Sum(x => x.CheckedCount);
    public string DisplayName => IsItem ? Name : $"{Name}  [{CheckedCount}/{TotalCount}]";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isMultiSelected;

    [ObservableProperty]
    private bool? _isChecked;

    partial void OnNameChanged(string value)
    {
      OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIsCheckedChanged(bool? value)
    {
      if (_suppressUpdate)
      {
        return;
      }

      if (IsItem)
      {
        if (value == null)
        {
          SetCheckedSilently(false);
          value = false;
        }

        if (Item != null)
        {
          Item.IsSelected = value == true;
        }
        Parent?.UpdateFromChildren();
        return;
      }

      bool targetValue = value == true;
      foreach (PrintTreeNode child in Items)
      {
        child.SetCheckedFromParent(targetValue);
      }

      UpdateCountsUp();
    }

    public void AddChild(PrintTreeNode child)
    {
      child.Parent = this;
      Items.Add(child);
      OnPropertyChanged(nameof(TotalCount));
      OnPropertyChanged(nameof(CheckedCount));
      OnPropertyChanged(nameof(DisplayName));
    }

    public void UpdateFromChildren()
    {
      if (IsItem)
      {
        SetCheckedSilently(Item?.IsSelected == true);
        Parent?.UpdateFromChildren();
        return;
      }

      SetFolderStateFromChildren();

      UpdateCountsUp();
      Parent?.UpdateFromChildren();
    }

    public void SyncSelectionState()
    {
      if (IsItem)
      {
        SetCheckedSilently(Item?.IsSelected == true);
      }
      else
      {
        foreach (PrintTreeNode child in Items)
        {
          child.SyncSelectionState();
        }

        SetFolderStateFromChildren();
      }

      UpdateCountsUp();
    }

    private void SetCheckedFromParent(bool? value)
    {
      _suppressUpdate = true;
      IsChecked = value;
      _suppressUpdate = false;

      if (IsItem)
      {
        if (Item != null)
        {
          Item.IsSelected = value == true;
        }
      }
      else
      {
        foreach (PrintTreeNode child in Items)
        {
          child.SetCheckedFromParent(value);
        }
      }

      UpdateCountsUp();
    }

    private void SetCheckedSilently(bool? value)
    {
      _suppressUpdate = true;
      IsChecked = value;
      _suppressUpdate = false;
    }

    private void SetFolderStateFromChildren()
    {
      bool? state = null;
      if (Items.Count > 0 && Items.All(x => x.IsChecked == true))
      {
        state = true;
      }
      else if (Items.All(x => x.IsChecked == false))
      {
        state = false;
      }

      SetCheckedSilently(state);
    }

    private void UpdateCountsUp()
    {
      OnPropertyChanged(nameof(CheckedCount));
      OnPropertyChanged(nameof(TotalCount));
      OnPropertyChanged(nameof(DisplayName));
      Parent?.UpdateCountsUp();
    }

    public bool ApplyFilter(string keyword)
    {
      bool hasKeyword = !string.IsNullOrWhiteSpace(keyword);
      bool selfMatch = !hasKeyword ||
        Name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
        (Item?.Number.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0) == true ||
        (Item?.Revision.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0) == true ||
        (Item?.RevisionDate.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0) == true;

      bool childMatch = false;
      foreach (PrintTreeNode child in Items)
      {
        childMatch |= child.ApplyFilter(keyword);
      }

      IsVisible = selfMatch || childMatch;
      if (hasKeyword && childMatch)
      {
        IsExpanded = true;
      }

      return IsVisible;
    }
  }
}

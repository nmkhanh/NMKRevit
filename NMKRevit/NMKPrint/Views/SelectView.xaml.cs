using NMKRevit.NMKPrint.Models;
using NMKRevit.NMKPrint.ViewModels;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NMKRevit.NMKPrint.Views
{
  public partial class SelectView : System.Windows.Controls.UserControl
  {
    private readonly HashSet<PrintTreeNode> _multiSelectedNodes = new();
    private PrintTreeNode? _lastSelectedNode;

    public SelectView()
    {
      InitializeComponent();
    }

    private void TreeNode_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (FindAncestor<System.Windows.Controls.CheckBox>(e.OriginalSource as DependencyObject) != null)
      {
        return;
      }

      if ((sender as FrameworkElement)?.DataContext is not PrintTreeNode node)
      {
        return;
      }

      ModifierKeys modifiers = Keyboard.Modifiers;
      SelectNode(
        node,
        modifiers.HasFlag(ModifierKeys.Control),
        modifiers.HasFlag(ModifierKeys.Shift));

      e.Handled = true;
    }

    private void TreeCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if ((sender as FrameworkElement)?.DataContext is not PrintTreeNode node)
      {
        return;
      }

      bool multi = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
      if (!_multiSelectedNodes.Contains(node))
      {
        SelectNode(node, multi, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
      }

      bool target = node.IsChecked != true;
      foreach (PrintTreeNode selected in _multiSelectedNodes)
      {
        selected.IsChecked = target;
      }

      e.Handled = true;
    }

    private void SelectNode(PrintTreeNode node, bool append, bool range)
    {
      if (range && _lastSelectedNode != null)
      {
        SelectRange(_lastSelectedNode, node, append);
        _lastSelectedNode = node;
        return;
      }

      if (!append)
      {
        ClearMultiSelection();
      }

      if (_multiSelectedNodes.Contains(node))
      {
        if (append)
        {
          _multiSelectedNodes.Remove(node);
          node.IsMultiSelected = false;
        }
        return;
      }

      _multiSelectedNodes.Add(node);
      node.IsMultiSelected = true;
      _lastSelectedNode = node;
    }

    private void SelectRange(PrintTreeNode start, PrintTreeNode end, bool append)
    {
      if (!append)
      {
        ClearMultiSelection();
      }

      List<PrintTreeNode> visibleNodes = GetVisibleNodes().ToList();
      int startIndex = visibleNodes.IndexOf(start);
      int endIndex = visibleNodes.IndexOf(end);
      if (startIndex < 0 || endIndex < 0)
      {
        SelectNode(end, append, range: false);
        return;
      }

      if (startIndex > endIndex)
      {
        (startIndex, endIndex) = (endIndex, startIndex);
      }

      for (int index = startIndex; index <= endIndex; index++)
      {
        PrintTreeNode node = visibleNodes[index];
        _multiSelectedNodes.Add(node);
        node.IsMultiSelected = true;
      }
    }

    private void ClearMultiSelection()
    {
      foreach (PrintTreeNode selected in _multiSelectedNodes)
      {
        selected.IsMultiSelected = false;
      }

      _multiSelectedNodes.Clear();
    }

    private IEnumerable<PrintTreeNode> GetVisibleNodes()
    {
      if (DataContext is not SelectViewModel viewModel)
      {
        yield break;
      }

      foreach (PrintTreeNode node in viewModel.TreeItems)
      {
        foreach (PrintTreeNode visible in FlattenVisible(node))
        {
          yield return visible;
        }
      }
    }

    private static IEnumerable<PrintTreeNode> FlattenVisible(PrintTreeNode node)
    {
      if (!node.IsVisible)
      {
        yield break;
      }

      yield return node;
      if (!node.IsExpanded)
      {
        yield break;
      }

      foreach (PrintTreeNode child in node.Items)
      {
        foreach (PrintTreeNode visible in FlattenVisible(child))
        {
          yield return visible;
        }
      }
    }

    private static T? FindAncestor<T>(DependencyObject? source)
      where T : DependencyObject
    {
      while (source != null)
      {
        if (source is T match)
        {
          return match;
        }

        source = VisualTreeHelper.GetParent(source);
      }

      return null;
    }
  }
}

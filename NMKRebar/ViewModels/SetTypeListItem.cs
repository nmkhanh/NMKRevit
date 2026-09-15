using CommunityToolkit.Mvvm.ComponentModel;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace NMKRebar.ViewModels
{
  public enum SetTypeQtyCheckState
  {
    NotChecked,
    Match,
    Mismatch
  }

  public partial class SetTypeListItem : ObservableObject
  {
    private static readonly MediaBrush DefaultNameBrush = CreateFrozenBrush(0x11, 0x18, 0x27);
    private static readonly MediaBrush MatchNameBrush = CreateFrozenBrush(0x16, 0xA3, 0x4A);
    private static readonly MediaBrush MismatchNameBrush = CreateFrozenBrush(0xDC, 0x26, 0x26);

    public SetTypeListItem(string typeName)
    {
      TypeName = typeName;
    }

    public string TypeName { get; }

    [ObservableProperty]
    private int? _expectedQty;

    [ObservableProperty]
    private int? _actualQty;

    [ObservableProperty]
    private SetTypeQtyCheckState _checkState = SetTypeQtyCheckState.NotChecked;

    public MediaBrush NameBrush => CheckState switch
    {
      SetTypeQtyCheckState.Match => MatchNameBrush,
      SetTypeQtyCheckState.Mismatch => MismatchNameBrush,
      _ => DefaultNameBrush
    };

    public string ExpectedQtyText => ExpectedQty.HasValue ? ExpectedQty.Value.ToString() : "—";

    public string ActualQtyText => ActualQty.HasValue ? ActualQty.Value.ToString() : "—";

    partial void OnExpectedQtyChanged(int? value)
    {
      OnPropertyChanged(nameof(ExpectedQtyText));
      RefreshCheckState();
    }

    partial void OnActualQtyChanged(int? value)
    {
      OnPropertyChanged(nameof(ActualQtyText));
      RefreshCheckState();
    }

    partial void OnCheckStateChanged(SetTypeQtyCheckState value)
    {
      OnPropertyChanged(nameof(NameBrush));
    }

    public void ResetCheck()
    {
      ActualQty = null;
      CheckState = SetTypeQtyCheckState.NotChecked;
    }

    public void ApplyActual(int? actual)
    {
      ActualQty = actual;
      RefreshCheckState();
    }

    private void RefreshCheckState()
    {
      if (!ActualQty.HasValue)
      {
        CheckState = SetTypeQtyCheckState.NotChecked;
        return;
      }

      if (!ExpectedQty.HasValue)
      {
        CheckState = SetTypeQtyCheckState.Mismatch;
        return;
      }

      CheckState = ExpectedQty.Value == ActualQty.Value
        ? SetTypeQtyCheckState.Match
        : SetTypeQtyCheckState.Mismatch;
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
      var brush = new SolidColorBrush(MediaColor.FromRgb(r, g, b));
      brush.Freeze();
      return brush;
    }
  }
}

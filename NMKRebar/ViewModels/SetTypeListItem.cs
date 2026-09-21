using CommunityToolkit.Mvvm.ComponentModel;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace NMKRebar.ViewModels
{
  public enum SetTypeQtyCheckState
  {
    NotChecked,
    BothMatch,
    SiteMatch,
    NoneMatch
  }

  public partial class SetTypeListItem : ObservableObject
  {
    private static readonly MediaBrush DefaultNameBrush = CreateFrozenBrush(0xE5, 0xE7, 0xEB);
    private static readonly MediaBrush BothMatchBrush = CreateFrozenBrush(0x8D, 0xB6, 0x00);
    private static readonly MediaBrush SiteMatchBrush = CreateFrozenBrush(0x25, 0x63, 0xEB);
    private static readonly MediaBrush NoneMatchBrush = CreateFrozenBrush(0xDC, 0x26, 0x26);

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
    private int? _shapeQty;

    [ObservableProperty]
    private SetTypeQtyCheckState _checkState = SetTypeQtyCheckState.NotChecked;

    public MediaBrush NameBrush => CheckState switch
    {
      SetTypeQtyCheckState.BothMatch => BothMatchBrush,
      SetTypeQtyCheckState.SiteMatch => SiteMatchBrush,
      SetTypeQtyCheckState.NoneMatch => NoneMatchBrush,
      _ => DefaultNameBrush
    };

    public string ExpectedQtyText => ExpectedQty.HasValue ? ExpectedQty.Value.ToString() : "—";

    public string ActualQtyText => ActualQty.HasValue ? ActualQty.Value.ToString() : "—";

    public string ShapeQtyText => ShapeQty.HasValue ? ShapeQty.Value.ToString() : "—";

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

    partial void OnShapeQtyChanged(int? value)
    {
      OnPropertyChanged(nameof(ShapeQtyText));
      RefreshCheckState();
    }

    partial void OnCheckStateChanged(SetTypeQtyCheckState value)
    {
      OnPropertyChanged(nameof(NameBrush));
    }

    public void ResetCheck()
    {
      ActualQty = null;
      ShapeQty = null;
      CheckState = SetTypeQtyCheckState.NotChecked;
    }

    public void ApplyActual(int? actual)
    {
      ActualQty = actual;
    }

    public void ApplyShape(int? shape)
    {
      ShapeQty = shape;
    }

    private void RefreshCheckState()
    {
      if (!ActualQty.HasValue || !ShapeQty.HasValue)
      {
        CheckState = SetTypeQtyCheckState.NotChecked;
        return;
      }

      int rebar = ActualQty.Value;
      int site = ShapeQty.Value;
      if (rebar == 0 && site == 0)
      {
        CheckState = SetTypeQtyCheckState.NotChecked;
        return;
      }

      bool rebarOk = ExpectedQty.HasValue && rebar == ExpectedQty.Value;
      bool siteOk = ExpectedQty.HasValue && site == ExpectedQty.Value;
      if (rebarOk)
      {
        CheckState = SetTypeQtyCheckState.BothMatch;
        return;
      }

      if (siteOk)
      {
        CheckState = SetTypeQtyCheckState.SiteMatch;
        return;
      }

      CheckState = SetTypeQtyCheckState.NoneMatch;
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
      var brush = new SolidColorBrush(MediaColor.FromRgb(r, g, b));
      brush.Freeze();
      return brush;
    }
  }
}

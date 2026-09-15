using System.Drawing;
using System.Windows.Forms.Integration;
using Autodesk.AutoCAD.Windows;
using NMKAcad.ViewModels;
using NMKAcad.Views;

namespace NMKAcad.Services
{
  internal static class WblockPalette
  {
    private static readonly Guid PaletteId = new("b7e2c4a1-9d3f-4e18-8c6a-1f2d9e8b4a70");
    private static PaletteSet? _palette;
    private static WblockViewModel? _viewModel;

    public static void Show()
    {
      EnsureCreated();
      if (_palette != null)
      {
        _palette.Visible = true;
      }
    }

    public static void HideForPick()
    {
      if (_palette != null)
      {
        _palette.KeepFocus = false;
      }
    }

    public static void RestoreAfterPick()
    {
      if (_palette != null)
      {
        _palette.Visible = true;
        _palette.KeepFocus = false;
      }
    }

    public static void Dispose()
    {
      _viewModel?.Dispose();
      _viewModel = null;
      if (_palette != null)
      {
        _palette.Visible = false;
        _palette = null;
      }
    }

    private static void EnsureCreated()
    {
      if (_palette != null)
      {
        return;
      }

      if (System.Windows.Application.Current == null)
      {
        new System.Windows.Application
        {
          ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };
      }

      var view = new WblockView();
      _viewModel = new WblockViewModel(view.Dispatcher);
      view.DataContext = _viewModel;

      var host = new ElementHost
      {
        AutoSize = false,
        Dock = System.Windows.Forms.DockStyle.Fill,
        Child = view
      };

      _palette = new PaletteSet("NMK Wblock", "NMKWBLOCK", PaletteId)
      {
        Style = PaletteSetStyles.ShowAutoHideButton
          | PaletteSetStyles.ShowCloseButton
          | PaletteSetStyles.Snappable
          | PaletteSetStyles.ShowPropertiesMenu,
        MinimumSize = new Size(560, 360),
        Size = new Size(580, 440),
        DockEnabled = DockSides.Left | DockSides.Right | DockSides.None,
        KeepFocus = false
      };
      _palette.Add("Wblock", host);
    }
  }
}

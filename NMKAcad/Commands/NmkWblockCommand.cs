using Autodesk.AutoCAD.Runtime;
using NMKAcad.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

namespace NMKAcad.Commands
{
  public sealed class NmkWblockCommand
  {
    [CommandMethod("NMKWBLOCK", CommandFlags.Session)]
    public void ShowWblock()
    {
      try
      {
        WblockPalette.Show();
      }
      catch (Exception ex)
      {
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nNMK Wblock: " + ex.Message + "\n");
      }
    }
  }
}

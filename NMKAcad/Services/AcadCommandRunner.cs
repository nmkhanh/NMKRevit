using Autodesk.AutoCAD.ApplicationServices;
using Exception = System.Exception;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace NMKAcad.Services
{
  internal static class AcadCommandRunner
  {
    public static async Task RunAsync(Action action)
    {
      DocumentCollection docs = AcadApp.DocumentManager;
      if (docs.MdiActiveDocument == null)
      {
        throw new InvalidOperationException("No active drawing.");
      }

      if (docs.IsApplicationContext)
      {
        Exception? captured = null;
        await docs.ExecuteInCommandContextAsync(
          _ =>
          {
            try
            {
              action();
            }
            catch (Exception ex)
            {
              captured = ex;
            }

            return Task.CompletedTask;
          },
          null);

        if (captured != null)
        {
          throw captured;
        }

        return;
      }

      action();
    }
  }
}

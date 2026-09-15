using Autodesk.Revit.UI;
using NMKRebar.Commands;
using Revit.Async;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NMKRebar.Services
{
  internal static class RevitTaskRun
  {
    public static async Task<T> Async<T>(UIApplication uiapp, Func<UIApplication, T> action)
    {
      RevitTask.Initialize(uiapp);
      Wake(uiapp);
      try
      {
        return await RevitTask.RunAsync(action);
      }
      finally
      {
        RestoreToolWindow();
      }
    }

    public static void Wake(UIApplication uiapp)
    {
      IntPtr revit = uiapp.MainWindowHandle;
      if (revit == IntPtr.Zero)
      {
        return;
      }

      EnableWindow(revit, true);
      SetForegroundWindow(revit);
      PostMessage(revit, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void RestoreToolWindow()
    {
      foreach (Window window in ToolWindowHost.Open.ToList())
      {
        window.Dispatcher.BeginInvoke(() =>
        {
          window.IsEnabled = true;
          IntPtr hwnd = new WindowInteropHelper(window).Handle;
          if (hwnd != IntPtr.Zero)
          {
            EnableWindow(hwnd, true);
          }
        });
      }
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
  }
}

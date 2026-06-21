using Autodesk.Revit.UI;
using System;
using System.IO;
using System.Reflection;
#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace NMKRevit
{
  public class NMKRevitApp : IExternalApplication
  {
    private const string TabName = "NMK";
    private const string PanelName = "Print";
    private static string _assemblyFolder = string.Empty;
#if NETCOREAPP
    private static AssemblyLoadContext? _loadContext;
#endif

    public Result OnStartup(UIControlledApplication application)
    {
      try
      {
        _assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAddinFolder;
#if NETCOREAPP
        _loadContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly()) ?? AssemblyLoadContext.Default;
        _loadContext.Resolving += ResolveFromAddinFolder;
#endif

        try
        {
          application.CreateRibbonTab(TabName);
        }
        catch
        {
          // The tab may already exist when other NMK tools are loaded.
        }

        RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var buttonData = new PushButtonData(
          "NMKPrint",
          "Print",
          assemblyPath,
          "NMKRevit.NMKPrint.Commands.PrintCommand")
        {
          ToolTip = "Print sheets/views to PDF24 PDF and export DWG."
        };

        panel.AddItem(buttonData);
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine(ex);
        return Result.Failed;
      }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
      AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromAddinFolder;
#if NETCOREAPP
      if (_loadContext != null)
      {
        _loadContext.Resolving -= ResolveFromAddinFolder;
        _loadContext = null;
      }
#endif
      return Result.Succeeded;
    }

    private static Assembly? ResolveFromAddinFolder(object? sender, ResolveEventArgs args)
    {
      return ResolveFromAddinFolder(new AssemblyName(args.Name));
    }

#if NETCOREAPP
    private static Assembly? ResolveFromAddinFolder(AssemblyLoadContext context, AssemblyName assemblyName)
    {
      string? assemblyPath = GetAssemblyPath(assemblyName);
      return assemblyPath == null ? null : context.LoadFromAssemblyPath(assemblyPath);
    }
#endif

    private static Assembly? ResolveFromAddinFolder(AssemblyName assemblyName)
    {
      string? assemblyPath = GetAssemblyPath(assemblyName);
      return assemblyPath == null ? null : Assembly.LoadFrom(assemblyPath);
    }

    private static string? GetAssemblyPath(AssemblyName assemblyName)
    {
      if (string.IsNullOrWhiteSpace(_assemblyFolder))
      {
        return null;
      }

      string assemblyFileName = assemblyName.Name + ".dll";
      string assemblyPath = Path.Combine(_assemblyFolder, assemblyFileName);
      return File.Exists(assemblyPath) ? assemblyPath : null;
    }
  }
}

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
    private const string PrintPanelName = "Print";
    private const string FloorPanelName = "Floor";
    private const string FilledRegionsTabName = "Filled Regions";
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

        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        RibbonPanel panel = application.CreateRibbonPanel(TabName, PrintPanelName);
        var buttonData = new PushButtonData(
          "NMKPrint",
          "Print",
          assemblyPath,
          "NMKRevit.NMKPrint.Commands.PrintCommand")
        {
          ToolTip = "Print sheets/views to PDF24 PDF and export DWG."
        };

        panel.AddItem(buttonData);
        RegisterModelingTools(application, assemblyPath);
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine(ex);
        return Result.Failed;
      }
    }

    private static void RegisterModelingTools(UIControlledApplication application, string assemblyPath)
    {
      if (!int.TryParse(application.ControlledApplication.VersionNumber, out int version) || version < 2022)
      {
        return;
      }

      RibbonPanel floorPanel = application.CreateRibbonPanel(TabName, FloorPanelName);
      floorPanel.AddItem(new PushButtonData(
        "NMKFloorTool",
        "Floor\nTool",
        assemblyPath,
        "NMKRevit.FloorTool.Commands.FloorToolCommand")
      {
        ToolTip = "Analyze, split, and join Floors."
      });

      try
      {
        application.CreateRibbonTab(FilledRegionsTabName);
      }
      catch
      {
        // The tab may already exist.
      }

      RibbonPanel filledRegionPanel = application.CreateRibbonPanel(FilledRegionsTabName, "Tools");
      filledRegionPanel.AddItem(new PushButtonData(
        "NMKSplitFilledRegionLoops",
        "Split\nLoops",
        assemblyPath,
        "NMKRevit.FilledRegions.Commands.SplitFilledRegionLoopsCommand")
      {
        ToolTip = "Split a FilledRegion with multiple islands into separate FilledRegions."
      });
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

using Autodesk.Revit.UI;
using System.IO;
using System.Reflection;
#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace NMKRebar
{
  public class NMKRebarApp : IExternalApplication
  {
    private const string TabName = "NMKRebar";
    private const string PanelName = "Family";
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
          // Tab may already exist.
        }

        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        RibbonPanel familyPanel = application.CreateRibbonPanel(TabName, PanelName);
        familyPanel.AddItem(new PushButtonData(
          "NMKFamilySupport",
          "Family\nSupport",
          assemblyPath,
          "NMKRebar.Commands.NMKFamilySupportCommand")
        {
          ToolTip = "Family document: Add Para By CSV and Copy And Add Para."
        });

        RibbonPanel projectPanel = application.CreateRibbonPanel(TabName, "Project");
        projectPanel.AddItem(new PushButtonData(
          "NMKCreateRebarType",
          "Create\nRebar Type",
          assemblyPath,
          "NMKRebar.Commands.NMKCreateRebarTypeCommand")
        {
          ToolTip = "Project document: create RebarBarType and NMK_Rebar_Array types from Rebar.txt."
        });
        projectPanel.AddItem(new PushButtonData(
          "NMKSetType",
          "Set Type",
          assemblyPath,
          "NMKRebar.Commands.NMKSetTypeCommand")
        {
          ToolTip = "Project document: edit type dimensions, set Z, create rebar by line."
        });
        projectPanel.AddItem(new PushButtonData(
          "NMKCheckPath",
          "Check\nPath",
          assemblyPath,
          "NMKRebar.Commands.NMKCheckPathCommand")
        {
          ToolTip = "Project document: create model lines from NMK_Rebar_Array paths to check order and gaps."
        });
        projectPanel.AddItem(new PushButtonData(
          "NMKProject",
          "Project",
          assemblyPath,
          "NMKRebar.Commands.NMKProjectCommand")
        {
          ToolTip = "Project document: Set Rebar Varies, Place Coupler."
        });

        Revit.Async.RevitTask.Initialize(application);

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

      string assemblyPath = Path.Combine(_assemblyFolder, assemblyName.Name + ".dll");
      return File.Exists(assemblyPath) ? assemblyPath : null;
    }
  }
}

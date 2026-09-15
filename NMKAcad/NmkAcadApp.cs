using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;

namespace NMKAcad
{
  public sealed class NmkAcadApp : IExtensionApplication
  {
    private static string _assemblyFolder = string.Empty;

    public void Initialize()
    {
      _assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
      AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAddinFolder;
    }

    public void Terminate()
    {
      AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromAddinFolder;
      Services.WblockPalette.Dispose();
    }

    private static Assembly? ResolveFromAddinFolder(object sender, ResolveEventArgs args)
    {
      if (string.IsNullOrWhiteSpace(_assemblyFolder))
      {
        return null;
      }

      string name = new AssemblyName(args.Name).Name ?? string.Empty;
      if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
      {
        return null;
      }

      string path = Path.Combine(_assemblyFolder, name + ".dll");
      return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
  }
}

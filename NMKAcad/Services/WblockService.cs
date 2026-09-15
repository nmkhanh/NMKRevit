using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace NMKAcad.Services
{
  internal static class WblockService
  {
    public static string ExportSelection(string folder, string fileName)
    {
      Document doc = AcadApp.DocumentManager.MdiActiveDocument
        ?? throw new InvalidOperationException("No active drawing.");
      Editor editor = doc.Editor;
      Database source = doc.Database;

      PromptSelectionResult selection = editor.GetSelection();
      if (selection.Status != PromptStatus.OK)
      {
        throw new OperationCanceledException("Selection cancelled.");
      }

      ObjectId[] ids = selection.Value.GetObjectIds();
      if (ids.Length == 0)
      {
        throw new InvalidOperationException("No objects selected.");
      }

      string safeName = SanitizeFileName(fileName);
      if (string.IsNullOrWhiteSpace(safeName))
      {
        throw new InvalidOperationException("File name is empty.");
      }

      Directory.CreateDirectory(folder);
      string path = Path.Combine(folder, safeName + ".dwg");
      if (File.Exists(path))
      {
        File.Delete(path);
      }

      using (doc.LockDocument())
      {
        var idSet = new ObjectIdCollection(ids);
        using (Database dest = source.Wblock(idSet, Point3d.Origin))
        {
          dest.SaveAs(path, DwgVersion.Current);
        }
      }

      return path;
    }

    public static string CurrentDrawingName()
    {
      Document? doc = AcadApp.DocumentManager.MdiActiveDocument;
      if (doc == null)
      {
        return "(no drawing)";
      }

      return string.IsNullOrWhiteSpace(doc.Name) ? "(unnamed)" : Path.GetFileName(doc.Name);
    }

    public static string SanitizeFileName(string value)
    {
      char[] invalid = Path.GetInvalidFileNameChars();
      var chars = value.Trim().ToCharArray();
      for (int i = 0; i < chars.Length; i++)
      {
        if (Array.IndexOf(invalid, chars[i]) >= 0)
        {
          chars[i] = '_';
        }
      }

      return new string(chars).Trim();
    }
  }
}

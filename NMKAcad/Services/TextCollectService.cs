using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace NMKAcad.Services
{
  internal static class TextCollectService
  {
    public static string CollectFromSelection()
    {
      var doc = AcadApp.DocumentManager.MdiActiveDocument
        ?? throw new InvalidOperationException("No active drawing.");
      Editor editor = doc.Editor;

      var filter = new SelectionFilter(new[]
      {
        new TypedValue((int)DxfCode.Start, "TEXT,MTEXT")
      });
      PromptSelectionResult selection = editor.GetSelection(filter);
      if (selection.Status != PromptStatus.OK)
      {
        throw new OperationCanceledException("Selection cancelled.");
      }

      ObjectId[] ids = selection.Value.GetObjectIds();
      var rows = new List<(double Y, double X, string Value)>();

      using (doc.LockDocument())
      using (Transaction tr = doc.TransactionManager.StartTransaction())
      {
        foreach (ObjectId id in ids)
        {
          if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
          {
            continue;
          }

          string value = GetPlainText(entity);
          if (string.IsNullOrWhiteSpace(value))
          {
            continue;
          }

          Point3d sortPoint = GetSortPoint(entity);
          rows.Add((sortPoint.Y, sortPoint.X, FlattenForExcel(value)));
        }

        tr.Commit();
      }

      if (rows.Count == 0)
      {
        throw new InvalidOperationException("No TEXT or MTEXT in the selection.");
      }

      rows.Sort((a, b) =>
      {
        int byY = b.Y.CompareTo(a.Y);
        return byY != 0 ? byY : a.X.CompareTo(b.X);
      });

      var text = new StringBuilder();
      for (int i = 0; i < rows.Count; i++)
      {
        if (i > 0)
        {
          text.Append("\r\n");
        }

        text.Append(rows[i].Value);
      }

      return text.ToString();
    }

    private static string GetPlainText(Entity entity)
    {
      if (entity is DBText dbText)
      {
        return dbText.TextString ?? string.Empty;
      }

      if (entity is MText mText)
      {
        return string.IsNullOrWhiteSpace(mText.Text) ? (mText.Contents ?? string.Empty) : mText.Text;
      }

      return string.Empty;
    }

    private static Point3d GetSortPoint(Entity entity)
    {
      try
      {
        Extents3d extents = entity.GeometricExtents;
        return new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0);
      }
      catch
      {
        if (entity is DBText dbText)
        {
          return dbText.Position;
        }

        if (entity is MText mText)
        {
          return mText.Location;
        }

        return Point3d.Origin;
      }
    }

    private static string FlattenForExcel(string value)
    {
      return value
        .Replace("\r\n", " ")
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();
    }
  }
}

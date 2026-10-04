using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Globalization;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;
using View = Autodesk.Revit.DB.View;

namespace NMKRebar.Services
{
  public static class RotateSoleService
  {
    private const int CenterDecimals = 5;
    private const double DegreeEps = 1e-9;

    public static string RotatePicked(UIDocument uidoc, string angleText)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Rotate Sole runs in a project document.");
      }

      if (!TryParseDegrees(angleText, out double stepDegrees))
      {
        throw new InvalidOperationException("Rotate Sole: enter a numeric angle in degrees.");
      }

      if (stepDegrees <= 0 || stepDegrees > 360)
      {
        throw new InvalidOperationException("Rotate Sole: angle must be greater than 0 and at most 360.");
      }

      IList<Reference> picks = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new RebarElementSelectionFilter(),
        "Select arc rebars to rotate by center");
      List<ElementId> ids = picks
        .Select(pick => pick.ElementId)
        .Distinct()
        .ToList();
      if (ids.Count == 0)
      {
        throw new InvalidOperationException("No rebar was selected.");
      }

      View? view = doc.ActiveView;
      XYZ up = view?.UpDirection ?? XYZ.BasisY;
      XYZ right = view?.RightDirection ?? XYZ.BasisX;
      var byCenter = new Dictionary<string, List<(RevitRebar Rebar, Arc Arc, XYZ Mid)>>(StringComparer.Ordinal);
      int skipped = 0;
      foreach (ElementId id in ids.GroupBy(CreateRebarByLineService.IdValue).Select(group => group.First()))
      {
        if (doc.GetElement(id) is not RevitRebar rebar || !TryGetArc(rebar, out Arc arc))
        {
          skipped++;
          continue;
        }

        string key = CenterKey(arc.Center);
        if (!byCenter.TryGetValue(key, out List<(RevitRebar Rebar, Arc Arc, XYZ Mid)>? group))
        {
          group = new List<(RevitRebar, Arc, XYZ)>();
          byCenter[key] = group;
        }

        group.Add((rebar, arc, arc.Evaluate(0.5, true)));
      }

      if (byCenter.Count == 0)
      {
        throw new InvalidOperationException("No selected rebar has an arc centerline.");
      }

      int rotated = 0;
      int unchanged = 0;
      var warnings = new List<string>();
      using (var tx = new Transaction(doc, "NMK Rotate Sole"))
      {
        tx.Start();
        foreach (List<(RevitRebar Rebar, Arc Arc, XYZ Mid)> group in byCenter.Values)
        {
          List<(RevitRebar Rebar, Arc Arc, XYZ Mid)> ordered = group
            .OrderByDescending(item => item.Mid.DotProduct(up))
            .ThenBy(item => item.Mid.DotProduct(right))
            .ToList();
          XYZ origin = ordered[0].Arc.Center;
          XYZ axisDir = ordered[0].Arc.Normal;
          if (axisDir.IsZeroLength())
          {
            warnings.Add("An arc has a zero normal; skipped that center group.");
            continue;
          }

          Line axis = Line.CreateBound(origin, origin + axisDir.Normalize());
          for (int i = 0; i < ordered.Count; i++)
          {
            double degrees = NormalizeDegrees(i * stepDegrees);
            if (degrees < DegreeEps)
            {
              unchanged++;
              continue;
            }

            RevitRebar rebar = ordered[i].Rebar;
            bool pinned = rebar.Pinned;
            try
            {
              if (pinned)
              {
                rebar.Pinned = false;
              }

              ElementTransformUtils.RotateElement(doc, rebar.Id, axis, degrees * Math.PI / 180.0);
              rotated++;
            }
            catch (Exception ex)
            {
              warnings.Add($"Rebar {CreateRebarByLineService.IdValue(rebar.Id)}: {ex.Message}");
            }
            finally
            {
              if (pinned)
              {
                try
                {
                  rebar.Pinned = true;
                }
                catch
                {
                }
              }
            }
          }
        }

        tx.Commit();
      }

      string text = $"Rotate Sole {stepDegrees.ToString("0.###", CultureInfo.InvariantCulture)}°: {rotated} rotated, {unchanged} kept, {byCenter.Count} center group(s).";
      if (skipped > 0)
      {
        text += $" Skipped {skipped} without arc.";
      }

      if (warnings.Count > 0)
      {
        text += " " + warnings[0];
      }

      return text;
    }

    private static bool TryGetArc(RevitRebar rebar, out Arc arc)
    {
      IList<Curve> curves = rebar.GetCenterlineCurves(
        false,
        false,
        false,
        MultiplanarOption.IncludeOnlyPlanarCurves,
        0);
      Arc? longest = null;
      double longestLength = -1;
      if (curves != null)
      {
        foreach (Curve curve in curves)
        {
          if (curve is not Arc found)
          {
            continue;
          }

          double length = found.Length;
          if (longest == null || length > longestLength)
          {
            longest = found;
            longestLength = length;
          }
        }
      }

      arc = longest!;
      return longest != null;
    }

    private static string CenterKey(XYZ center)
    {
      return string.Join(
        ",",
        RoundCoord(center.X).ToString("0.00000", CultureInfo.InvariantCulture),
        RoundCoord(center.Y).ToString("0.00000", CultureInfo.InvariantCulture));
    }

    private static double RoundCoord(double value)
    {
      return Math.Round(value, CenterDecimals, MidpointRounding.AwayFromZero);
    }

    private static double NormalizeDegrees(double degrees)
    {
      double wrapped = degrees % 360.0;
      if (wrapped < 0)
      {
        wrapped += 360.0;
      }

      if (Math.Abs(wrapped - 360.0) < DegreeEps || wrapped < DegreeEps)
      {
        return 0;
      }

      return wrapped;
    }

    private static bool TryParseDegrees(string? text, out double degrees)
    {
      string raw = (text ?? string.Empty).Trim();
      if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees))
      {
        return true;
      }

      return double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out degrees);
    }
  }
}

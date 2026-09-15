using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Globalization;
using System.Text;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace NMKRebar.Services
{
  public sealed class NmkRebarBarTypeSelectionFilter : ISelectionFilter
  {
    private readonly Document _doc;
    private readonly string _typeName;

    public NmkRebarBarTypeSelectionFilter(Document doc, string typeName)
    {
      _doc = doc;
      _typeName = typeName;
    }

    public bool AllowElement(Element elem)
    {
      if (elem is not RevitRebar rebar)
      {
        return false;
      }

      if (_doc.GetElement(rebar.GetTypeId()) is not RebarBarType barType)
      {
        return false;
      }

      return barType.Name.Equals(_typeName, StringComparison.OrdinalIgnoreCase);
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }

  public sealed class PointOnAnyElementFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem) => elem != null;

    public bool AllowReference(Reference reference, XYZ position) => true;
  }

  public sealed class SetSupportRebarVariesResult
  {
    public int RebarCount { get; set; }

    public int ValuesSet { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Rebars: {RebarCount}");
      text.AppendLine($"Values set: {ValuesSet}");
      if (Warnings.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("Warnings:");
        foreach (string warning in Warnings.Take(20))
        {
          text.AppendLine("- " + warning);
        }
      }

      return text.ToString().TrimEnd();
    }
  }

  public static class SetSupportRebarVariesService
  {
    public static SetSupportRebarVariesResult SetVarries(
      UIDocument uidoc,
      string folder,
      string typeName,
      string parameterName)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Set Varries runs in a project document.");
      }

      if (string.IsNullOrWhiteSpace(typeName))
      {
        throw new InvalidOperationException("Select a type in the list first.");
      }

      string para = VariesLengthParameters.Normalize(parameterName);
      IReadOnlyList<double> values = SetRebarVariesService.LoadSortedValues(folder, typeName);
      if (values.Count == 0)
      {
        throw new InvalidOperationException($"No Varries.csv values for type '{typeName}'.");
      }

      IList<Reference> picked = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new NmkRebarBarTypeSelectionFilter(doc, typeName),
        $"Select rebars of type {typeName}");
      List<RevitRebar> rebars = picked
        .Select(item => doc.GetElement(item))
        .OfType<RevitRebar>()
        .GroupBy(rebar => CreateRebarByLineService.IdValue(rebar.Id))
        .Select(group => group.First())
        .ToList();
      if (rebars.Count == 0)
      {
        throw new InvalidOperationException($"No rebar of type '{typeName}' was selected.");
      }

      XYZ start = PickPointOnElement(uidoc, "Pick first point on an element (sort start)");
      XYZ end = PickPointOnElement(uidoc, "Pick second point on an element (sort direction)");
      XYZ direction = end - start;
      if (direction.GetLength() < 1.0 / 304.8)
      {
        throw new InvalidOperationException("The two points are too close to define a direction.");
      }

      direction = direction.Normalize();
      List<RevitRebar> ordered = rebars
        .Select(rebar => (Rebar: rebar, Distance: (GetLocation(rebar) - start).DotProduct(direction)))
        .OrderBy(item => item.Distance)
        .Select(item => item.Rebar)
        .ToList();

      var result = new SetSupportRebarVariesResult { RebarCount = ordered.Count };
      int count = Math.Min(ordered.Count, values.Count);
      if (ordered.Count > values.Count)
      {
        result.Warnings.Add($"{ordered.Count - values.Count} rebar(s) have no Varries value.");
      }
      else if (values.Count > ordered.Count)
      {
        result.Warnings.Add($"{values.Count - ordered.Count} Varries value(s) unused.");
      }

      using (var tx = new Transaction(doc, "NMK Set Varries"))
      {
        tx.Start();
        for (int i = 0; i < count; i++)
        {
          RevitRebar rebar = ordered[i];
          Parameter? parameter = rebar.LookupParameter(para);
          if (parameter == null)
          {
            result.Warnings.Add($"{rebar.Id}: no parameter '{para}'.");
            continue;
          }

          if (CsvValueConverter.TrySetParameter(
            parameter,
            values[i].ToString(CultureInfo.InvariantCulture),
            result.Warnings))
          {
            result.ValuesSet++;
          }
        }

        tx.Commit();
      }

      return result;
    }

    private static XYZ PickPointOnElement(UIDocument uidoc, string prompt)
    {
      Reference picked = uidoc.Selection.PickObject(
        ObjectType.PointOnElement,
        new PointOnAnyElementFilter(),
        prompt);
      XYZ point = picked.GlobalPoint;
      return point ?? throw new InvalidOperationException("Could not read the picked point.");
    }

    private static XYZ GetLocation(RevitRebar rebar)
    {
      IList<Curve> curves = rebar.GetCenterlineCurves(
        false,
        false,
        false,
        MultiplanarOption.IncludeOnlyPlanarCurves,
        0);
      if (curves != null && curves.Count > 0)
      {
        XYZ sum = XYZ.Zero;
        int count = 0;
        foreach (Curve curve in curves)
        {
          sum += curve.Evaluate(0.5, true);
          count++;
        }

        if (count > 0)
        {
          return sum / count;
        }
      }

      Location? location = rebar.Location;
      if (location is LocationPoint point)
      {
        return point.Point;
      }

      if (location is LocationCurve line && line.Curve != null)
      {
        return line.Curve.Evaluate(0.5, true);
      }

      BoundingBoxXYZ? box = rebar.get_BoundingBox(null);
      if (box != null)
      {
        return (box.Min + box.Max) * 0.5;
      }

      return XYZ.Zero;
    }
  }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMaterial = Autodesk.Revit.DB.Material;

namespace NMKRebar.Services
{
  public sealed class GenericModelSelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return DirectShapeNonIntersectService.IsGenericModel(elem);
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }

  public static class DirectShapeNonIntersectService
  {
    private const double MinVolume = 1e-9;

    public static bool IsGenericModel(Element elem)
    {
      return elem != null
        && elem.Category != null
        && CreateRebarByLineService.IdValue(elem.Category.Id) == (long)BuiltInCategory.OST_GenericModel;
    }

    public static string CreateFromPicked(UIDocument uidoc)
    {
      return CreateFromPicked(uidoc, convertMesh: false);
    }

    public static string CreateFromPickedMesh(UIDocument uidoc)
    {
      return CreateFromPicked(uidoc, convertMesh: true);
    }

    private static string CreateFromPicked(UIDocument uidoc, bool convertMesh)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Non-Intersect DirectShape runs in a project document.");
      }

      List<Element> targets = PickGenericModels(uidoc, "Select Generic Model(s) to cut");
      if (targets.Count == 0)
      {
        throw new InvalidOperationException("Select Generic Model(s) to cut.");
      }

      List<Element> cutters = PickGenericModels(uidoc, "Select Generic Model(s) used to cut");
      if (cutters.Count == 0)
      {
        throw new InvalidOperationException("Select Generic Model(s) used to cut.");
      }

      var warnings = new List<string>();
      Solid? targetSolid = UnionElements(targets, convertMesh, warnings);
      Solid? cutterSolid = UnionElements(cutters, convertMesh, warnings);
      if (targetSolid == null)
      {
        throw new InvalidOperationException(convertMesh
          ? "No solid after mesh convert on the Generic Model(s) to cut."
          : "No solid on the Generic Model(s) to cut.");
      }

      if (cutterSolid == null)
      {
        throw new InvalidOperationException(convertMesh
          ? "No solid after mesh convert on the Generic Model(s) used to cut."
          : "No solid on the Generic Model(s) used to cut.");
      }

      Solid? cut = TryBoolean(targetSolid, cutterSolid, BooleanOperationsType.Difference);
      if (cut == null)
      {
        throw new InvalidOperationException("Cut failed or nothing remained.");
      }

      ElementId createdId;
      using (var tx = new Transaction(doc, "NMK DirectShape Non-Intersect"))
      {
        tx.Start();
        DirectShapeFromInPlaceService.UnhideGenericModel(doc);
        RevitMaterial? concrete = DirectShapeFromInPlaceService.GetOrCreateConcreteMaterial(doc);
        ElementId categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
        if (!DirectShape.IsValidCategoryId(categoryId, doc))
        {
          throw new InvalidOperationException("Generic Model is not a valid DirectShape category.");
        }

        DirectShape ds = DirectShape.CreateElement(doc, categoryId);
        ds.ApplicationId = "NMKRebar";
        ds.ApplicationDataId = "non-intersect";
        ds.SetName("DS_NonIntersect");
        ds.SetShape(new List<GeometryObject> { cut });
        DirectShapeFromInPlaceService.AssignConcreteMaterial(ds, concrete);
        createdId = ds.Id;
        tx.Commit();
      }

      uidoc.Selection.SetElementIds(new[] { createdId });
      Element created = doc.GetElement(createdId);
      bool hostOk = created != null && RebarHostData.GetRebarHostData(created) != null;
      string text = convertMesh
        ? $"Mesh cut {targets.Count} by {cutters.Count}. Rebar host: {(hostOk ? "yes" : "no")}."
        : $"Cut {targets.Count} by {cutters.Count}. Rebar host: {(hostOk ? "yes" : "no")}.";
      if (warnings.Count > 0)
      {
        text += " " + warnings[0];
      }

      return text;
    }

    private static List<Element> PickGenericModels(UIDocument uidoc, string prompt)
    {
      Document doc = uidoc.Document;
      IList<Reference> picks = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new GenericModelSelectionFilter(),
        prompt);
      return picks
        .Select(pick => doc.GetElement(pick.ElementId))
        .Where(IsGenericModel)
        .GroupBy(element => CreateRebarByLineService.IdValue(element.Id))
        .Select(group => group.First())
        .ToList();
    }

    private static Solid? UnionElements(IReadOnlyList<Element> elements, bool convertMesh, List<string> warnings)
    {
      var solids = new List<Solid>();
      foreach (Element element in elements)
      {
        Solid? merged = UnionSolids(ExtractSolids(element, convertMesh, warnings), warnings, element.Name);
        if (merged == null)
        {
          warnings.Add($"{element.Name}: no solid.");
          continue;
        }

        solids.Add(merged);
      }

      return UnionSolids(solids, warnings, "group");
    }

    private static List<Solid> ExtractSolids(Element element, bool convertMesh, List<string> warnings)
    {
      var solids = new List<Solid>();
      var meshes = new List<Mesh>();
      var options = new Options
      {
        ComputeReferences = false,
        IncludeNonVisibleObjects = true,
        DetailLevel = ViewDetailLevel.Fine
      };
      GeometryElement? geometry = element.get_Geometry(options);
      if (geometry == null)
      {
        return solids;
      }

      CollectGeometry(geometry, solids, meshes);
      if (convertMesh)
      {
        foreach (Mesh mesh in meshes)
        {
          Solid? fromMesh = TryMeshToSolid(mesh, warnings, element.Name);
          if (fromMesh != null)
          {
            solids.Add(fromMesh);
          }
        }
      }
      else if (solids.Count == 0 && meshes.Count > 0)
      {
        warnings.Add($"{element.Name}: mesh only, use Mesh Cut.");
      }

      return solids;
    }

    private static void CollectGeometry(GeometryElement geometry, List<Solid> solids, List<Mesh> meshes)
    {
      foreach (GeometryObject geo in geometry)
      {
        if (geo is Solid solid && solid.Faces.Size > 0 && solid.Volume > MinVolume)
        {
          solids.Add(solid);
        }
        else if (geo is Mesh mesh && mesh.NumTriangles > 0)
        {
          meshes.Add(mesh);
        }
        else if (geo is GeometryInstance instance)
        {
          GeometryElement? nested = instance.GetInstanceGeometry();
          if (nested != null)
          {
            CollectGeometry(nested, solids, meshes);
          }
        }
      }
    }

    private static Solid? TryMeshToSolid(Mesh mesh, List<string> warnings, string name)
    {
      try
      {
        var builder = new TessellatedShapeBuilder();
        builder.Target = TessellatedShapeBuilderTarget.Solid;
        builder.Fallback = TessellatedShapeBuilderFallback.Abort;
        builder.OpenConnectedFaceSet(true);
        int added = 0;
        for (int i = 0; i < mesh.NumTriangles; i++)
        {
          MeshTriangle triangle = mesh.get_Triangle(i);
          XYZ a = triangle.get_Vertex(0);
          XYZ b = triangle.get_Vertex(1);
          XYZ c = triangle.get_Vertex(2);
          if (a.DistanceTo(b) < 1e-9 || b.DistanceTo(c) < 1e-9 || c.DistanceTo(a) < 1e-9)
          {
            continue;
          }

          builder.AddFace(new TessellatedFace(
            new List<XYZ> { a, b, c },
            ElementId.InvalidElementId));
          added++;
        }

        if (added == 0)
        {
          warnings.Add($"{name}: mesh has no valid triangles.");
          return null;
        }

        builder.CloseConnectedFaceSet();
        builder.Build();
        TessellatedShapeBuilderResult result = builder.GetBuildResult();
        foreach (GeometryObject geo in result.GetGeometricalObjects())
        {
          if (geo is Solid solid && solid.Faces.Size > 0 && solid.Volume > MinVolume)
          {
            return solid;
          }
        }

        warnings.Add($"{name}: mesh did not become a solid (not watertight).");
        return null;
      }
      catch (Exception ex)
      {
        warnings.Add($"{name}: mesh convert {ex.Message}");
        return null;
      }
    }

    private static Solid? UnionSolids(List<Solid> solids, List<string> warnings, string name)
    {
      Solid? result = null;
      foreach (Solid solid in solids)
      {
        Solid? next = TryClone(solid);
        if (next == null)
        {
          continue;
        }

        if (result == null)
        {
          result = next;
          continue;
        }

        Solid? union = TryBoolean(result, next, BooleanOperationsType.Union);
        if (union != null)
        {
          result = union;
        }
        else
        {
          warnings.Add($"{name}: union skipped a solid.");
        }
      }

      return result != null && result.Volume > MinVolume ? result : null;
    }

    private static Solid? TryClone(Solid solid)
    {
      try
      {
        return SolidUtils.Clone(solid);
      }
      catch
      {
        return solid;
      }
    }

    private static Solid? TryBoolean(Solid first, Solid second, BooleanOperationsType type)
    {
      try
      {
        Solid result = BooleanOperationsUtils.ExecuteBooleanOperation(first, second, type);
        return result != null && result.Volume > MinVolume ? result : null;
      }
      catch
      {
        return null;
      }
    }
  }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMaterial = Autodesk.Revit.DB.Material;
using View = Autodesk.Revit.DB.View;

namespace NMKRebar.Services
{
  public sealed class InPlaceFamilySelectionFilter : ISelectionFilter
  {
    public bool AllowElement(Element elem)
    {
      return elem is FamilyInstance instance
        && instance.Symbol?.Family != null
        && instance.Symbol.Family.IsInPlace;
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
      return false;
    }
  }

  public static class DirectShapeFromInPlaceService
  {
    private const double MinVolume = 1e-9;
    private const string ConcreteMaterialName = "NMK Concrete";

    public static string CreateFromPicked(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Select Model In-Place runs in a project document.");
      }

      IList<Reference> picks = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new InPlaceFamilySelectionFilter(),
        "Select model in-place instances");
      List<FamilyInstance> instances = picks
        .Select(pick => doc.GetElement(pick.ElementId))
        .OfType<FamilyInstance>()
        .Where(instance => instance.Symbol?.Family != null && instance.Symbol.Family.IsInPlace)
        .GroupBy(instance => CreateRebarByLineService.IdValue(instance.Id))
        .Select(group => group.First())
        .ToList();
      if (instances.Count == 0)
      {
        throw new InvalidOperationException("No model in-place instance was selected.");
      }

      var created = new List<ElementId>();
      var warnings = new List<string>();
      int solidCount = 0;
      using (var tx = new Transaction(doc, "NMK DirectShape from In-Place"))
      {
        tx.Start();
        DirectShapeFromInPlaceService.UnhideGenericModel(doc);
        RevitMaterial? concrete = GetOrCreateConcreteMaterial(doc);
        foreach (FamilyInstance instance in instances)
        {
          try
          {
            List<Solid> solids = ExtractSolids(instance);
            if (solids.Count == 0)
            {
              warnings.Add($"{instance.Name}: no solid (voids skipped).");
              continue;
            }

            List<GeometryObject> shape = ToDirectShapeGeometry(solids);
            if (shape.Count == 0)
            {
              warnings.Add($"{instance.Name}: solids could not be copied.");
              continue;
            }

            ElementId categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
            if (!DirectShape.IsValidCategoryId(categoryId, doc))
            {
              throw new InvalidOperationException("Generic Model is not a valid DirectShape category.");
            }

            DirectShape ds = DirectShape.CreateElement(doc, categoryId);
            ds.ApplicationId = "NMKRebar";
            ds.ApplicationDataId = CreateRebarByLineService.IdValue(instance.Id).ToString();
            ds.SetName(SanitizeName($"IP_{instance.Name}"));
            ds.SetShape(shape);
            DirectShapeFromInPlaceService.AssignConcreteMaterial(ds, concrete);
            TrySetString(ds, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, $"From in-place {instance.Name}");
            solidCount += solids.Count;
            created.Add(ds.Id);
          }
          catch (Exception ex)
          {
            warnings.Add($"{instance.Name}: {ex.Message}");
          }
        }

        if (created.Count == 0)
        {
          tx.RollBack();
        }
        else
        {
          tx.Commit();
        }
      }

      if (created.Count == 0)
      {
        string fail = "No DirectShape was created.";
        if (warnings.Count > 0)
        {
          fail += " " + string.Join(" ", warnings.Take(3));
        }

        throw new InvalidOperationException(fail);
      }

      uidoc.Selection.SetElementIds(created);
      int hostOk = created.Count(id =>
        doc.GetElement(id) is Element element && RebarHostData.GetRebarHostData(element) != null);
      string text = $"Created {created.Count} DirectShape(s) from {solidCount} solid(s). Rebar host: {hostOk}/{created.Count}.";
      if (warnings.Count > 0)
      {
        text += " " + string.Join(" ", warnings.Take(3));
      }

      return text;
    }

    private static List<Solid> ExtractSolids(FamilyInstance instance)
    {
      var solids = new List<Solid>();
      var options = new Options
      {
        ComputeReferences = false,
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Fine
      };
      GeometryElement? geometry = instance.get_Geometry(options);
      if (geometry == null)
      {
        return solids;
      }

      CollectSolids(geometry, solids);
      return solids;
    }

    private static void CollectSolids(GeometryElement geometry, List<Solid> solids)
    {
      foreach (GeometryObject geo in geometry)
      {
        if (geo is Solid solid && IsKeepSolid(solid))
        {
          solids.Add(solid);
        }
        else if (geo is GeometryInstance instance)
        {
          GeometryElement? nested = instance.GetInstanceGeometry();
          if (nested != null)
          {
            CollectSolids(nested, solids);
          }
        }
      }
    }

    private static bool IsKeepSolid(Solid solid)
    {
      return solid != null
        && solid.Faces.Size > 0
        && solid.Volume > MinVolume;
    }

    private static List<GeometryObject> ToDirectShapeGeometry(List<Solid> solids)
    {
      var copied = new List<GeometryObject>();
      foreach (Solid solid in solids)
      {
        try
        {
          copied.Add(SolidUtils.Clone(solid));
        }
        catch
        {
        }
      }

      if (copied.Count <= 1)
      {
        return copied;
      }

      Solid? merged = copied[0] as Solid;
      var leftover = new List<GeometryObject>();
      for (int i = 1; i < copied.Count; i++)
      {
        if (merged == null || copied[i] is not Solid next)
        {
          leftover.Add(copied[i]);
          continue;
        }

        try
        {
          Solid union = BooleanOperationsUtils.ExecuteBooleanOperation(
            merged,
            next,
            BooleanOperationsType.Union);
          if (union != null && union.Volume > MinVolume)
          {
            merged = union;
          }
          else
          {
            leftover.Add(next);
          }
        }
        catch
        {
          leftover.Add(next);
        }
      }

      var result = new List<GeometryObject>();
      if (merged != null && merged.Volume > MinVolume)
      {
        result.Add(merged);
      }

      result.AddRange(leftover);
      return result;
    }

    internal static RevitMaterial? GetOrCreateConcreteMaterial(Document doc)
    {
      List<RevitMaterial> materials = new FilteredElementCollector(doc)
        .OfClass(typeof(RevitMaterial))
        .Cast<RevitMaterial>()
        .ToList();
      RevitMaterial? material = materials.FirstOrDefault(IsNamedConcrete)
        ?? materials.FirstOrDefault(item => IsConcreteAsset(doc, item));
      if (material == null)
      {
        ElementId id = RevitMaterial.Create(doc, ConcreteMaterialName);
        material = doc.GetElement(id) as RevitMaterial;
        if (material != null)
        {
          material.Color = new Autodesk.Revit.DB.Color(180, 180, 180);
        }
      }

      if (material != null)
      {
        EnsureConcreteStructuralAsset(doc, material);
      }

      return material;
    }

    private static bool IsNamedConcrete(RevitMaterial material)
    {
      string name = material.Name ?? string.Empty;
      return name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Beton", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Bê tông", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Be tong", StringComparison.OrdinalIgnoreCase) >= 0
        || string.Equals(material.MaterialClass, "Concrete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConcreteAsset(Document doc, RevitMaterial material)
    {
      try
      {
        if (material.StructuralAssetId == ElementId.InvalidElementId)
        {
          return false;
        }

        if (doc.GetElement(material.StructuralAssetId) is not PropertySetElement pse)
        {
          return false;
        }

        StructuralAsset? asset = pse.GetStructuralAsset();
        return asset != null && asset.StructuralAssetClass == StructuralAssetClass.Concrete;
      }
      catch
      {
        return false;
      }
    }

    private static void EnsureConcreteStructuralAsset(Document doc, RevitMaterial material)
    {
      if (IsConcreteAsset(doc, material))
      {
        return;
      }

      try
      {
        var asset = new StructuralAsset("NMK Concrete", StructuralAssetClass.Concrete);
        PropertySetElement pse = PropertySetElement.Create(doc, asset);
        material.SetMaterialAspectByPropertySet(MaterialAspect.Structural, pse.Id);
      }
      catch
      {
      }
    }

    internal static void AssignConcreteMaterial(DirectShape ds, RevitMaterial? concrete)
    {
      if (concrete == null)
      {
        return;
      }

      TrySetId(ds, BuiltInParameter.STRUCTURAL_MATERIAL_PARAM, concrete.Id);
      TrySetId(ds, BuiltInParameter.MATERIAL_ID_PARAM, concrete.Id);
    }

    private static void TrySetId(Element element, BuiltInParameter builtIn, ElementId id)
    {
      Parameter? parameter = element.get_Parameter(builtIn);
      if (parameter == null || parameter.IsReadOnly)
      {
        return;
      }

      try
      {
        parameter.Set(id);
      }
      catch
      {
      }
    }

    private static void TrySetString(Element element, BuiltInParameter builtIn, string value)
    {
      Parameter? parameter = element.get_Parameter(builtIn);
      if (parameter == null || parameter.IsReadOnly)
      {
        return;
      }

      try
      {
        parameter.Set(value);
      }
      catch
      {
      }
    }

    internal static void UnhideGenericModel(Document doc)
    {
      View? view = doc.ActiveView;
      ElementId categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
      if (view == null || !view.CanCategoryBeHidden(categoryId))
      {
        return;
      }

      try
      {
        view.SetCategoryHidden(categoryId, false);
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException)
      {
      }
    }

    private static string SanitizeName(string name)
    {
      char[] prohibited = { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '{', '}' };
      string result = name;
      foreach (char c in prohibited)
      {
        result = result.Replace(c, '_');
      }

      result = result.Trim().TrimEnd('.', '_');
      return string.IsNullOrEmpty(result) ? "InPlace_DS" : result;
    }
  }
}

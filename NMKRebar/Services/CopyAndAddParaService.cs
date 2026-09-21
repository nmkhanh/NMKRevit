using Autodesk.Revit.DB;
using System.Text;
using System.Text.RegularExpressions;

namespace NMKRebar.Services
{
  public sealed class CopyAndAddParaResult
  {
    public int ParametersDeleted { get; set; }

    public int MadeInstance { get; set; }

    public int ParametersAdded { get; set; }

    public int InstancesCopied { get; set; }

    public int AssociationsSet { get; set; }

    public int FormulasSet { get; set; }

    public int ValuesSet { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"X/Y parameters deleted: {ParametersDeleted}");
      text.AppendLine($"Z/Visible made instance: {MadeInstance}");
      text.AppendLine($"Parameters added: {ParametersAdded}");
      text.AppendLine($"Instances copied: {InstancesCopied}");
      text.AppendLine($"Associations set: {AssociationsSet}");
      text.AppendLine($"Z values set: {ValuesSet} (Z_1=0, others=100mm)");
      text.AppendLine($"Visible formulas: {FormulasSet} (not(Z_n > 0))");
      if (Warnings.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("Warnings:");
        foreach (string warning in Warnings.Take(20))
        {
          text.AppendLine("- " + warning);
        }
      }

      return text.ToString();
    }
  }

  public static class CopyAndAddParaService
  {
    public const int Count = 100;

    private static readonly Regex XyName = new(@"^(X|Y)_?\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ZOrVisibleName = new(
      @"^(Z|Visible)_?\d+$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CopyAndAddParaResult Apply(Document doc)
    {
      return Apply(doc, copyElements: true, mapCount: Count);
    }

    public static CopyAndAddParaResult MapExisting(Document doc, int mapCount = 1)
    {
      return Apply(doc, copyElements: false, mapCount: mapCount);
    }

    public static CopyAndAddParaResult Apply(Document doc, bool copyElements, int mapCount)
    {
      if (!doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("NMKCopyAndAddPara runs in a family document.");
      }

      int targetCount = mapCount < 1 ? 1 : mapCount;
      var result = new CopyAndAddParaResult();
      FamilyManager fm = doc.FamilyManager;
      ForgeTypeId general = FamilyParameterGroups.General();

      using (var tx = new Transaction(doc, copyElements ? "NMK Copy And Add Para" : "NMK Map Beam"))
      {
        tx.Start();
        EnsureCurrentType(fm);

        result.ParametersDeleted = DeleteXyParameters(fm, result);
        ClearZAndVisibleFormulas(fm, result);
        result.MadeInstance = MakeAllZAndVisibleInstance(fm, result);

        for (int n = 1; n <= Count; n++)
        {
          result.ParametersAdded += EnsureInstanceParameter(fm, $"Z_{n}", general, SpecTypeId.Length, result);
          result.ParametersAdded += EnsureInstanceParameter(fm, $"Visible_{n}", general, SpecTypeId.Boolean.YesNo, result);
          result.MadeInstance += MakeInstanceIfNeeded(fm, $"Z_{n}", result);
          result.MadeInstance += MakeInstanceIfNeeded(fm, $"Z{n}", result);
          result.MadeInstance += MakeInstanceIfNeeded(fm, $"Visible_{n}", result);
          result.MadeInstance += MakeInstanceIfNeeded(fm, $"Visible{n}", result);
          result.FormulasSet += SetVisibleFormula(fm, n, result);
          result.ValuesSet += SetZValue(fm, n, result);
        }

        result.MadeInstance += MakeAllZAndVisibleInstance(fm, result);
        WarnIfZOrVisibleStillType(fm, result);

        List<Element> elements = CollectModelElements(doc);
        if (elements.Count == 0)
        {
          throw new InvalidOperationException("No copyable model element was found in this family.");
        }

        if (copyElements)
        {
          Element source = elements[0];
          while (elements.Count < targetCount)
          {
            ICollection<ElementId> copied = ElementTransformUtils.CopyElement(doc, source.Id, XYZ.Zero);
            foreach (ElementId id in copied)
            {
              Element? copiedElement = doc.GetElement(id);
              if (copiedElement != null && IsCopyableModelElement(copiedElement))
              {
                elements.Add(copiedElement);
                result.InstancesCopied++;
              }
            }

            if (copied.Count == 0)
            {
              result.Warnings.Add($"CopyElement returned no ids from {source.Id}.");
              break;
            }
          }

          doc.Regenerate();
          elements = CollectModelElements(doc).Take(targetCount).ToList();
          if (elements.Count < targetCount)
          {
            result.Warnings.Add($"Expected {targetCount} model elements, found {elements.Count}.");
          }
        }
        else
        {
          elements = elements.Take(targetCount).ToList();
        }

        for (int i = 0; i < elements.Count; i++)
        {
          int n = i + 1;
          Element element = elements[i];
          string zName = ZName(fm, n);
          string visibleName = VisibleName(fm, n);
          result.AssociationsSet += AssociateElevation(fm, element, zName, result);
          result.AssociationsSet += AssociateOffsetFromHost(fm, element, zName, result);
          result.AssociationsSet += Associate(fm, element, "Visible", visibleName, result);
        }

        tx.Commit();
      }

      return result;
    }

    private static void EnsureCurrentType(FamilyManager fm)
    {
      if (fm.CurrentType != null)
      {
        return;
      }

      FamilyType? first = fm.Types.Cast<FamilyType>().FirstOrDefault();
      if (first != null)
      {
        fm.CurrentType = first;
      }
    }

    private static int DeleteXyParameters(FamilyManager fm, CopyAndAddParaResult result)
    {
      int deleted = 0;
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        string name = parameter.Definition.Name;
        if (!XyName.IsMatch(name))
        {
          continue;
        }

        try
        {
          fm.RemoveParameter(parameter);
          deleted++;
        }
        catch (Exception ex)
        {
          result.Warnings.Add($"Delete {name}: {ex.Message}");
        }
      }

      return deleted;
    }

    private static void ClearZAndVisibleFormulas(FamilyManager fm, CopyAndAddParaResult result)
    {
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        string name = parameter.Definition.Name;
        if (!ZOrVisibleName.IsMatch(name) || !parameter.IsDeterminedByFormula)
        {
          continue;
        }

        try
        {
          fm.SetFormula(parameter, null);
        }
        catch (Exception ex)
        {
          result.Warnings.Add($"Clear formula {name}: {ex.Message}");
        }
      }
    }

    private static int MakeAllZAndVisibleInstance(FamilyManager fm, CopyAndAddParaResult result)
    {
      int converted = 0;
      foreach (FamilyParameter parameter in fm.Parameters.Cast<FamilyParameter>().ToList())
      {
        string name = parameter.Definition.Name;
        if (!ZOrVisibleName.IsMatch(name))
        {
          continue;
        }

        converted += MakeInstanceIfNeeded(fm, name, result);
      }

      return converted;
    }

    private static void WarnIfZOrVisibleStillType(FamilyManager fm, CopyAndAddParaResult result)
    {
      for (int n = 1; n <= Count; n++)
      {
        WarnIfStillType(FindFamilyParameter(fm, $"Z_{n}") ?? FindFamilyParameter(fm, $"Z{n}"), result);
        WarnIfStillType(FindFamilyParameter(fm, $"Visible_{n}") ?? FindFamilyParameter(fm, $"Visible{n}"), result);
      }
    }

    private static void WarnIfStillType(FamilyParameter? parameter, CopyAndAddParaResult result)
    {
      if (parameter == null || parameter.IsInstance)
      {
        return;
      }

      result.Warnings.Add($"{parameter.Definition.Name} is still type.");
    }

    private static int EnsureInstanceParameter(
      FamilyManager fm,
      string name,
      ForgeTypeId group,
      ForgeTypeId spec,
      CopyAndAddParaResult result)
    {
      if (FindFamilyParameter(fm, name) != null
          || FindFamilyParameter(fm, name.Replace("_", string.Empty)) != null)
      {
        return 0;
      }

      try
      {
        fm.AddParameter(name, group, spec, true);
        return 1;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"Add {name}: {ex.Message}");
        return 0;
      }
    }

    private static int MakeInstanceIfNeeded(FamilyManager fm, string name, CopyAndAddParaResult result)
    {
      FamilyParameter? parameter = FindFamilyParameter(fm, name);
      if (parameter == null || parameter.IsInstance)
      {
        return 0;
      }

      try
      {
        fm.MakeInstance(parameter);
        return 1;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"Make instance {name}: {ex.Message}");
        return 0;
      }
    }

    private static int SetZValue(FamilyManager fm, int n, CopyAndAddParaResult result)
    {
      FamilyParameter? parameter = FindFamilyParameter(fm, ZName(fm, n));
      if (parameter == null)
      {
        result.Warnings.Add($"Missing family parameter '{ZName(fm, n)}'.");
        return 0;
      }

      if (parameter.IsDeterminedByFormula)
      {
        result.Warnings.Add($"{parameter.Definition.Name} is formula-driven.");
        return 0;
      }

      try
      {
        double mm = n == 1 ? 0 : 100;
        fm.Set(parameter, CsvValueConverter.ToInternalValue(SpecTypeId.Length, mm));
        return 1;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"{parameter.Definition.Name} value: {ex.Message}");
        return 0;
      }
    }

    private static int SetVisibleFormula(FamilyManager fm, int n, CopyAndAddParaResult result)
    {
      string visibleName = VisibleName(fm, n);
      string zName = ZName(fm, n);
      FamilyParameter? visible = FindFamilyParameter(fm, visibleName);
      if (visible == null)
      {
        result.Warnings.Add($"Missing family parameter '{visibleName}'.");
        return 0;
      }

      try
      {
        fm.SetFormula(visible, $"not({zName} > 0)");
        return 1;
      }
      catch (Exception ex)
      {
        result.Warnings.Add($"{visibleName} formula: {ex.Message}");
        return 0;
      }
    }

    private static string ZName(FamilyManager fm, int n)
    {
      return ExistingName(fm, "Z", n);
    }

    private static string VisibleName(FamilyManager fm, int n)
    {
      return ExistingName(fm, "Visible", n);
    }

    private static string ExistingName(FamilyManager fm, string prefix, int n)
    {
      string underscored = $"{prefix}_{n}";
      if (FindFamilyParameter(fm, underscored) != null)
      {
        return underscored;
      }

      string compact = $"{prefix}{n}";
      if (FindFamilyParameter(fm, compact) != null)
      {
        return compact;
      }

      return underscored;
    }

    private static int Associate(FamilyManager fm, Element element, string elementParamName, string familyParamName, CopyAndAddParaResult result)
    {
      Parameter? elementParam = element.LookupParameter(elementParamName);
      return AssociateParameter(fm, elementParam, familyParamName, $"{element.Id}: {elementParamName}", result);
    }

    private static int AssociateElevation(FamilyManager fm, Element element, string familyParamName, CopyAndAddParaResult result)
    {
      Parameter? elementParam = element.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)
        ?? element.LookupParameter("Elevation from Level");
      return AssociateParameter(fm, elementParam, familyParamName, $"{element.Id}: Elevation from Level", result, optional: true);
    }

    private static int AssociateOffsetFromHost(FamilyManager fm, Element element, string familyParamName, CopyAndAddParaResult result)
    {
      Parameter? elementParam = element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)
        ?? element.LookupParameter("Offset from Host");
      return AssociateParameter(fm, elementParam, familyParamName, $"{element.Id}: Offset from Host", result, optional: true);
    }

    private static int AssociateParameter(
      FamilyManager fm,
      Parameter? elementParam,
      string familyParamName,
      string context,
      CopyAndAddParaResult result,
      bool optional = false)
    {
      if (elementParam == null)
      {
        if (!optional)
        {
          result.Warnings.Add($"Missing {context}.");
        }

        return 0;
      }

      FamilyParameter? familyParam = FindFamilyParameter(fm, familyParamName);
      if (familyParam == null)
      {
        result.Warnings.Add($"Missing family parameter '{familyParamName}'.");
        return 0;
      }

      try
      {
        fm.AssociateElementParameterToFamilyParameter(elementParam, familyParam);
        return 1;
      }
      catch (Exception ex)
      {
        if (!optional)
        {
          result.Warnings.Add($"{context} -> {familyParamName}: {ex.Message}");
        }

        return 0;
      }
    }

    private static List<Element> CollectModelElements(Document doc)
    {
      var ids = new HashSet<ElementId>();
      foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
      {
        ids.Add(element.Id);
      }

      if (doc.ActiveView != null)
      {
        foreach (Element element in new FilteredElementCollector(doc, doc.ActiveView.Id).WhereElementIsNotElementType())
        {
          ids.Add(element.Id);
        }
      }

      return ids
        .Select(doc.GetElement)
        .Where(element => element != null && IsCopyableModelElement(element))
        .Cast<Element>()
        .OrderBy(element => IdValue(element.Id))
        .ToList();
    }

    private static bool IsCopyableModelElement(Element element)
    {
      if (element is Autodesk.Revit.DB.View
          || element is ReferencePlane
          || element is Dimension
          || element is SpotDimension
          || element is Sketch
          || element is SketchPlane
          || element is Autodesk.Revit.DB.Material
          || element is FillPatternElement
          || element is LinePatternElement
          || element is Family
          || element is FamilySymbol
          || element is Level
          || element is Grid)
      {
        return false;
      }

      return element is FamilyInstance
        || element is ImportInstance
        || element is DirectShape
        || element is GenericForm
        || element is GeomCombination
        || element.LookupParameter("X") != null
        || element.LookupParameter("Visible") != null
        || element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) != null;
    }

    private static FamilyParameter? FindFamilyParameter(FamilyManager fm, string name)
    {
      foreach (FamilyParameter parameter in fm.Parameters)
      {
        if (string.Equals(parameter.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
        {
          return parameter;
        }
      }

      return null;
    }

    private static long IdValue(ElementId id)
    {
#if NETFRAMEWORK
      return id.IntegerValue;
#else
      return id.Value;
#endif
    }
  }
}

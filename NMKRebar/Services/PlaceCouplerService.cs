using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Text;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;
using View = Autodesk.Revit.DB.View;

namespace NMKRebar.Services
{
  public sealed class PlaceCouplerResult
  {
    public int SuccessCount { get; set; }

    public int FailCount { get; set; }

    public int RebarCount { get; set; }

    public int GroupCount { get; set; }

    public int IsolatedCount { get; set; }

    public string IsolatedView { get; set; } = string.Empty;

    public string BarTypeFilter { get; set; } = string.Empty;

    public string Placement { get; set; } = string.Empty;

    public Dictionary<string, int> CouplersPerDiameter { get; } = new();

    public List<string> Errors { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Couplers placed: {SuccessCount}");
      text.AppendLine($"Placement: {Placement}");
      text.AppendLine($"Groups: {GroupCount}");
      text.AppendLine($"Rebars: {RebarCount}");
      if (!string.IsNullOrEmpty(BarTypeFilter))
      {
        text.AppendLine($"Bar type: {BarTypeFilter}");
      }
      if (IsolatedCount > 0)
      {
        text.AppendLine($"Temporary isolate: {IsolatedCount} on '{IsolatedView}'");
      }
      if (CouplersPerDiameter.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("By diameter:");
        foreach (KeyValuePair<string, int> pair in CouplersPerDiameter.OrderBy(item => item.Key))
        {
          text.AppendLine($"  {pair.Key}: {pair.Value}");
        }
      }

      if (FailCount > 0)
      {
        text.AppendLine();
        text.AppendLine($"Failed: {FailCount}");
      }

      if (Errors.Count > 0)
      {
        if (FailCount == 0)
        {
          text.AppendLine();
        }

        foreach (string error in Errors.Take(8))
        {
          text.AppendLine("- " + error);
        }
      }

      return text.ToString();
    }
  }

  public static class PlaceCouplerService
  {
    private static readonly int[] CommonDiameters = { 10, 12, 13, 16, 19, 22, 25, 29, 32 };

    public static IReadOnlyList<string> CollectGroupTypeNames(Document doc)
    {
      return new FilteredElementCollector(doc)
        .OfClass(typeof(Group))
        .Cast<Group>()
        .Select(group => group.GroupType?.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, Comparer<string>.Create(CompareNumericNames))
        .ToList();
    }

    public static bool HasNumberedSuffixVariants(IEnumerable<string> typeNames, string selectedName)
    {
      if (string.IsNullOrWhiteSpace(selectedName))
      {
        return false;
      }

      string baseName = GetSuffixBase(selectedName.Trim());
      var numbered = typeNames
        .Where(name => TryGetNumberSuffix(name, out string itemBase, out _)
          && itemBase.Equals(baseName, StringComparison.OrdinalIgnoreCase))
        .Select(name => TryGetNumberSuffix(name, out _, out int number) ? number : -1)
        .Distinct()
        .ToList();
      return numbered.Count >= 2 && numbered.Contains(1);
    }

    public static int CompareNumericNames(string left, string right)
    {
      List<string> a = SplitNumericTokens(left ?? string.Empty);
      List<string> b = SplitNumericTokens(right ?? string.Empty);
      int count = Math.Min(a.Count, b.Count);
      for (int i = 0; i < count; i++)
      {
        bool aNum = long.TryParse(a[i], out long aValue);
        bool bNum = long.TryParse(b[i], out long bValue);
        if (aNum && bNum)
        {
          int compared = aValue.CompareTo(bValue);
          if (compared != 0)
          {
            return compared;
          }
        }
        else
        {
          int compared = string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
          if (compared != 0)
          {
            return compared;
          }
        }
      }

      return a.Count.CompareTo(b.Count);
    }

    public static IReadOnlyList<string> CollectCouplerFamilyNames(Document doc)
    {
      return new FilteredElementCollector(doc)
        .OfClass(typeof(ElementType))
        .Cast<ElementType>()
        .Where(type => IsCouplerCategory(type.Category) && !string.IsNullOrWhiteSpace(type.FamilyName))
        .Select(type => type.FamilyName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    public static int SelectRebarsInActiveView(UIDocument uidoc)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Select Rebar runs in a project document.");
      }

      IList<Reference> picked = uidoc.Selection.PickObjects(
        ObjectType.Element,
        new NmkRebarElementSelectionFilter(),
        "Select rebars in the active view");
      var ids = picked
        .Select(item => item.ElementId)
        .GroupBy(IdValue)
        .Select(item => item.First())
        .Where(id => doc.GetElement(id) is RevitRebar)
        .ToList();
      uidoc.Selection.SetElementIds(ids);
      return ids.Count;
    }

    public static PlaceCouplerResult PlaceForBarType(
      UIDocument uidoc,
      string barTypeName,
      string couplerFamilyName,
      bool placeOne)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Place Coupler runs in a project document.");
      }

      if (string.IsNullOrWhiteSpace(barTypeName))
      {
        throw new InvalidOperationException("Select a type in the list first.");
      }

      if (string.IsNullOrWhiteSpace(couplerFamilyName))
      {
        throw new InvalidOperationException("Select a coupler family.");
      }

      List<RevitRebar> rebars = CollectRebarsOfBarType(doc, barTypeName);
      if (rebars.Count == 0)
      {
        throw new InvalidOperationException($"No rebar of type '{barTypeName}' was found.");
      }

      return PlaceOnRebars(
        uidoc,
        rebars,
        new List<Group>(),
        rebars,
        couplerFamilyName,
        placeOne,
        true);
    }

    public static List<RevitRebar> CollectRebarsOfBarType(Document doc, string barTypeName)
    {
      return new FilteredElementCollector(doc)
        .OfClass(typeof(RevitRebar))
        .Cast<RevitRebar>()
        .Where(rebar =>
        {
          if (doc.GetElement(rebar.GetTypeId()) is not RebarBarType barType)
          {
            return false;
          }

          return barType.Name.Equals(barTypeName, StringComparison.OrdinalIgnoreCase);
        })
        .ToList();
    }

    public static PlaceCouplerResult Place(
      UIDocument uidoc,
      string groupTypeName,
      string couplerFamilyName,
      bool placeOne,
      bool placeAllSuffixes)
    {
      Document doc = uidoc.Document;
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("Place Coupler runs in a project document.");
      }

      if (string.IsNullOrWhiteSpace(groupTypeName))
      {
        throw new InvalidOperationException("Select a group.");
      }

      if (string.IsNullOrWhiteSpace(couplerFamilyName))
      {
        throw new InvalidOperationException("Select a coupler family.");
      }

      IReadOnlyList<string> allTypeNames = CollectGroupTypeNames(doc);
      string selected = ResolveSelectedGroupName(allTypeNames, groupTypeName.Trim());
      List<Group> groups = FindGroups(doc, selected, true);
      if (groups.Count == 0)
      {
        throw new InvalidOperationException($"No group named '{groupTypeName}' was found.");
      }

      var allRebars = new List<RevitRebar>();
      var usedGroups = new List<Group>();
      foreach (Group group in RootsOnly(groups))
      {
        CollectRebars(doc, group, true, selected, allRebars, usedGroups);
      }

      var rebars = placeAllSuffixes ? allRebars : KeepOneBarType(doc, allRebars);

      if (rebars.Count == 0)
      {
        string scope = placeAllSuffixes ? "All bar types" : "one bar type";
        throw new InvalidOperationException($"No rebars were found in group '{groupTypeName}' ({scope}).");
      }

      return PlaceOnRebars(
        uidoc,
        rebars,
        usedGroups,
        allRebars,
        couplerFamilyName,
        placeOne,
        placeAllSuffixes);
    }

    private static PlaceCouplerResult PlaceOnRebars(
      UIDocument uidoc,
      List<RevitRebar> rebars,
      List<Group> usedGroups,
      List<RevitRebar> isolateRebars,
      string couplerFamilyName,
      bool placeOne,
      bool placeAllSuffixes)
    {
      Document doc = uidoc.Document;
      int placementOption = placeOne ? 1 : 2;
      var result = new PlaceCouplerResult
      {
        RebarCount = rebars.Count,
        GroupCount = usedGroups.Count,
        Placement = placeOne ? "One" : "Two",
        BarTypeFilter = placeAllSuffixes
          ? string.Empty
          : (doc.GetElement(rebars[0].GetTypeId())?.Name ?? "one type")
      };

      var couplerIds = new List<ElementId>();
      View? isolateView = uidoc.ActiveGraphicalView ?? doc.ActiveView;
      using (var tx = new Transaction(doc, "NMK Place Couplers"))
      {
        tx.Start();
        if (isolateView != null && !isolateView.IsTemplate)
        {
          EnsureCategoryVisible(isolateView, BuiltInCategory.OST_Coupler);
          EnsureCategoryVisible(isolateView, BuiltInCategory.OST_Rebar);
        }
        foreach (RevitRebar rebar in rebars)
        {
          try
          {
            if (doc.GetElement(rebar.GetTypeId()) is not RebarBarType rebarType)
            {
              result.FailCount++;
              result.Errors.Add($"Rebar {IdText(rebar.Id)}: Could not get rebar type");
              continue;
            }

            Parameter? barDiameterParam = rebarType.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
            if (barDiameterParam == null)
            {
              result.FailCount++;
              result.Errors.Add($"Rebar {IdText(rebar.Id)}: No Bar Diameter parameter");
              continue;
            }

            int diameterMm = (int)Math.Round(barDiameterParam.AsDouble() * 304.8);
            ElementType? couplerType = FindCouplerType(doc, couplerFamilyName, diameterMm);
            if (couplerType == null)
            {
              int closestDiameter = CommonDiameters.OrderBy(item => Math.Abs(item - diameterMm)).First();
              if (closestDiameter != diameterMm)
              {
                couplerType = FindCouplerType(doc, couplerFamilyName, closestDiameter);
              }

              if (couplerType == null)
              {
                result.FailCount++;
                result.Errors.Add($"Rebar {IdText(rebar.Id)}: No coupler type for {diameterMm}mm (CSS{diameterMm}) in '{couplerFamilyName}'");
                continue;
              }

              result.Errors.Add($"Rebar {IdText(rebar.Id)}: Using closest coupler {closestDiameter}mm for {diameterMm}mm");
            }

            int placedCount = PlaceCouplersOnRebar(doc, rebar, couplerType, placementOption, couplerIds);
            if (placedCount > 0)
            {
              result.SuccessCount += placedCount;
              string key = $"{diameterMm}mm";
              if (!result.CouplersPerDiameter.ContainsKey(key))
              {
                result.CouplersPerDiameter[key] = 0;
              }

              result.CouplersPerDiameter[key] += placedCount;
              if (placementOption == 2 && placedCount < 2)
              {
                result.FailCount++;
                result.Errors.Add($"Rebar {IdText(rebar.Id)}: Placed only {placedCount}/2 couplers");
              }
            }
            else
            {
              result.FailCount++;
              result.Errors.Add($"Rebar {IdText(rebar.Id)}: Could not place coupler at {(placeOne ? "one end" : "both ends")}");
            }
          }
          catch (Exception ex)
          {
            result.FailCount++;
            result.Errors.Add($"Rebar {IdText(rebar.Id)}: {ex.Message}");
          }
        }

        tx.Commit();
      }

      IsolateAfterPlace(
        isolateView,
        usedGroups,
        couplerIds,
        isolateRebars,
        result);
      return result;
    }

    private static void CollectRebars(
      Document doc,
      Group group,
      bool placeAllSuffixes,
      string selectedBase,
      List<RevitRebar> rebars,
      List<Group> usedGroups)
    {
      int? number = GetGroupVariantNumber(group, selectedBase);
      if (!placeAllSuffixes && number is > 1)
      {
        return;
      }

      bool addedGroup = false;
      var nested = new List<Group>();
      try
      {
        foreach (ElementId id in group.GetMemberIds())
        {
          Element? element = doc.GetElement(id);
          if (element is Group nestedGroup)
          {
            nested.Add(nestedGroup);
          }
        }

        foreach (ElementId id in group.GetMemberIds())
        {
          Element? element = doc.GetElement(id);
          if (element is RevitRebar rebar)
          {
            rebars.Add(rebar);
            if (!addedGroup)
            {
              usedGroups.Add(group);
              addedGroup = true;
            }
          }
        }

        foreach (Group nestedGroup in nested)
        {
          CollectRebars(doc, nestedGroup, true, selectedBase, rebars, usedGroups);
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"Error getting rebars from group: {ex.Message}");
      }
    }

    private static List<RevitRebar> KeepOneBarType(Document doc, List<RevitRebar> rebars)
    {
      var byType = rebars
        .GroupBy(rebar => rebar.GetTypeId())
        .Select(group => new
        {
          TypeId = group.Key,
          Name = doc.GetElement(group.Key)?.Name ?? string.Empty,
          Bars = group.ToList()
        })
        .ToList();
      if (byType.Count == 0)
      {
        return rebars;
      }

      var chosen = byType.FirstOrDefault(item =>
          TryGetNumberSuffix(item.Name, out _, out int number) && number == 1)
        ?? byType.OrderBy(item => item.Name, Comparer<string>.Create(CompareNumericNames)).First();
      return chosen.Bars;
    }

    private static ElementType? FindCouplerType(Document doc, string familyName, int diameterMm)
    {
      string targetName = "CSS" + diameterMm;
      return new FilteredElementCollector(doc)
        .OfClass(typeof(ElementType))
        .Cast<ElementType>()
        .FirstOrDefault(type =>
          IsCouplerCategory(type.Category)
          && type.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase)
          && (type.Name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0
            || type.Name.EndsWith(targetName, StringComparison.OrdinalIgnoreCase)));
    }

    private static int PlaceCouplersOnRebar(
      Document doc,
      RevitRebar rebar,
      ElementType couplerType,
      int placementOption,
      List<ElementId> couplerIds)
    {
      try
      {
        IList<Curve> curves = rebar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (curves == null || curves.Count == 0)
        {
          return 0;
        }

        if (placementOption == 1)
        {
          if (TryPlaceCouplerAtEnd(doc, rebar, couplerType, 0, couplerIds))
          {
            return 1;
          }

          return TryPlaceCouplerAtEnd(doc, rebar, couplerType, 1, couplerIds) ? 1 : 0;
        }

        int placedCount = 0;
        if (TryPlaceCouplerAtEnd(doc, rebar, couplerType, 0, couplerIds))
        {
          placedCount++;
        }

        if (TryPlaceCouplerAtEnd(doc, rebar, couplerType, 1, couplerIds))
        {
          placedCount++;
        }

        return placedCount;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"Error placing coupler: {ex.Message}");
        return 0;
      }
    }

    private static bool TryPlaceCouplerAtEnd(
      Document doc,
      RevitRebar rebar,
      ElementType couplerType,
      int endIndex,
      List<ElementId> couplerIds)
    {
      try
      {
        RebarReinforcementData reinforcementData = RebarReinforcementData.Create(rebar.Id, endIndex);
        ElementId existingId = rebar.GetCouplerId(endIndex);
        if (IdValue(existingId) > 0)
        {
          couplerIds.Add(existingId);
          TryPin(doc.GetElement(existingId));
          return true;
        }

        RebarCoupler coupler = RebarCoupler.Create(doc, couplerType.Id, reinforcementData, null, out RebarCouplerError error);
        if (coupler == null)
        {
          System.Diagnostics.Debug.WriteLine($"Coupler creation error at end {endIndex}: {error}");
          return false;
        }

        TryPin(coupler);
        couplerIds.Add(coupler.Id);
        return true;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"Error placing coupler at end {endIndex}: {ex.Message}");
        return false;
      }
    }

    private static string ResolveSelectedGroupName(IReadOnlyList<string> allTypeNames, string selectedName)
    {
      string? exact = allTypeNames.FirstOrDefault(name => name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
      if (exact != null)
      {
        return exact;
      }

      string baseName = GetSuffixBase(selectedName);
      string? baseMatch = allTypeNames.FirstOrDefault(name => name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
      if (baseMatch != null)
      {
        return baseMatch;
      }

      if (HasNumberedSuffixVariants(allTypeNames, selectedName))
      {
        return baseName;
      }

      return allTypeNames.FirstOrDefault(name =>
        name.IndexOf(selectedName, StringComparison.OrdinalIgnoreCase) >= 0) ?? selectedName;
    }

    private static List<Group> FindGroups(Document doc, string selectedName, bool placeAllSuffixes)
    {
      string baseName = GetSuffixBase(selectedName);
      List<Group> matched = new FilteredElementCollector(doc)
        .OfClass(typeof(Group))
        .Cast<Group>()
        .Where(group => group.GroupType != null && BelongsToBase(group, baseName))
        .ToList();

      AssignOrderSuffixes(matched, baseName);
      if (placeAllSuffixes)
      {
        return matched;
      }

      return matched
        .Where(group =>
        {
          int? number = GetGroupVariantNumber(group, baseName);
          return number == null || number == 1;
        })
        .ToList();
    }

    private static bool BelongsToBase(Group group, string baseName)
    {
      string typeName = group.GroupType?.Name ?? string.Empty;
      string instanceName = group.Name ?? string.Empty;
      return typeName.Equals(baseName, StringComparison.OrdinalIgnoreCase)
        || GetSuffixBase(typeName).Equals(baseName, StringComparison.OrdinalIgnoreCase)
        || GetSuffixBase(instanceName).Equals(baseName, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<long, int> OrderSuffixes = new();

    private static void AssignOrderSuffixes(List<Group> matched, string baseName)
    {
      OrderSuffixes.Clear();
      List<Group> unnumbered = matched
        .Where(group =>
          group.GroupType != null
          && group.GroupType.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)
          && GetGroupVariantNumber(group, baseName) == null)
        .OrderBy(group => IdValue(group.Id))
        .ToList();
      if (unnumbered.Count < 2)
      {
        return;
      }

      for (int i = 0; i < unnumbered.Count; i++)
      {
        OrderSuffixes[IdValue(unnumbered[i].Id)] = i + 1;
      }
    }

    private static int? GetGroupVariantNumber(Group group, string baseName)
    {
      string typeName = group.GroupType?.Name ?? string.Empty;
      if (TryGetNumberSuffix(typeName, out string typeBase, out int typeNumber)
          && typeBase.Equals(baseName, StringComparison.OrdinalIgnoreCase))
      {
        return typeNumber;
      }

      string instanceName = group.Name ?? string.Empty;
      if (TryGetNumberSuffix(instanceName, out string instanceBase, out int instanceNumber)
          && instanceBase.Equals(baseName, StringComparison.OrdinalIgnoreCase))
      {
        return instanceNumber;
      }

      Parameter? mark = group.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
      string markText = mark?.AsString() ?? string.Empty;
      if (markText.Length > 0
          && TryGetNumberSuffix(markText, out string markBase, out int markNumber)
          && markBase.Equals(baseName, StringComparison.OrdinalIgnoreCase))
      {
        return markNumber;
      }

      if (markText.Length > 0 && int.TryParse(markText.Trim(), out int markOnly) && markOnly > 0)
      {
        return markOnly;
      }

      if (OrderSuffixes.TryGetValue(IdValue(group.Id), out int assigned))
      {
        return assigned;
      }

      return null;
    }

    private static List<Group> RootsOnly(List<Group> groups)
    {
      var nestedIds = new HashSet<long>();
      foreach (Group group in groups)
      {
        try
        {
          foreach (ElementId id in group.GetMemberIds())
          {
            nestedIds.Add(IdValue(id));
          }
        }
        catch
        {
        }
      }

      return groups.Where(group => !nestedIds.Contains(IdValue(group.Id))).ToList();
    }

    private static string GetSuffixBase(string name)
    {
      return TryGetNumberSuffix(name, out string baseName, out _) ? baseName : name;
    }

    private static bool TryGetNumberSuffix(string name, out string baseName, out int number)
    {
      baseName = name;
      number = 0;
      int dash = name.LastIndexOf('-');
      if (dash <= 0 || dash >= name.Length - 1)
      {
        return false;
      }

      if (!int.TryParse(name.Substring(dash + 1), out number))
      {
        return false;
      }

      baseName = name.Substring(0, dash);
      return true;
    }

    private static List<string> SplitNumericTokens(string text)
    {
      var tokens = new List<string>();
      var current = new StringBuilder();
      bool? digit = null;
      foreach (char ch in text)
      {
        bool isDigit = char.IsDigit(ch);
        if (digit != null && digit.Value != isDigit && current.Length > 0)
        {
          tokens.Add(current.ToString());
          current.Clear();
        }

        digit = isDigit;
        current.Append(ch);
      }

      if (current.Length > 0)
      {
        tokens.Add(current.ToString());
      }

      return tokens;
    }

    private static void IsolateAfterPlace(
      View? view,
      IReadOnlyCollection<Group> groups,
      IReadOnlyCollection<ElementId> couplerIds,
      IReadOnlyCollection<RevitRebar> rebars,
      PlaceCouplerResult result)
    {
      if (view == null || view.IsTemplate)
      {
        result.Errors.Add("Temporary isolate skipped: no graphical view.");
        return;
      }

      var ids = UniqueIds(
        groups.Select(item => item.Id)
          .Concat(rebars.Select(item => item.Id))
          .Concat(couplerIds));
      if (ids.Count == 0)
      {
        result.Errors.Add("Temporary isolate skipped: no hideable group/rebar/coupler.");
        return;
      }

      try
      {
        if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
        {
          result.IsolatedCount = ids.Count;
          result.IsolatedView = view.Name;
          return;
        }

        using (var tx = new Transaction(view.Document, "NMK Temporary Isolate"))
        {
          tx.Start();
          EnsureCategoryVisible(view, BuiltInCategory.OST_Coupler);
          EnsureCategoryVisible(view, BuiltInCategory.OST_Rebar);
          view.IsolateElementsTemporary(ids);
          tx.Commit();
        }

        result.IsolatedCount = ids.Count;
        result.IsolatedView = view.Name;
      }
      catch (Exception ex)
      {
        result.Errors.Add($"Temporary isolate failed: {ex.Message}");
      }
    }

    private static List<ElementId> UniqueIds(IEnumerable<ElementId> source)
    {
      return source.GroupBy(IdValue).Select(item => item.First()).ToList();
    }

    private static void EnsureCategoryVisible(View view, BuiltInCategory category)
    {
      try
      {
        Category? item = view.Document.Settings.Categories.get_Item(category);
        if (item != null && view.GetCategoryHidden(item.Id))
        {
          view.SetCategoryHidden(item.Id, false);
        }
      }
      catch
      {
      }
    }

    private static void TryPin(Element? element)
    {
      if (element == null)
      {
        return;
      }

      try
      {
        element.Pinned = true;
      }
      catch (Autodesk.Revit.Exceptions.InvalidOperationException)
      {
      }
    }

    private static bool IsCouplerCategory(Category? category)
    {
      if (category == null)
      {
        return false;
      }

      return IdValue(category.Id) == (long)BuiltInCategory.OST_Coupler;
    }

    private static string IdText(ElementId id)
    {
      return IdValue(id).ToString();
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

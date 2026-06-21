using Autodesk.Revit.DB;
using NMKRevit.NMKPrint.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NMKRevit.NMKPrint.Services
{
  public class NamingTemplateService
  {
    public List<NamingTemplateItem> Load(string templatePath)
    {
      if (!File.Exists(templatePath))
      {
        return new List<NamingTemplateItem>();
      }

      string json = File.ReadAllText(templatePath);
      return JsonConvert.DeserializeObject<List<NamingTemplateItem>>(json) ?? new List<NamingTemplateItem>();
    }

    public List<NamingTemplateItem> ScanProjectParameters(Document doc, IEnumerable<PrintItem> sheets, IEnumerable<PrintItem> views)
    {
      var result = new Dictionary<string, NamingTemplateItem>(StringComparer.OrdinalIgnoreCase);

      void Add(string id, string type, string name)
      {
        string key = GetLogicalParameterKey(type, name);
        if (!result.TryGetValue(key, out NamingTemplateItem? existing))
        {
          result[key] = new NamingTemplateItem
          {
            Id = id,
            Name = name,
            Type = type,
            Separator = "-",
            IsChecked = false
          };
          return;
        }

        if (ShouldPreferParameter(existing.Id, id))
        {
          existing.Id = id;
        }
      }

      Add(((int)BuiltInParameter.SHEET_NUMBER).ToString(), "Sheet", "Sheet : Sheet Number");
      Add(((int)BuiltInParameter.SHEET_NAME).ToString(), "Sheet", "Sheet : Sheet Name");
      Add(((int)BuiltInParameter.SHEET_CURRENT_REVISION).ToString(), "Sheet", "Sheet : Current Revision");
      Add(((int)BuiltInParameter.SHEET_CURRENT_REVISION_DATE).ToString(), "Sheet", "Sheet : Current Revision Date");
      Add(((int)BuiltInParameter.VIEW_NAME).ToString(), "View", "View : View Name");

      foreach (Parameter parameter in doc.ProjectInformation.Parameters)
      {
        Add(GetParameterId(parameter), "Project", $"Project : {parameter.Definition.Name}");
      }

      foreach (PrintItem sheet in sheets)
      {
        foreach (Parameter parameter in sheet.View.Parameters)
        {
          Add(GetParameterId(parameter), "Sheet", $"Sheet : {parameter.Definition.Name}");
        }
      }

      foreach (PrintItem view in views)
      {
        foreach (Parameter parameter in view.View.Parameters)
        {
          Add(GetParameterId(parameter), "View", $"View : {parameter.Definition.Name}");
        }
      }

      return result.Values.OrderBy(x => x.Name ?? string.Empty, NaturalStringComparer.Instance).ToList();
    }

    public List<NamingTemplateItem> MergeTemplate(IEnumerable<NamingTemplateItem> available, IEnumerable<NamingTemplateItem> template)
    {
      var templateItems = DedupeTemplateItems(template).ToList();
      var templateByTypedId = templateItems
        .Where(x => !string.IsNullOrWhiteSpace(x.Id))
        .GroupBy(x => GetTemplateKey(x.Type, x.Id, null))
        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

      var templateByTypedName = templateItems
        .Where(x => !string.IsNullOrWhiteSpace(x.Name))
        .GroupBy(x => GetTemplateKey(x.Type, null, x.Name))
        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

      var templateByDisplayName = templateItems
        .Where(x => !string.IsNullOrWhiteSpace(x.Name))
        .GroupBy(x => x.Name!)
        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

      var merged = new List<NamingTemplateItem>();
      foreach (NamingTemplateItem parameter in available)
      {
        NamingTemplateItem? match = null;
        if (!string.IsNullOrWhiteSpace(parameter.Id))
        {
          templateByTypedId.TryGetValue(GetTemplateKey(parameter.Type, parameter.Id, null), out match);
        }

        if (match == null && !string.IsNullOrWhiteSpace(parameter.Name))
        {
          templateByTypedName.TryGetValue(GetTemplateKey(parameter.Type, null, parameter.Name), out match);
        }

        if (match == null && !string.IsNullOrWhiteSpace(parameter.Name))
        {
          templateByDisplayName.TryGetValue(parameter.Name!, out match);
        }

        merged.Add(new NamingTemplateItem
        {
          FontWeight = match?.FontWeight ?? parameter.FontWeight,
          Id = parameter.Id,
          Index = match?.Index ?? parameter.Index,
          Index_Old = match?.Index_Old ?? parameter.Index_Old,
          IsChecked = match?.IsChecked ?? false,
          ParameterInfo = parameter.ParameterInfo,
          Type = parameter.Type ?? match?.Type,
          Name = parameter.Name,
          Prefix = match?.Prefix,
          Suffix = match?.Suffix,
          Separator = match?.Separator ?? parameter.Separator ?? "-"
        });
      }

      return merged
        .OrderByDescending(x => x.IsChecked)
        .ThenBy(x => x.Index <= 0 ? int.MaxValue : x.Index)
        .ThenBy(x => x.Name)
        .ToList();
    }

    private static IEnumerable<NamingTemplateItem> DedupeTemplateItems(IEnumerable<NamingTemplateItem> template)
    {
      return template
        .Where(x => !string.IsNullOrWhiteSpace(x.Name))
        .OrderBy(x => x.Index <= 0 ? int.MaxValue : x.Index)
        .GroupBy(x => GetLogicalParameterKey(x.Type, x.Name!), StringComparer.OrdinalIgnoreCase)
        .Select(x => x.First());
    }

    private static string GetLogicalParameterKey(string? type, string name)
    {
      string resolvedType = string.IsNullOrWhiteSpace(type) ? GetTypeFromDisplayName(name) : type!;
      return $"{resolvedType.Trim()}|{StripTypePrefix(name)}";
    }

    private static bool ShouldPreferParameter(string? currentId, string candidateId)
    {
      if (string.IsNullOrWhiteSpace(currentId))
      {
        return true;
      }

      bool currentBuiltIn = long.TryParse(currentId, out long currentValue) && currentValue < 0;
      bool candidateBuiltIn = long.TryParse(candidateId, out long candidateValue) && candidateValue < 0;
      return !currentBuiltIn && candidateBuiltIn;
    }

    private static string GetTemplateKey(string? type, string? id, string? name)
    {
      string resolvedType = string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(name)
        ? GetTypeFromDisplayName(name!)
        : type ?? string.Empty;
      return $"{resolvedType.Trim()}|{id?.Trim()}|{name?.Trim()}";
    }

    public string BuildName(Document doc, PrintItem item, IEnumerable<NamingTemplateItem> template)
    {
      var selected = template
        .Where(x => x.IsChecked)
        .OrderBy(x => x.Index <= 0 ? int.MaxValue : x.Index)
        .ToList();

      if (selected.Count == 0)
      {
        string defaultName = item.IsSheet ? $"{item.Number}-{item.Name}" : item.Name;
        return FileNameSanitizer.Sanitize(defaultName);
      }

      var parts = new List<string>();
      foreach (NamingTemplateItem token in selected)
      {
        string value = ResolveTokenValue(doc, item, token);
        if (string.IsNullOrWhiteSpace(value))
        {
          continue;
        }

        parts.Add($"{token.Prefix}{value}{token.Suffix}{token.Separator}");
      }

      return FileNameSanitizer.Sanitize(string.Join(string.Empty, parts));
    }

    private static string ResolveTokenValue(Document doc, PrintItem item, NamingTemplateItem token)
    {
      string name = token.Name ?? string.Empty;
      string type = token.Type ?? GetTypeFromDisplayName(name);

      if (type.Equals("Project", StringComparison.OrdinalIgnoreCase))
      {
        Parameter? projectParameter = FindParameter(doc.ProjectInformation, token);
        return ParameterToString(projectParameter);
      }

      Parameter? viewParameter = FindParameter(item.View, token);
      return ParameterToString(viewParameter);
    }

    private static Parameter? FindParameter(Element element, NamingTemplateItem token)
    {
      if (long.TryParse(token.Id, out long idValue))
      {
        if (idValue < 0 && idValue >= int.MinValue)
        {
          Parameter? builtIn = element.get_Parameter((BuiltInParameter)(int)idValue);
          if (builtIn != null)
          {
            return builtIn;
          }
        }

        foreach (Parameter parameter in element.Parameters)
        {
          if (GetParameterId(parameter).Equals(token.Id, StringComparison.OrdinalIgnoreCase))
          {
            return parameter;
          }
        }
      }

      string parameterName = StripTypePrefix(token.Name ?? string.Empty);
      return string.IsNullOrWhiteSpace(parameterName) ? null : element.LookupParameter(parameterName);
    }

    private static string ParameterToString(Parameter? parameter)
    {
      if (parameter == null)
      {
        return string.Empty;
      }

      return parameter.AsString()
        ?? parameter.AsValueString()
        ?? (parameter.StorageType == StorageType.Integer ? parameter.AsInteger().ToString() : null)
        ?? (parameter.StorageType == StorageType.Double ? parameter.AsDouble().ToString("G") : null)
        ?? (parameter.StorageType == StorageType.ElementId ? GetElementIdValue(parameter.AsElementId()) : null)
        ?? string.Empty;
    }

    private static string GetTypeFromDisplayName(string name)
    {
      int separator = name.IndexOf(':');
      return separator > 0 ? name.Substring(0, separator).Trim() : string.Empty;
    }

    private static string StripTypePrefix(string name)
    {
      int separator = name.IndexOf(':');
      return separator >= 0 ? name.Substring(separator + 1).Trim() : name.Trim();
    }

    private static string GetParameterId(Parameter parameter)
    {
#if D2024 || D2025 || D2026 || D2027
      return parameter.Id.Value.ToString();
#else
      return parameter.Id.IntegerValue.ToString();
#endif
    }

    private static string GetElementIdValue(ElementId id)
    {
#if D2024 || D2025 || D2026 || D2027
      return id.Value.ToString();
#else
      return id.IntegerValue.ToString();
#endif
    }
  }
}

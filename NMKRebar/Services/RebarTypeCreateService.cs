using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System.IO;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class RebarTxtRow
  {
    public RebarTxtRow(string typeName, int diameter, int qty = 0)
    {
      TypeName = typeName;
      Diameter = diameter;
      Qty = qty;
    }

    public string TypeName { get; }

    public int Diameter { get; }

    public int Qty { get; }

    public string RebarTypeName => $"{TypeName}_D{Diameter}";
  }

  public static class RebarTxtParser
  {
    public static IReadOnlyList<RebarTxtRow> Load(string path)
    {
      var rows = new List<RebarTxtRow>();
      foreach (string line in VerticalCsvService.ReadAllLinesShared(path).Skip(1))
      {
        if (string.IsNullOrWhiteSpace(line))
        {
          continue;
        }

        string[] parts = line.Split('\t');
        if (parts.Length < 2)
        {
          parts = line.Split(',');
        }

        if (parts.Length < 2)
        {
          continue;
        }

        string typeName = parts[0].Trim();
        string diaRaw = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(typeName)
            || !diaRaw.StartsWith("D", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(diaRaw.Substring(1), out int dia))
        {
          continue;
        }

        int qty = 0;
        if (parts.Length >= 3)
        {
          _ = int.TryParse(parts[2].Trim(), out qty);
        }

        rows.Add(new RebarTxtRow(typeName, dia, qty));
      }

      return rows;
    }

    public const string FileName = "Rebar.txt";

    public static string FindTxt(string folder)
    {
      if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
      {
        throw new InvalidOperationException("Select the folder that contains Rebar.txt.");
      }

      string path = Path.Combine(folder, FileName);
      if (!File.Exists(path))
      {
        throw new InvalidOperationException($"Rebar.txt was not found in:\n{folder}");
      }

      return path;
    }
  }

  public sealed class RebarTypeCreateResult
  {
    public string TxtPath { get; set; } = string.Empty;

    public int RebarTypesCreated { get; set; }

    public int RebarTypesSkipped { get; set; }

    public int ArrayTypesCreated { get; set; }

    public int ArrayTypesSkipped { get; set; }

    public int ArrayParametersSet { get; set; }

    public List<string> Warnings { get; } = new();

    public string ToMessage()
    {
      var text = new StringBuilder();
      text.AppendLine($"Rebar.txt: {TxtPath}");
      text.AppendLine($"RebarBarType created: {RebarTypesCreated}, skipped: {RebarTypesSkipped}");
      text.AppendLine($"NMK_Rebar_Array types created: {ArrayTypesCreated}, existing: {ArrayTypesSkipped}");
      text.AppendLine($"d / Rebar Type set: {ArrayParametersSet}");
      if (Warnings.Count > 0)
      {
        text.AppendLine();
        text.AppendLine("Warnings:");
        foreach (string warning in Warnings.Take(15))
        {
          text.AppendLine("- " + warning);
        }

        if (Warnings.Count > 15)
        {
          text.AppendLine($"... and {Warnings.Count - 15} more");
        }
      }

      return text.ToString();
    }
  }

  public static class RebarTypeCreateService
  {
    public const string ArrayFamilyName = "NMK_Rebar_Array";
    public const string TypeShapeFileName = "TypeShape.csv";
    public const string TypeDataFileName = "TypeData.csv";

    public static RebarTypeCreateResult Create(Document doc, string folder)
    {
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("NMKCreateRebarType runs in a project document.");
      }

      string txtPath = RebarTxtParser.FindTxt(folder);
      IReadOnlyList<RebarTxtRow> rows = RebarTxtParser.Load(txtPath);
      if (rows.Count == 0)
      {
        throw new InvalidOperationException($"No valid rebar rows in:\n{txtPath}");
      }

      var result = new RebarTypeCreateResult { TxtPath = txtPath };
      var existingRebar = new FilteredElementCollector(doc)
        .OfClass(typeof(RebarBarType))
        .Cast<RebarBarType>()
        .ToList();

      Family? arrayFamily = new FilteredElementCollector(doc)
        .OfClass(typeof(Family))
        .Cast<Family>()
        .FirstOrDefault(family => family.Name.Equals(ArrayFamilyName, StringComparison.OrdinalIgnoreCase));

      List<FamilySymbol> arraySymbols = arrayFamily == null
        ? new List<FamilySymbol>()
        : arrayFamily.GetFamilySymbolIds()
          .Select(id => doc.GetElement(id))
          .OfType<FamilySymbol>()
          .ToList();

      FamilySymbol? arrayTemplate = arraySymbols.FirstOrDefault();

      using (var tx = new Transaction(doc, "NMK Create Rebar Types"))
      {
        tx.Start();

        foreach (RebarTxtRow row in rows)
        {
          if (existingRebar.Any(type => type.Name.Equals(row.RebarTypeName, StringComparison.OrdinalIgnoreCase)))
          {
            result.RebarTypesSkipped++;
          }
          else
          {
            string templateName = "CSS" + row.Diameter;
            RebarBarType? template = existingRebar.FirstOrDefault(type =>
              type.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
              result.Warnings.Add($"[{row.RebarTypeName}] missing template '{templateName}'");
            }
            else
            {
              try
              {
                if (template.Duplicate(row.RebarTypeName) is RebarBarType created)
                {
                  existingRebar.Add(created);
                  result.RebarTypesCreated++;
                }
              }
              catch (Exception ex)
              {
                result.Warnings.Add($"[{row.RebarTypeName}] {ex.Message}");
              }
            }
          }

          if (arrayFamily == null || arrayTemplate == null)
          {
            continue;
          }

          FamilySymbol? arraySymbol = arraySymbols.FirstOrDefault(symbol =>
            symbol.Name.Equals(row.RebarTypeName, StringComparison.OrdinalIgnoreCase));
          if (arraySymbol == null)
          {
            try
            {
              if (arrayTemplate.Duplicate(row.RebarTypeName) is FamilySymbol createdSymbol)
              {
                arraySymbol = createdSymbol;
                arraySymbols.Add(createdSymbol);
                result.ArrayTypesCreated++;
              }
            }
            catch (Exception ex)
            {
              result.Warnings.Add($"[{ArrayFamilyName}/{row.RebarTypeName}] {ex.Message}");
            }
          }
          else
          {
            result.ArrayTypesSkipped++;
          }

          if (arraySymbol != null)
          {
            result.ArrayParametersSet += SetArrayDAndRebarType(arraySymbol, row, existingRebar, result);
          }
        }

        tx.Commit();
      }

      if (arrayFamily == null)
      {
        result.Warnings.Add($"Family '{ArrayFamilyName}' is not loaded in this project.");
      }

      return result;
    }

    private static int SetArrayDAndRebarType(
      FamilySymbol symbol,
      RebarTxtRow row,
      List<RebarBarType> rebarTypes,
      RebarTypeCreateResult result)
    {
      if (!symbol.IsActive)
      {
        symbol.Activate();
      }

      int set = 0;
      Parameter? diameter = symbol.LookupParameter("d") ?? symbol.LookupParameter("D");
      if (diameter == null)
      {
        result.Warnings.Add($"[{symbol.Name}] missing parameter 'd'.");
      }
      else if (CsvValueConverter.TrySetParameter(
        diameter,
        row.Diameter.ToString(System.Globalization.CultureInfo.InvariantCulture),
        result.Warnings))
      {
        set++;
      }

      Parameter? rebarType = symbol.LookupParameter("Rebar Type");
      if (rebarType == null)
      {
        result.Warnings.Add($"[{symbol.Name}] missing parameter 'Rebar Type'.");
        return set;
      }

      if (rebarType.StorageType == StorageType.ElementId)
      {
        RebarBarType? barType = rebarTypes.FirstOrDefault(type =>
          type.Name.Equals(row.RebarTypeName, StringComparison.OrdinalIgnoreCase));
        if (barType == null)
        {
          result.Warnings.Add($"[{symbol.Name}] RebarBarType '{row.RebarTypeName}' was not found.");
          return set;
        }

        try
        {
          if (rebarType.Set(barType.Id))
          {
            set++;
          }
        }
        catch (Exception ex)
        {
          result.Warnings.Add($"[{symbol.Name}] Rebar Type: {ex.Message}");
        }

        return set;
      }

      if (CsvValueConverter.TrySetParameter(rebarType, row.RebarTypeName, result.Warnings))
      {
        set++;
      }

      return set;
    }

    public static void EnsureCsvTemplates(string folder)
    {
      string addinFolder = Path.GetDirectoryName(typeof(RebarTypeCreateService).Assembly.Location) ?? string.Empty;
      CopyIfMissing(Path.Combine(addinFolder, TypeShapeFileName), Path.Combine(folder, TypeShapeFileName));
      CopyIfMissing(Path.Combine(addinFolder, TypeDataFileName), Path.Combine(folder, TypeDataFileName));
    }

    private static void CopyIfMissing(string source, string target)
    {
      if (File.Exists(target) || !File.Exists(source))
      {
        return;
      }

      File.Copy(source, target);
    }
  }
}

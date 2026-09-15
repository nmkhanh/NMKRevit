using System.Globalization;
using System.IO;
using System.Text;

namespace NMKRebar.Services
{
  public sealed class TypeShapeRow
  {
    public TypeShapeRow(string typeName, IReadOnlyDictionary<string, string> values)
    {
      TypeName = typeName;
      Values = values;
    }

    public string TypeName { get; }

    public IReadOnlyDictionary<string, string> Values { get; }
  }

  public sealed class TypeShapeTable
  {
    public TypeShapeTable(IReadOnlyList<string> parameterNames, IReadOnlyList<TypeShapeRow> rows)
    {
      ParameterNames = parameterNames;
      Rows = rows;
    }

    public IReadOnlyList<string> ParameterNames { get; }

    public IReadOnlyList<TypeShapeRow> Rows { get; }
  }

  public sealed class VerticalCsvTable
  {
    public VerticalCsvTable(IReadOnlyList<string> parameterNames, IReadOnlyList<string> typeNames, IReadOnlyList<IReadOnlyList<string>> valueRows)
    {
      ParameterNames = parameterNames;
      TypeNames = typeNames;
      ValueRows = valueRows;
    }

    public IReadOnlyList<string> ParameterNames { get; }

    public IReadOnlyList<string> TypeNames { get; }

    public IReadOnlyList<IReadOnlyList<string>> ValueRows { get; }

    public TypeShapeTable ToTypeShapeTable()
    {
      var rows = new List<TypeShapeRow>();
      for (int t = 0; t < TypeNames.Count; t++)
      {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int p = 0; p < ParameterNames.Count; p++)
        {
          IReadOnlyList<string> row = ValueRows[p];
          values[ParameterNames[p]] = t < row.Count ? row[t] : string.Empty;
        }

        rows.Add(new TypeShapeRow(TypeNames[t], values));
      }

      return new TypeShapeTable(ParameterNames, rows);
    }
  }

  public static class VerticalCsvService
  {
    public const string RebarTypeRowName = "Rebar Type";

    public static VerticalCsvTable Load(string path)
    {
      string[] lines = ReadAllLinesShared(path, Encoding.UTF8)
        .Select(line => line.TrimEnd())
        .Where(line => line.Length > 0)
        .ToArray();

      if (lines.Length < 2)
      {
        throw new InvalidOperationException($"{Path.GetFileName(path)} needs a type header row and parameter rows.");
      }

      IReadOnlyList<string> header = ParseCsvLine(lines[0]);
      var typeNames = header.Skip(1).Select(name => name.Trim()).Where(name => name.Length > 0).ToList();

      var parameterNames = new List<string>();
      var valueRows = new List<IReadOnlyList<string>>();

      for (int i = 1; i < lines.Length; i++)
      {
        IReadOnlyList<string> cells = ParseCsvLine(lines[i]);
        if (cells.Count == 0 || string.IsNullOrWhiteSpace(cells[0]))
        {
          continue;
        }

        parameterNames.Add(cells[0].Trim());
        var values = new List<string>(typeNames.Count);
        for (int t = 0; t < typeNames.Count; t++)
        {
          int cellIndex = t + 1;
          values.Add(cellIndex < cells.Count ? cells[cellIndex].Trim() : string.Empty);
        }

        valueRows.Add(values);
      }

      if (parameterNames.Count == 0)
      {
        throw new InvalidOperationException($"{Path.GetFileName(path)} has no parameter rows.");
      }

      return new VerticalCsvTable(parameterNames, typeNames, valueRows);
    }

    public static VerticalCsvTable LoadTypeShape(string path)
    {
      return EnsureRebarTypeFirstRow(Load(path));
    }

    public static void SaveTypeShape(string path, VerticalCsvTable table)
    {
      VerticalCsvTable shape = EnsureRebarTypeFirstRow(table);
      var lines = new List<string>
      {
        ToCsvLine(new[] { RebarTypeRowName }.Concat(shape.TypeNames))
      };

      for (int p = 0; p < shape.ParameterNames.Count; p++)
      {
        if (shape.ParameterNames[p].Equals(RebarTypeRowName, StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        IReadOnlyList<string> values = p < shape.ValueRows.Count ? shape.ValueRows[p] : Array.Empty<string>();
        var cells = new List<string> { shape.ParameterNames[p] };
        for (int t = 0; t < shape.TypeNames.Count; t++)
        {
          cells.Add(t < values.Count ? values[t] : string.Empty);
        }

        lines.Add(ToCsvLine(cells));
      }

      File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    public static void Save(string path, VerticalCsvTable table)
    {
      var lines = new List<string>
      {
        ToCsvLine(new[] { string.Empty }.Concat(table.TypeNames))
      };

      for (int p = 0; p < table.ParameterNames.Count; p++)
      {
        IReadOnlyList<string> values = p < table.ValueRows.Count ? table.ValueRows[p] : Array.Empty<string>();
        var cells = new List<string> { table.ParameterNames[p] };
        for (int t = 0; t < table.TypeNames.Count; t++)
        {
          cells.Add(t < values.Count ? values[t] : string.Empty);
        }

        lines.Add(ToCsvLine(cells));
      }

      File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    public static VerticalCsvTable EnsureTypeColumns(VerticalCsvTable table, IEnumerable<string> typeNames)
    {
      var names = table.TypeNames.ToList();
      var rows = table.ValueRows.Select(row => row.ToList()).ToList();
      var toInsert = typeNames
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Where(name => !names.Any(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase)))
        .ToList();
      if (toInsert.Count == 0)
      {
        return table;
      }

      names.InsertRange(0, toInsert);
      foreach (List<string> row in rows)
      {
        row.InsertRange(0, Enumerable.Repeat(string.Empty, toInsert.Count));
      }

      return new VerticalCsvTable(table.ParameterNames, names, rows.Select(row => (IReadOnlyList<string>)row).ToList());
    }

    public static int IndexOfType(VerticalCsvTable table, string typeName)
    {
      for (int i = 0; i < table.TypeNames.Count; i++)
      {
        if (table.TypeNames[i].Equals(typeName, StringComparison.OrdinalIgnoreCase))
        {
          return i;
        }
      }

      return -1;
    }

    public static int IndexOfParameter(VerticalCsvTable table, string parameterName)
    {
      for (int i = 0; i < table.ParameterNames.Count; i++)
      {
        if (table.ParameterNames[i].Equals(parameterName, StringComparison.OrdinalIgnoreCase))
        {
          return i;
        }
      }

      return -1;
    }

    public static VerticalCsvTable SetCell(VerticalCsvTable table, string parameterName, string typeName, string value)
    {
      int typeIndex = IndexOfType(table, typeName);
      int paramIndex = IndexOfParameter(table, parameterName);
      if (typeIndex < 0 || paramIndex < 0)
      {
        return table;
      }

      var rows = table.ValueRows.Select(row => row.ToList()).ToList();
      List<string> row = rows[paramIndex];
      while (row.Count <= typeIndex)
      {
        row.Add(string.Empty);
      }

      row[typeIndex] = value ?? string.Empty;
      return new VerticalCsvTable(
        table.ParameterNames,
        table.TypeNames,
        rows.Select(r => (IReadOnlyList<string>)r).ToList());
    }

    public static VerticalCsvTable CopyTypeColumn(VerticalCsvTable target, VerticalCsvTable source, string typeName)
    {
      int sourceIndex = IndexOfType(source, typeName);
      if (sourceIndex < 0)
      {
        return target;
      }

      VerticalCsvTable result = target;
      for (int p = 0; p < source.ParameterNames.Count; p++)
      {
        string parameterName = source.ParameterNames[p];
        if (IndexOfParameter(result, parameterName) < 0)
        {
          continue;
        }

        IReadOnlyList<string> row = source.ValueRows[p];
        string value = sourceIndex < row.Count ? row[sourceIndex] : string.Empty;
        result = SetCell(result, parameterName, typeName, value);
      }

      return result;
    }

    public static VerticalCsvTable EnsureRebarTypeFirstRow(VerticalCsvTable table)
    {
      var names = table.ParameterNames.ToList();
      var rows = table.ValueRows.Select(row => row.ToList()).ToList();
      int existing = names.FindIndex(name => name.Equals(RebarTypeRowName, StringComparison.OrdinalIgnoreCase));
      List<string> values;
      if (existing >= 0)
      {
        values = rows[existing];
        names.RemoveAt(existing);
        rows.RemoveAt(existing);
      }
      else
      {
        values = Enumerable.Repeat(string.Empty, table.TypeNames.Count).ToList();
      }

      while (values.Count < table.TypeNames.Count)
      {
        values.Add(string.Empty);
      }

      for (int t = 0; t < table.TypeNames.Count; t++)
      {
        if (string.IsNullOrWhiteSpace(values[t]))
        {
          values[t] = table.TypeNames[t];
        }
      }

      names.Insert(0, RebarTypeRowName);
      rows.Insert(0, values);
      return new VerticalCsvTable(
        names,
        table.TypeNames,
        rows.Select(row => (IReadOnlyList<string>)row).ToList());
    }

    public static VerticalCsvTable WithCumulativeZ(VerticalCsvTable table)
    {
      var zRows = new SortedDictionary<int, int>();
      for (int p = 0; p < table.ParameterNames.Count; p++)
      {
        if (TryParseZIndex(table.ParameterNames[p], out int n))
        {
          zRows[n] = p;
        }
      }

      if (zRows.Count == 0)
      {
        return table;
      }

      var rows = table.ValueRows.Select(row => row.ToList()).ToList();
      for (int t = 0; t < table.TypeNames.Count; t++)
      {
        double sum = 0;
        foreach (KeyValuePair<int, int> pair in zRows)
        {
          List<string> row = rows[pair.Value];
          string raw = t < row.Count ? row[t] : string.Empty;
          if (!TryParseNumber(raw, out double delta))
          {
            continue;
          }

          while (row.Count <= t)
          {
            row.Add(string.Empty);
          }

          if (delta > 0)
          {
            sum = delta;
            row[t] = delta.ToString(CultureInfo.InvariantCulture);
            continue;
          }

          sum += delta;
          row[t] = sum.ToString(CultureInfo.InvariantCulture);
        }
      }

      return new VerticalCsvTable(
        table.ParameterNames,
        table.TypeNames,
        rows.Select(row => (IReadOnlyList<string>)row).ToList());
    }

    public static bool TryParseZIndex(string parameterName, out int index)
    {
      index = 0;
      return parameterName.StartsWith("Z_", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(parameterName.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
        && index > 0;
    }

    public static string ToCsvLine(IEnumerable<string> cells)
    {
      return string.Join(",", cells.Select(Escape));
    }

    public static IReadOnlyList<string> ParseCsvLine(string line)
    {
      var cells = new List<string>();
      var current = new StringBuilder();
      bool inQuotes = false;

      for (int i = 0; i < line.Length; i++)
      {
        char ch = line[i];
        if (ch == '"')
        {
          if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
          {
            current.Append('"');
            i++;
          }
          else
          {
            inQuotes = !inQuotes;
          }
        }
        else if (ch == ',' && !inQuotes)
        {
          cells.Add(current.ToString());
          current.Clear();
        }
        else
        {
          current.Append(ch);
        }
      }

      cells.Add(current.ToString());
      return cells;
    }

    public static bool TryParseYesNo(string text, out int value)
    {
      value = 0;
      if (string.IsNullOrWhiteSpace(text))
      {
        return false;
      }

      string normalized = text.Trim();
      if (normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
          || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
          || normalized == "1")
      {
        value = 1;
        return true;
      }

      if (normalized.Equals("no", StringComparison.OrdinalIgnoreCase)
          || normalized.Equals("false", StringComparison.OrdinalIgnoreCase)
          || normalized == "0")
      {
        value = 0;
        return true;
      }

      return false;
    }

    public static bool TryParseNumber(string text, out double value)
    {
      value = 0;
      if (string.IsNullOrWhiteSpace(text))
      {
        return false;
      }

      string normalized = text.Trim()
        .Replace("°", " ")
        .Replace("'", " ")
        .Replace("\"", " ");

      string[] parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length > 0
          && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
      {
        return true;
      }

      return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    public static string[] ReadAllLinesShared(string path, Encoding? encoding = null)
    {
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      using var reader = encoding == null
        ? new StreamReader(stream, detectEncodingFromByteOrderMarks: true)
        : new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
      var lines = new List<string>();
      while (reader.ReadLine() is string line)
      {
        lines.Add(line);
      }

      return lines.ToArray();
    }

    private static string Escape(string value)
    {
      if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
      {
        return value;
      }

      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}

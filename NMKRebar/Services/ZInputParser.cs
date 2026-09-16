using Autodesk.Revit.DB;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NMKRebar.Services
{
  public static class ZInputParser
  {
    public static IReadOnlyList<string> EncodeSpacingsForDisplay(IReadOnlyList<double> spacingsMm, int rowCount)
    {
      var rows = new string[rowCount];
      int index = 0;
      int i = 0;
      while (i < spacingsMm.Count && index < rowCount)
      {
        int j = i + 1;
        while (j < spacingsMm.Count && NearlyEqual(spacingsMm[j], spacingsMm[i]))
        {
          j++;
        }

        int run = j - i;
        rows[index] = run >= 2
          ? $"{run}x{FormatSpacing(spacingsMm[i])}"
          : FormatSpacing(spacingsMm[i]);
        index++;
        i = j;
      }

      return rows;
    }

    public static IReadOnlyList<double> ReadSpacingsFromInstance(Element instance, int maxCount, List<string> warnings)
    {
      var cumulativeMm = new List<double>();
      for (int n = 1; n <= maxCount; n++)
      {
        Parameter? parameter = SetTypeEditorService.FindZ(instance, n);
        if (parameter == null || !parameter.HasValue)
        {
          break;
        }

        if (!TryReadLengthMm(parameter, out double mm))
        {
          warnings.Add($"Z_{n}: could not read length.");
          break;
        }

        if (mm > 0)
        {
          break;
        }

        cumulativeMm.Add(mm);
      }

      return SpacingsFromCumulativeMm(cumulativeMm);
    }

    /// <summary>
    /// Z_n on instance are cumulative (≤ 0). First spacing is |Z_1|; each next is |Z_n − Z_{n−1}|.
    /// </summary>
    public static IReadOnlyList<double> SpacingsFromCumulativeMm(IReadOnlyList<double> cumulativeMm)
    {
      var spacings = new List<double>();
      for (int i = 0; i < cumulativeMm.Count; i++)
      {
        double spacing = i == 0
          ? Math.Abs(RoundMm(cumulativeMm[i]))
          : Math.Abs(RoundMm(cumulativeMm[i] - cumulativeMm[i - 1]));
        if (spacing > 0 || i == 0)
        {
          spacings.Add(spacing);
        }
      }

      return spacings;
    }

    public static IReadOnlyList<string> LoadZTextRowsFromInstance(Element instance, int rowCount, List<string> warnings)
    {
      IReadOnlyList<double> spacings = ReadSpacingsFromInstance(instance, rowCount, warnings);
      return EncodeSpacingsForDisplay(spacings, rowCount);
    }

    private static bool TryReadLengthMm(Parameter parameter, out double mm)
    {
      mm = 0;
      string display = parameter.AsValueString() ?? string.Empty;
      if (VerticalCsvService.TryParseNumber(display, out mm))
      {
        mm = RoundMm(mm);
        return true;
      }

      if (parameter.StorageType == StorageType.Double)
      {
        mm = RoundMm(CsvValueConverter.FromInternalValue(parameter, parameter.AsDouble()));
        return true;
      }

      return false;
    }

    private static double RoundMm(double value) => Math.Round(value, 3);

    private static string FormatSpacing(double value)
    {
      return RoundMm(value).ToString(CultureInfo.InvariantCulture);
    }

    private static bool NearlyEqual(double a, double b)
    {
      return Math.Abs(a - b) < 0.001;
    }
    private static readonly Regex Repeat = new(
      @"^\s*(\d+)\s*[xX×]\s*(.+)$",
      RegexOptions.CultureInvariant);

    public static Dictionary<int, double> ParseExpanded(IReadOnlyList<string> texts, int count, List<string> warnings)
    {
      double?[] values = new double?[count];
      int index = 0;
      for (int row = 0; row < count; row++)
      {
        string raw = row < texts.Count ? (texts[row] ?? string.Empty).Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
          continue;
        }

        if (TryParseRepeat(raw, out int repeat, out double spacing))
        {
          if (repeat <= 0)
          {
            warnings.Add($"Row {row + 1}: repeat count must be > 0 ({raw}).");
            continue;
          }

          for (int k = 0; k < repeat && index < count; k++)
          {
            values[index++] = spacing;
          }

          continue;
        }

        if (VerticalCsvService.TryParseNumber(raw, out double value))
        {
          if (index < count)
          {
            values[index++] = value;
          }

          continue;
        }

        warnings.Add($"Row {row + 1}: could not parse '{raw}'.");
      }

      var parsed = new Dictionary<int, double>();
      for (int i = 0; i < count; i++)
      {
        if (values[i] is double number)
        {
          parsed[i + 1] = number;
        }
      }

      return parsed;
    }

    public static List<double> ExpandInOrder(IReadOnlyList<string> texts, int count)
    {
      Dictionary<int, double> parsed = ParseExpanded(texts, count, new List<string>());
      var list = new List<double>();
      for (int n = 1; n <= count; n++)
      {
        if (!parsed.TryGetValue(n, out double value))
        {
          break;
        }

        list.Add(value);
      }

      return list;
    }

    public static Dictionary<int, double> ParseCumulative(IReadOnlyList<string> texts, int count, List<string> warnings)
    {
      Dictionary<int, double> deltas = ParseExpanded(texts, count, warnings);
      double sum = 0;
      var values = new Dictionary<int, double>();
      for (int n = 1; n <= count; n++)
      {
        if (!deltas.TryGetValue(n, out double delta))
        {
          continue;
        }

        sum += -Math.Abs(delta);
        values[n] = sum;
      }

      return values;
    }

    public static bool TryParseRepeat(string raw, out int count, out double spacing)
    {
      count = 0;
      spacing = 0;
      Match match = Repeat.Match(raw);
      if (!match.Success)
      {
        return false;
      }

      if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
      {
        return false;
      }

      return VerticalCsvService.TryParseNumber(match.Groups[2].Value, out spacing);
    }
  }
}

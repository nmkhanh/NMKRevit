using System.Globalization;

namespace NMKRebar.Services
{
  public static class AngleDms
  {
    public static void Split(string decimalDegrees, out string degrees, out string minutes, out string seconds)
    {
      degrees = string.Empty;
      minutes = string.Empty;
      seconds = string.Empty;
      if (string.IsNullOrWhiteSpace(decimalDegrees) || !TryParseDegrees(decimalDegrees, out double value))
      {
        return;
      }

      int sign = value < 0 ? -1 : 1;
      double abs = Math.Abs(value);
      int d = (int)Math.Floor(abs + 1e-12);
      double minutePart = (abs - d) * 60;
      int m = (int)Math.Floor(minutePart + 1e-12);
      double s = (minutePart - m) * 60;
      s = Math.Round(s, 3, MidpointRounding.AwayFromZero);
      if (s >= 60 - 1e-9)
      {
        s = 0;
        m++;
      }

      if (m >= 60)
      {
        m = 0;
        d++;
      }

      degrees = (sign * d).ToString(CultureInfo.InvariantCulture);
      minutes = m.ToString(CultureInfo.InvariantCulture);
      seconds = FormatSeconds(s);
    }

    public static bool TryCombine(string degreesText, string minutesText, string secondsText, out string decimalDegrees)
    {
      decimalDegrees = string.Empty;
      bool empty =
        string.IsNullOrWhiteSpace(degreesText)
        && string.IsNullOrWhiteSpace(minutesText)
        && string.IsNullOrWhiteSpace(secondsText);
      if (empty)
      {
        return true;
      }

      if (!TryNumberOrZero(degreesText, out double degrees)
          || !TryNumberOrZero(minutesText, out double minutes)
          || !TryNumberOrZero(secondsText, out double seconds))
      {
        return false;
      }

      int sign = degrees < 0 || (degrees == 0 && (minutes < 0 || seconds < 0)) ? -1 : 1;
      double value = Math.Abs(degrees) + Math.Abs(minutes) / 60 + Math.Abs(seconds) / 3600;
      value *= sign;
      decimalDegrees = Math.Abs(value - Math.Round(value, 3)) < 1e-9
        ? Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture)
        : value.ToString("0.######", CultureInfo.InvariantCulture);
      return true;
    }

    private static bool TryParseDegrees(string text, out double value)
    {
      value = 0;
      string normalized = text.Trim()
        .Replace("°", " ")
        .Replace("'", " ")
        .Replace("\"", " ")
        .Replace(",", ".");
      string[] parts = normalized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length >= 3
          && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
          && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m)
          && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
      {
        int sign = d < 0 ? -1 : 1;
        value = sign * (Math.Abs(d) + Math.Abs(m) / 60 + Math.Abs(s) / 3600);
        return true;
      }

      return VerticalCsvService.TryParseNumber(text, out value);
    }

    private static bool TryNumberOrZero(string text, out double value)
    {
      if (string.IsNullOrWhiteSpace(text))
      {
        value = 0;
        return true;
      }

      return VerticalCsvService.TryParseNumber(text, out value);
    }

    private static string FormatSeconds(double seconds)
    {
      if (Math.Abs(seconds - Math.Round(seconds)) < 1e-9)
      {
        return Math.Round(seconds).ToString(CultureInfo.InvariantCulture);
      }

      return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
  }
}

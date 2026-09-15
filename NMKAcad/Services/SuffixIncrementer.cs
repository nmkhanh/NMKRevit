using System.Text;

namespace NMKAcad.Services
{
  internal static class SuffixIncrementer
  {
    public static string Next(string? suffix)
    {
      string value = suffix ?? string.Empty;
      if (string.IsNullOrWhiteSpace(value))
      {
        return "1";
      }

      if (IsDigits(value))
      {
        return IncrementDigits(value);
      }

      if (IsLatinLetters(value))
      {
        return IncrementLetters(value);
      }

      int last = value.Length - 1;
      int digitStart = last;
      while (digitStart >= 0 && char.IsDigit(value[digitStart]))
      {
        digitStart--;
      }

      if (digitStart < last)
      {
        return value.Substring(0, digitStart + 1) + IncrementDigits(value.Substring(digitStart + 1));
      }

      int letterStart = last;
      while (letterStart >= 0 && IsLatinLetter(value[letterStart]))
      {
        letterStart--;
      }

      if (letterStart < last)
      {
        return value.Substring(0, letterStart + 1) + IncrementLetters(value.Substring(letterStart + 1));
      }

      return value + "1";
    }

    private static bool IsDigits(string value)
    {
      for (int i = 0; i < value.Length; i++)
      {
        if (!char.IsDigit(value[i]))
        {
          return false;
        }
      }

      return value.Length > 0;
    }

    private static bool IsLatinLetter(char c)
    {
      return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }

    private static bool IsLatinLetters(string value)
    {
      for (int i = 0; i < value.Length; i++)
      {
        if (!IsLatinLetter(value[i]))
        {
          return false;
        }
      }

      return value.Length > 0;
    }

    private static string IncrementDigits(string digits)
    {
      if (!long.TryParse(digits, out long number))
      {
        return digits;
      }

      number++;
      return number.ToString().PadLeft(digits.Length, '0');
    }

    private static string IncrementLetters(string letters)
    {
      var chars = letters.ToCharArray();
      for (int i = chars.Length - 1; i >= 0; i--)
      {
        char original = chars[i];
        bool lower = char.IsLower(original);
        char upper = char.ToUpperInvariant(original);
        if (upper < 'Z')
        {
          char next = (char)(upper + 1);
          chars[i] = lower ? char.ToLowerInvariant(next) : next;
          return new string(chars);
        }

        chars[i] = lower ? 'a' : 'A';
      }

      var text = new StringBuilder(chars.Length + 1);
      bool firstLower = char.IsLower(letters[0]);
      text.Append(firstLower ? 'a' : 'A');
      text.Append(chars);
      return text.ToString();
    }
  }
}

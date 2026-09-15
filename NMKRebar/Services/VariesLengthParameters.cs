namespace NMKRebar.Services
{
  public static class VariesLengthParameters
  {
    public static readonly string[] Names =
    {
      "A", "B", "C", "D", "E", "F", "G", "H", "I"
    };

    public static bool IsValid(string? parameterName)
    {
      string name = (parameterName ?? string.Empty).Trim();
      return name.Length > 0
        && Names.Any(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string? parameterName)
    {
      string name = (parameterName ?? string.Empty).Trim();
      if (name.Length == 0)
      {
        return "A";
      }

      return Names.FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? "A";
    }
  }
}

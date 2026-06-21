using System.IO;

namespace NMKRevit.NMKPrint.Services
{
  public static class FileNameSanitizer
  {
    public static string Sanitize(string value)
    {
      string result = string.IsNullOrWhiteSpace(value) ? "Untitled" : value;
      foreach (char invalid in Path.GetInvalidFileNameChars())
      {
        result = result.Replace(invalid, '_');
      }

      return result.Trim().TrimEnd('.', ' ');
    }
  }
}

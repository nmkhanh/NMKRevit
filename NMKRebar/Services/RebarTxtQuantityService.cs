using System.IO;

namespace NMKRebar.Services
{
  public static class RebarTxtQuantityService
  {
    public static IReadOnlyList<string> EnumerateTxtFiles(string rootFolder)
    {
      var paths = new List<string>();
      if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
      {
        return paths;
      }

      foreach (string path in Directory.EnumerateFiles(rootFolder, RebarTxtParser.FileName, SearchOption.AllDirectories))
      {
        paths.Add(path);
      }

      return paths;
    }

    public static Dictionary<string, int> LoadExpectedQuantities(string rootFolder)
    {
      var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (string path in EnumerateTxtFiles(rootFolder))
      {
        foreach (RebarTxtRow row in RebarTxtParser.Load(path))
        {
          if (row.Qty <= 0)
          {
            continue;
          }

          totals.TryGetValue(row.RebarTypeName, out int sum);
          totals[row.RebarTypeName] = sum + row.Qty;
        }
      }

      return totals;
    }
  }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace NMKAcad.Services
{
  public sealed class CsvOptions
  {
    public List<string> Prefixes { get; } = new List<string>();
    public List<string> Mains { get; } = new List<string>();
    public List<string> Suffixes { get; } = new List<string>();
  }

  public static class CsvOptionsService
  {
    public const string ExplicitCsvPath = @"D:\MCP\NMKRevit\NMKAcad\wblock_options.csv";

    public static string GetCsvPath()
    {
      if (File.Exists(ExplicitCsvPath))
      {
        return ExplicitCsvPath;
      }

      string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
      if (!string.IsNullOrEmpty(assemblyDir))
      {
        string localPath = Path.Combine(assemblyDir, "wblock_options.csv");
        if (File.Exists(localPath))
        {
          return localPath;
        }

        string projPath = Path.GetFullPath(Path.Combine(assemblyDir, @"..\..\wblock_options.csv"));
        if (File.Exists(projPath))
        {
          return projPath;
        }
      }

      return ExplicitCsvPath;
    }

    public static CsvOptions LoadOptions(string? path = null)
    {
      string csvPath = string.IsNullOrWhiteSpace(path) ? GetCsvPath() : path!;
      var options = new CsvOptions();

      if (!File.Exists(csvPath))
      {
        CreateDefaultCsv(csvPath);
      }

      using var stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      using var reader = new StreamReader(stream, Encoding.UTF8);

      bool isFirstLine = true;
      while (!reader.EndOfStream)
      {
        string? line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
          continue;
        }

        char delimiter = line.Contains(";") && !line.Contains(",") ? ';' : ',';
        string[] cols = ParseCsvLine(line, delimiter);

        if (isFirstLine)
        {
          isFirstLine = false;
          // Skip header row if it contains column labels
          if (cols.Length > 0 && cols[0].Trim().Equals("Prefix", StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }
        }

        if (cols.Length > 0 && !string.IsNullOrWhiteSpace(cols[0]))
        {
          string val = cols[0].Trim();
          if (!options.Prefixes.Contains(val))
          {
            options.Prefixes.Add(val);
          }
        }

        if (cols.Length > 1 && !string.IsNullOrWhiteSpace(cols[1]))
        {
          string val = cols[1].Trim();
          if (!options.Mains.Contains(val))
          {
            options.Mains.Add(val);
          }
        }

        if (cols.Length > 2 && !string.IsNullOrWhiteSpace(cols[2]))
        {
          string val = cols[2].Trim();
          if (!options.Suffixes.Contains(val))
          {
            options.Suffixes.Add(val);
          }
        }
      }

      return options;
    }

    public static void CreateDefaultCsv(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        path = ExplicitCsvPath;
      }

      string? dir = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
      {
        Directory.CreateDirectory(dir);
      }

      const string content = "Prefix,Main,Suffix\r\n"
        + "BEAM_,B1,_01\r\n"
        + "COL_,B2,_02\r\n"
        + "SLAB_,B3,_03\r\n"
        + "WALL_,C1,_04\r\n"
        + "FOOTING_,C2,_05\r\n"
        + "STAIR_,C3,_A\r\n"
        + "RAMP_,S1,_B\r\n"
        + "SECTION_,W1,_C\r\n"
        + "PLAN_,F1,_TOP\r\n"
        + "DETAIL_,D1,_BOT\r\n";

      File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static string[] ParseCsvLine(string line, char delimiter)
    {
      var result = new List<string>();
      var current = new StringBuilder();
      bool inQuotes = false;

      for (int i = 0; i < line.Length; i++)
      {
        char c = line[i];
        if (c == '\"')
        {
          if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
          {
            current.Append('\"');
            i++;
          }
          else
          {
            inQuotes = !inQuotes;
          }
        }
        else if (c == delimiter && !inQuotes)
        {
          result.Add(current.ToString());
          current.Clear();
        }
        else
        {
          current.Append(c);
        }
      }

      result.Add(current.ToString());
      return result.ToArray();
    }
  }
}

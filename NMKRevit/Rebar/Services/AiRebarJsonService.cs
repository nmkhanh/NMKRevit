using NMKRevit.Rebar.Models;
using Newtonsoft.Json;
using System;
using System.IO;

namespace NMKRevit.Rebar.Services
{
  public sealed class AiRebarJsonService
  {
    public AiRebarConfig Load(string path)
    {
      if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
      {
        throw new InvalidOperationException("Chua chon file JSON hop le.");
      }

      var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
      AiRebarConfig? config = JsonConvert.DeserializeObject<AiRebarConfig>(File.ReadAllText(path), settings);
      if (config == null)
      {
        throw new InvalidOperationException("JSON rong.");
      }
      if (config.SchemaVersion != 1)
      {
        throw new InvalidOperationException("Chi ho tro schemaVersion = 1.");
      }
      if (!config.Units.Equals("mm", StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException("JSON phai dung units = mm.");
      }
      if (config.Bars.Count == 0)
      {
        throw new InvalidOperationException("JSON phai co bars.");
      }

      foreach (AiRebarBarConfig bar in config.Bars)
      {
        if (string.IsNullOrWhiteSpace(bar.Id))
        {
          throw new InvalidOperationException("Moi bar can id.");
        }
        if (bar.DiameterMm <= 0)
        {
          throw new InvalidOperationException($"{bar.Id}: diameterMm phai > 0.");
        }
        if (bar.Shape.Points.Count < 2)
        {
          throw new InvalidOperationException($"{bar.Id}: shape.points can it nhat 2 diem.");
        }
      }

      return config;
    }
  }
}

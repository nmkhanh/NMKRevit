using Autodesk.Revit.DB;
using System.Globalization;
using System.IO;
using System.Text;

namespace NMKRebar.Services
{
  public enum CsvCoordinateUnit
  {
    Meters,
    Millimeters,
    Feet
  }

  public sealed class CsvPointData
  {
    public string Axis { get; set; } = string.Empty;
    public string PointName { get; set; } = string.Empty;
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }

    public string LineStyleName => $"{Axis}-{PointName}";

    public bool IsComplete => X.HasValue && Y.HasValue && Z.HasValue;
  }

  public sealed class CsvProfileTable
  {
    public List<string> AxisOrder { get; } = new();
    public List<string> ProfileNames { get; } = new();
    public Dictionary<(string Axis, string ProfileName), CsvPointData> PointsMap { get; } = new();
    public List<CsvPointData> Points => PointsMap.Values.ToList();
  }

  public sealed class ModelLinesFromCsvResult
  {
    public int TotalPointsRead { get; set; }
    public int CreatedLinesCount { get; set; }
    public List<string> CreatedLineStyles { get; } = new();
    public List<ElementId> CreatedModelLineIds { get; } = new();
    public List<string> Warnings { get; } = new();

    public string GetSummaryMessage()
    {
      var sb = new StringBuilder();
      sb.AppendLine($"Tổng số điểm đọc được: {TotalPointsRead}");
      sb.AppendLine($"Số Model Line đã tạo: {CreatedLinesCount}");
      if (CreatedLineStyles.Count > 0)
      {
        sb.AppendLine($"\nCác Line Style đã áp dụng ({CreatedLineStyles.Count}):");
        foreach (string style in CreatedLineStyles.Take(15))
        {
          sb.AppendLine($"  • {style}");
        }
        if (CreatedLineStyles.Count > 15)
        {
          sb.AppendLine($"  ... và {CreatedLineStyles.Count - 15} style khác.");
        }
      }

      if (Warnings.Count > 0)
      {
        sb.AppendLine($"\nCảnh báo ({Warnings.Count}):");
        foreach (string warn in Warnings.Take(10))
        {
          sb.AppendLine($"  ⚠ {warn}");
        }
        if (Warnings.Count > 10)
        {
          sb.AppendLine($"  ... và {Warnings.Count - 10} cảnh báo khác.");
        }
      }

      return sb.ToString();
    }
  }

  public static class ModelLinesFromCsvService
  {
    private const double ToleranceFeet = 1e-5;

    private static readonly Autodesk.Revit.DB.Color[] ColorPalette =
    {
      new(230, 25, 75),   // Red
      new(60, 180, 75),   // Green
      new(0, 130, 200),   // Blue
      new(245, 130, 48),  // Orange
      new(145, 30, 180),  // Purple
      new(70, 240, 240),  // Cyan
      new(240, 50, 230),  // Magenta
      new(210, 245, 60),  // Lime
      new(250, 190, 212), // Pink
      new(0, 128, 128),   // Teal
      new(170, 110, 40),  // Brown
      new(128, 0, 0),     // Maroon
      new(170, 255, 195), // Mint
      new(128, 128, 0),   // Olive
      new(0, 0, 128)      // Navy
    };

    /// <summary>
    /// Parses a CSV file and returns the table structure preserving axis order and profile names.
    /// </summary>
    public static CsvProfileTable ParseCsvTable(string path, out List<string> warnings)
    {
      warnings = new List<string>();
      if (!File.Exists(path))
      {
        throw new FileNotFoundException($"Không tìm thấy file: {path}");
      }

      string[] lines = VerticalCsvService.ReadAllLinesShared(path, Encoding.UTF8)
        .Select(l => l.Trim())
        .Where(l => l.Length > 0)
        .ToArray();

      if (lines.Length < 2)
      {
        throw new InvalidOperationException("File CSV cần ít nhất 1 dòng tiêu đề điểm và các dòng dữ liệu tọa độ.");
      }

      // Row 0: Point/Profile headers (from col 2 onwards)
      IReadOnlyList<string> header = VerticalCsvService.ParseCsvLine(lines[0]);
      var colToProfileName = new Dictionary<int, string>();
      var table = new CsvProfileTable();

      for (int c = 2; c < header.Count; c++)
      {
        string pointName = header[c].Trim();
        if (!string.IsNullOrEmpty(pointName))
        {
          colToProfileName[c] = pointName;
          if (!table.ProfileNames.Contains(pointName, StringComparer.OrdinalIgnoreCase))
          {
            table.ProfileNames.Add(pointName);
          }
        }
      }

      if (colToProfileName.Count == 0)
      {
        throw new InvalidOperationException("Không tìm thấy tên điểm/profile nào ở hàng đầu (từ cột thứ 3 trở đi).");
      }

      string currentAxis = string.Empty;

      for (int i = 1; i < lines.Length; i++)
      {
        IReadOnlyList<string> cells = VerticalCsvService.ParseCsvLine(lines[i]);
        if (cells.Count < 2)
        {
          continue;
        }

        string axisCell = cells[0].Trim();
        if (!string.IsNullOrEmpty(axisCell))
        {
          currentAxis = axisCell;
          if (!table.AxisOrder.Contains(currentAxis, StringComparer.OrdinalIgnoreCase))
          {
            table.AxisOrder.Add(currentAxis);
          }
        }

        if (string.IsNullOrEmpty(currentAxis))
        {
          warnings.Add($"Dòng {i + 1}: Bỏ qua vì chưa xác định được trục (cột đầu trống).");
          continue;
        }

        string coordTypeRaw = cells[1].Trim().ToUpperInvariant();
        string coordType;
        if (coordTypeRaw.StartsWith("X", StringComparison.Ordinal))
        {
          coordType = "X";
        }
        else if (coordTypeRaw.StartsWith("Y", StringComparison.Ordinal))
        {
          coordType = "Y";
        }
        else if (coordTypeRaw.StartsWith("Z", StringComparison.Ordinal))
        {
          coordType = "Z";
        }
        else
        {
          warnings.Add($"Dòng {i + 1}: Ký hiệu tọa độ không hợp lệ '{cells[1]}' (cần X, Y hoặc Z).");
          continue;
        }

        foreach (var kvp in colToProfileName)
        {
          int colIndex = kvp.Key;
          string profileName = kvp.Value;

          if (colIndex >= cells.Count)
          {
            continue;
          }

          string valText = cells[colIndex].Trim();
          if (string.IsNullOrEmpty(valText))
          {
            continue;
          }

          if (!TryParseCoordinate(valText, out double val))
          {
            warnings.Add($"Dòng {i + 1}, cột {colIndex + 1} ({profileName}): Giá trị '{valText}' không phải số hợp lệ.");
            continue;
          }

          var key = (currentAxis, profileName);
          if (!table.PointsMap.TryGetValue(key, out CsvPointData? pointData))
          {
            pointData = new CsvPointData
            {
              Axis = currentAxis,
              PointName = profileName
            };
            table.PointsMap[key] = pointData;
          }

          switch (coordType)
          {
            case "X":
              pointData.X = val;
              break;
            case "Y":
              pointData.Y = val;
              break;
            case "Z":
              pointData.Z = val;
              break;
          }
        }
      }

      return table;
    }

    /// <summary>
    /// Parses a CSV file where:
    /// - Row 0: columns starting from index 2 contain point names (e.g. PD3, GE1, ...).
    /// - Data rows: column 0 contains axis name (or empty if same as previous row),
    ///   column 1 contains coordinate type (X, Y, or Z),
    ///   columns from index 2 contain coordinate values for each point.
    /// </summary>
    public static List<CsvPointData> ParseCsv(string path, out List<string> warnings)
    {
      CsvProfileTable table = ParseCsvTable(path, out warnings);
      return table.Points;
    }

    /// <summary>
    /// Tool 1: Creates vertical Model Lines for each point: (X, Y, 0) -> (X, Y, Z).
    /// Line styles are named according to "trục-điểm" format (e.g. DL1-PD3).
    /// </summary>
    public static ModelLinesFromCsvResult CreateModelLines(Document doc, string csvPath, CsvCoordinateUnit unit = CsvCoordinateUnit.Meters)
    {
      var result = new ModelLinesFromCsvResult();
      List<CsvPointData> points = ParseCsv(csvPath, out List<string> parseWarnings);
      result.Warnings.AddRange(parseWarnings);
      result.TotalPointsRead = points.Count;

      if (points.Count == 0)
      {
        throw new InvalidOperationException("Không tìm thấy dữ liệu điểm nào trong file CSV.");
      }

      double scaleToFeet = unit switch
      {
        CsvCoordinateUnit.Meters => 1.0 / 0.3048,
        CsvCoordinateUnit.Millimeters => 1.0 / 304.8,
        _ => 1.0
      };

      int styleColorIndex = 0;
      var styleCache = new Dictionary<string, GraphicsStyle>(StringComparer.OrdinalIgnoreCase);

      using (var tx = new Transaction(doc, "NMK Create Model Lines from CSV"))
      {
        tx.Start();

        foreach (CsvPointData point in points)
        {
          if (!point.IsComplete)
          {
            result.Warnings.Add($"Điểm {point.Axis}-{point.PointName}: Thiếu tọa độ "
              + $"({(point.X.HasValue ? "" : "X ")}{(point.Y.HasValue ? "" : "Y ")}{(point.Z.HasValue ? "" : "Z ")}). Bỏ qua.");
            continue;
          }

          double xFeet = point.X!.Value * scaleToFeet;
          double yFeet = point.Y!.Value * scaleToFeet;
          double zFeet = point.Z!.Value * scaleToFeet;

          double deltaZ = Math.Abs(zFeet);
          if (deltaZ < ToleranceFeet || deltaZ < doc.Application.ShortCurveTolerance)
          {
            result.Warnings.Add($"Điểm {point.Axis}-{point.PointName}: Chiều cao Z = 0 (quá ngắn để tạo đường thẳng). Bỏ qua.");
            continue;
          }

          var p0 = new XYZ(xFeet, yFeet, 0.0);
          var p1 = new XYZ(xFeet, yFeet, zFeet);

          try
          {
            Line line = Line.CreateBound(p0, p1);

            XYZ dir = (p1 - p0).Normalize();
            XYZ normal;
            if (Math.Abs(dir.DotProduct(XYZ.BasisZ)) < 0.99)
            {
              normal = dir.CrossProduct(XYZ.BasisZ).Normalize();
            }
            else
            {
              normal = dir.CrossProduct(XYZ.BasisX).Normalize();
            }

            Plane plane = Plane.CreateByNormalAndOrigin(normal, p0);
            SketchPlane sketch = SketchPlane.Create(doc, plane);

            ModelCurve modelCurve = doc.IsFamilyDocument
              ? doc.FamilyCreate.NewModelCurve(line, sketch)
              : doc.Create.NewModelCurve(line, sketch);

            string styleName = point.LineStyleName;
            if (!styleCache.TryGetValue(styleName, out GraphicsStyle? style))
            {
              style = GetOrCreateLineStyle(doc, styleName, styleColorIndex++);
              styleCache[styleName] = style;
              result.CreatedLineStyles.Add(styleName);
            }

            try
            {
              modelCurve.LineStyle = style;
            }
            catch (Exception ex)
            {
              result.Warnings.Add($"Điểm {styleName}: Đã tạo đường thẳng nhưng không thể gán LineStyle ({ex.Message}).");
            }

            result.CreatedModelLineIds.Add(modelCurve.Id);
            result.CreatedLinesCount++;
          }
          catch (Exception ex)
          {
            result.Warnings.Add($"Lỗi khi tạo model line cho {point.Axis}-{point.PointName}: {ex.Message}");
          }
        }

        tx.Commit();
      }

      return result;
    }

    /// <summary>
    /// Tool 2: Connects the points for each profile column sequentially across the axes.
    /// Line style is named after the profile (e.g. PD3, GE1).
    /// </summary>
    public static ModelLinesFromCsvResult CreateProfileLines(Document doc, string csvPath, CsvCoordinateUnit unit = CsvCoordinateUnit.Meters)
    {
      var result = new ModelLinesFromCsvResult();
      CsvProfileTable table = ParseCsvTable(csvPath, out List<string> parseWarnings);
      result.Warnings.AddRange(parseWarnings);
      result.TotalPointsRead = table.Points.Count;

      if (table.Points.Count == 0 || table.ProfileNames.Count == 0)
      {
        throw new InvalidOperationException("Không tìm thấy dữ liệu điểm/profile nào trong file CSV.");
      }

      double scaleToFeet = unit switch
      {
        CsvCoordinateUnit.Meters => 1.0 / 0.3048,
        CsvCoordinateUnit.Millimeters => 1.0 / 304.8,
        _ => 1.0
      };

      int styleColorIndex = 0;
      var styleCache = new Dictionary<string, GraphicsStyle>(StringComparer.OrdinalIgnoreCase);

      using (var tx = new Transaction(doc, "NMK Connect Profile Lines from CSV"))
      {
        tx.Start();

        foreach (string profileName in table.ProfileNames)
        {
          var validPoints = new List<(string Axis, XYZ Point)>();
          foreach (string axis in table.AxisOrder)
          {
            var key = (axis, profileName);
            if (table.PointsMap.TryGetValue(key, out CsvPointData? pt) && pt.IsComplete)
            {
              double xFeet = pt.X!.Value * scaleToFeet;
              double yFeet = pt.Y!.Value * scaleToFeet;
              double zFeet = pt.Z!.Value * scaleToFeet;
              validPoints.Add((axis, new XYZ(xFeet, yFeet, zFeet)));
            }
          }

          if (validPoints.Count < 2)
          {
            result.Warnings.Add($"Profile '{profileName}': Chỉ có {validPoints.Count} điểm hợp lệ, cần ít nhất 2 điểm (2 trục) để nối thành đường. Bỏ qua.");
            continue;
          }

          string styleName = profileName;
          if (!styleCache.TryGetValue(styleName, out GraphicsStyle? style))
          {
            style = GetOrCreateLineStyle(doc, styleName, styleColorIndex++);
            styleCache[styleName] = style;
            result.CreatedLineStyles.Add(styleName);
          }

          for (int i = 0; i < validPoints.Count - 1; i++)
          {
            XYZ p0 = validPoints[i].Point;
            XYZ p1 = validPoints[i + 1].Point;

            double dist = p0.DistanceTo(p1);
            if (dist < ToleranceFeet || dist < doc.Application.ShortCurveTolerance)
            {
              result.Warnings.Add($"Profile '{profileName}' đoạn {validPoints[i].Axis}→{validPoints[i + 1].Axis}: Chiều dài quá ngắn ({dist:0.######} ft), bỏ qua.");
              continue;
            }

            try
            {
              Line line = Line.CreateBound(p0, p1);

              XYZ dir = (p1 - p0).Normalize();
              XYZ normal;
              if (Math.Abs(dir.DotProduct(XYZ.BasisZ)) < 0.99)
              {
                normal = dir.CrossProduct(XYZ.BasisZ).Normalize();
              }
              else
              {
                normal = dir.CrossProduct(XYZ.BasisX).Normalize();
              }

              Plane plane = Plane.CreateByNormalAndOrigin(normal, p0);
              SketchPlane sketch = SketchPlane.Create(doc, plane);

              ModelCurve modelCurve = doc.IsFamilyDocument
                ? doc.FamilyCreate.NewModelCurve(line, sketch)
                : doc.Create.NewModelCurve(line, sketch);

              try
              {
                modelCurve.LineStyle = style;
              }
              catch (Exception ex)
              {
                result.Warnings.Add($"Profile '{profileName}' ({validPoints[i].Axis}→{validPoints[i + 1].Axis}): Tạo đường thành công nhưng không thể gán LineStyle ({ex.Message}).");
              }

              result.CreatedModelLineIds.Add(modelCurve.Id);
              result.CreatedLinesCount++;
            }
            catch (Exception ex)
            {
              result.Warnings.Add($"Profile '{profileName}' đoạn {validPoints[i].Axis}→{validPoints[i + 1].Axis}: Lỗi khi tạo model line ({ex.Message}).");
            }
          }
        }

        tx.Commit();
      }

      return result;
    }

    /// <summary>
    /// Tool 3: Connects the points for each row (axis) sequentially across the profile columns.
    /// Line style is named according to "PROFILE_<Axis>" (e.g. PROFILE_DL1).
    /// </summary>
    public static ModelLinesFromCsvResult CreateRowLines(Document doc, string csvPath, CsvCoordinateUnit unit = CsvCoordinateUnit.Meters)
    {
      var result = new ModelLinesFromCsvResult();
      CsvProfileTable table = ParseCsvTable(csvPath, out List<string> parseWarnings);
      result.Warnings.AddRange(parseWarnings);
      result.TotalPointsRead = table.Points.Count;

      if (table.Points.Count == 0 || table.AxisOrder.Count == 0)
      {
        throw new InvalidOperationException("Không tìm thấy dữ liệu điểm/trục nào trong file CSV.");
      }

      double scaleToFeet = unit switch
      {
        CsvCoordinateUnit.Meters => 1.0 / 0.3048,
        CsvCoordinateUnit.Millimeters => 1.0 / 304.8,
        _ => 1.0
      };

      int styleColorIndex = 0;
      var styleCache = new Dictionary<string, GraphicsStyle>(StringComparer.OrdinalIgnoreCase);

      using (var tx = new Transaction(doc, "NMK Connect Row Lines from CSV"))
      {
        tx.Start();

        foreach (string axis in table.AxisOrder)
        {
          var validPoints = new List<(string Profile, XYZ Point)>();
          foreach (string profileName in table.ProfileNames)
          {
            var key = (axis, profileName);
            if (table.PointsMap.TryGetValue(key, out CsvPointData? pt) && pt.IsComplete)
            {
              double xFeet = pt.X!.Value * scaleToFeet;
              double yFeet = pt.Y!.Value * scaleToFeet;
              double zFeet = pt.Z!.Value * scaleToFeet;
              validPoints.Add((profileName, new XYZ(xFeet, yFeet, zFeet)));
            }
          }

          if (validPoints.Count < 2)
          {
            result.Warnings.Add($"Trục '{axis}': Chỉ có {validPoints.Count} điểm hợp lệ, cần ít nhất 2 điểm để nối thành đường. Bỏ qua.");
            continue;
          }

          string styleName = $"PROFILE_{axis}";
          if (!styleCache.TryGetValue(styleName, out GraphicsStyle? style))
          {
            style = GetOrCreateLineStyle(doc, styleName, styleColorIndex++);
            styleCache[styleName] = style;
            result.CreatedLineStyles.Add(styleName);
          }

          for (int i = 0; i < validPoints.Count - 1; i++)
          {
            XYZ p0 = validPoints[i].Point;
            XYZ p1 = validPoints[i + 1].Point;

            double dist = p0.DistanceTo(p1);
            if (dist < ToleranceFeet || dist < doc.Application.ShortCurveTolerance)
            {
              result.Warnings.Add($"Trục '{axis}' đoạn {validPoints[i].Profile}→{validPoints[i + 1].Profile}: Chiều dài quá ngắn ({dist:0.######} ft), bỏ qua.");
              continue;
            }

            try
            {
              Line line = Line.CreateBound(p0, p1);

              XYZ dir = (p1 - p0).Normalize();
              XYZ normal;
              if (Math.Abs(dir.DotProduct(XYZ.BasisZ)) < 0.99)
              {
                normal = dir.CrossProduct(XYZ.BasisZ).Normalize();
              }
              else
              {
                normal = dir.CrossProduct(XYZ.BasisX).Normalize();
              }

              Plane plane = Plane.CreateByNormalAndOrigin(normal, p0);
              SketchPlane sketch = SketchPlane.Create(doc, plane);

              ModelCurve modelCurve = doc.IsFamilyDocument
                ? doc.FamilyCreate.NewModelCurve(line, sketch)
                : doc.Create.NewModelCurve(line, sketch);

              try
              {
                modelCurve.LineStyle = style;
              }
              catch (Exception ex)
              {
                result.Warnings.Add($"Trục '{axis}' ({validPoints[i].Profile}→{validPoints[i + 1].Profile}): Tạo đường thành công nhưng không thể gán LineStyle ({ex.Message}).");
              }

              result.CreatedModelLineIds.Add(modelCurve.Id);
              result.CreatedLinesCount++;
            }
            catch (Exception ex)
            {
              result.Warnings.Add($"Trục '{axis}' đoạn {validPoints[i].Profile}→{validPoints[i + 1].Profile}: Lỗi khi tạo model line ({ex.Message}).");
            }
          }
        }

        tx.Commit();
      }

      return result;
    }

    public static GraphicsStyle GetOrCreateLineStyle(Document doc, string name, int colorIndex)
    {
      Categories categories = doc.Settings.Categories;
      Category lines = categories.get_Item(BuiltInCategory.OST_Lines)
        ?? throw new InvalidOperationException("OST_Lines category was not found in document.");

      Category? sub = null;
      foreach (Category category in lines.SubCategories)
      {
        if (category.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
          sub = category;
          break;
        }
      }

      if (sub == null)
      {
        sub = categories.NewSubcategory(lines, name);
        try
        {
          sub.LineColor = ColorPalette[Math.Abs(colorIndex) % ColorPalette.Length];
          sub.SetLineWeight(5, GraphicsStyleType.Projection);
        }
        catch
        {
        }
      }

      return sub.GetGraphicsStyle(GraphicsStyleType.Projection)
        ?? throw new InvalidOperationException($"Line style '{name}' does not have a projection graphics style.");
    }

    private static bool TryParseCoordinate(string text, out double value)
    {
      value = 0;
      if (string.IsNullOrWhiteSpace(text))
      {
        return false;
      }

      string clean = text.Trim();
      if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
      {
        return true;
      }

      // Fallback in case local comma format is used
      if (double.TryParse(clean.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
      {
        return true;
      }

      return false;
    }
  }
}

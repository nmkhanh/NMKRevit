using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.NMKPrint.Models;
using Revit.Async;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ITextDocument = iTextSharp.text.Document;
using PdfCopy = iTextSharp.text.pdf.PdfCopy;
using PdfReader = iTextSharp.text.pdf.PdfReader;

namespace NMKRevit.NMKPrint.Services
{
  public class PrintService
  {
    private static readonly SemaphoreSlim PrintQueue = new(1, 1);
    private readonly PaperSizeService _paperSizeService;
    private readonly Pdf24Service _pdf24Service;

    public PrintService(PaperSizeService paperSizeService, Pdf24Service pdf24Service)
    {
      _paperSizeService = paperSizeService;
      _pdf24Service = pdf24Service;
    }

    public async Task RunAsync(
      UIApplication uiapp,
      IEnumerable<PrintJob> jobs,
      PrintSettings settings,
      Action<LogLevel, string, string?> log,
      Func<PrintJob, LogLevel, string, string?, Task>? jobLog = null,
      Func<LogLevel, string, string?, Task>? stageLog = null)
    {
      await PrintQueue.WaitAsync();
      try
      {
        List<PrintJob> jobList = jobs.ToList();
        if (jobList.Count == 0)
        {
          log(LogLevel.Warning, "No sheets/views selected.", null);
          return;
        }

        bool hasActiveDocument = false;
        await RevitTask.RunAsync(app =>
        {
          hasActiveDocument = app.ActiveUIDocument?.Document != null;
        });
        if (!hasActiveDocument)
        {
          log(LogLevel.Error, "No active Revit document.", null);
          return;
        }

        Directory.CreateDirectory(settings.OutputFolder);
        if (!ValidateOutputTargets(jobList, settings, log))
        {
          return;
        }

        bool hasSheetPdfJobs = jobList.Any(x => x.Format == PrintFormat.PDF && x.Item.IsSheet);
        bool usePdfPrinter = !hasSheetPdfJobs || ResolvePdfPrinter(settings, log);

        var pdfJobs = jobList.Where(x => x.Format == PrintFormat.PDF).ToList();
        if (pdfJobs.Count > 0)
        {
          await RunPdfJobsAsync(uiapp, pdfJobs, settings, usePdfPrinter, log, jobLog, stageLog);
        }

        foreach (PrintJob job in jobList.Where(x => x.Format == PrintFormat.DWG))
        {
          await RunDwgJobAsync(uiapp, job, settings, log, jobLog);
        }
      }
      finally
      {
        PrintQueue.Release();
      }
    }

    private async Task RunDwgJobAsync(
      UIApplication uiapp,
      PrintJob job,
      PrintSettings settings,
      Action<LogLevel, string, string?> log,
      Func<PrintJob, LogLevel, string, string?, Task>? jobLog)
    {
      string outputPath = Path.Combine(job.OutputFolder, job.FileName + ".dwg");
      if (jobLog != null)
      {
        await jobLog(job, LogLevel.Printing, "Printing...", outputPath);
      }

      try
      {
        await RevitTask.RunAsync(app =>
        {
          ExportDwg(app.ActiveUIDocument.Document, job, settings, outputPath);
        });
        if (jobLog != null)
        {
          await jobLog(job, LogLevel.Done, "Done", outputPath);
        }
      }
      catch (Exception ex)
      {
        if (jobLog != null)
        {
          await jobLog(job, LogLevel.Error, ex.Message, outputPath);
        }
      }
    }

    private async Task RunPdfJobsAsync(
      UIApplication uiapp,
      List<PrintJob> jobs,
      PrintSettings settings,
      bool usePdfPrinter,
      Action<LogLevel, string, string?> log,
      Func<PrintJob, LogLevel, string, string?, Task>? jobLog,
      Func<LogLevel, string, string?, Task>? stageLog)
    {
      string tempFolder = GetPdfTempFolder();
      var tempFilesToClean = new List<string>();
      var tempFiles = new List<string>();
      var succeededJobs = new List<PrintJob>();

      try
      {
        Directory.CreateDirectory(tempFolder);
        _pdf24Service.ConfigureAutoSave(tempFolder);

        foreach (PrintJob job in jobs)
        {
          string tempName = Guid.NewGuid().ToString();
          string tempPath = Path.Combine(tempFolder, tempName + ".pdf");
          tempFilesToClean.Add(tempPath);
          var tempJob = new PrintJob
          {
            Item = job.Item,
            Format = PrintFormat.PDF,
            FileName = tempName,
            OutputFolder = tempFolder
          };

          try
          {
            if (jobLog != null)
            {
              await jobLog(job, LogLevel.Printing, "Printing...", tempPath);
            }

            await PrintPdfJobAsync(uiapp, tempJob, settings, tempPath, usePdfPrinter);
            tempFiles.Add(tempPath);
            succeededJobs.Add(job);

            if (!settings.CombinePdf)
            {
              string outputPath = Path.Combine(job.OutputFolder, job.FileName + ".pdf");
              File.Copy(tempPath, outputPath, true);
              if (jobLog != null)
              {
                await jobLog(job, LogLevel.Done, "Done", outputPath);
              }
            }
            else if (jobLog != null)
            {
              await jobLog(job, LogLevel.Done, "Done", tempPath);
            }
          }
          catch (Exception ex)
          {
            if (jobLog != null)
            {
              await jobLog(job, LogLevel.Error, ex.Message, tempPath);
            }
          }
        }

        if (settings.CombinePdf)
        {
          string? outputPath = null;
          try
          {
            await WaitForFilesAsync(tempFiles, settings.TimeoutSeconds);
            await Task.Delay(500);

            string outputName = FileNameSanitizer.Sanitize(jobs[0].FileName);
            outputPath = Path.Combine(jobs[0].OutputFolder, outputName + ".pdf");
            if (stageLog != null)
            {
              await stageLog(LogLevel.Printing, "Combining PDF...", outputPath);
            }

            CombinePdfFiles(tempFiles, outputPath);
            await Task.Delay(500);
            if (stageLog != null)
            {
              await stageLog(LogLevel.Done, "Done", outputPath);
            }
          }
          catch (Exception ex)
          {
            if (stageLog != null)
            {
              await stageLog(LogLevel.Error, ex.Message, outputPath);
            }

            if (succeededJobs.Count == 0)
            {
              log(LogLevel.Error, $"Error - PDF: {ex.Message}", outputPath);
              return;
            }
          }
        }
      }
      catch (Exception ex)
      {
        log(LogLevel.Error, $"Error - PDF: {ex.Message}", null);
      }
      finally
      {
        DeleteTempFiles(tempFilesToClean);
      }
    }

    private async Task PrintPdfJobAsync(
      UIApplication uiapp,
      PrintJob job,
      PrintSettings settings,
      string outputPath,
      bool usePdfPrinter)
    {
      if (job.Item.IsSheet)
      {
        if (!usePdfPrinter)
        {
          throw new InvalidOperationException("PDF24 printer is required for sheet printing.");
        }

        await RevitTask.RunAsync(app =>
        {
          _pdf24Service.ConfigureAutoSave(job.OutputFolder);
          PrintPdfWithPrinter(app.ActiveUIDocument.Document, job, settings, outputPath);
        });
        await WaitForFileAsync(outputPath, settings.TimeoutSeconds);
        return;
      }

#if D2022 || D2023 || D2024 || D2025 || D2026 || D2027
      await RevitTask.RunAsync(app =>
      {
        ExportPdfNative(app.ActiveUIDocument.Document, job, settings, outputPath);
      });
      await WaitForFileAsync(outputPath, settings.TimeoutSeconds);
#else
      throw new InvalidOperationException("PDF24 printer is not available and native PDF export is not supported by this Revit version.");
#endif
    }

    private void PrintPdfWithPrinter(Document doc, PrintJob job, PrintSettings settings, string outputPath)
    {
      Directory.CreateDirectory(job.OutputFolder);
      TryDelete(outputPath);
      _paperSizeService.EnsurePaperForm(settings.PrinterName, job.Item.Paper);

      PrintManager printManager = doc.PrintManager;
      printManager.SelectNewPrintDriver(settings.PrinterName);
      printManager.PrintRange = PrintRange.Select;
      printManager.PrintToFile = true;
      printManager.CombinedFile = true;
      printManager.PrintToFileName = outputPath;

      using var viewSet = new ViewSet();
      viewSet.Insert(job.Item.View);

      string setupName = $"NMKPrint_{Guid.NewGuid():N}";
      using TransactionGroup group = new TransactionGroup(doc, "NMK Print PDF");
      group.Start();
      try
      {
        using (Transaction transaction = new Transaction(doc, "NMK Print PDF Setup"))
        {
          transaction.Start();
          PrintSetup printSetup = printManager.PrintSetup;
          printSetup.SaveAs(setupName);
          PrintSetting setting = doc.GetPrintSettingIds()
            .Select(id => doc.GetElement(id) as PrintSetting)
            .First(x => x != null && x.Name == setupName)!;
          printSetup.CurrentPrintSetting = setting;
          ApplyPrintParameters(printSetup.CurrentPrintSetting.PrintParameters, job.Item, settings);
          printManager.Apply();
          printSetup.Save();
          doc.Print(viewSet, true);
          transaction.Commit();
        }

        using (Transaction transaction = new Transaction(doc, "NMK Delete Print Setup"))
        {
          transaction.Start();
          PrintSetting? setting = doc.GetPrintSettingIds()
            .Select(id => doc.GetElement(id) as PrintSetting)
            .FirstOrDefault(x => x != null && x.Name == setupName);
          if (setting != null)
          {
            doc.Delete(setting.Id);
          }

          transaction.Commit();
        }

        group.Assimilate();
      }
      catch
      {
        group.RollBack();
        throw;
      }
    }

#if D2022 || D2023 || D2024 || D2025 || D2026 || D2027
    private void ExportPdfNative(Document doc, PrintJob job, PrintSettings settings, string outputPath)
    {
      Directory.CreateDirectory(job.OutputFolder);
      TryDelete(outputPath);

      var options = CreatePdfExportOptions(settings);
      options.FileName = Path.GetFileNameWithoutExtension(outputPath);
      doc.Export(job.OutputFolder, new List<ElementId> { job.Item.Id }, options);
    }

    private static PDFExportOptions CreatePdfExportOptions(PrintSettings settings)
    {
      var options = new PDFExportOptions();
      options.PaperPlacement = settings.PaperPlacementCenter ? PaperPlacementType.Center : PaperPlacementType.LowerLeft;
      if (settings.OffsetUserDefined)
      {
        options.OriginOffsetX = UnitUtils.ConvertToInternalUnits(settings.OffsetX, UnitTypeId.Millimeters);
        options.OriginOffsetY = UnitUtils.ConvertToInternalUnits(settings.OffsetY, UnitTypeId.Millimeters);
      }

      options.ZoomType = settings.ZoomFitToPage ? ZoomType.FitToPage : ZoomType.Zoom;
      options.ZoomPercentage = settings.ZoomPercentage;
      options.AlwaysUseRaster = settings.RasterProcessing;
      options.RasterQuality = settings.SelectedRasterQuality switch
      {
        "Low" => RasterQualityType.Low,
        "Medium" => RasterQualityType.Medium,
        "High" => RasterQualityType.High,
        _ => RasterQualityType.Presentation
      };
      options.ColorDepth = settings.SelectedColor switch
      {
        "Black Lines" => ColorDepthType.BlackLine,
        "Gray Scale" => ColorDepthType.GrayScale,
        _ => ColorDepthType.Color
      };
      options.ViewLinksInBlue = settings.ViewLinksInBlue;
      options.HideReferencePlane = settings.HideRefWorkPlanes;
      options.HideUnreferencedViewTags = settings.HideUnreferencedViewTags;
      options.HideScopeBoxes = settings.HideScopeBoxes;
      options.HideCropBoundaries = settings.HideCropBoundaries;
      options.ReplaceHalftoneWithThinLines = settings.ReplaceHalftoneWithThinLines;
      options.MaskCoincidentLines = settings.RegionEdgesMaskCoincidentLines;
      options.Combine = true;
      options.ExportQuality = PDFExportQualityType.DPI600;
      options.PaperFormat = ExportPaperFormat.Default;
      options.PaperOrientation = PageOrientationType.Auto;
      options.StopOnError = false;
      return options;
    }
#endif

    private void ApplyPrintParameters(PrintParameters parameters, PrintItem item, PrintSettings settings)
    {
      parameters.PaperPlacement = settings.PaperPlacementCenter ? PaperPlacementType.Center : PaperPlacementType.LowerLeft;
#if D2022 || D2023 || D2024 || D2025 || D2026 || D2027
      if (settings.OffsetUserDefined)
      {
        parameters.PaperPlacement = PaperPlacementType.Margins;
        parameters.OriginOffsetX = UnitUtils.ConvertToInternalUnits(settings.OffsetX, UnitTypeId.Millimeters);
        parameters.OriginOffsetY = UnitUtils.ConvertToInternalUnits(settings.OffsetY, UnitTypeId.Millimeters);
      }
#endif

      parameters.ZoomType = settings.ZoomFitToPage ? ZoomType.FitToPage : ZoomType.Zoom;
      parameters.Zoom = settings.ZoomPercentage;
      parameters.ColorDepth = settings.SelectedColor switch
      {
        "Black Lines" => ColorDepthType.BlackLine,
        "Gray Scale" => ColorDepthType.GrayScale,
        _ => ColorDepthType.Color
      };
      parameters.RasterQuality = settings.SelectedRasterQuality switch
      {
        "Low" => RasterQualityType.Low,
        "Medium" => RasterQualityType.Medium,
        "High" => RasterQualityType.High,
        _ => RasterQualityType.Presentation
      };
      parameters.ViewLinksinBlue = settings.ViewLinksInBlue;
      parameters.HideReforWorkPlanes = settings.HideRefWorkPlanes;
      parameters.HideUnreferencedViewTags = settings.HideUnreferencedViewTags;
      parameters.HideScopeBoxes = settings.HideScopeBoxes;
      parameters.HideCropBoundaries = settings.HideCropBoundaries;
      parameters.ReplaceHalftoneWithThinLines = settings.ReplaceHalftoneWithThinLines;
      parameters.MaskCoincidentLines = settings.RegionEdgesMaskCoincidentLines;
      parameters.PageOrientation = item.Orientation == "Landscape" ? PageOrientationType.Landscape : PageOrientationType.Portrait;

      if (item.Size != "-" && parameters.PaperSize?.Name != item.Size)
      {
        var printManager = item.View.Document.PrintManager;
        PaperSize? matched = printManager.PaperSizes.Cast<PaperSize>()
          .FirstOrDefault(p => string.Equals(p.Name, item.Size, StringComparison.OrdinalIgnoreCase));
        if (matched != null)
        {
          parameters.PaperSize = matched;
        }
      }
    }

    private void ExportDwg(Document doc, PrintJob job, PrintSettings settings, string outputPath)
    {
      Directory.CreateDirectory(job.OutputFolder);
      string folder = Path.GetDirectoryName(outputPath) ?? settings.OutputFolder;
      string fileName = Path.GetFileNameWithoutExtension(outputPath);
      var options = string.IsNullOrWhiteSpace(settings.DwgExportSetup)
        ? new DWGExportOptions()
        : DWGExportOptions.GetPredefinedOptions(doc, settings.DwgExportSetup);
      options.MergedViews = !settings.ExportViewsAsExternalReferences;

      doc.Export(folder, fileName, new List<ElementId> { job.Item.Id }, options);
    }

    private static bool ValidateOutputTargets(IEnumerable<PrintJob> jobs, PrintSettings settings, Action<LogLevel, string, string?> log)
    {
      var paths = new List<string>();
      List<PrintJob> jobList = jobs.ToList();
      if (settings.CombinePdf)
      {
        PrintJob? firstPdf = jobList.FirstOrDefault(x => x.Format == PrintFormat.PDF);
        if (firstPdf != null)
        {
          paths.Add(Path.Combine(firstPdf.OutputFolder, firstPdf.FileName + ".pdf"));
        }

        paths.AddRange(jobList
          .Where(x => x.Format != PrintFormat.PDF)
          .Select(x => Path.Combine(x.OutputFolder, x.FileName + ".dwg")));
      }
      else
      {
        paths.AddRange(jobList.Select(x =>
          Path.Combine(x.OutputFolder, x.FileName + (x.Format == PrintFormat.PDF ? ".pdf" : ".dwg"))));
      }

      foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
      {
        if (!PrepareOutputFile(path, out string reason))
        {
          log(LogLevel.Error, $"Output file is locked or cannot be overwritten: {reason}", path);
          return false;
        }
      }

      return true;
    }

    private bool ResolvePdfPrinter(PrintSettings settings, Action<LogLevel, string, string?> log)
    {
      if (_pdf24Service.IsInstalled(settings.PrinterName))
      {
        return true;
      }

      string resolvedPrinter = _pdf24Service.ResolvePrinterName();
      if (_pdf24Service.IsInstalled(resolvedPrinter))
      {
        log(LogLevel.Warning, $"Printer '{settings.PrinterName}' was not found. Using '{resolvedPrinter}'.", null);
        settings.PrinterName = resolvedPrinter;
        return true;
      }

      log(LogLevel.Error, "PDF24 printer was not found. Sheet PDF output requires printing through PDF24.", null);
      return false;
    }

    private static bool PrepareOutputFile(string path, out string reason)
    {
      reason = path;
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        if (!File.Exists(path))
        {
          return true;
        }

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        File.Delete(path);
        return true;
      }
      catch (Exception ex)
      {
        reason = $"{path} ({ex.Message})";
        return false;
      }
    }

    private static void CombinePdfFiles(IReadOnlyList<string> inputFiles, string outputPath)
    {
      if (inputFiles.Count == 0)
      {
        throw new InvalidOperationException("No PDF files were created to combine.");
      }

      foreach (string inputFile in inputFiles)
      {
        if (!File.Exists(inputFile))
        {
          throw new FileNotFoundException("Printed PDF was not found.", inputFile);
        }
      }

      Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);
      TryDelete(outputPath);

      using FileStream stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
      using var document = new ITextDocument();
      using var copy = new PdfCopy(document, stream);
      document.Open();

      foreach (string inputFile in inputFiles)
      {
        using var reader = new PdfReader(inputFile);
        copy.AddDocument(reader);
        copy.FreeReader(reader);
      }
    }

    private static string GetPdfTempFolder()
    {
      string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
      return Path.Combine(programData, "Autodesk", "ApplicationPlugins", "NMKRevit.bundle", "Temp");
    }

    private static async Task WaitForFilesAsync(IEnumerable<string> paths, int timeoutSeconds)
    {
      foreach (string path in paths)
      {
        await WaitForFileAsync(path, timeoutSeconds);
      }
    }

    private static async Task WaitForFileAsync(string path, int timeoutSeconds)
    {
      var stopwatch = Stopwatch.StartNew();
      while (stopwatch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)))
      {
        if (File.Exists(path))
        {
          try
          {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 0)
            {
              return;
            }
          }
          catch
          {
            // PDF24 can keep the file locked for a short period after Revit returns.
          }
        }

        await Task.Delay(500);
      }

      throw new TimeoutException($"Timed out waiting for PDF file: {path}");
    }

    private static void TryDelete(string path)
    {
      try
      {
        if (File.Exists(path))
        {
          File.Delete(path);
        }
      }
      catch
      {
        // The following print/export call will report the actionable error if overwrite still fails.
      }
    }

    private static void DeleteTempFiles(IEnumerable<string> paths)
    {
      foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
      {
        TryDelete(path);
      }
    }
  }
}

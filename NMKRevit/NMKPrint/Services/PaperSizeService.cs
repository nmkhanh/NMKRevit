using Autodesk.Revit.DB;
using NMKRevit.NMKPrint.Models;
using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Runtime.InteropServices;

namespace NMKRevit.NMKPrint.Services
{
  public class PaperSizeService
  {
    public PaperSpec FromTitleBlock(FamilyInstance? titleBlock)
    {
      if (titleBlock == null)
      {
        return new PaperSpec("-", 0, 0);
      }

      Parameter widthParameter = titleBlock.get_Parameter(BuiltInParameter.SHEET_WIDTH);
      Parameter heightParameter = titleBlock.get_Parameter(BuiltInParameter.SHEET_HEIGHT);
      if (widthParameter == null || heightParameter == null)
      {
        return new PaperSpec("-", 0, 0);
      }

#if D2020 || D2021
      double width = UnitUtils.ConvertFromInternalUnits(widthParameter.AsDouble(), DisplayUnitType.DUT_MILLIMETERS);
      double height = UnitUtils.ConvertFromInternalUnits(heightParameter.AsDouble(), DisplayUnitType.DUT_MILLIMETERS);
#else
      double width = UnitUtils.ConvertFromInternalUnits(widthParameter.AsDouble(), UnitTypeId.Millimeters);
      double height = UnitUtils.ConvertFromInternalUnits(heightParameter.AsDouble(), UnitTypeId.Millimeters);
#endif
      return new PaperSpec(GetPaperName(width, height), width, height);
    }

    public bool PrinterHasPaper(string printerName, string paperName)
    {
      if (string.IsNullOrWhiteSpace(printerName) || string.IsNullOrWhiteSpace(paperName) || paperName == "-")
      {
        return false;
      }

      var settings = new PrinterSettings { PrinterName = printerName };
      return settings.PaperSizes.Cast<System.Drawing.Printing.PaperSize>()
        .Any(p => string.Equals(p.PaperName, paperName, StringComparison.OrdinalIgnoreCase));
    }

    public void EnsurePaperForm(string printerName, PaperSpec paper)
    {
      if (paper.Name == "-" || paper.WidthMm <= 0 || paper.HeightMm <= 0)
      {
        return;
      }

      if (PrinterHasPaper(printerName, paper.Name))
      {
        return;
      }

      PaperFormManager.CreatePaperFormMM(printerName, paper.Name, Math.Max(paper.WidthMm, paper.HeightMm), Math.Min(paper.WidthMm, paper.HeightMm));
    }

    private static string GetPaperName(double widthMm, double heightMm)
    {
      double w = Math.Max(widthMm, heightMm);
      double h = Math.Min(widthMm, heightMm);

      var standards = new Dictionary<string, (double W, double H)>
      {
        ["A0"] = (1189, 841),
        ["A1"] = (841, 594),
        ["A2"] = (594, 420),
        ["A3"] = (420, 297),
        ["A4"] = (297, 210),
        ["A5"] = (210, 148),
        ["Letter"] = (279, 216),
        ["Legal"] = (356, 216),
        ["Tabloid"] = (432, 279)
      };

      foreach (var standard in standards)
      {
        if (IsSize(w, h, standard.Value.W, standard.Value.H))
        {
          return standard.Key;
        }
      }

      return $"PS_{Math.Round(w)}x{Math.Round(h)}";
    }

    private static bool IsSize(double w, double h, double standardW, double standardH)
    {
      const double tolerance = 5;
      return Math.Abs(w - standardW) <= tolerance && Math.Abs(h - standardH) <= tolerance;
    }

    private static class PaperFormManager
    {
      [StructLayout(LayoutKind.Sequential)]
      private struct Sizel
      {
        public int cx;
        public int cy;
      }

      [StructLayout(LayoutKind.Sequential)]
      private struct Rectl
      {
        public int left;
        public int top;
        public int right;
        public int bottom;
      }

      [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
      private struct FormInfo1
      {
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pName;
        public Sizel Size;
        public Rectl ImageableArea;
      }

      [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
      private static extern bool OpenPrinter(string? pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

      [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
      private static extern bool AddForm(IntPtr hPrinter, uint level, ref FormInfo1 pForm);

      [DllImport("winspool.drv", SetLastError = true)]
      private static extern bool ClosePrinter(IntPtr hPrinter);

      public static void CreatePaperFormMM(string printerName, string formName, double widthMm, double heightMm)
      {
        var form = new FormInfo1
        {
          Flags = 0,
          pName = formName,
          Size = new Sizel { cx = (int)(widthMm * 1000), cy = (int)(heightMm * 1000) },
          ImageableArea = new Rectl
          {
            left = 0,
            top = 0,
            right = (int)(widthMm * 1000),
            bottom = (int)(heightMm * 1000)
          }
        };

        if (!OpenPrinter(printerName, out IntPtr printerHandle, IntPtr.Zero))
        {
          throw new InvalidOperationException($"OpenPrinter failed for '{printerName}'. Win32Error={Marshal.GetLastWin32Error()}");
        }

        try
        {
          if (!AddForm(printerHandle, 1, ref form))
          {
            int error = Marshal.GetLastWin32Error();
            if (error != 183)
            {
              string message = error == 5 ? "Access denied. Run Revit as Administrator to add custom paper forms." : $"AddForm failed. Win32Error={error}";
              throw new InvalidOperationException(message);
            }
          }
        }
        finally
        {
          ClosePrinter(printerHandle);
        }
      }
    }
  }
}

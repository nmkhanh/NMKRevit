using System;
using System.Globalization;

namespace NMKRevit.Rebar.ViewModels
{
  public static class RebarPlacementViewModelParser
  {
    public static bool TryReadNumber(string value, out double result)
    {
      return double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
             double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    public static double ReadNumber(string value)
    {
      if (TryReadNumber(value, out double result))
      {
        return result;
      }
      throw new InvalidOperationException("Offset phai la so hop le.");
    }
  }
}

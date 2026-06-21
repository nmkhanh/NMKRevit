using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NMKRevit.NMKPrint.Services
{
  public sealed class NaturalStringComparer : IComparer<string>
  {
    public static NaturalStringComparer Instance { get; } = new();

    private NaturalStringComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
      return StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);
  }
}

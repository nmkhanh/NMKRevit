using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using RevitView = Autodesk.Revit.DB.View;

namespace NMKRevit.NMKPrint.Models
{
  public partial class PrintItem : ObservableObject
  {
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _displayName = string.Empty;

    public ElementId Id { get; init; } = ElementId.InvalidElementId;
    public RevitView View { get; init; } = null!;
    public bool IsSheet { get; init; }
    public string Number { get; init; } = "-";
    public string Name { get; init; } = "-";
    public string Revision { get; init; } = "-";
    public string RevisionDate { get; init; } = "-";
    public PaperSpec Paper { get; init; } = new("-", 0, 0);
    public IReadOnlyList<string> BrowserPath { get; init; } = Array.Empty<string>();
    public string Size => Paper.Name;
    public string Orientation => Paper.Orientation;
  }
}

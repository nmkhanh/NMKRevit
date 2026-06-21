using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Linq;

namespace NMKRevit.NMKPrint.Models
{
  public partial class PrintSettings : ObservableObject
  {
    [ObservableProperty]
    private string _printerName = "PDF24";

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private bool _exportPdf = true;

    [ObservableProperty]
    private bool _exportDwg;

    [ObservableProperty]
    private bool _combinePdf;

    [ObservableProperty]
    private bool _createSeparateFiles = true;

    partial void OnCreateSeparateFilesChanged(bool value)
    {
      if (value)
      {
        CombinePdf = false;
      }
      OnPropertyChanged(nameof(CanUseCustomName));
    }

    partial void OnCombinePdfChanged(bool value)
    {
      if (value)
      {
        CreateSeparateFiles = false;
      }
      else if (!CreateSeparateFiles)
      {
        CreateSeparateFiles = true;
      }

      OnPropertyChanged(nameof(CanUseCustomName));
    }

    public bool CanUseCustomName => CreateSeparateFiles;

    [ObservableProperty]
    private bool _useNamingConvention = true;

    [ObservableProperty]
    private string _namingDate = DateTime.Now.ToString("yyMMdd");

    [ObservableProperty]
    private string _namingProjectCode = string.Empty;

    [ObservableProperty]
    private string _namingNode = "GA PLANS";

    [ObservableProperty]
    private string _fileCombineName = string.Empty;

    public string FileCombineNameConvention
    {
      get
      {
        string firstPart = string.Join("_", new[] { NamingDate, NamingProjectCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.Join("-", new[] { firstPart, NamingNode }.Where(x => !string.IsNullOrWhiteSpace(x)));
      }
    }

    public string EffectiveFileCombineName
    {
      get => UseNamingConvention ? FileCombineNameConvention : FileCombineName;
      set
      {
        if (!UseNamingConvention)
        {
          FileCombineName = value;
        }
      }
    }

    public bool CanEditFileCombineName => !UseNamingConvention;

    partial void OnNamingDateChanged(string value)
    {
      OnPropertyChanged(nameof(FileCombineNameConvention));
      OnPropertyChanged(nameof(EffectiveFileCombineName));
    }

    partial void OnNamingProjectCodeChanged(string value)
    {
      OnPropertyChanged(nameof(FileCombineNameConvention));
      OnPropertyChanged(nameof(EffectiveFileCombineName));
    }

    partial void OnNamingNodeChanged(string value)
    {
      OnPropertyChanged(nameof(FileCombineNameConvention));
      OnPropertyChanged(nameof(EffectiveFileCombineName));
    }

    partial void OnUseNamingConventionChanged(bool value)
    {
      OnPropertyChanged(nameof(CanEditFileCombineName));
      OnPropertyChanged(nameof(EffectiveFileCombineName));
    }

    partial void OnFileCombineNameChanged(string value)
    {
      OnPropertyChanged(nameof(EffectiveFileCombineName));
    }

    [ObservableProperty]
    private bool _splitByFormat;

    public bool SaveAllInSameFolder
    {
      get => !SplitByFormat;
      set => SplitByFormat = !value;
    }

    partial void OnSplitByFormatChanged(bool value)
    {
      OnPropertyChanged(nameof(SaveAllInSameFolder));
    }

    [ObservableProperty]
    private int _timeoutSeconds = 120;

    [ObservableProperty]
    private string _dwgExportSetup = string.Empty;

    [ObservableProperty]
    private bool _exportViewsAsExternalReferences;

    [ObservableProperty]
    private bool _paperPlacementCenter;

    [ObservableProperty]
    private bool _paperPlacementOffsetFromCorner = true;

    [ObservableProperty]
    private bool _offsetNoMargin = true;

    [ObservableProperty]
    private bool _offsetPrinterLimit;

    [ObservableProperty]
    private bool _offsetUserDefined;

    [ObservableProperty]
    private double _offsetX;

    [ObservableProperty]
    private double _offsetY;

    [ObservableProperty]
    private bool _zoomFitToPage;

    [ObservableProperty]
    private bool _zoomCustom = true;

    [ObservableProperty]
    private int _zoomPercentage = 100;

    [ObservableProperty]
    private bool _vectorProcessing = true;

    [ObservableProperty]
    private bool _rasterProcessing;

    [ObservableProperty]
    private string _selectedRasterQuality = "Presentation";

    [ObservableProperty]
    private string _selectedColor = "Color";

    [ObservableProperty]
    private bool _viewLinksInBlue = true;

    [ObservableProperty]
    private bool _hideRefWorkPlanes = true;

    [ObservableProperty]
    private bool _hideUnreferencedViewTags = true;

    [ObservableProperty]
    private bool _hideScopeBoxes = true;

    [ObservableProperty]
    private bool _hideCropBoundaries = true;

    [ObservableProperty]
    private bool _replaceHalftoneWithThinLines;

    [ObservableProperty]
    private bool _regionEdgesMaskCoincidentLines;
  }
}

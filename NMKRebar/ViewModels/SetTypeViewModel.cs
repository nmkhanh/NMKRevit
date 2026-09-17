using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKRebar.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Forms = System.Windows.Forms;

namespace NMKRebar.ViewModels
{
  public partial class ShapeParameterRow : ObservableObject
  {
    public ShapeParameterRow(string name, bool isYesNo, string value)
    {
      Name = name;
      IsYesNo = isYesNo;
      _value = value ?? string.Empty;
    }

    public string Name { get; }

    public bool IsYesNo { get; }

    [ObservableProperty]
    private string _value = string.Empty;

    public bool IsChecked
    {
      get => VerticalCsvService.TryParseYesNo(Value, out int yesNo) && yesNo == 1;
      set
      {
        Value = value ? "Yes" : "No";
        OnPropertyChanged();
      }
    }

    partial void OnValueChanged(string value)
    {
      OnPropertyChanged(nameof(IsChecked));
    }

    public ShapeParameterValue ToValue()
    {
      return new ShapeParameterValue(Name, Value, IsYesNo);
    }
  }

  public partial class DimensionRow : ObservableObject
  {
    public DimensionRow(int index)
    {
      Index = index;
    }

    public int Index { get; }

    [ObservableProperty]
    private string _angleDegrees = string.Empty;

    [ObservableProperty]
    private string _angleMinutes = string.Empty;

    [ObservableProperty]
    private string _angleSeconds = string.Empty;

    [ObservableProperty]
    private string _bending = string.Empty;

    [ObservableProperty]
    private string _l = string.Empty;

    [ObservableProperty]
    private bool _visible;

    [ObservableProperty]
    private bool _hookFromD;

    private string _manualL = string.Empty;

    public bool TryAngleDecimal(out string decimalDegrees)
    {
      return AngleDms.TryCombine(AngleDegrees, AngleMinutes, AngleSeconds, out decimalDegrees);
    }

    public IEnumerable<ShapeParameterValue> ToValues()
    {
      TryAngleDecimal(out string angle);
      yield return new ShapeParameterValue($"{Index}_V", Visible ? "Yes" : "No", true);
      yield return new ShapeParameterValue($"{Index}_Angle", angle, false);
      yield return new ShapeParameterValue($"{Index}_Bending", Bending, false);
      yield return new ShapeParameterValue($"{Index}_L", L, false);
    }

    public bool UsesHookFromD => HookFromD && Visible;

    public void ApplyDisplayedLength(double? diameterMm, bool curve)
    {
      if (UsesHookFromD
          && diameterMm is > 0
          && TryHookFactor(curve, out double factor))
      {
        L = SetTypeEditorService.FormatBarMultipleMm(diameterMm.Value, factor);
        return;
      }

      if (!UsesHookFromD)
      {
        L = _manualL;
      }
    }

    public bool TryHookFactor(bool curve, out double factor)
    {
      factor = 0;
      if (!TryAngleDecimal(out string decimalDegrees)
          || !VerticalCsvService.TryParseNumber(decimalDegrees, out double angle))
      {
        return false;
      }

      if (Math.Abs(angle - 90) < 0.5)
      {
        factor = curve ? 12 : 15;
        return true;
      }

      if (Math.Abs(angle) < 0.5)
      {
        factor = 8;
        return true;
      }

      return false;
    }

    public bool IsAngleZero()
    {
      return UsesHookFromD
        && TryHookFactor(true, out double factor)
        && Math.Abs(factor - 8) < 0.001;
    }

    partial void OnLChanged(string value)
    {
      if (!UsesHookFromD)
      {
        _manualL = value ?? string.Empty;
      }
    }

    public void LoadFrom(IReadOnlyDictionary<string, ShapeParameterValue> values)
    {
      Visible = VerticalCsvService.TryParseYesNo(ValueOf($"{Index}_V", values), out int yesNo) && yesNo == 1;
      AngleDms.Split(ValueOf($"{Index}_Angle", values), out string degrees, out string minutes, out string seconds);
      AngleDegrees = degrees;
      AngleMinutes = minutes;
      AngleSeconds = seconds;
      Bending = ValueOf($"{Index}_Bending", values);
      string loaded = ValueOf($"{Index}_L", values);
      _manualL = loaded;
      L = loaded;
      HookFromD = false;
    }

    public void Clear()
    {
      Visible = false;
      HookFromD = false;
      AngleDegrees = string.Empty;
      AngleMinutes = string.Empty;
      AngleSeconds = string.Empty;
      Bending = string.Empty;
      _manualL = string.Empty;
      L = string.Empty;
    }

    private static string ValueOf(string name, IReadOnlyDictionary<string, ShapeParameterValue> values)
    {
      return values.TryGetValue(name, out ShapeParameterValue row) ? row.Value ?? string.Empty : string.Empty;
    }
  }

  public partial class ZInputRow : ObservableObject
  {
    public ZInputRow(int index)
    {
      Index = index;
    }

    public int Index { get; }

    [ObservableProperty]
    private string _x = string.Empty;

    [ObservableProperty]
    private string _y = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;
  }

  public partial class SetTypeViewModel : ObservableObject
  {
    private readonly UIApplication _uiapp;
    private bool _siteShownHideRebar;
    private bool _ready;
    private bool _loadingType;
    private bool _loadingShape;
    private bool _applyingHook;
    private string _savedTypeName = string.Empty;
    private double? _barDiameterMm;

    public SetTypeViewModel(UIApplication uiapp)
    {
      _uiapp = uiapp;
      for (int n = 1; n <= SetTypeEditorService.ZCount; n++)
      {
        ZRows.Add(new ZInputRow(n));
      }

      for (int n = SetTypeEditorService.DimensionStart; n <= SetTypeEditorService.DimensionEnd; n++)
      {
        var row = new DimensionRow(n);
        row.PropertyChanged += OnDimensionRowPropertyChanged;
        DimensionRows.Add(row);
      }

      CurveRow.PropertyChanged += OnCurveRowPropertyChanged;

      var settings = NMKRebar.Properties.Settings.Default;
      DataFolder = settings.DataFolder ?? string.Empty;
      TypeSearch = settings.SetTypeSearch ?? string.Empty;
      CreateAsFreeForm = settings.CreateAsFreeForm;
      foreach (string name in VariesLengthParameters.Names)
      {
        VarriesLengthOptions.Add(name);
      }

      string savedLength = settings.VarriesLengthParameter ?? "A";
      SelectedVarriesLengthParameter = VariesLengthParameters.Normalize(savedLength);
      _savedTypeName = settings.LastSetTypeName ?? string.Empty;
      RebarHostElementId = CreateRebarByLineService.ParseHostElementId(settings.LastRebarHostElementId);
      CreateAllFilteredTypes = settings.CreateAllFilteredTypes;
      VariesLineIndex = settings.VariesLineIndex < 1 ? 1 : settings.VariesLineIndex;
      VariesMiddle = settings.VariesMiddle;
      SubtractBending = settings.SubtractBending;
      BendingFactor = settings.BendingFactor <= 0 ? 3 : settings.BendingFactor;
      RevertVaries = settings.RevertVaries;
      SelectTypeRebar = settings.SelectTypeRebar;
      SelectTypeArray = settings.SelectTypeArray;
      ChangeTypeFrom = settings.ChangeTypeFrom ?? string.Empty;
      ChangeTypeTo = settings.ChangeTypeTo ?? string.Empty;
      SameShape2From = settings.SameShape2From ?? string.Empty;
      SameShape2To = settings.SameShape2To ?? string.Empty;
      AddXyBlock = settings.AddXyBlock;
      SameShapeTypeText = settings.LastSameShapeTypeName ?? string.Empty;
      PlaceOneCoupler = settings.PlaceOneCoupler;
      SelectedCouplerFamilyName = string.IsNullOrWhiteSpace(settings.LastCouplerFamilyName)
        ? null
        : settings.LastCouplerFamilyName;
      RebarHostDisplayName = "(no host)";
      _ready = true;
      SaveFolder();
      RefreshCouplerFamilies();
      _ = RefreshHostDisplayAsync();
      _ = ReloadTypesAsync();
    }

    [ObservableProperty]
    private string _dataFolder = string.Empty;

    private readonly List<SetTypeListItem> _allTypeItems = new();

    [ObservableProperty]
    private string _typeSearch = string.Empty;

    [ObservableProperty]
    private string? _selectedTypeName;

    [ObservableProperty]
    private string _status = "Select a folder, then a type.";

    [ObservableProperty]
    private bool _createAsFreeForm;

    [ObservableProperty]
    private string _selectedVarriesLengthParameter = "A";

    [ObservableProperty]
    private bool _createAllFilteredTypes;

    [ObservableProperty]
    private int _variesLineIndex = 1;

    [ObservableProperty]
    private bool _variesMiddle;

    [ObservableProperty]
    private bool _subtractBending = true;

    [ObservableProperty]
    private double _bendingFactor = 3;

    [ObservableProperty]
    private bool _revertVaries;

    [ObservableProperty]
    private bool _selectTypeRebar = true;

    [ObservableProperty]
    private bool _selectTypeArray = true;

    public IReadOnlyList<int> VariesLineIndexOptions { get; } = Enumerable.Range(1, 20).ToList();

    [ObservableProperty]
    private long _rebarHostElementId;

    [ObservableProperty]
    private string _rebarHostDisplayName = "(no host)";

    [ObservableProperty]
    private bool _placeOneCoupler = true;

    [ObservableProperty]
    private string? _selectedCouplerFamilyName;

    public bool PlaceTwoCouplers
    {
      get => !PlaceOneCoupler;
      set
      {
        if (value)
        {
          PlaceOneCoupler = false;
        }
      }
    }

    public ObservableCollection<SetTypeListItem> TypeItems { get; } = new();

    public ObservableCollection<string> VarriesLengthOptions { get; } = new();

    public ObservableCollection<string> CouplerFamilyNames { get; } = new();

    public ShapeParameterRow CurveRow { get; } = new("Curve", true, "No");

    public ShapeParameterRow AngleRow { get; } = new("Angle", false, string.Empty);

    [ObservableProperty]
    private string _angleDegrees = string.Empty;

    [ObservableProperty]
    private string _angleMinutes = string.Empty;

    [ObservableProperty]
    private string _angleSeconds = string.Empty;

    public ObservableCollection<DimensionRow> DimensionRows { get; } = new();

    public ObservableCollection<ZInputRow> ZRows { get; } = new();

    [ObservableProperty]
    private bool _addXyToX = true;

    [ObservableProperty]
    private bool _addXySole;

    [ObservableProperty]
    private bool _addXyStart = true;

    [ObservableProperty]
    private bool _addXyBlock;

    public IReadOnlyList<string> RevertXyzAxisOptions { get; } = new[] { "X", "Y", "Z" };

    [ObservableProperty]
    private string _selectedRevertXyzAxis = "X";

    public string MoveXyButtonText => AddXyToX ? "X → Y" : "Y → X";

    public ObservableCollection<string> SameShapeTypeNames { get; } = new();

    [ObservableProperty]
    private string? _selectedSameShapeType;

    [ObservableProperty]
    private string _sameShapeTypeText = string.Empty;

    [ObservableProperty]
    private string _changeTypeFrom = string.Empty;

    [ObservableProperty]
    private string _changeTypeTo = string.Empty;

    [ObservableProperty]
    private string _sameShape2From = string.Empty;

    [ObservableProperty]
    private string _sameShape2To = string.Empty;

    private bool _syncingSameShapeType;

    [RelayCommand]
    private void BrowseFolder()
    {
      try
      {
        using var dialog = new Forms.FolderBrowserDialog
        {
          Description = "Folder containing TypeShape.csv or Rebar.txt",
          SelectedPath = Directory.Exists(DataFolder) ? DataFolder : string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
          return;
        }

        DataFolder = dialog.SelectedPath;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private async Task SaveShape()
    {
      try
      {
        SaveFolder();
        string typeName = RequireType();
        string folder = DataFolder;
        if (!AngleDms.TryCombine(AngleDegrees, AngleMinutes, AngleSeconds, out string angleDecimal))
        {
          Status = "Angle: enter numeric degrees, minutes, seconds.";
          return;
        }

        foreach (DimensionRow segment in DimensionRows)
        {
          if (!segment.TryAngleDecimal(out _))
          {
            Status = $"{segment.Index}_Angle: enter numeric degrees, minutes, seconds.";
            return;
          }
        }

        AngleRow.Value = angleDecimal;
        List<ShapeParameterValue> rows = new() { CurveRow.ToValue(), AngleRow.ToValue() };
        foreach (DimensionRow segment in DimensionRows)
        {
          rows.AddRange(segment.ToValues());
        }
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          return SetTypeEditorService.SaveShape(doc, folder, typeName, rows).ToMessage();
        });
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void ReloadTypes()
    {
      _ = ReloadTypesAsync();
    }

    [RelayCommand]
    private void CheckQuantities()
    {
      _ = CheckQuantitiesAsync();
    }

    [RelayCommand]
    private void SelectType()
    {
      _ = SelectTypeAsync();
    }

    private async Task CheckQuantitiesAsync()
    {
      try
      {
        Dictionary<string, int> actuals = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          return SetTypeRebarCountService.CountBarsByBarTypeName(doc);
        });

        int match = 0;
        int mismatch = 0;
        foreach (SetTypeListItem item in _allTypeItems)
        {
          actuals.TryGetValue(item.TypeName, out int count);
          item.ApplyActual(count);
          if (item.CheckState == SetTypeQtyCheckState.Match)
          {
            match++;
          }
          else if (item.CheckState == SetTypeQtyCheckState.Mismatch)
          {
            mismatch++;
          }
        }

        Status = $"Check: {match} OK, {mismatch} mismatch ({_allTypeItems.Count} type(s)).";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    private async Task SelectTypeAsync()
    {
      try
      {
        List<string> typeNames = TypeItems.Select(item => item.TypeName).ToList();
        if (typeNames.Count == 0)
        {
          throw new InvalidOperationException("No types in the filtered list.");
        }

        bool rebar = SelectTypeRebar;
        bool array = SelectTypeArray;
        int count = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return SetTypeEditorService.SelectType(uidoc, typeNames, rebar, array);
        });

        Status = $"Select Type: {count} element(s) from {typeNames.Count} filtered type(s).";
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void SelectArrays()
    {
      _ = SelectArraysAsync();
    }

    [RelayCommand]
    private void SelectHost()
    {
      _ = SelectHostAsync();
    }

    private async Task SelectHostAsync()
    {
      try
      {
        (long id, string label) = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          Element host = CreateRebarByLineService.PickRebarHost(uidoc);
          return (CreateRebarByLineService.IdValue(host.Id), CreateRebarByLineService.FormatHostDisplayName(host));
        });

        RebarHostElementId = id;
        RebarHostDisplayName = label;
        SaveFolder();
        Status = $"Host: {label}";
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Host selection cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    private async Task RefreshHostDisplayAsync()
    {
      if (RebarHostElementId == 0)
      {
        RebarHostDisplayName = "(no host)";
        return;
      }

      try
      {
        string label = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document? doc = uiapp.ActiveUIDocument?.Document;
          if (doc == null)
          {
            return "(no host)";
          }

          Element? host = doc.GetElement(CreateRebarByLineService.ToElementId(RebarHostElementId));
          return CreateRebarByLineService.FormatHostDisplayName(host);
        });
        RebarHostDisplayName = label;
      }
      catch
      {
        RebarHostDisplayName = $"(host {RebarHostElementId})";
      }
    }

    private async Task SelectArraysAsync()
    {
      try
      {
        string typeName = RequireType();
        int count = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return SetTypeEditorService.SelectArrays(uidoc, typeName);
        });

        Status = $"Selected {count} instance(s) of {typeName}.";
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Selection cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void CreateRebar()
    {
      _ = CreateRebarAsync();
    }

    private async Task CreateRebarAsync()
    {
      try
      {
        SaveFolder();
        List<string> filteredTypes = TypeItems.Select(item => item.TypeName).ToList();
        if (!CreateAllFilteredTypes)
        {
          filteredTypes = new List<string> { RequireType() };
        }
        else if (filteredTypes.Count == 0)
        {
          throw new InvalidOperationException("No types in the filtered list.");
        }

        string folder = DataFolder;
        bool freeForm = CreateAsFreeForm;
        long hostId = RebarHostElementId;
        bool allTypes = CreateAllFilteredTypes;
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return CreateRebarByLineService.CreateForSetType(uidoc, new SetTypeCreateRebarRequest
          {
            Folder = folder,
            UseFreeForm = freeForm,
            AllFilteredTypes = allTypes,
            FilteredTypeNames = filteredTypes,
            SelectedTypeName = SelectedTypeName,
            HostElementId = hostId,
            VariesLineIndex = VariesLineIndex,
            VariesMiddle = VariesMiddle,
            SubtractBending = SubtractBending,
            BendingFactor = BendingFactor,
            RevertVaries = RevertVaries
          }).ToMessage();
        });

        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Selection cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void AddXyData()
    {
      _ = AddXyDataAsync();
    }

    private async Task AddXyDataAsync()
    {
      try
      {
        bool toX = AddXyToX;
        bool sole = AddXySole;
        bool start = AddXyStart;
        bool block = AddXyBlock;
        SetTypeEditorService.AddXyDataResult computed = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return SetTypeEditorService.AddXyzFromPicks(uidoc, toX, sole, start);
        });

        for (int i = 0; i < ZRows.Count; i++)
        {
          ZRows[i].Text = i < computed.ZValues.Count ? computed.ZValues[i] : string.Empty;
          string axis = i < computed.AxisValues.Count ? computed.AxisValues[i] : string.Empty;
          if (computed.ToX)
          {
            ZRows[i].X = axis;
            if (!block)
            {
              ZRows[i].Y = string.Empty;
            }
          }
          else
          {
            ZRows[i].Y = axis;
            if (!block)
            {
              ZRows[i].X = string.Empty;
            }
          }
        }

        string axisName = computed.ToX ? "X" : "Y";
        Status = $"Add XYZ data: {computed.ZValues.Count} Z, {computed.AxisValues.Count} {axisName}. SET to write.";
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Pick cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void RevertXyz()
    {
      try
      {
        List<string> axisTexts = string.Equals(SelectedRevertXyzAxis, "Y", StringComparison.OrdinalIgnoreCase)
          ? ZRows.Select(row => row.Y).ToList()
          : ZRows.Select(row => row.X).ToList();
        if (string.Equals(SelectedRevertXyzAxis, "Z", StringComparison.OrdinalIgnoreCase))
        {
          axisTexts = ZRows.Select(row => row.Text).ToList();
        }

        SetTypeEditorService.RevertXyzResult reverted = SetTypeEditorService.RevertXyz(
          SelectedRevertXyzAxis,
          axisTexts);
        IReadOnlyList<string> encoded = ZInputParser.EncodeSpacingsForDisplay(
          reverted.Values,
          SetTypeEditorService.ZCount);
        for (int i = 0; i < ZRows.Count; i++)
        {
          string text = i < encoded.Count ? encoded[i] ?? string.Empty : string.Empty;
          if (string.Equals(reverted.Axis, "Z", StringComparison.OrdinalIgnoreCase))
          {
            ZRows[i].Text = text;
          }
          else if (string.Equals(reverted.Axis, "Y", StringComparison.OrdinalIgnoreCase))
          {
            ZRows[i].Y = text;
          }
          else
          {
            ZRows[i].X = text;
          }
        }

        Status = reverted.Message;
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void MoveXy()
    {
      try
      {
        bool toY = AddXyToX;
        int moved = 0;
        for (int i = 0; i < ZRows.Count; i++)
        {
          if (toY)
          {
            if (string.IsNullOrWhiteSpace(ZRows[i].X))
            {
              continue;
            }

            ZRows[i].Y = ZRows[i].X;
            ZRows[i].X = string.Empty;
            moved++;
          }
          else
          {
            if (string.IsNullOrWhiteSpace(ZRows[i].Y))
            {
              continue;
            }

            ZRows[i].X = ZRows[i].Y;
            ZRows[i].Y = string.Empty;
            moved++;
          }
        }

        if (moved == 0)
        {
          Status = toY ? "No X values to move to Y." : "No Y values to move to X.";
          return;
        }

        Status = toY
          ? $"Moved {moved} X value(s) to Y. SET to write."
          : $"Moved {moved} Y value(s) to X. SET to write.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void RefreshXyz()
    {
      try
      {
        for (int i = 0; i < ZRows.Count; i++)
        {
          bool first = i == 0;
          ZRows[i].X = first ? "0" : string.Empty;
          ZRows[i].Y = first ? "0" : string.Empty;
          ZRows[i].Text = first ? "0" : string.Empty;
        }

        Status = "XYZ refreshed: X1/Y1/Z1=0, other rows cleared. SET to write.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void SelectModelInPlace()
    {
      _ = SelectModelInPlaceAsync();
    }

    private async Task SelectModelInPlaceAsync()
    {
      try
      {
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return DirectShapeFromInPlaceService.CreateFromPicked(uidoc);
        });
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Select Model In-Place cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void SameShape()
    {
      _ = SameShapeAsync();
    }

    private async Task SameShapeAsync()
    {
      try
      {
        SaveFolder();
        string target = RequireType();
        string source = ResolveSameShapeType()
          ?? throw new InvalidOperationException("Select a source type in the combo.");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
          throw new InvalidOperationException("Source and listbox type are the same.");
        }

        string folder = DataFolder;
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          List<ShapeParameterValue> rows = SetTypeEditorService.LoadShapeParameters(doc, folder, source)
            .Where(row => !string.Equals(row.Name, "d", StringComparison.OrdinalIgnoreCase))
            .ToList();
          return SetTypeEditorService.SaveShape(doc, folder, target, rows).ToMessage();
        });
        await LoadShapeAsync();
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void SameShape2()
    {
      _ = SameShape2Async();
    }

    private async Task SameShape2Async()
    {
      try
      {
        SaveFolder();
        string from = SameShape2From;
        string to = SameShape2To;
        string folder = DataFolder;
        List<string> typeNames = TypeItems.Select(item => item.TypeName).ToList();
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          return SetTypeEditorService.CopyShapesByNameReplace(doc, folder, typeNames, from, to);
        });
        await LoadShapeAsync();
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void ChangeArrayType()
    {
      _ = ChangeArrayTypeAsync();
    }

    private async Task ChangeArrayTypeAsync()
    {
      try
      {
        SaveFolder();
        string from = ChangeTypeFrom;
        string to = ChangeTypeTo;
        List<string> typeNames = TypeItems.Select(item => item.TypeName).ToList();
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return SetTypeEditorService.ChangeArrayTypesFromTo(uidoc, typeNames, from, to);
        });
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Change cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void RenameViewByCad()
    {
      _ = RenameViewByCadAsync();
    }

    private async Task RenameViewByCadAsync()
    {
      try
      {
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return RenameViewByCadService.RenameSelectedViews(uidoc);
        });
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void GroupRebarByType()
    {
      _ = GroupRebarByTypeAsync();
    }

    private async Task GroupRebarByTypeAsync()
    {
      try
      {
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return GroupRebarByTypeService.GroupPicked(uidoc);
        });
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Group cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void ToggleSiteRebar()
    {
      _ = ToggleSiteRebarAsync();
    }

    private async Task ToggleSiteRebarAsync()
    {
      try
      {
        bool showSite = !_siteShownHideRebar;
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          Autodesk.Revit.DB.View view = doc.ActiveView ?? throw new InvalidOperationException("No active view.");
          return ViewCategoryVisibilityService.ToggleSiteVersusRebar(view, showSite);
        });
        _siteShownHideRebar = showSite;
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void ShowAllCategories()
    {
      _ = ShowAllCategoriesAsync();
    }

    private async Task ShowAllCategoriesAsync()
    {
      try
      {
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          Autodesk.Revit.DB.View view = doc.ActiveView ?? throw new InvalidOperationException("No active view.");
          return ViewCategoryVisibilityService.ShowAllCategories(view);
        });
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void PlaceCoupler()
    {
      _ = PlaceCouplerAsync();
    }

    private async Task PlaceCouplerAsync()
    {
      try
      {
        SaveFolder();
        string typeName = RequireType();
        string family = SelectedCouplerFamilyName
          ?? throw new InvalidOperationException("Select a coupler family.");
        if (string.IsNullOrWhiteSpace(family))
        {
          throw new InvalidOperationException("Select a coupler family.");
        }

        bool placeOne = PlaceOneCoupler;
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return PlaceCouplerService.PlaceForBarType(uidoc, typeName, family, placeOne).ToMessage();
        });

        RefreshCouplerFamilies();
        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Coupler cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    partial void OnCreateAsFreeFormChanged(bool value) => SaveFolder();

    partial void OnSelectedVarriesLengthParameterChanged(string value) => SaveFolder();

    partial void OnCreateAllFilteredTypesChanged(bool value) => SaveFolder();

    partial void OnVariesLineIndexChanged(int value) => SaveFolder();

    partial void OnVariesMiddleChanged(bool value) => SaveFolder();

    partial void OnSubtractBendingChanged(bool value) => SaveFolder();

    partial void OnBendingFactorChanged(double value) => SaveFolder();

    partial void OnRevertVariesChanged(bool value) => SaveFolder();

    partial void OnSelectTypeRebarChanged(bool value) => SaveFolder();

    partial void OnSelectTypeArrayChanged(bool value) => SaveFolder();

    partial void OnChangeTypeFromChanged(string value) => SaveFolder();

    partial void OnChangeTypeToChanged(string value) => SaveFolder();

    partial void OnSameShape2FromChanged(string value) => SaveFolder();

    partial void OnSameShape2ToChanged(string value) => SaveFolder();

    partial void OnAddXyToXChanged(bool value) => OnPropertyChanged(nameof(MoveXyButtonText));

    partial void OnAddXyBlockChanged(bool value) => SaveFolder();

    partial void OnPlaceOneCouplerChanged(bool value)
    {
      OnPropertyChanged(nameof(PlaceTwoCouplers));
      SaveFolder();
    }

    partial void OnSelectedCouplerFamilyNameChanged(string? value) => SaveFolder();

    [RelayCommand]
    private void SelectInstances()
    {
      _ = SelectInstancesAsync();
    }

    private async Task SelectInstancesAsync()
    {
      try
      {
        SetTypeEditorService.SelectInstanceZResult loaded = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return SetTypeEditorService.SelectInstanceAndLoadZ(uidoc);
        });

        if (!string.IsNullOrWhiteSpace(loaded.TypeName)
            && TypeItems.Any(item => item.TypeName.Equals(loaded.TypeName, StringComparison.OrdinalIgnoreCase)))
        {
          SelectedTypeName = TypeItems.First(item =>
            item.TypeName.Equals(loaded.TypeName, StringComparison.OrdinalIgnoreCase)).TypeName;
        }

        for (int i = 0; i < ZRows.Count; i++)
        {
          ZRows[i].Text = i < loaded.ZTexts.Count ? loaded.ZTexts[i] ?? string.Empty : string.Empty;
          ZRows[i].X = i < loaded.XTexts.Count ? loaded.XTexts[i] ?? string.Empty : string.Empty;
          ZRows[i].Y = i < loaded.YTexts.Count ? loaded.YTexts[i] ?? string.Empty : string.Empty;
        }

        string typeLabel = string.IsNullOrWhiteSpace(loaded.TypeName) ? "instance" : loaded.TypeName;
        int filled = loaded.ZTexts.Count(text => !string.IsNullOrWhiteSpace(text));
        Status = filled == 0
          ? $"Selected {typeLabel}. No Z ≤ 0 on instance."
          : $"Selected {typeLabel}. Loaded {filled} Z row(s) from instance.";
        if (loaded.Warnings.Count > 0)
        {
          Status += " " + loaded.Warnings[0];
        }

        RevitTaskRun.Wake(_uiapp);
      }
      catch (Autodesk.Revit.Exceptions.OperationCanceledException)
      {
        Status = "Selection cancelled.";
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    [RelayCommand]
    private void SetZ()
    {
      _ = SetZAsync();
    }

    private async Task SetZAsync()
    {
      try
      {
        List<string> texts = ZRows.Select(row => row.Text).ToList();
        List<string> xs = ZRows.Select(row => row.X).ToList();
        List<string> ys = ZRows.Select(row => row.Y).ToList();
        Status = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          UIDocument uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
          return SetTypeEditorService.SetZOnSelection(uidoc, texts, xs, ys).ToMessage();
        });

        RevitTaskRun.Wake(_uiapp);
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    partial void OnDataFolderChanged(string value)
    {
      SaveFolder();
      if (_ready)
      {
        _ = ReloadTypesAsync();
      }
    }

    partial void OnSelectedTypeNameChanged(string? value)
    {
      if (_ready && !_loadingType)
      {
        SaveFolder();
        _ = LoadShapeAsync();
      }
    }

    partial void OnTypeSearchChanged(string value)
    {
      if (_ready)
      {
        ApplyTypeFilter(keepSelection: true);
        SaveFolder();
      }
    }

    private async Task ReloadTypesAsync()
    {
      try
      {
        string folder = DataFolder;
        string? keep = SelectedTypeName;
        if (string.IsNullOrWhiteSpace(keep) && !string.IsNullOrWhiteSpace(_savedTypeName))
        {
          keep = _savedTypeName;
          _savedTypeName = string.Empty;
        }

        List<string> names = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          return SetTypeEditorService.CollectTypeNames(doc, folder).ToList();
        });

        Dictionary<string, int> expected = RebarTxtQuantityService.LoadExpectedQuantities(folder);
        _allTypeItems.Clear();
        foreach (string name in names)
        {
          var item = new SetTypeListItem(name);
          if (expected.TryGetValue(name, out int qty))
          {
            item.ExpectedQty = qty;
          }

          item.ResetCheck();
          _allTypeItems.Add(item);
        }

        ApplyTypeFilter(keepSelection: false, keep);
        RefreshSameShapeTypes();
        await LoadShapeAsync();
        string? varies = SetRebarVariesService.FindVariesFile(folder, SelectedTypeName ?? string.Empty);
        int txtFiles = RebarTxtQuantityService.EnumerateTxtFiles(folder).Count;
        Status = _allTypeItems.Count == 0
          ? "No types found. Select a folder with TypeShape.csv or load NMK_Rebar_Array."
          : string.IsNullOrWhiteSpace(SelectedTypeName)
            ? $"Loaded {_allTypeItems.Count} type(s) from {txtFiles} Rebar.txt file(s). Press Check for model counts."
            : varies == null
              ? $"Loaded {_allTypeItems.Count} type(s). {SelectedTypeName}: no varies file."
              : $"Loaded {_allTypeItems.Count} type(s). {SelectedTypeName} varies: {Path.GetFileName(varies)}.";
      }
      catch (Exception ex)
      {
        _loadingType = false;
        Status = ex.Message;
      }
    }

    private void ApplyTypeFilter(bool keepSelection, string? preferred = null)
    {
      string? keep = preferred ?? SelectedTypeName;
      string search = (TypeSearch ?? string.Empty).Trim();
      IEnumerable<SetTypeListItem> filtered = _allTypeItems;
      if (search.Length > 0)
      {
        filtered = _allTypeItems.Where(item =>
          item.TypeName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
      }

      _loadingType = true;
      TypeItems.Clear();
      foreach (SetTypeListItem item in filtered.OrderBy(item => item.TypeName, Comparer<string>.Create(PlaceCouplerService.CompareNumericNames)))
      {
        TypeItems.Add(item);
      }

      if (!string.IsNullOrWhiteSpace(keep)
          && TypeItems.Any(item => item.TypeName.Equals(keep, StringComparison.OrdinalIgnoreCase)))
      {
        SelectedTypeName = TypeItems.First(item => item.TypeName.Equals(keep, StringComparison.OrdinalIgnoreCase)).TypeName;
      }
      else if (!keepSelection)
      {
        SelectedTypeName = TypeItems.FirstOrDefault()?.TypeName;
      }
      else if (SelectedTypeName != null
          && !TypeItems.Any(item => item.TypeName.Equals(SelectedTypeName, StringComparison.OrdinalIgnoreCase)))
      {
        SelectedTypeName = TypeItems.FirstOrDefault()?.TypeName;
      }

      _loadingType = false;
    }

    private void RefreshSameShapeTypes()
    {
      string? keep = SelectedSameShapeType;
      if (string.IsNullOrWhiteSpace(keep) && !string.IsNullOrWhiteSpace(SameShapeTypeText))
      {
        keep = SameShapeTypeText;
      }

      SameShapeTypeNames.Clear();
      foreach (SetTypeListItem item in _allTypeItems)
      {
        SameShapeTypeNames.Add(item.TypeName);
      }

      string? selected = null;
      if (!string.IsNullOrWhiteSpace(keep))
      {
        selected = SameShapeTypeNames.FirstOrDefault(name =>
          name.Equals(keep, StringComparison.OrdinalIgnoreCase));
        selected ??= FindNearestTypeName(keep);
      }

      selected ??= SameShapeTypeNames.FirstOrDefault();
      _syncingSameShapeType = true;
      SelectedSameShapeType = selected;
      SameShapeTypeText = selected ?? string.Empty;
      _syncingSameShapeType = false;
    }

    partial void OnSelectedSameShapeTypeChanged(string? value)
    {
      if (_syncingSameShapeType)
      {
        return;
      }

      _syncingSameShapeType = true;
      SameShapeTypeText = value ?? string.Empty;
      _syncingSameShapeType = false;
      SaveFolder();
    }

    partial void OnSameShapeTypeTextChanged(string value)
    {
      if (_syncingSameShapeType || !_ready)
      {
        return;
      }

      string? nearest = FindNearestTypeName(value);
      if (nearest == null)
      {
        return;
      }

      _syncingSameShapeType = true;
      SelectedSameShapeType = nearest;
      SameShapeTypeText = nearest;
      _syncingSameShapeType = false;
    }

    private string? ResolveSameShapeType()
    {
      if (!string.IsNullOrWhiteSpace(SelectedSameShapeType)
          && SameShapeTypeNames.Any(name => name.Equals(SelectedSameShapeType, StringComparison.OrdinalIgnoreCase)))
      {
        return SameShapeTypeNames.First(name =>
          name.Equals(SelectedSameShapeType, StringComparison.OrdinalIgnoreCase));
      }

      return FindNearestTypeName(SameShapeTypeText);
    }

    private string? FindNearestTypeName(string? typed)
    {
      if (SameShapeTypeNames.Count == 0)
      {
        return null;
      }

      string text = (typed ?? string.Empty).Trim();
      if (text.Length == 0)
      {
        return SameShapeTypeNames.FirstOrDefault();
      }

      string? exact = SameShapeTypeNames.FirstOrDefault(name =>
        name.Equals(text, StringComparison.OrdinalIgnoreCase));
      if (exact != null)
      {
        return exact;
      }

      List<string> starts = SameShapeTypeNames
        .Where(name => name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        .OrderBy(name => name.Length)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (starts.Count > 0)
      {
        return starts[0];
      }

      List<string> contains = SameShapeTypeNames
        .Where(name => name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
        .OrderBy(name => name.Length)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (contains.Count > 0)
      {
        return contains[0];
      }

      return SameShapeTypeNames
        .OrderBy(name => Levenshtein(name, text))
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .First();
    }

    private static int Levenshtein(string left, string right)
    {
      string a = left.ToLowerInvariant();
      string b = right.ToLowerInvariant();
      int n = a.Length;
      int m = b.Length;
      var d = new int[n + 1, m + 1];
      for (int i = 0; i <= n; i++)
      {
        d[i, 0] = i;
      }

      for (int j = 0; j <= m; j++)
      {
        d[0, j] = j;
      }

      for (int i = 1; i <= n; i++)
      {
        for (int j = 1; j <= m; j++)
        {
          int cost = a[i - 1] == b[j - 1] ? 0 : 1;
          d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
      }

      return d[n, m];
    }

    private void OnCurveRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
      if (_loadingShape || !_ready)
      {
        return;
      }

      if (e.PropertyName != nameof(ShapeParameterRow.Value)
          && e.PropertyName != nameof(ShapeParameterRow.IsChecked))
      {
        return;
      }

      ApplyStraightBendingIfNeeded();
    }

    private void OnDimensionRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
      if (_loadingShape || !_ready || _applyingHook)
      {
        return;
      }

      if (e.PropertyName != nameof(DimensionRow.HookFromD) || sender is not DimensionRow row)
      {
        return;
      }

      ApplyHookLengthForRow(row);
    }

    private void ApplyHookLengthForRow(DimensionRow row)
    {
      if (_loadingShape || !_ready || _applyingHook)
      {
        return;
      }

      _applyingHook = true;
      try
      {
        if (row.IsAngleZero() && !CurveRow.IsChecked)
        {
          CurveRow.IsChecked = true;
        }

        TryResolveBarDiameter(out double? diameterMm);
        if (row.UsesHookFromD
            && row.TryHookFactor(CurveRow.IsChecked, out _)
            && diameterMm is not > 0)
        {
          Status = $"{row.Index}: need _Dxx (or d) for 12*d / 15*d / 8*d.";
          return;
        }

        row.ApplyDisplayedLength(diameterMm, CurveRow.IsChecked);
      }
      finally
      {
        _applyingHook = false;
      }
    }

    private bool ApplyStraightBendingIfNeeded()
    {
      if (CurveRow.IsChecked)
      {
        return true;
      }

      if (!_barDiameterMm.HasValue)
      {
        if (!SetTypeEditorService.TryParseBarDiameterMm(SelectedTypeName ?? string.Empty, out double fromName))
        {
          return false;
        }

        _barDiameterMm = fromName;
      }

      string bending = SetTypeEditorService.FormatStraightBendingMm(_barDiameterMm.Value);
      foreach (DimensionRow segment in DimensionRows)
      {
        segment.Bending = bending;
      }

      return true;
    }

    private bool TryResolveBarDiameter(out double? diameterMm)
    {
      if (_barDiameterMm is > 0)
      {
        diameterMm = _barDiameterMm;
        return true;
      }

      if (SetTypeEditorService.TryParseBarDiameterMm(SelectedTypeName ?? string.Empty, out double fromName))
      {
        _barDiameterMm = fromName;
        diameterMm = fromName;
        return true;
      }

      diameterMm = null;
      return false;
    }

    private void ApplyAngleDmsFromDecimal(string decimalDegrees)
    {
      AngleDms.Split(decimalDegrees, out string degrees, out string minutes, out string seconds);
      AngleDegrees = degrees;
      AngleMinutes = minutes;
      AngleSeconds = seconds;
    }

    private void ClearAngleDms()
    {
      AngleDegrees = string.Empty;
      AngleMinutes = string.Empty;
      AngleSeconds = string.Empty;
    }

    private async Task LoadShapeAsync()
    {
      if (string.IsNullOrWhiteSpace(SelectedTypeName))
      {
        CurveRow.Value = "No";
        AngleRow.Value = string.Empty;
        ClearAngleDms();
        _barDiameterMm = null;
        foreach (DimensionRow segment in DimensionRows)
        {
          segment.Clear();
        }

        return;
      }

      try
      {
        string folder = DataFolder;
        string typeName = SelectedTypeName!;
        List<ShapeParameterValue> rows = await RevitTaskRun.Async(_uiapp, uiapp =>
        {
          Document doc = RequireProject(uiapp);
          return SetTypeEditorService.LoadShapeParameters(doc, folder, typeName);
        });

        var values = new Dictionary<string, ShapeParameterValue>(StringComparer.OrdinalIgnoreCase);
        foreach (ShapeParameterValue row in rows)
        {
          values[row.Name] = row;
        }

        _loadingShape = true;
        try
        {
          CurveRow.Value = values.TryGetValue("Curve", out ShapeParameterValue curve)
            ? curve.Value ?? "No"
            : "No";
          AngleRow.Value = values.TryGetValue("Angle", out ShapeParameterValue angle)
            ? angle.Value ?? string.Empty
            : string.Empty;
          ApplyAngleDmsFromDecimal(AngleRow.Value);
          foreach (DimensionRow segment in DimensionRows)
          {
            segment.LoadFrom(values);
          }
        }
        finally
        {
          _loadingShape = false;
        }

        if (SetTypeEditorService.TryResolveBarDiameterMm(typeName, values, out double diameterMm))
        {
          _barDiameterMm = diameterMm;
        }
        else
        {
          _barDiameterMm = null;
        }

        ApplyStraightBendingIfNeeded();

        string? varies = SetRebarVariesService.FindVariesFile(folder, typeName);
        if (varies != null)
        {
          Status = $"{typeName} varies: {Path.GetFileName(varies)}.";
        }
      }
      catch (Exception ex)
      {
        Status = ex.Message;
      }
    }

    private void RefreshCouplerFamilies()
    {
      string? keepFamily = SelectedCouplerFamilyName;
      CouplerFamilyNames.Clear();
      Document? doc = _uiapp.ActiveUIDocument?.Document;
      if (doc == null || doc.IsFamilyDocument)
      {
        return;
      }

      foreach (string name in PlaceCouplerService.CollectCouplerFamilyNames(doc))
      {
        CouplerFamilyNames.Add(name);
      }

      if (!string.IsNullOrWhiteSpace(keepFamily)
          && CouplerFamilyNames.Any(name => name.Equals(keepFamily, StringComparison.OrdinalIgnoreCase)))
      {
        SelectedCouplerFamilyName = CouplerFamilyNames.First(name =>
          name.Equals(keepFamily, StringComparison.OrdinalIgnoreCase));
      }
      else
      {
        string? preferred = CouplerFamilyNames.FirstOrDefault(name =>
          name.Equals("カプラー", StringComparison.OrdinalIgnoreCase));
        SelectedCouplerFamilyName = preferred ?? CouplerFamilyNames.FirstOrDefault();
      }
    }

    private string RequireType()
    {
      if (string.IsNullOrWhiteSpace(SelectedTypeName))
      {
        throw new InvalidOperationException("Select a type in the list.");
      }

      return SelectedTypeName!;
    }

    public void PersistSettings() => SaveFolder();

    private void SaveFolder()
    {
      if (!_ready)
      {
        return;
      }

      var settings = NMKRebar.Properties.Settings.Default;
      settings.DataFolder = DataFolder ?? string.Empty;
      settings.SetTypeSearch = TypeSearch ?? string.Empty;
      settings.LastSetTypeName = SelectedTypeName ?? string.Empty;
      settings.CreateAsFreeForm = CreateAsFreeForm;
      settings.VarriesLengthParameter = VariesLengthParameters.Normalize(SelectedVarriesLengthParameter);
      settings.CreateAllFilteredTypes = CreateAllFilteredTypes;
      settings.VariesLineIndex = VariesLineIndex;
      settings.VariesMiddle = VariesMiddle;
      settings.SubtractBending = SubtractBending;
      settings.BendingFactor = BendingFactor <= 0 ? 3 : BendingFactor;
      settings.RevertVaries = RevertVaries;
      settings.SelectTypeRebar = SelectTypeRebar;
      settings.SelectTypeArray = SelectTypeArray;
      settings.ChangeTypeFrom = ChangeTypeFrom ?? string.Empty;
      settings.ChangeTypeTo = ChangeTypeTo ?? string.Empty;
      settings.SameShape2From = SameShape2From ?? string.Empty;
      settings.SameShape2To = SameShape2To ?? string.Empty;
      settings.AddXyBlock = AddXyBlock;
      settings.LastSameShapeTypeName = SelectedSameShapeType ?? SameShapeTypeText ?? string.Empty;
      settings.PlaceOneCoupler = PlaceOneCoupler;
      settings.LastCouplerFamilyName = SelectedCouplerFamilyName ?? string.Empty;
      settings.LastRebarHostElementId = RebarHostElementId == 0
        ? string.Empty
        : RebarHostElementId.ToString(System.Globalization.CultureInfo.InvariantCulture);
      settings.Save();
    }

    private static Document RequireProject(UIApplication uiapp)
    {
      Document doc = uiapp.ActiveUIDocument?.Document
        ?? throw new InvalidOperationException("No active document.");
      if (doc.IsFamilyDocument)
      {
        throw new InvalidOperationException("This command runs in a project document.");
      }

      return doc;
    }
  }
}

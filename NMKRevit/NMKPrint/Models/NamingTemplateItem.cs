using CommunityToolkit.Mvvm.ComponentModel;

namespace NMKRevit.NMKPrint.Models
{
  public class NamingTemplateItem : ObservableObject
  {
    private string? _fontWeight;
    private string? _id;
    private int _index;
    private int _indexOld;
    private bool _isChecked;
    private object? _parameterInfo;
    private string? _type;
    private string? _name;
    private string? _prefix;
    private string? _suffix;
    private string? _separator;

    public string? FontWeight
    {
      get => _fontWeight;
      set => SetProperty(ref _fontWeight, value);
    }

    public string? Id
    {
      get => _id;
      set => SetProperty(ref _id, value);
    }

    public int Index
    {
      get => _index;
      set => SetProperty(ref _index, value);
    }

    public int Index_Old
    {
      get => _indexOld;
      set => SetProperty(ref _indexOld, value);
    }

    public bool IsChecked
    {
      get => _isChecked;
      set => SetProperty(ref _isChecked, value);
    }

    public object? ParameterInfo
    {
      get => _parameterInfo;
      set => SetProperty(ref _parameterInfo, value);
    }

    public string? Type
    {
      get => _type;
      set => SetProperty(ref _type, value);
    }

    public string? Name
    {
      get => _name;
      set => SetProperty(ref _name, value);
    }

    public string? Prefix
    {
      get => _prefix;
      set => SetProperty(ref _prefix, value);
    }

    public string? Suffix
    {
      get => _suffix;
      set => SetProperty(ref _suffix, value);
    }

    public string? Separator
    {
      get => _separator;
      set => SetProperty(ref _separator, value);
    }
  }
}

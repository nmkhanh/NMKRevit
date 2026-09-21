using System.Configuration;

namespace NMKRebar.Properties
{
  internal sealed class Settings : ApplicationSettingsBase
  {
    private static readonly Settings DefaultInstance = (Settings)Synchronized(new Settings());

    public static Settings Default => DefaultInstance;

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string DataFolder
    {
      get => (string)this[nameof(DataFolder)];
      set => this[nameof(DataFolder)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastGroupTypeName
    {
      get => (string)this[nameof(LastGroupTypeName)];
      set => this[nameof(LastGroupTypeName)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastCouplerFamilyName
    {
      get => (string)this[nameof(LastCouplerFamilyName)];
      set => this[nameof(LastCouplerFamilyName)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("True")]
    public bool PlaceOneCoupler
    {
      get => (bool)this[nameof(PlaceOneCoupler)];
      set => this[nameof(PlaceOneCoupler)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("True")]
    public bool PlaceAllSuffixGroups
    {
      get => (bool)this[nameof(PlaceAllSuffixGroups)];
      set => this[nameof(PlaceAllSuffixGroups)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool CreateAsFreeForm
    {
      get => (bool)this[nameof(CreateAsFreeForm)];
      set => this[nameof(CreateAsFreeForm)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("A")]
    public string VarriesLengthParameter
    {
      get => (string)this[nameof(VarriesLengthParameter)];
      set => this[nameof(VarriesLengthParameter)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string SetTypeSearch
    {
      get => (string)this[nameof(SetTypeSearch)];
      set => this[nameof(SetTypeSearch)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastSetTypeName
    {
      get => (string)this[nameof(LastSetTypeName)];
      set => this[nameof(LastSetTypeName)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastRebarHostElementId
    {
      get => (string)this[nameof(LastRebarHostElementId)];
      set => this[nameof(LastRebarHostElementId)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool CreateAllFilteredTypes
    {
      get => (bool)this[nameof(CreateAllFilteredTypes)];
      set => this[nameof(CreateAllFilteredTypes)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("1")]
    public int VariesLineIndex
    {
      get => (int)this[nameof(VariesLineIndex)];
      set => this[nameof(VariesLineIndex)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool VariesMiddle
    {
      get => (bool)this[nameof(VariesMiddle)];
      set => this[nameof(VariesMiddle)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("True")]
    public bool SubtractBending
    {
      get => (bool)this[nameof(SubtractBending)];
      set => this[nameof(SubtractBending)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("3")]
    public double BendingFactor
    {
      get => (double)this[nameof(BendingFactor)];
      set => this[nameof(BendingFactor)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool RevertVaries
    {
      get => (bool)this[nameof(RevertVaries)];
      set => this[nameof(RevertVaries)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("True")]
    public bool SelectTypeRebar
    {
      get => (bool)this[nameof(SelectTypeRebar)];
      set => this[nameof(SelectTypeRebar)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("True")]
    public bool SelectTypeArray
    {
      get => (bool)this[nameof(SelectTypeArray)];
      set => this[nameof(SelectTypeArray)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string ChangeTypeFrom
    {
      get => this[nameof(ChangeTypeFrom)] as string ?? string.Empty;
      set => this[nameof(ChangeTypeFrom)] = value ?? string.Empty;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string ChangeTypeTo
    {
      get => this[nameof(ChangeTypeTo)] as string ?? string.Empty;
      set => this[nameof(ChangeTypeTo)] = value ?? string.Empty;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastViewFamilyTypeName
    {
      get => this[nameof(LastViewFamilyTypeName)] as string ?? string.Empty;
      set => this[nameof(LastViewFamilyTypeName)] = value ?? string.Empty;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastSameShapeTypeName
    {
      get => this[nameof(LastSameShapeTypeName)] as string ?? string.Empty;
      set => this[nameof(LastSameShapeTypeName)] = value ?? string.Empty;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string SameShape2From
    {
      get => this[nameof(SameShape2From)] as string ?? string.Empty;
      set => this[nameof(SameShape2From)] = value ?? string.Empty;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string SameShape2To
    {
      get => this[nameof(SameShape2To)] as string ?? string.Empty;
      set => this[nameof(SameShape2To)] = value ?? string.Empty;
    }

    [UserScopedSetting]
    [DefaultSettingValue("False")]
    public bool AddXyBlock
    {
      get => (bool)this[nameof(AddXyBlock)];
      set => this[nameof(AddXyBlock)] = value;
    }
  }
}

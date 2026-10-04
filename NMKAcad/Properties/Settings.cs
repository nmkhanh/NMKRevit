using System.Configuration;

namespace NMKAcad.Properties
{
  internal sealed class Settings : ApplicationSettingsBase
  {
    private static readonly Settings DefaultInstance = (Settings)Synchronized(new Settings());

    public static Settings Default => DefaultInstance;

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string SaveFolder
    {
      get => (string)this[nameof(SaveFolder)];
      set => this[nameof(SaveFolder)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string Prefix
    {
      get => (string)this[nameof(Prefix)];
      set => this[nameof(Prefix)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string Main
    {
      get => (string)this[nameof(Main)];
      set => this[nameof(Main)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string Suffix
    {
      get => (string)this[nameof(Suffix)];
      set => this[nameof(Suffix)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("true")]
    public bool AutoIncrementSuffix
    {
      get => (bool)this[nameof(AutoIncrementSuffix)];
      set => this[nameof(AutoIncrementSuffix)] = value;
    }
  }
}

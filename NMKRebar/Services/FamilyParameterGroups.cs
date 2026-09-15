using Autodesk.Revit.DB;

namespace NMKRebar.Services
{
  internal static class FamilyParameterGroups
  {
    public static ForgeTypeId Dimensions()
    {
#if NETFRAMEWORK
      return ParameterUtils.GetParameterGroupTypeId(BuiltInParameterGroup.PG_GEOMETRY);
#else
      return GroupTypeId.Geometry;
#endif
    }

    public static void AddFamilyParameter(
      FamilyManager fm,
      string name,
      ForgeTypeId spec,
      bool otherGroup,
      bool isInstance)
    {
      AddFamilyParameter(fm, name, spec, isInstance, otherGroup ? ParameterBucket.Other : ParameterBucket.Dimensions);
    }

    public static void AddFamilyParameter(
      FamilyManager fm,
      string name,
      ForgeTypeId spec,
      bool isInstance,
      ParameterBucket bucket)
    {
      if (bucket == ParameterBucket.Other)
      {
#if NETFRAMEWORK
        fm.AddParameter(name, BuiltInParameterGroup.INVALID, ToParameterType(spec), isInstance);
#else
        fm.AddParameter(name, new ForgeTypeId(string.Empty), spec, isInstance);
#endif
        return;
      }

      if (bucket == ParameterBucket.General)
      {
#if NETFRAMEWORK
        fm.AddParameter(name, BuiltInParameterGroup.PG_GENERAL, ToParameterType(spec), isInstance);
#else
        fm.AddParameter(name, General(), spec, isInstance);
#endif
        return;
      }

      fm.AddParameter(name, Dimensions(), spec, isInstance);
    }

    public enum ParameterBucket
    {
      Dimensions,
      General,
      Other
    }

    public static bool IsGeneral(FamilyParameter parameter)
    {
      if (parameter?.Definition == null)
      {
        return false;
      }

#if NETFRAMEWORK
      if (parameter.Definition is InternalDefinition internalDefinition)
      {
        return internalDefinition.ParameterGroup == BuiltInParameterGroup.PG_GENERAL;
      }
#endif
      return parameter.Definition.GetGroupTypeId() == General();
    }

    public static bool IsBuiltIn(FamilyParameter parameter)
    {
      return parameter.Definition is InternalDefinition internalDefinition
        && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID;
    }

    public static ForgeTypeId General()
    {
#if NETFRAMEWORK
      return ParameterUtils.GetParameterGroupTypeId(BuiltInParameterGroup.PG_GENERAL);
#else
      return GroupTypeId.General;
#endif
    }

#if NETFRAMEWORK
    private static ParameterType ToParameterType(ForgeTypeId spec)
    {
      if (spec == SpecTypeId.Angle)
      {
        return ParameterType.Angle;
      }

      if (spec == SpecTypeId.Boolean.YesNo)
      {
        return ParameterType.YesNo;
      }

      if (spec == SpecTypeId.Number)
      {
        return ParameterType.Number;
      }

      if (spec == SpecTypeId.String.Text)
      {
        return ParameterType.Text;
      }

      return ParameterType.Length;
    }
#endif
  }
}

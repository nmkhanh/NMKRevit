using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKRevit.FilledRegions.Services;
using System;
using System.Linq;

namespace NMKRevit.FilledRegions.Commands
{
  [Transaction(TransactionMode.Manual)]
  public sealed class SplitFilledRegionLoopsCommand : IExternalCommand
  {
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        var service = new FilledRegionSplitService();
        FilledRegionSplitResult result = service.Execute(commandData.Application.ActiveUIDocument);
        if (result.Cancelled)
        {
          return Result.Cancelled;
        }

        var dialog = new Autodesk.Revit.UI.TaskDialog("Split FilledRegion Loops")
        {
          MainInstruction = "FilledRegion split completed",
          MainContent = $"Checked: {result.Checked}\nSplit originals: {result.Split}\nCreated regions: {result.Created}\nSkipped single-part: {result.Skipped}\nFailed: {result.Failures.Count}"
        };
        if (result.Failures.Count > 0)
        {
          dialog.ExpandedContent = string.Join("\n", result.Failures.Take(30));
        }
        dialog.Show();
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.ToString();
        return Result.Failed;
      }
    }
  }
}
